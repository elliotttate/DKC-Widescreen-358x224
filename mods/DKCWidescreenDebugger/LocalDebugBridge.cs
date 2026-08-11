using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace DKCWidescreenDebugger
{
    internal sealed class BridgeRequest : IDisposable
    {
        public string Id;
        public string Command;
        public Dictionary<string, string> Arguments;
        public string ResultJson;
        public Exception Error;
        public readonly ManualResetEventSlim Complete = new ManualResetEventSlim(false);
        private int _producerFinished;
        private int _waiterFinished;
        private int _disposed;

        public void Signal()
        {
            Complete.Set();
            Volatile.Write(ref _producerFinished, 1);
            TryDisposeCompletion();
        }

        public void FinishWait()
        {
            Volatile.Write(ref _waiterFinished, 1);
            TryDisposeCompletion();
        }

        private void TryDisposeCompletion()
        {
            if (Volatile.Read(ref _producerFinished) == 0 || Volatile.Read(ref _waiterFinished) == 0) return;
            Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Complete.Dispose();
        }
    }

    internal sealed class LocalDebugBridge : IDisposable
    {
        private readonly ConcurrentQueue<BridgeRequest> _requests = new ConcurrentQueue<BridgeRequest>();
        private readonly ManualLogSource _log;
        private readonly string _endpointFile;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        public int Port { get; private set; }
        public string Token { get; private set; }

        public LocalDebugBridge(string endpointFile, ManualLogSource log)
        {
            _endpointFile = endpointFile;
            _log = log;
        }

        public void Start(int requestedPort)
        {
            if (_running) return;
            Token = Guid.NewGuid().ToString("N");
            _listener = new TcpListener(IPAddress.Loopback, Math.Max(0, requestedPort));
            try { _listener.Start(); }
            catch (SocketException) when (requestedPort != 0)
            {
                try { _listener.Stop(); } catch { }
                _log.LogWarning("Configured MCP bridge port " + requestedPort + " is busy; selecting an available loopback port.");
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
            }
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            Directory.CreateDirectory(Path.GetDirectoryName(_endpointFile));
            File.WriteAllText(_endpointFile, Json.Object(new Dictionary<string, object>
            {
                { "host", "127.0.0.1" }, { "port", Port }, { "token", Token },
                { "pid", Process.GetCurrentProcess().Id }, { "protocol", 1 },
                { "pluginVersion", DKCWidescreenDebuggerPlugin.PluginVersion }
            }));
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "DKC debugger bridge" };
            _acceptThread.Start();
            _log.LogInfo("LLM debug bridge listening on 127.0.0.1:" + Port + "; endpoint file: " + _endpointFile);
        }

        public bool TryDequeue(out BridgeRequest request) { return _requests.TryDequeue(out request); }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch (SocketException) { if (_running) _log.LogWarning("LLM bridge accept failed."); }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { if (_running) _log.LogError("LLM bridge error: " + ex); }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 35000;
                client.SendTimeout = 35000;
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192, true))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, true) { AutoFlush = true })
                {
                    while (_running)
                    {
                        string line;
                        try { line = reader.ReadLine(); }
                        catch { break; }
                        if (line == null) break;
                        if (line.Length > 1024 * 1024)
                        {
                            writer.WriteLine(ErrorReply(string.Empty, "Request exceeds the 1 MiB limit."));
                            continue;
                        }
                        BridgeRequest request = null;
                        var enqueued = false;
                        try
                        {
                            request = Parse(line);
                            if (!string.Equals(request.Arguments["__token"], Token, StringComparison.Ordinal))
                            {
                                writer.WriteLine(ErrorReply(request.Id, "Authentication failed."));
                                continue;
                            }
                            request.Arguments.Remove("__token");
                            _requests.Enqueue(request);
                            enqueued = true;
                            if (!request.Complete.Wait(TimeSpan.FromSeconds(30)))
                            {
                                writer.WriteLine(ErrorReply(request.Id, "The game did not process the request within 30 seconds."));
                                continue;
                            }
                            writer.WriteLine(request.Error == null
                                ? "{\"id\":" + Json.Escape(request.Id) + ",\"ok\":true,\"result\":" + (request.ResultJson ?? "null") + "}"
                                : ErrorReply(request.Id, request.Error.Message));
                        }
                        catch (Exception ex)
                        {
                            writer.WriteLine(ErrorReply(request == null ? string.Empty : request.Id, ex.Message));
                        }
                        finally
                        {
                            if (request != null)
                            {
                                if (enqueued) request.FinishWait();
                                else request.Dispose();
                            }
                        }
                    }
                }
            }
        }

        private static BridgeRequest Parse(string line)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || ((parts.Length - 3) & 1) != 0) throw new FormatException("Malformed bridge request.");
            var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "__token", parts[1] }
            };
            for (var i = 3; i < parts.Length; i += 2)
                arguments[Decode(parts[i])] = Decode(parts[i + 1]);
            return new BridgeRequest { Id = parts[0], Command = parts[2], Arguments = arguments };
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static string ErrorReply(string id, string message)
        {
            return "{\"id\":" + Json.Escape(id) + ",\"ok\":false,\"error\":" + Json.Escape(message) + "}";
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            try { if (_acceptThread != null && _acceptThread.IsAlive) _acceptThread.Join(1000); } catch { }
            try { if (File.Exists(_endpointFile)) File.Delete(_endpointFile); } catch { }
            BridgeRequest request;
            while (_requests.TryDequeue(out request))
            {
                request.Error = new IOException("The game debug bridge stopped.");
                request.Signal();
            }
        }
    }
}
