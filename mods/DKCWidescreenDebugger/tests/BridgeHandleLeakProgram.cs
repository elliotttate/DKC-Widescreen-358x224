using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BepInEx.Logging
{
    internal sealed class ManualLogSource
    {
        public void LogInfo(object value) { }
        public void LogWarning(object value) { }
        public void LogError(object value) { }
    }
}

namespace DKCWidescreenDebugger
{
    internal static class DKCWidescreenDebuggerPlugin
    {
        public const string PluginVersion = "test";
    }

    internal static class Json
    {
        public static string Escape(string value)
        {
            if (value == null) return "null";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public static string Object(IDictionary<string, object> values)
        {
            var first = true;
            var result = new StringBuilder("{");
            foreach (var pair in values)
            {
                if (!first) result.Append(',');
                first = false;
                result.Append(Escape(pair.Key)).Append(':');
                if (pair.Value is string) result.Append(Escape((string)pair.Value));
                else result.Append(Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            return result.Append('}').ToString();
        }
    }

    internal static class BridgeHandleLeakProgram
    {
        private static int Main()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("Debugger bridge handle test skipped: Windows required.");
                return 0;
            }
            var directory = Path.Combine(Path.GetTempPath(), "dkc-debug-bridge-handle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var bridge = new LocalDebugBridge(Path.Combine(directory, "bridge.json"), new BepInEx.Logging.ManualLogSource());
            var pumping = true;
            Thread pump = null;
            try
            {
                bridge.Start(0);
                pump = new Thread(() =>
                {
                    while (Volatile.Read(ref pumping))
                    {
                        BridgeRequest request;
                        if (bridge.TryDequeue(out request))
                        {
                            request.ResultJson = "{}";
                            request.Signal();
                        }
                        else Thread.Sleep(1);
                    }
                }) { IsBackground = true };
                pump.Start();

                for (var i = 0; i < 20; i++) Request(bridge.Port, bridge.Token, "warmup-" + i);
                Collect();
                var before = Process.GetCurrentProcess().HandleCount;
                const int requests = 500;
                for (var i = 0; i < requests; i++) Request(bridge.Port, bridge.Token, "request-" + i);
                Collect();
                var after = Process.GetCurrentProcess().HandleCount;
                var delta = after - before;
                Console.WriteLine("Debugger bridge bounded handle test: requests=" + requests + ", before=" + before + ", after=" + after + ", delta=" + delta + ".");
                if (delta > 8) throw new InvalidOperationException("Debugger bridge leaked more than 8 handles across the bounded request loop.");
                return 0;
            }
            finally
            {
                Volatile.Write(ref pumping, false);
                if (pump != null) pump.Join(1000);
                bridge.Dispose();
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static void Request(int port, string token, string id)
        {
            using (var client = new TcpClient())
            {
                client.Connect("127.0.0.1", port);
                using (var stream = client.GetStream())
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
                {
                    writer.WriteLine(id + "\t" + token + "\tget_status");
                    var response = reader.ReadLine();
                    if (response == null || response.IndexOf("\"ok\":true", StringComparison.Ordinal) < 0)
                        throw new InvalidOperationException("Debugger bridge request failed: " + response);
                }
            }
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(100);
        }
    }
}
