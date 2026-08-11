using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace DKCTilemapInspector
{
    internal static class Program
    {
        private static volatile bool _processRequests = true;

        private static int Main()
        {
            var testRoot = Path.Combine(Path.GetTempPath(), "DKCTilemapInspector-bridge-stress-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            var endpoint = Path.Combine(testRoot, "bridge.json");
            var bridge = new LoopbackBridge(endpoint, new ManualLogSource(), TimeSpan.FromSeconds(2));
            Thread processor = null;
            try
            {
                bridge.Start(0);
                processor = new Thread(() => ProcessRequests(bridge)) { IsBackground = true, Name = "bridge stress main-thread stand-in" };
                processor.Start();

                for (var i = 0; i < 32; i++) AssertOk(Call(bridge.Port, "warmup-" + i, bridge.Token, "status"));
                WaitForWorkers(bridge);
                Collect();
                var process = Process.GetCurrentProcess();
                process.Refresh();
                var baselineHandles = process.HandleCount;
                var baselineThreads = process.Threads.Count;

                for (var i = 0; i < 1000; i++) AssertOk(Call(bridge.Port, "stress-" + i, bridge.Token, "status"));
                for (var i = 0; i < 100; i++) AssertError(Call(bridge.Port, "bad-token-" + i, "wrong", "status"));
                for (var i = 0; i < 100; i++) AssertError(CallRaw(bridge.Port, "malformed"));

                WaitForWorkers(bridge);
                Collect();
                process.Refresh();
                var finalHandles = process.HandleCount;
                var finalThreads = process.Threads.Count;
                var handleDelta = finalHandles - baselineHandles;
                var threadDelta = finalThreads - baselineThreads;

                if (bridge.ActiveClientWorkers != 0) throw new InvalidOperationException("Client workers did not drain.");
                if (bridge.PeakClientWorkers > 8) throw new InvalidOperationException("Client worker cap was exceeded.");
                if (handleDelta > 8) throw new InvalidOperationException("Handle count grew by " + handleDelta + ".");
                if (threadDelta > 4) throw new InvalidOperationException("Thread count grew by " + threadDelta + ".");

                Console.WriteLine("PASS requests=1232 activeWorkers=" + bridge.ActiveClientWorkers +
                    " peakWorkers=" + bridge.PeakClientWorkers + " handleDelta=" + handleDelta +
                    " threadDelta=" + threadDelta);
                return 0;
            }
            finally
            {
                bridge.Dispose();
                _processRequests = false;
                if (processor != null && processor.IsAlive) processor.Join(2000);
                if (File.Exists(endpoint)) throw new InvalidOperationException("Endpoint file was not removed on dispose.");
                try { Directory.Delete(testRoot, true); } catch { }
            }
        }

        private static void ProcessRequests(LoopbackBridge bridge)
        {
            while (_processRequests)
            {
                BridgeRequest request;
                if (!bridge.TryDequeue(out request))
                {
                    Thread.Sleep(1);
                    continue;
                }
                request.ResultJson = "{\"status\":\"ok\"}";
                request.SignalCompleted();
            }
        }

        private static string Call(int port, string id, string token, string command)
        {
            return CallRaw(port, id + "\t" + token + "\t" + command);
        }

        private static string CallRaw(int port, string line)
        {
            using (var client = new TcpClient())
            {
                client.Connect("127.0.0.1", port);
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    writer.WriteLine(line);
                    return reader.ReadLine();
                }
            }
        }

        private static void AssertOk(string response)
        {
            if (response == null || response.IndexOf("\"ok\":true", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("Expected success, received: " + response);
        }

        private static void AssertError(string response)
        {
            if (response == null || response.IndexOf("\"ok\":false", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("Expected error, received: " + response);
        }

        private static void WaitForWorkers(LoopbackBridge bridge)
        {
            var timer = Stopwatch.StartNew();
            while (bridge.ActiveClientWorkers != 0 && timer.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(10);
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(250);
        }
    }
}
