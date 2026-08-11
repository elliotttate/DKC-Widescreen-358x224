using System;
using System.Collections.Concurrent;
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
using UnityEngine;

namespace SuperZSNESRuntimePauseProbe
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESRuntimePauseProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.runtimepauseprobe";
        public const string PluginName = "SuperZSNES Runtime Pause Probe";
        public const string PluginVersion = "0.1.0";

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<int> _gapThresholdMs;
        private ConfigEntry<int> _watchdogIntervalMs;
        private ConfigEntry<int> _periodicSampleSeconds;
        private ConfigEntry<bool> _includeRareCallStacks;
        private Harmony _harmony;
        private bool _armed;
        private string _rootDirectory;
        private string _sessionDirectory;

        private void Awake()
        {
            _enabled = Config.Bind(
                "Probe", "Enabled", false,
                "Arm at process startup. False starts no thread and installs no Harmony patches. A restart is required after changing this value.");
            _gapThresholdMs = Config.Bind(
                "Probe", "GapThresholdMs", 100,
                new ConfigDescription("Only record timing events at or above this duration.", new AcceptableValueRange<int>(25, 5000)));
            _watchdogIntervalMs = Config.Bind(
                "Probe", "WatchdogIntervalMs", 25,
                new ConfigDescription("Background heartbeat interval. Lower values improve pause resolution but add wakeups.", new AcceptableValueRange<int>(10, 250)));
            _periodicSampleSeconds = Config.Bind(
                "Probe", "PeriodicSampleSeconds", 5,
                new ConfigDescription("Low-frequency GC/process sample interval.", new AcceptableValueRange<int>(1, 60)));
            _includeRareCallStacks = Config.Bind(
                "Probe", "IncludeRareCallStacks", true,
                "Capture a managed stack only when PauseGame or ResumeGame actually changes the pause state.");

            _rootDirectory = Path.Combine(Paths.BepInExRootPath, "RuntimePauseProbe");
            Directory.CreateDirectory(_rootDirectory);
            if (!_enabled.Value)
            {
                WriteStatus("loaded-disabled", null);
                Logger.LogInfo(PluginName + " " + PluginVersion +
                               " loaded disabled; no watcher thread was started and no target methods were patched.");
                return;
            }

            try
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
                _sessionDirectory = Path.Combine(_rootDirectory, "session-" + stamp);
                ProbeRuntime.Start(
                    _sessionDirectory,
                    _gapThresholdMs.Value,
                    _watchdogIntervalMs.Value,
                    _periodicSampleSeconds.Value,
                    _includeRareCallStacks.Value,
                    Application.isFocused,
                    Application.runInBackground,
                    Logger);

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
            var pause = AccessTools.Method(typeof(MasterExecutor), nameof(MasterExecutor.PauseGame), Type.EmptyTypes);
            var resume = AccessTools.Method(typeof(MasterExecutor), nameof(MasterExecutor.ResumeGame), Type.EmptyTypes);
            var step = AccessTools.Method(typeof(MasterExecutor), nameof(MasterExecutor.StepFrameForward), Type.EmptyTypes);
            var returnToGame = AccessTools.Method(typeof(MasterExecutor), nameof(MasterExecutor.ReturnToGame), Type.EmptyTypes);
            var escapeToMenu = AccessTools.Method(typeof(MasterExecutor), nameof(MasterExecutor.EscapeBackToMenu));
            var overlayEnable = AccessTools.Method(typeof(SaveStateSelectOverlay), "OnEnable", Type.EmptyTypes);
            var overlayDisable = AccessTools.Method(typeof(SaveStateSelectOverlay), "OnDisable", Type.EmptyTypes);
            if (update == null || runFrame == null || pause == null || resume == null || step == null ||
                returnToGame == null || escapeToMenu == null || overlayEnable == null || overlayDisable == null)
                throw new MissingMethodException("Expected SuperZSNES v0.230 pause-probe targets were not found.");

            _harmony.Patch(update,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.MasterUpdatePrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.MasterUpdatePostfix))));
            _harmony.Patch(runFrame,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.RunFramePrefix))));
            _harmony.Patch(pause,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.PauseGamePrefix))));
            _harmony.Patch(resume,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.ResumeGamePrefix))));
            _harmony.Patch(step,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.StepFramePrefix))));
            _harmony.Patch(returnToGame,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.ReturnToGamePrefix))));
            _harmony.Patch(escapeToMenu,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.EscapeBackToMenuPrefix))));
            _harmony.Patch(overlayEnable,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.SaveOverlayEnablePrefix))));
            _harmony.Patch(overlayDisable,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.SaveOverlayDisablePrefix))));
        }

        private void OnApplicationFocus(bool focused)
        {
            if (_armed) ProbeRuntime.FocusChanged(focused);
        }

        private void OnApplicationPause(bool paused)
        {
            if (_armed) ProbeRuntime.ApplicationPauseChanged(paused);
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
                var json = "{\"pluginVersion\":\"" + PluginVersion + "\",\"state\":\"" + Json(state) +
                           "\",\"configuredEnabled\":" + (_enabled != null && _enabled.Value ? "true" : "false") +
                           ",\"armed\":" + (_armed ? "true" : "false") +
                           ",\"sessionDirectory\":" + (string.IsNullOrEmpty(_sessionDirectory) ? "null" : "\"" + Json(_sessionDirectory) + "\"") +
                           ",\"error\":" + (string.IsNullOrEmpty(error) ? "null" : "\"" + Json(error) + "\"") + "}";
                File.WriteAllText(Path.Combine(_rootDirectory, "status.json"), json);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not write runtime-pause probe status: " + ex.Message);
            }
        }

        internal static string Json(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }

    internal static class ProbeHooks
    {
        public static void MasterUpdatePrefix(
            bool ____executing,
            bool ____gamePaused,
            EmuUIInterface ___uiInterface,
            out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            var emuState = ___uiInterface == null ? -1 : (int)___uiInterface.emuState;
            ProbeRuntime.MasterUpdateStarted(__state, ____executing, ____gamePaused, emuState);
        }

        public static void MasterUpdatePostfix(long __state)
        {
            ProbeRuntime.MasterUpdateFinished(__state, Stopwatch.GetTimestamp());
        }

        public static void RunFramePrefix()
        {
            ProbeRuntime.RunFrameStarted(Stopwatch.GetTimestamp());
        }

        public static void PauseGamePrefix(bool ____gamePaused)
        {
            if (!____gamePaused) ProbeRuntime.ControlTransition("PauseGame", false, true);
        }

        public static void ResumeGamePrefix(bool ____gamePaused)
        {
            if (____gamePaused) ProbeRuntime.ControlTransition("ResumeGame", true, false);
        }

        public static void StepFramePrefix()
        {
            ProbeRuntime.Marker("StepFrameForward");
        }

        public static void ReturnToGamePrefix()
        {
            ProbeRuntime.Marker("ReturnToGame");
        }

        public static void EscapeBackToMenuPrefix()
        {
            ProbeRuntime.Marker("EscapeBackToMenu");
        }

        public static void SaveOverlayEnablePrefix()
        {
            ProbeRuntime.Marker("SaveStateSelectOverlay.OnEnable");
        }

        public static void SaveOverlayDisablePrefix()
        {
            ProbeRuntime.Marker("SaveStateSelectOverlay.OnDisable");
        }
    }

    internal static class ProbeRuntime
    {
        private static readonly ConcurrentQueue<ProbeEvent> Queue = new ConcurrentQueue<ProbeEvent>();
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static readonly object LifecycleLock = new object();
        private static readonly long Frequency = Stopwatch.Frequency;
        private static Thread _writerThread;
        private static volatile bool _stopping;
        private static volatile bool _running;
        private static ManualLogSource _logger;
        private static string _sessionDirectory;
        private static long _thresholdTicks;
        private static int _watchdogIntervalMs;
        private static int _periodicSampleSeconds;
        private static bool _includeRareCallStacks;
        private static int _focused;
        private static int _runInBackground;
        private static int _queuedCount;
        private static long _droppedEvents;

        private static long _lastMasterUpdate;
        private static long _lastRunFrame;
        private static long _updateSerial;
        private static long _frameSerial;
        private static long _lastFrameUpdateSerial;
        private static long _gatedUpdateTotal;
        private static long _lastFrameGatedTotal;
        private static long _maxUpdateGapSinceFrame;
        private static long _maxWatchdogGapSinceFrame;
        private static long _lastWatchdogWake;
        private static int _lastExecuting = -1;
        private static int _lastPaused = -1;
        private static int _lastEmuState = int.MinValue;
        private static long _gateStart;
        private static long _gateStartUpdate;
        private static int _gateGc0;
        private static int _gateGc1;
        private static int _gateGc2;

        [ThreadStatic] private static long _updateFrameSerialAtStart;
        [ThreadStatic] private static bool _updateWasGated;

        internal static void Start(
            string sessionDirectory,
            int gapThresholdMs,
            int watchdogIntervalMs,
            int periodicSampleSeconds,
            bool includeRareCallStacks,
            bool focused,
            bool runInBackground,
            ManualLogSource logger)
        {
            lock (LifecycleLock)
            {
                if (_running) throw new InvalidOperationException("Runtime pause probe is already running.");
                Directory.CreateDirectory(sessionDirectory);
                _sessionDirectory = sessionDirectory;
                _logger = logger;
                _thresholdTicks = MillisecondsToTicks(gapThresholdMs);
                _watchdogIntervalMs = watchdogIntervalMs;
                _periodicSampleSeconds = periodicSampleSeconds;
                _includeRareCallStacks = includeRareCallStacks;
                Volatile.Write(ref _focused, focused ? 1 : 0);
                Volatile.Write(ref _runInBackground, runInBackground ? 1 : 0);
                ResetCounters();
                _stopping = false;
                _running = true;
                _writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "SuperZSNES Runtime Pause Probe"
                };
                _writerThread.Start();
                Enqueue(CreateEvent("probe-start", 0, 0, false, -1, "watchdog-and-transition-probe-armed", null));
            }
        }

        internal static void Stop()
        {
            Thread thread;
            lock (LifecycleLock)
            {
                if (!_running) return;
                _stopping = true;
                Wake.Set();
                thread = _writerThread;
            }
            if (thread != null && thread != Thread.CurrentThread) thread.Join(3000);
            lock (LifecycleLock)
            {
                _running = false;
                _writerThread = null;
                _logger = null;
            }
        }

        private static void ResetCounters()
        {
            while (Queue.TryDequeue(out _)) { }
            _queuedCount = 0;
            _droppedEvents = 0;
            _lastMasterUpdate = 0;
            _lastRunFrame = 0;
            _updateSerial = 0;
            _frameSerial = 0;
            _lastFrameUpdateSerial = 0;
            _gatedUpdateTotal = 0;
            _lastFrameGatedTotal = 0;
            _maxUpdateGapSinceFrame = 0;
            _maxWatchdogGapSinceFrame = 0;
            _lastWatchdogWake = Stopwatch.GetTimestamp();
            _lastExecuting = -1;
            _lastPaused = -1;
            _lastEmuState = int.MinValue;
            _gateStart = 0;
            _gateStartUpdate = 0;
            ProbeEvent.LastFrameGc0 = GC.CollectionCount(0);
            ProbeEvent.LastFrameGc1 = GC.CollectionCount(1);
            ProbeEvent.LastFrameGc2 = GC.CollectionCount(2);
        }

        internal static void MasterUpdateStarted(long now, bool executing, bool paused, int emuState)
        {
            if (!_running) return;
            var serial = Interlocked.Increment(ref _updateSerial);
            var previous = Interlocked.Exchange(ref _lastMasterUpdate, now);
            var gap = previous == 0 || now <= previous ? 0 : now - previous;
            UpdateMaximum(ref _maxUpdateGapSinceFrame, gap);
            _updateFrameSerialAtStart = Interlocked.Read(ref _frameSerial);
            _updateWasGated = !executing || paused || emuState != (int)EmuUIInterface.EmuState.Normal;

            var oldExecuting = Interlocked.Exchange(ref _lastExecuting, executing ? 1 : 0);
            var oldPaused = Interlocked.Exchange(ref _lastPaused, paused ? 1 : 0);
            var oldState = Interlocked.Exchange(ref _lastEmuState, emuState);
            var oldGated = oldExecuting == 0 || oldPaused > 0 ||
                           (oldState != int.MinValue && oldState != (int)EmuUIInterface.EmuState.Normal);
            if (oldExecuting < 0 || oldPaused < 0 || oldState == int.MinValue)
            {
                Enqueue(CreateEvent("initial-emulation-state", 0, 0, paused, emuState, null, null));
                if (_updateWasGated) BeginGate(now, serial);
            }
            else if (oldExecuting != (executing ? 1 : 0) || oldPaused != (paused ? 1 : 0) || oldState != emuState)
            {
                Enqueue(CreateEvent(
                    "emulation-state-transition", 0, 0, paused, emuState,
                    "previousExecuting=" + (oldExecuting > 0 ? "true" : "false") +
                    ";previousPaused=" + (oldPaused > 0 ? "true" : "false") + ";previousEmuState=" + oldState,
                    null));
                if (!oldGated && _updateWasGated) BeginGate(now, serial);
                if (oldGated && !_updateWasGated) EndGate(now, serial, paused, emuState);
            }

            if (gap >= _thresholdTicks)
            {
                Enqueue(CreateEvent(
                    "master-update-gap", gap, 0, paused, emuState,
                    "Unity MasterExecutor.Update was not entered during this interval.", null));
            }
        }

        internal static void MasterUpdateFinished(long start, long end)
        {
            if (!_running || start <= 0 || end <= start) return;
            if (_updateWasGated && Interlocked.Read(ref _frameSerial) == _updateFrameSerialAtStart)
                Interlocked.Increment(ref _gatedUpdateTotal);
            var duration = end - start;
            if (duration >= _thresholdTicks)
            {
                Enqueue(CreateEvent(
                    "master-update-duration", 0, duration,
                    Volatile.Read(ref _lastPaused) > 0,
                    Volatile.Read(ref _lastEmuState),
                    "MasterExecutor.Update itself exceeded the threshold.", null));
            }
        }

        internal static void RunFrameStarted(long now)
        {
            if (!_running) return;
            Interlocked.Increment(ref _frameSerial);
            var previous = Interlocked.Exchange(ref _lastRunFrame, now);
            var currentUpdate = Interlocked.Read(ref _updateSerial);
            var currentGated = Interlocked.Read(ref _gatedUpdateTotal);
            var updates = currentUpdate - Interlocked.Exchange(ref _lastFrameUpdateSerial, currentUpdate);
            var gatedUpdates = currentGated - Interlocked.Exchange(ref _lastFrameGatedTotal, currentGated);
            var maxUpdateGap = Interlocked.Exchange(ref _maxUpdateGapSinceFrame, 0);
            var maxWatchdogGap = Interlocked.Exchange(ref _maxWatchdogGapSinceFrame, 0);
            var lastWatchdogWake = Interlocked.Read(ref _lastWatchdogWake);
            var watchdogStaleness = lastWatchdogWake > 0 && now > lastWatchdogWake ? now - lastWatchdogWake : 0;
            if (watchdogStaleness > maxWatchdogGap) maxWatchdogGap = watchdogStaleness;
            if (previous == 0 || now <= previous) return;
            var gap = now - previous;
            if (gap < _thresholdTicks) return;

            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var gc2 = GC.CollectionCount(2);
            var gc0Delta = gc0 - ProbeEvent.LastFrameGc0;
            var gc1Delta = gc1 - ProbeEvent.LastFrameGc1;
            var gc2Delta = gc2 - ProbeEvent.LastFrameGc2;
            ProbeEvent.LastFrameGc0 = gc0;
            ProbeEvent.LastFrameGc1 = gc1;
            ProbeEvent.LastFrameGc2 = gc2;
            var classification = ClassifyGap(gap, maxUpdateGap, maxWatchdogGap, gatedUpdates, gc0Delta, gc1Delta, gc2Delta);
            var detail = "classification=" + classification +
                         ";updatesSincePriorRunFrame=" + updates +
                         ";gatedUpdatesSincePriorRunFrame=" + gatedUpdates +
                         ";maxMasterUpdateGapMs=" + TicksToMilliseconds(maxUpdateGap).ToString("0.###", CultureInfo.InvariantCulture) +
                         ";maxWatchdogGapMs=" + TicksToMilliseconds(maxWatchdogGap).ToString("0.###", CultureInfo.InvariantCulture);
            var item = CreateEvent(
                "runframe-start-gap", gap, 0,
                Volatile.Read(ref _lastPaused) > 0,
                Volatile.Read(ref _lastEmuState), detail, null);
            item.Gc0Delta = gc0Delta;
            item.Gc1Delta = gc1Delta;
            item.Gc2Delta = gc2Delta;
            item.UpdatesSinceFrame = updates;
            item.GatedUpdatesSinceFrame = gatedUpdates;
            item.MaxUpdateGapMs = TicksToMilliseconds(maxUpdateGap);
            item.MaxWatchdogGapMs = TicksToMilliseconds(maxWatchdogGap);
            item.Classification = classification;
            Enqueue(item);
        }

        internal static void ControlTransition(string method, bool before, bool after)
        {
            if (!_running) return;
            var stack = _includeRareCallStacks ? Environment.StackTrace : null;
            Enqueue(CreateEvent(
                "pause-control-transition", 0, 0, after,
                Volatile.Read(ref _lastEmuState),
                method + ":" + before.ToString().ToLowerInvariant() + "->" + after.ToString().ToLowerInvariant(),
                stack));
        }

        internal static void Marker(string name)
        {
            if (!_running) return;
            Enqueue(CreateEvent(
                "control-marker", 0, 0,
                Volatile.Read(ref _lastPaused) > 0,
                Volatile.Read(ref _lastEmuState), name, null));
        }

        internal static void FocusChanged(bool focused)
        {
            if (!_running) return;
            Volatile.Write(ref _focused, focused ? 1 : 0);
            Enqueue(CreateEvent(
                "application-focus", 0, 0,
                Volatile.Read(ref _lastPaused) > 0,
                Volatile.Read(ref _lastEmuState), focused ? "focused" : "unfocused", null));
        }

        internal static void ApplicationPauseChanged(bool paused)
        {
            if (!_running) return;
            Enqueue(CreateEvent(
                "application-pause", 0, 0,
                Volatile.Read(ref _lastPaused) > 0,
                Volatile.Read(ref _lastEmuState), paused ? "application-paused" : "application-resumed", null));
        }

        private static void BeginGate(long now, long updateSerial)
        {
            Interlocked.Exchange(ref _gateStart, now);
            Interlocked.Exchange(ref _gateStartUpdate, updateSerial);
            _gateGc0 = GC.CollectionCount(0);
            _gateGc1 = GC.CollectionCount(1);
            _gateGc2 = GC.CollectionCount(2);
        }

        private static void EndGate(long now, long updateSerial, bool paused, int emuState)
        {
            var start = Interlocked.Exchange(ref _gateStart, 0);
            var firstUpdate = Interlocked.Exchange(ref _gateStartUpdate, 0);
            var item = CreateEvent(
                "emulation-gate-interval", start > 0 && now > start ? now - start : 0, 0,
                paused, emuState,
                "gatedMasterUpdates=" + Math.Max(0, updateSerial - firstUpdate), null);
            item.Gc0Delta = GC.CollectionCount(0) - _gateGc0;
            item.Gc1Delta = GC.CollectionCount(1) - _gateGc1;
            item.Gc2Delta = GC.CollectionCount(2) - _gateGc2;
            Enqueue(item);
        }

        internal static string ClassifyGap(
            long runFrameGapTicks,
            long maxMasterUpdateGapTicks,
            long maxWatchdogGapTicks,
            long gatedUpdates,
            int gc0Delta,
            int gc1Delta,
            int gc2Delta)
        {
            if (gatedUpdates > 0) return "emulation-gated";
            if (maxMasterUpdateGapTicks < _thresholdTicks) return "scheduler-no-frame-with-updates";
            var watchdogComparable = maxWatchdogGapTicks >= maxMasterUpdateGapTicks * 3 / 5;
            if (!watchdogComparable) return "unity-main-thread-stall";
            if (gc0Delta > 0 || gc1Delta > 0 || gc2Delta > 0) return "runtime-wide-pause-with-gc";
            return "runtime-wide-pause-or-process-deschedule";
        }

        private static ProbeEvent CreateEvent(
            string kind,
            long gapTicks,
            long durationTicks,
            bool paused,
            int emuState,
            string detail,
            string stack)
        {
            return new ProbeEvent
            {
                Utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Kind = kind,
                GapMs = TicksToMilliseconds(gapTicks),
                DurationMs = TicksToMilliseconds(durationTicks),
                UpdateSerial = Interlocked.Read(ref _updateSerial),
                FrameSerial = Interlocked.Read(ref _frameSerial),
                Executing = Volatile.Read(ref _lastExecuting) > 0,
                Paused = paused,
                EmuState = emuState,
                Focused = Volatile.Read(ref _focused) != 0,
                RunInBackground = Volatile.Read(ref _runInBackground) != 0,
                Gc0 = GC.CollectionCount(0),
                Gc1 = GC.CollectionCount(1),
                Gc2 = GC.CollectionCount(2),
                ManagedBytes = GC.GetTotalMemory(false),
                Detail = detail,
                Stack = stack
            };
        }

        private static void Enqueue(ProbeEvent item)
        {
            if (!_running || item == null) return;
            if (Interlocked.Increment(ref _queuedCount) > 1024)
            {
                Interlocked.Decrement(ref _queuedCount);
                Interlocked.Increment(ref _droppedEvents);
                return;
            }
            Queue.Enqueue(item);
            Wake.Set();
        }

        private static void WriterLoop()
        {
            StreamWriter writer = null;
            Process process = null;
            try
            {
                writer = new StreamWriter(Path.Combine(_sessionDirectory, "events.jsonl"), false, new UTF8Encoding(false));
                process = Process.GetCurrentProcess();
                var lastWake = Stopwatch.GetTimestamp();
                var nextPeriodic = lastWake;
                while (!_stopping)
                {
                    Wake.WaitOne(_watchdogIntervalMs);
                    var now = Stopwatch.GetTimestamp();
                    var watchdogGap = now > lastWake ? now - lastWake : 0;
                    lastWake = now;
                    Interlocked.Exchange(ref _lastWatchdogWake, now);
                    var wrote = false;
                    UpdateMaximum(ref _maxWatchdogGapSinceFrame, watchdogGap);
                    if (watchdogGap >= _thresholdTicks)
                    {
                        var item = CreateEvent(
                            "watchdog-thread-gap", watchdogGap, 0,
                            Volatile.Read(ref _lastPaused) > 0,
                            Volatile.Read(ref _lastEmuState),
                            "The independent managed watchdog was not scheduled during this interval.", null);
                        WriteEvent(writer, process, item);
                        wrote = true;
                    }
                    if (now >= nextPeriodic)
                    {
                        nextPeriodic = now + (long)_periodicSampleSeconds * Frequency;
                        var item = CreateEvent(
                            "periodic", 0, 0,
                            Volatile.Read(ref _lastPaused) > 0,
                            Volatile.Read(ref _lastEmuState),
                            "queued=" + Volatile.Read(ref _queuedCount) + ";dropped=" + Interlocked.Read(ref _droppedEvents), null);
                        WriteEvent(writer, process, item);
                        wrote = true;
                    }
                    if (Drain(writer, process)) wrote = true;
                    if (wrote) writer.Flush();
                }
                Drain(writer, process);
                var stopped = CreateEvent(
                    "probe-stop", 0, 0,
                    Volatile.Read(ref _lastPaused) > 0,
                    Volatile.Read(ref _lastEmuState),
                    "dropped=" + Interlocked.Read(ref _droppedEvents), null);
                WriteEvent(writer, process, stopped);
                writer.Flush();
            }
            catch (Exception ex)
            {
                try { _logger?.LogError("Runtime pause probe writer stopped: " + ex); } catch { }
            }
            finally
            {
                try { writer?.Dispose(); } catch { }
                try { process?.Dispose(); } catch { }
            }
        }

        private static bool Drain(StreamWriter writer, Process process)
        {
            var wrote = false;
            while (Queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _queuedCount);
                WriteEvent(writer, process, item);
                wrote = true;
            }
            return wrote;
        }

        private static void WriteEvent(StreamWriter writer, Process process, ProbeEvent item)
        {
            try
            {
                process.Refresh();
                item.WorkingSetBytes = process.WorkingSet64;
                item.PrivateBytes = process.PrivateMemorySize64;
                item.ProcessCpuMs = process.TotalProcessorTime.TotalMilliseconds;
                item.ThreadCount = process.Threads.Count;
                item.HandleCount = process.HandleCount;
            }
            catch
            {
                // Timing and GC fields remain useful if a process counter is unavailable.
            }
            writer.WriteLine(item.ToJson());
        }

        private static long MillisecondsToTicks(double milliseconds)
        {
            return Math.Max(1L, (long)Math.Round(Frequency * milliseconds / 1000.0));
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks <= 0 ? 0 : ticks * 1000.0 / Frequency;
        }

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

    internal sealed class ProbeEvent
    {
        internal static int LastFrameGc0 = GC.CollectionCount(0);
        internal static int LastFrameGc1 = GC.CollectionCount(1);
        internal static int LastFrameGc2 = GC.CollectionCount(2);

        internal string Utc;
        internal string Kind;
        internal double GapMs;
        internal double DurationMs;
        internal long UpdateSerial;
        internal long FrameSerial;
        internal long UpdatesSinceFrame;
        internal long GatedUpdatesSinceFrame;
        internal double MaxUpdateGapMs;
        internal double MaxWatchdogGapMs;
        internal bool Executing;
        internal bool Paused;
        internal int EmuState;
        internal bool Focused;
        internal bool RunInBackground;
        internal int Gc0;
        internal int Gc1;
        internal int Gc2;
        internal int Gc0Delta;
        internal int Gc1Delta;
        internal int Gc2Delta;
        internal long ManagedBytes;
        internal long WorkingSetBytes;
        internal long PrivateBytes;
        internal double ProcessCpuMs;
        internal int ThreadCount;
        internal int HandleCount;
        internal string Classification;
        internal string Detail;
        internal string Stack;

        internal string ToJson()
        {
            return "{\"utc\":\"" + SuperZSNESRuntimePauseProbePlugin.Json(Utc) +
                   "\",\"kind\":\"" + SuperZSNESRuntimePauseProbePlugin.Json(Kind) +
                   "\",\"gapMs\":" + Number(GapMs) +
                   ",\"durationMs\":" + Number(DurationMs) +
                   ",\"updateSerial\":" + UpdateSerial +
                   ",\"frameSerial\":" + FrameSerial +
                   ",\"updatesSincePriorRunFrame\":" + UpdatesSinceFrame +
                   ",\"gatedUpdatesSincePriorRunFrame\":" + GatedUpdatesSinceFrame +
                   ",\"maxMasterUpdateGapMs\":" + Number(MaxUpdateGapMs) +
                   ",\"maxWatchdogGapMs\":" + Number(MaxWatchdogGapMs) +
                   ",\"executing\":" + Bool(Executing) +
                   ",\"paused\":" + Bool(Paused) +
                   ",\"emuState\":" + EmuState +
                   ",\"focused\":" + Bool(Focused) +
                   ",\"runInBackground\":" + Bool(RunInBackground) +
                   ",\"gc\":{\"gen0\":" + Gc0 + ",\"gen1\":" + Gc1 + ",\"gen2\":" + Gc2 +
                   ",\"delta0\":" + Gc0Delta + ",\"delta1\":" + Gc1Delta + ",\"delta2\":" + Gc2Delta + "}" +
                   ",\"managedBytes\":" + ManagedBytes +
                   ",\"workingSetBytes\":" + WorkingSetBytes +
                   ",\"privateBytes\":" + PrivateBytes +
                   ",\"processCpuMs\":" + Number(ProcessCpuMs) +
                   ",\"threadCount\":" + ThreadCount +
                   ",\"handleCount\":" + HandleCount +
                   ",\"classification\":" + TextOrNull(Classification) +
                   ",\"detail\":" + TextOrNull(Detail) +
                   ",\"stack\":" + TextOrNull(Stack) + "}";
        }

        private static string Bool(bool value) => value ? "true" : "false";
        private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string TextOrNull(string value) => value == null ? "null" : "\"" + SuperZSNESRuntimePauseProbePlugin.Json(value) + "\"";
    }
}
