using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SuperZSNESAllocationProbe
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESAllocationProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.allocationprobe";
        public const string PluginName = "SuperZSNES Allocation Scope Probe";
        public const string PluginVersion = "0.1.0";

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<int> _windowSeconds;
        private ConfigEntry<bool> _includePerBackground;
        private Harmony _harmony;
        private bool _armed;
        private string _rootDirectory;
        private string _sessionDirectory;

        private void Awake()
        {
            _enabled = Config.Bind(
                "Probe", "Enabled", false,
                "Arm at process startup. False installs no Harmony patches and starts no writer thread; changing this requires a restart.");
            _windowSeconds = Config.Bind(
                "Probe", "WindowSeconds", 5,
                new ConfigDescription("Aggregation interval in seconds.", new AcceptableValueRange<int>(1, 60)));
            _includePerBackground = Config.Bind(
                "Probe", "IncludePerBackgroundScope", true,
                "Measure each of the normally three PPURenderer.GenerateBackground calls in addition to the composite render call.");

            _rootDirectory = Path.Combine(Paths.BepInExRootPath, "AllocationProbe");
            Directory.CreateDirectory(_rootDirectory);
            if (!_enabled.Value)
            {
                WriteStatus("loaded-disabled", null);
                Logger.LogInfo(PluginName + " " + PluginVersion +
                               " loaded disabled; no target methods were patched and no writer thread was started.");
                return;
            }

            try
            {
                AllocationCounter.Verify();
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
                _sessionDirectory = Path.Combine(_rootDirectory, "session-" + stamp);
                ProbeRuntime.Start(_sessionDirectory, _windowSeconds.Value, Logger);
                _harmony = new Harmony(PluginGuid);
                PatchTargets();
                _armed = true;
                WriteStatus("armed", null);
                Logger.LogInfo(PluginName + " armed. Output: " + _sessionDirectory);
            }
            catch (Exception ex)
            {
                try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
                try { ProbeRuntime.Stop(); } catch { }
                _armed = false;
                WriteStatus("arm-failed", ex.Message);
                Logger.LogError(PluginName + " failed closed: " + ex);
            }
        }

        private void PatchTargets()
        {
            var update = AccessTools.Method(typeof(MasterExecutor), "Update", Type.EmptyTypes);
            var runFrame = AccessTools.Method(typeof(MasterExecutor), "RunFrame", Type.EmptyTypes);
            var generateBackgrounds = AccessTools.Method(typeof(PPURenderer), nameof(PPURenderer.GenerateBackgrounds), Type.EmptyTypes);
            var generateBackground = AccessTools.Method(typeof(PPURenderer), "GenerateBackground");
            if (update == null || runFrame == null || generateBackgrounds == null || generateBackground == null)
                throw new MissingMethodException("Expected SuperZSNES v0.230 allocation-scope targets were not found.");

            _harmony.Patch(update,
                prefix: Hook(nameof(ProbeHooks.MasterUpdatePrefix)),
                postfix: Hook(nameof(ProbeHooks.MasterUpdatePostfix)));
            _harmony.Patch(runFrame,
                prefix: Hook(nameof(ProbeHooks.RunFramePrefix)),
                postfix: Hook(nameof(ProbeHooks.RunFramePostfix)));
            _harmony.Patch(generateBackgrounds,
                prefix: Hook(nameof(ProbeHooks.GenerateBackgroundsPrefix)),
                postfix: Hook(nameof(ProbeHooks.GenerateBackgroundsPostfix)));
            if (_includePerBackground.Value)
            {
                _harmony.Patch(generateBackground,
                    prefix: Hook(nameof(ProbeHooks.GenerateBackgroundPrefix)),
                    postfix: Hook(nameof(ProbeHooks.GenerateBackgroundPostfix)));
            }
        }

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), name));
        }

        // Two cumulative, allocation-free reads bracket the whole main-thread Unity
        // frame. The delta includes scripts outside MasterExecutor.Update as well.
        private void LateUpdate()
        {
            if (_armed)
                ProbeRuntime.FrameBoundary(Stopwatch.GetTimestamp(), AllocationCounter.Read());
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            try { ProbeRuntime.Stop(); } catch { }
            _armed = false;
            try { WriteStatus("shutdown", null); } catch { }
        }

        private void WriteStatus(string state, string error)
        {
            try
            {
                Directory.CreateDirectory(_rootDirectory);
                var json = "{\"pluginVersion\":\"" + PluginVersion + "\",\"state\":\"" + Escape(state) +
                           "\",\"configuredEnabled\":" + (_enabled != null && _enabled.Value ? "true" : "false") +
                           ",\"armed\":" + (_armed ? "true" : "false") +
                           ",\"includePerBackgroundScope\":" + (_includePerBackground != null && _includePerBackground.Value ? "true" : "false") +
                           ",\"sessionDirectory\":" + (string.IsNullOrEmpty(_sessionDirectory) ? "null" : "\"" + Escape(_sessionDirectory) + "\"") +
                           ",\"error\":" + (string.IsNullOrEmpty(error) ? "null" : "\"" + Escape(error) + "\"") + "}";
                File.WriteAllText(Path.Combine(_rootDirectory, "status.json"), json);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not write allocation-probe status: " + ex.Message);
            }
        }

        internal static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }

    internal static class AllocationCounter
    {
        private static Func<long> _read;

        // SuperZSNES's bundled Unity Mono mscorlib exposes this cumulative,
        // per-thread counter. The net472 targeting facade omits the declaration,
        // so resolve it once and use a strongly typed delegate on hot paths.
        // Reading it does not force or schedule a collection.
        internal static long Read()
        {
            var read = _read;
            if (read == null) read = Resolve();
            return read();
        }

        internal static void Verify()
        {
            Resolve();
            var before = Read();
            var sample = new byte[128];
            GC.KeepAlive(sample);
            var after = Read();
            if (after < before)
                throw new InvalidOperationException("The per-thread allocation counter was not monotonic.");
        }

        private static Func<long> Resolve()
        {
            var read = _read;
            if (read != null) return read;
            var method = typeof(GC).GetMethod(
                "GetAllocatedBytesForCurrentThread",
                BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(long))
                throw new MissingMethodException("System.GC.GetAllocatedBytesForCurrentThread() is unavailable.");
            read = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
            Interlocked.CompareExchange(ref _read, read, null);
            return _read;
        }
    }

    internal enum ScopeId
    {
        UnityFrame = 0,
        MasterUpdate = 1,
        RunFrame = 2,
        GenerateBackgrounds = 3,
        GenerateBackground = 4,
        Count = 5
    }

    internal readonly struct ScopeState
    {
        internal readonly long StartTicks;
        internal readonly long StartBytes;

        internal ScopeState(long startTicks, long startBytes)
        {
            StartTicks = startTicks;
            StartBytes = startBytes;
        }
    }

    internal static class ProbeHooks
    {
        private static ScopeState Start()
        {
            return new ScopeState(Stopwatch.GetTimestamp(), AllocationCounter.Read());
        }

        private static void Finish(ScopeId id, ScopeState state)
        {
            var bytes = AllocationCounter.Read();
            var ticks = Stopwatch.GetTimestamp();
            ProbeRuntime.Record(id, ticks - state.StartTicks, bytes - state.StartBytes);
        }

        public static void MasterUpdatePrefix(out ScopeState __state) { __state = Start(); }
        public static void MasterUpdatePostfix(ScopeState __state) { Finish(ScopeId.MasterUpdate, __state); }
        public static void RunFramePrefix(out ScopeState __state) { __state = Start(); }
        public static void RunFramePostfix(ScopeState __state) { Finish(ScopeId.RunFrame, __state); }
        public static void GenerateBackgroundsPrefix(out ScopeState __state) { __state = Start(); }
        public static void GenerateBackgroundsPostfix(ScopeState __state) { Finish(ScopeId.GenerateBackgrounds, __state); }
        public static void GenerateBackgroundPrefix(out ScopeState __state) { __state = Start(); }
        public static void GenerateBackgroundPostfix(ScopeState __state) { Finish(ScopeId.GenerateBackground, __state); }
    }

    internal static class ProbeRuntime
    {
        private static readonly ScopeMetric[] Metrics = CreateMetrics();
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static Thread _writerThread;
        private static volatile bool _running;
        private static volatile bool _stopping;
        private static int _windowSeconds;
        private static string _sessionDirectory;
        private static ManualLogSource _logger;
        private static long _lastFrameTicks;
        private static long _lastFrameBytes;

        private static ScopeMetric[] CreateMetrics()
        {
            var metrics = new ScopeMetric[(int)ScopeId.Count];
            for (var index = 0; index < metrics.Length; index++) metrics[index] = new ScopeMetric();
            return metrics;
        }

        internal static void Start(string sessionDirectory, int windowSeconds, ManualLogSource logger)
        {
            if (_running) throw new InvalidOperationException("Allocation probe is already running.");
            Directory.CreateDirectory(sessionDirectory);
            _sessionDirectory = sessionDirectory;
            _windowSeconds = windowSeconds;
            _logger = logger;
            _lastFrameTicks = 0;
            _lastFrameBytes = 0;
            foreach (var metric in Metrics) metric.Reset();
            _stopping = false;
            _running = true;
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "SuperZSNES Allocation Probe Writer"
            };
            _writerThread.Start();
        }

        internal static void Stop()
        {
            if (!_running) return;
            _stopping = true;
            Wake.Set();
            var thread = _writerThread;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(3000);
            _writerThread = null;
            _running = false;
            _logger = null;
        }

        internal static void Record(ScopeId id, long ticks, long bytes)
        {
            if (!_running || ticks < 0) return;
            Metrics[(int)id].Record(ticks, bytes);
        }

        internal static void FrameBoundary(long nowTicks, long nowBytes)
        {
            if (!_running) return;
            var priorTicks = _lastFrameTicks;
            var priorBytes = _lastFrameBytes;
            _lastFrameTicks = nowTicks;
            _lastFrameBytes = nowBytes;
            if (priorTicks != 0 && nowTicks >= priorTicks && nowBytes >= priorBytes)
                Metrics[(int)ScopeId.UnityFrame].Record(nowTicks - priorTicks, nowBytes - priorBytes);
        }

        private static void WriterLoop()
        {
            StreamWriter writer = null;
            try
            {
                writer = new StreamWriter(Path.Combine(_sessionDirectory, "windows.jsonl"), false, new UTF8Encoding(false));
                while (!_stopping)
                {
                    Wake.WaitOne(_windowSeconds * 1000);
                    WriteSnapshot(writer, _stopping ? "shutdown" : "interval");
                }
                writer.Flush();
            }
            catch (Exception ex)
            {
                try { _logger?.LogError("Allocation probe writer stopped: " + ex); } catch { }
            }
            finally
            {
                try { writer?.Dispose(); } catch { }
            }
        }

        private static void WriteSnapshot(StreamWriter writer, string reason)
        {
            var snapshots = new ScopeSnapshot[(int)ScopeId.Count];
            for (var index = 0; index < snapshots.Length; index++)
                snapshots[index] = Metrics[index].SnapshotAndReset();
            var json = "{\"utc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                       "\",\"reason\":\"" + reason + "\",\"gc\":{\"gen0\":" + GC.CollectionCount(0) +
                       ",\"gen1\":" + GC.CollectionCount(1) + ",\"gen2\":" + GC.CollectionCount(2) +
                       "},\"managedBytes\":" + GC.GetTotalMemory(false) +
                       ",\"scopes\":{" +
                       "\"unityFrame\":" + snapshots[(int)ScopeId.UnityFrame].ToJson() + "," +
                       "\"masterUpdate\":" + snapshots[(int)ScopeId.MasterUpdate].ToJson() + "," +
                       "\"runFrame\":" + snapshots[(int)ScopeId.RunFrame].ToJson() + "," +
                       "\"generateBackgrounds\":" + snapshots[(int)ScopeId.GenerateBackgrounds].ToJson() + "," +
                       "\"generateBackground\":" + snapshots[(int)ScopeId.GenerateBackground].ToJson() + "}}";
            writer.WriteLine(json);
            writer.Flush();
        }
    }

    internal sealed class ScopeMetric
    {
        private long _calls;
        private long _totalTicks;
        private long _maximumTicks;
        private long _totalBytes;
        private long _maximumBytes;
        private long _nonzeroAllocationCalls;
        private long _negativeAllocationDeltas;

        internal void Record(long ticks, long bytes)
        {
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _totalTicks, ticks);
            UpdateMaximum(ref _maximumTicks, ticks);
            if (bytes < 0)
            {
                Interlocked.Increment(ref _negativeAllocationDeltas);
                return;
            }
            Interlocked.Add(ref _totalBytes, bytes);
            UpdateMaximum(ref _maximumBytes, bytes);
            if (bytes != 0) Interlocked.Increment(ref _nonzeroAllocationCalls);
        }

        internal ScopeSnapshot SnapshotAndReset()
        {
            return new ScopeSnapshot(
                Interlocked.Exchange(ref _calls, 0),
                Interlocked.Exchange(ref _totalTicks, 0),
                Interlocked.Exchange(ref _maximumTicks, 0),
                Interlocked.Exchange(ref _totalBytes, 0),
                Interlocked.Exchange(ref _maximumBytes, 0),
                Interlocked.Exchange(ref _nonzeroAllocationCalls, 0),
                Interlocked.Exchange(ref _negativeAllocationDeltas, 0));
        }

        internal void Reset() { SnapshotAndReset(); }

        private static void UpdateMaximum(ref long target, long value)
        {
            var observed = Interlocked.Read(ref target);
            while (value > observed)
            {
                var prior = Interlocked.CompareExchange(ref target, value, observed);
                if (prior == observed) return;
                observed = prior;
            }
        }
    }

    internal readonly struct ScopeSnapshot
    {
        private readonly long _calls;
        private readonly long _totalTicks;
        private readonly long _maximumTicks;
        private readonly long _totalBytes;
        private readonly long _maximumBytes;
        private readonly long _nonzeroAllocationCalls;
        private readonly long _negativeAllocationDeltas;

        internal ScopeSnapshot(long calls, long totalTicks, long maximumTicks, long totalBytes,
            long maximumBytes, long nonzeroAllocationCalls, long negativeAllocationDeltas)
        {
            _calls = calls;
            _totalTicks = totalTicks;
            _maximumTicks = maximumTicks;
            _totalBytes = totalBytes;
            _maximumBytes = maximumBytes;
            _nonzeroAllocationCalls = nonzeroAllocationCalls;
            _negativeAllocationDeltas = negativeAllocationDeltas;
        }

        internal string ToJson()
        {
            var averageUs = _calls == 0 ? 0 : _totalTicks * 1000000.0 / Stopwatch.Frequency / _calls;
            var maximumUs = _maximumTicks * 1000000.0 / Stopwatch.Frequency;
            var averageBytes = _calls == 0 ? 0 : (double)_totalBytes / _calls;
            return "{\"calls\":" + _calls +
                   ",\"avgUs\":" + Number(averageUs) +
                   ",\"maxUs\":" + Number(maximumUs) +
                   ",\"totalAllocatedBytes\":" + _totalBytes +
                   ",\"avgAllocatedBytes\":" + Number(averageBytes) +
                   ",\"maxAllocatedBytes\":" + _maximumBytes +
                   ",\"nonzeroAllocationCalls\":" + _nonzeroAllocationCalls +
                   ",\"negativeAllocationDeltas\":" + _negativeAllocationDeltas + "}";
        }

        private static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
