using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace DKCTilemapInspector
{
    internal sealed class BridgeRequest
    {
        private readonly object _completionGate = new object();
        private bool _completed;

        public string Id;
        public string Command;
        public Dictionary<string, string> Arguments;
        public string ResultJson;
        public Exception Error;

        public bool WaitForCompletion(TimeSpan timeout)
        {
            lock (_completionGate)
            {
                if (!_completed) Monitor.Wait(_completionGate, timeout);
                return _completed;
            }
        }

        public void SignalCompleted()
        {
            lock (_completionGate)
            {
                _completed = true;
                Monitor.PulseAll(_completionGate);
            }
        }
    }

    internal sealed class LoopbackBridge : IDisposable
    {
        private readonly ConcurrentQueue<BridgeRequest> _requests = new ConcurrentQueue<BridgeRequest>();
        private readonly ConcurrentDictionary<BridgeRequest, byte> _pendingRequests = new ConcurrentDictionary<BridgeRequest, byte>();
        private readonly object _requestGate = new object();
        private readonly object _clientGate = new object();
        private readonly HashSet<TcpClient> _clients = new HashSet<TcpClient>();
        private readonly string _endpointFile;
        private readonly ManualLogSource _log;
        private readonly TimeSpan _requestTimeout;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _activeClientWorkers;
        private int _peakClientWorkers;

        private const int MaxClientWorkers = 8;

        public int Port { get; private set; }
        public string Token { get; private set; }

        internal int ActiveClientWorkers { get { lock (_clientGate) return _activeClientWorkers; } }
        internal int PeakClientWorkers { get { lock (_clientGate) return _peakClientWorkers; } }

        public LoopbackBridge(string endpointFile, ManualLogSource log, TimeSpan? requestTimeout = null)
        {
            _endpointFile = endpointFile;
            _log = log;
            _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
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
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _log.LogWarning("Tilemap inspector port " + requestedPort + " was busy; selected a free loopback port.");
            }
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            Directory.CreateDirectory(Path.GetDirectoryName(_endpointFile));
            File.WriteAllText(_endpointFile, Json.Object(new Dictionary<string, object>
            {
                { "host", "127.0.0.1" }, { "port", Port }, { "token", Token },
                { "pid", Process.GetCurrentProcess().Id }, { "protocol", 1 },
                { "plugin", DKCTilemapInspectorPlugin.PluginGuid },
                { "pluginVersion", DKCTilemapInspectorPlugin.PluginVersion },
                { "commands", new[] { "status", "capture", "latest" } }
            }));
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "DKC tilemap inspector bridge" };
            _acceptThread.Start();
            _log.LogInfo("Tilemap inspector bridge listening on 127.0.0.1:" + Port);
        }

        public bool TryDequeue(out BridgeRequest request) { return _requests.TryDequeue(out request); }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    DispatchClient(client);
                }
                catch (SocketException) { if (_running) _log.LogWarning("Tilemap inspector accept failed."); }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { if (_running) _log.LogError(ex); }
            }
        }

        private void DispatchClient(TcpClient client)
        {
            lock (_clientGate)
            {
                if (!_running || _activeClientWorkers >= MaxClientWorkers)
                {
                    client.Dispose();
                    return;
                }

                _clients.Add(client);
                _activeClientWorkers++;
                if (_activeClientWorkers > _peakClientWorkers) _peakClientWorkers = _activeClientWorkers;
            }

            if (!ThreadPool.QueueUserWorkItem(_ =>
            {
                try { HandleClient(client); }
                finally
                {
                    lock (_clientGate)
                    {
                        _clients.Remove(client);
                        _activeClientWorkers--;
                        Monitor.PulseAll(_clientGate);
                    }
                }
            }))
            {
                client.Dispose();
                lock (_clientGate)
                {
                    _clients.Remove(client);
                    _activeClientWorkers--;
                    Monitor.PulseAll(_clientGate);
                }
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
                        BridgeRequest request = null;
                        try
                        {
                            request = Parse(line);
                            if (!string.Equals(request.Arguments["__token"], Token, StringComparison.Ordinal))
                            {
                                writer.WriteLine(ErrorReply(request.Id, "Authentication failed."));
                                continue;
                            }
                            request.Arguments.Remove("__token");
                            lock (_requestGate)
                            {
                                if (!_running) throw new IOException("The tilemap inspector bridge stopped.");
                                _pendingRequests.TryAdd(request, 0);
                                _requests.Enqueue(request);
                            }
                            if (!request.WaitForCompletion(_requestTimeout))
                            {
                                writer.WriteLine(ErrorReply(request.Id, "SuperZSNES did not process the request within " +
                                    _requestTimeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " seconds."));
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
                            if (request != null) _pendingRequests.TryRemove(request, out _);
                        }
                    }
                }
            }
        }

        private static BridgeRequest Parse(string line)
        {
            if (line.Length > 1024 * 1024) throw new FormatException("Request exceeds the 1 MiB limit.");
            var parts = line.Split('\t');
            if (parts.Length < 3 || ((parts.Length - 3) & 1) != 0) throw new FormatException("Malformed bridge request.");
            var request = new BridgeRequest
            {
                Id = parts[0],
                Command = parts[2],
                Arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "__token", parts[1] }
                }
            };
            for (var i = 3; i < parts.Length; i += 2)
                request.Arguments[Decode(parts[i])] = Decode(parts[i + 1]);
            return request;
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
            lock (_requestGate)
            {
                _running = false;
                var stopped = new IOException("The tilemap inspector bridge stopped.");
                BridgeRequest queued;
                while (_requests.TryDequeue(out queued))
                {
                    queued.Error = stopped;
                    queued.SignalCompleted();
                }
                foreach (var pending in _pendingRequests.Keys)
                {
                    pending.Error = stopped;
                    pending.SignalCompleted();
                }
            }
            try { if (_listener != null) _listener.Stop(); } catch { }
            TcpClient[] clients;
            lock (_clientGate)
            {
                clients = new TcpClient[_clients.Count];
                _clients.CopyTo(clients);
            }
            foreach (var client in clients) try { client.Dispose(); } catch { }
            try { if (_acceptThread != null && _acceptThread.IsAlive) _acceptThread.Join(1000); } catch { }
            var deadline = Stopwatch.StartNew();
            lock (_clientGate)
            {
                while (_activeClientWorkers != 0 && deadline.Elapsed < TimeSpan.FromSeconds(2))
                    Monitor.Wait(_clientGate, TimeSpan.FromMilliseconds(100));
            }
            try { if (File.Exists(_endpointFile)) File.Delete(_endpointFile); } catch { }
        }
    }
}
