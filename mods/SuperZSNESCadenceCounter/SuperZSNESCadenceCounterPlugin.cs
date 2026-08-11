using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESCadenceCounter
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESCadenceCounterPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.cadencecounter";
        public const string PluginName = "SuperZSNES Cadence Counter";
        public const string PluginVersion = "0.1.0";

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<float> _windowSeconds;
        private ConfigEntry<bool> _rendererBreakdown;
        private ConfigEntry<bool> _logWindows;
        private Harmony _harmony;
        private StreamWriter _writer;
        private string _sessionDirectory;
        private float _nextFlush;

        private void Awake()
        {
            _enabled = Config.Bind("Counter", "Enabled", false,
                "Enable the main-thread-only Update/RunFrame cadence counter. False installs no Harmony patches.");
            _windowSeconds = Config.Bind("Counter", "WindowSeconds", 5f,
                new ConfigDescription("Aggregation interval in seconds.", new AcceptableValueRange<float>(1f, 60f)));
            _rendererBreakdown = Config.Bind("Counter", "RendererBreakdown", false,
                "Also time PPURenderer.GenerateBackgrounds and each GenerateBackground layer. This adds Stopwatch calls but no atomics.");
            _logWindows = Config.Bind("Counter", "LogWindows", false,
                "Mirror each low-frequency JSON window to the BepInEx log.");

            if (!_enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion + " is disabled; no Harmony patches were applied.");
                return;
            }

            var master = AccessTools.TypeByName("MasterExecutor");
            var renderer = AccessTools.TypeByName("PPURenderer");
            var update = master == null ? null : AccessTools.Method(master, "Update", Type.EmptyTypes);
            var runFrame = master == null ? null : AccessTools.Method(master, "RunFrame", Type.EmptyTypes);
            var generateAll = renderer == null ? null : AccessTools.Method(renderer, "GenerateBackgrounds", Type.EmptyTypes);
            var generateLayer = renderer == null
                ? null
                : renderer.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SingleOrDefault(method => method.Name == "GenerateBackground" && method.GetParameters().Length == 2);
            if (update == null || runFrame == null)
                throw new MissingMethodException("SuperZSNES v0.230 MasterExecutor cadence targets were not found.");
            if (_rendererBreakdown.Value && (generateAll == null || generateLayer == null))
                throw new MissingMethodException("SuperZSNES v0.230 PPURenderer timing targets were not found.");

            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(update,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CounterHooks), nameof(CounterHooks.UpdatePrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CounterHooks), nameof(CounterHooks.UpdatePostfix))));
            _harmony.Patch(runFrame,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CounterHooks), nameof(CounterHooks.RunFramePostfix))));
            if (_rendererBreakdown.Value)
            {
                _harmony.Patch(generateAll,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CounterHooks), nameof(CounterHooks.RendererPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CounterHooks), nameof(CounterHooks.RendererPostfix))));
                _harmony.Patch(generateLayer,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CounterHooks), nameof(CounterHooks.LayerPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CounterHooks), nameof(CounterHooks.LayerPostfix))));
            }

            _sessionDirectory = Path.Combine(Paths.PluginPath, "SuperZSNESCadenceCounter",
                "session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(_sessionDirectory);
            _writer = new StreamWriter(Path.Combine(_sessionDirectory, "windows.jsonl"), false);
            CounterRuntime.Reset(Stopwatch.GetTimestamp());
            _nextFlush = Time.unscaledTime + _windowSeconds.Value;
            WriteStatus("collecting", null);
            Logger.LogInfo(PluginName + " enabled; renderer breakdown=" + _rendererBreakdown.Value +
                           ". Output: " + _sessionDirectory);
        }

        private void Update()
        {
            if (_writer == null || Time.unscaledTime < _nextFlush) return;
            Flush("interval");
            _nextFlush = Time.unscaledTime + _windowSeconds.Value;
        }

        private void Flush(string reason)
        {
            if (_writer == null) return;
            var json = CounterRuntime.SnapshotJson(reason, Stopwatch.GetTimestamp(),
                QualitySettings.vSyncCount, Application.targetFrameRate);
            _writer.WriteLine(json);
            _writer.Flush();
            if (_logWindows.Value) Logger.LogInfo(json);
            WriteStatus("collecting", json);
        }

        private void OnDestroy()
        {
            try { if (_writer != null) Flush("shutdown"); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            try { WriteStatus("shutdown", null); } catch { }
        }

        private void WriteStatus(string state, string latest)
        {
            if (string.IsNullOrEmpty(_sessionDirectory)) return;
            var json = "{\"pluginVersion\":\"" + PluginVersion + "\",\"state\":\"" + state +
                       "\",\"rendererBreakdown\":" + (_rendererBreakdown.Value ? "true" : "false") +
                       ",\"latest\":" + (latest ?? "null") + "}";
            File.WriteAllText(Path.Combine(_sessionDirectory, "status.json"), json);
        }
    }

    internal static class CounterHooks
    {
        public static void UpdatePrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            CounterRuntime.UpdateStarted(__state);
        }

        public static void UpdatePostfix(long __state)
        {
            CounterRuntime.UpdateFinished(__state, Stopwatch.GetTimestamp());
        }

        public static void RunFramePostfix()
        {
            CounterRuntime.RunFrameFinished();
        }

        public static void RendererPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void RendererPostfix(long __state)
        {
            CounterRuntime.RendererFinished(Stopwatch.GetTimestamp() - __state);
        }

        public static void LayerPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void LayerPostfix(int __0, long __state)
        {
            CounterRuntime.LayerFinished(__0, Stopwatch.GetTimestamp() - __state);
        }
    }

    // All hooks and the BepInEx Update/flush run on Unity's main thread. These are
    // ordinary field operations by design: no Interlocked/Monitor/ConcurrentQueue
    // traffic is added to RunFrame, MasterExecutor.Update, or the audio thread.
    internal static class CounterRuntime
    {
        private static readonly long[] FramesPerUpdate = new long[6];
        private static readonly long[] LayerCount = new long[4];
        private static readonly long[] LayerTicks = new long[4];
        private static readonly long[] LayerMaxTicks = new long[4];

        private static long _windowStart;
        private static long _lastUpdateStart;
        private static long _updateCount;
        private static long _updateTicks;
        private static long _updateMaxTicks;
        private static long _cadenceCount;
        private static long _cadenceTicks;
        private static long _cadenceMaxTicks;
        private static long _runFrameCount;
        private static long _orphanRunFrames;
        private static int _framesThisUpdate;
        private static bool _insideUpdate;
        private static long _rendererCount;
        private static long _rendererTicks;
        private static long _rendererMaxTicks;

        internal static void Reset(long now)
        {
            Array.Clear(FramesPerUpdate, 0, FramesPerUpdate.Length);
            Array.Clear(LayerCount, 0, LayerCount.Length);
            Array.Clear(LayerTicks, 0, LayerTicks.Length);
            Array.Clear(LayerMaxTicks, 0, LayerMaxTicks.Length);
            _windowStart = now;
            _lastUpdateStart = 0;
            _updateCount = _updateTicks = _updateMaxTicks = 0;
            _cadenceCount = _cadenceTicks = _cadenceMaxTicks = 0;
            _runFrameCount = _orphanRunFrames = 0;
            _framesThisUpdate = 0;
            _insideUpdate = false;
            _rendererCount = _rendererTicks = _rendererMaxTicks = 0;
        }

        internal static void UpdateStarted(long now)
        {
            if (_lastUpdateStart != 0)
            {
                var delta = now - _lastUpdateStart;
                _cadenceCount++;
                _cadenceTicks += delta;
                if (delta > _cadenceMaxTicks) _cadenceMaxTicks = delta;
            }
            _lastUpdateStart = now;
            _framesThisUpdate = 0;
            _insideUpdate = true;
        }

        internal static void RunFrameFinished()
        {
            _runFrameCount++;
            if (_insideUpdate) _framesThisUpdate++;
            else _orphanRunFrames++;
        }

        internal static void UpdateFinished(long start, long end)
        {
            var elapsed = end - start;
            _updateCount++;
            _updateTicks += elapsed;
            if (elapsed > _updateMaxTicks) _updateMaxTicks = elapsed;
            FramesPerUpdate[Math.Min(Math.Max(_framesThisUpdate, 0), 5)]++;
            _insideUpdate = false;
        }

        internal static void RendererFinished(long elapsed)
        {
            _rendererCount++;
            _rendererTicks += elapsed;
            if (elapsed > _rendererMaxTicks) _rendererMaxTicks = elapsed;
        }

        internal static void LayerFinished(int layer, long elapsed)
        {
            if ((uint)layer >= 4u) return;
            LayerCount[layer]++;
            LayerTicks[layer] += elapsed;
            if (elapsed > LayerMaxTicks[layer]) LayerMaxTicks[layer] = elapsed;
        }

        internal static string SnapshotJson(string reason, long now, int vSync, int targetFrameRate)
        {
            var seconds = Math.Max(0.000001, (double)(now - _windowStart) / Stopwatch.Frequency);
            var json = "{\"utc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                       "\",\"reason\":\"" + reason + "\",\"windowSeconds\":" + N(seconds) +
                       ",\"unity\":{\"vSyncCount\":" + vSync + ",\"targetFrameRate\":" + targetFrameRate + "}" +
                       ",\"updates\":{\"count\":" + _updateCount + ",\"hz\":" + N(_updateCount / seconds) +
                       ",\"avgMs\":" + N(Milliseconds(_updateTicks, _updateCount)) +
                       ",\"maxMs\":" + N(Milliseconds(_updateMaxTicks, 1)) +
                       ",\"cadenceAvgMs\":" + N(Milliseconds(_cadenceTicks, _cadenceCount)) +
                       ",\"cadenceMaxMs\":" + N(Milliseconds(_cadenceMaxTicks, 1)) + "}" +
                       ",\"runFrames\":{\"count\":" + _runFrameCount + ",\"hz\":" + N(_runFrameCount / seconds) +
                       ",\"orphan\":" + _orphanRunFrames + ",\"perUpdate\":{\"0\":" + FramesPerUpdate[0] +
                       ",\"1\":" + FramesPerUpdate[1] + ",\"2\":" + FramesPerUpdate[2] +
                       ",\"3\":" + FramesPerUpdate[3] + ",\"4\":" + FramesPerUpdate[4] +
                       ",\"5Plus\":" + FramesPerUpdate[5] + "}}" +
                       ",\"renderer\":{\"count\":" + _rendererCount +
                       ",\"avgMs\":" + N(Milliseconds(_rendererTicks, _rendererCount)) +
                       ",\"maxMs\":" + N(Milliseconds(_rendererMaxTicks, 1)) +
                       ",\"layers\":[" + LayerJson(0) + "," + LayerJson(1) + "," + LayerJson(2) + "," + LayerJson(3) + "]}}";
            Reset(now);
            return json;
        }

        private static string LayerJson(int layer)
        {
            return "{\"layer\":" + (layer + 1) + ",\"count\":" + LayerCount[layer] +
                   ",\"avgMs\":" + N(Milliseconds(LayerTicks[layer], LayerCount[layer])) +
                   ",\"maxMs\":" + N(Milliseconds(LayerMaxTicks[layer], 1)) + "}";
        }

        private static double Milliseconds(long ticks, long count)
        {
            return count == 0 ? 0 : (double)ticks * 1000.0 / Stopwatch.Frequency / count;
        }

        private static string N(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
