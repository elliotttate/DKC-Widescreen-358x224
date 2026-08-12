using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESPerformanceSuiteIL2CPP
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class PerformanceSuitePlugin : BasePlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.performance.il2cpp";
        public const string PluginName = "SuperZSNES Performance Suite IL2CPP";
        public const string PluginVersion = "0.1.1";

        private Harmony _harmony;

        public override void Load()
        {
            ConfigEntry<bool> enabled = Config.Bind("Diagnostics", "Enabled", false,
                "Enable timing hooks and any selected optimizations.");
            ConfigEntry<int> statusEvery = Config.Bind("Diagnostics", "StatusEveryUpdates", 120,
                "Write status.json after this many Unity Update calls.");
            ConfigEntry<bool> recoverBacklog = Config.Bind("Optimizations", "RecoverDroppedBacklog", false,
                "Retain normal-speed frames above the stock five-frame batch cap.");
            ConfigEntry<int> maxBacklog = Config.Bind("Optimizations", "EmergencyMaxBacklogFrames", 120,
                "Maximum retained normal-speed backlog; zero is unbounded.");
            ConfigEntry<bool> disableHistory = Config.Bind("Optimizations", "DisableHistoryCapture", false,
                "Temporarily disable the synchronous 20-second history screenshot during each Update.");
            ConfigEntry<bool> disableRewind = Config.Bind("Optimizations", "DisableRewindCapture", false,
                "Temporarily disable rewind-state capture during each Update.");
            ConfigEntry<bool> gateAtlas = Config.Bind("Optimizations", "GateAtlasUploadsOnTileDirty", false,
                "Suppress stock atlas-page uploads unless at least one decoded tile on the page changed.");
            ConfigEntry<int> injectAfter = Config.Bind("Diagnostics", "InjectStallAfterUpdates", 0,
                "Test-only: inject one stall after this many Update calls; zero disables it.");
            ConfigEntry<int> injectMilliseconds = Config.Bind("Diagnostics", "InjectStallMilliseconds", 0,
                "Test-only stall duration. Leave zero outside a controlled scheduler test.");
            Config.Save();

            if (!enabled.Value)
            {
                Log.LogInfo(PluginName + " disabled; no Harmony patches applied.");
                return;
            }

            string directory = Path.Combine(Paths.PluginPath, "SuperZSNESPerformanceSuiteIL2CPP");
            Directory.CreateDirectory(directory);
            PerformanceState.Initialize(Log, Path.Combine(directory, "status.json"),
                Math.Max(30, statusEvery.Value), recoverBacklog.Value,
                Math.Max(0, maxBacklog.Value), disableHistory.Value, disableRewind.Value,
                gateAtlas.Value, Math.Max(0, injectAfter.Value), Math.Max(0, injectMilliseconds.Value));

            try
            {
                _harmony = new Harmony(PluginGuid);
                PatchRequired(typeof(MasterExecutor), "Update", Type.EmptyTypes,
                    nameof(PerformanceHooks.UpdatePrefix), nameof(PerformanceHooks.UpdatePostfix));
                PatchRequired(typeof(MasterExecutor), "RunFrame", Type.EmptyTypes,
                    nameof(PerformanceHooks.RunFramePrefix), nameof(PerformanceHooks.RunFramePostfix));
                PatchRequired(typeof(PPURenderer), "GenerateBackgrounds", Type.EmptyTypes,
                    nameof(PerformanceHooks.GenerateBackgroundsPrefix), nameof(PerformanceHooks.GenerateBackgroundsPostfix));

                if (gateAtlas.Value)
                {
                    PatchRequired(typeof(TileTextureGen), "StartFrame", new[] { typeof(SNESPPU) },
                        nameof(AtlasUploadGate.StartFramePrefix), null);
                    PatchRequired(typeof(TileTextureGen), "Get2bppTexture", new[] { typeof(int) },
                        nameof(AtlasUploadGate.Get2Prefix), nameof(AtlasUploadGate.Get2Postfix));
                    PatchRequired(typeof(TileTextureGen), "Get4bppTexture", new[] { typeof(int) },
                        nameof(AtlasUploadGate.Get4Prefix), nameof(AtlasUploadGate.Get4Postfix));
                    PatchRequired(typeof(TileTextureGen), "Get8bppTexture", new[] { typeof(int) },
                        nameof(AtlasUploadGate.Get8Prefix), nameof(AtlasUploadGate.Get8Postfix));
                }

                PerformanceState.WriteStatus("active");
                Log.LogWarning(PluginName + " active. backlog=" + recoverBacklog.Value +
                               ", history=" + disableHistory.Value + ", rewind=" + disableRewind.Value +
                               ", atlas=" + gateAtlas.Value);
            }
            catch (Exception exception)
            {
                try { _harmony?.UnpatchSelf(); } catch { }
                PerformanceState.WriteFailure(exception);
                Log.LogError("Performance suite failed closed: " + exception);
            }
        }

        public override bool Unload()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            PerformanceState.WriteStatus("unloaded");
            return true;
        }

        private void PatchRequired(Type type, string methodName, Type[] parameters,
            string prefixName, string postfixName)
        {
            MethodInfo target = AccessTools.Method(type, methodName, parameters);
            if (target == null) throw new MissingMethodException(type.FullName, methodName);
            Type hookType = type == typeof(TileTextureGen) ? typeof(AtlasUploadGate) : typeof(PerformanceHooks);
            HarmonyMethod prefix = prefixName == null ? null :
                new HarmonyMethod(hookType, prefixName) { priority = Priority.First };
            HarmonyMethod postfix = postfixName == null ? null :
                new HarmonyMethod(hookType, postfixName) { priority = Priority.Last };
            _harmony.Patch(target, prefix, postfix);
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null || (!string.IsNullOrEmpty(prefixName) &&
                !info.Prefixes.Any(p => p.owner == PluginGuid)) ||
                (!string.IsNullOrEmpty(postfixName) &&
                !info.Postfixes.Any(p => p.owner == PluginGuid)))
                throw new InvalidOperationException("Harmony did not retain patch for " + type.Name + "." + methodName);
        }
    }

    internal static class PerformanceHooks
    {
        internal static void UpdatePrefix(MasterExecutor __instance)
        {
            PerformanceState.BeginUpdate(__instance);
        }

        internal static void UpdatePostfix(MasterExecutor __instance)
        {
            PerformanceState.EndUpdate(__instance);
        }

        internal static void RunFramePrefix()
        {
            PerformanceState.BeginRunFrame();
        }

        internal static void RunFramePostfix()
        {
            PerformanceState.EndRunFrame();
        }

        internal static void GenerateBackgroundsPrefix()
        {
            PerformanceState.BeginPresentation();
        }

        internal static void GenerateBackgroundsPostfix()
        {
            PerformanceState.EndPresentation();
        }
    }

    internal static class PerformanceState
    {
        private static readonly long Frequency = Stopwatch.Frequency;
        private static ManualLogSource _log;
        private static string _statusPath;
        private static int _statusEvery;
        private static bool _recoverBacklog;
        private static int _maxBacklogFrames;
        private static bool _disableHistory;
        private static bool _disableRewind;
        private static bool _gateAtlas;
        private static int _injectAfterUpdates;
        private static int _injectMilliseconds;
        private static string _stallRequestPath;
        private static long _updateStart;
        private static long _runFrameStart;
        private static long _presentationStart;
        private static long _lastUpdateStart;
        private static float _prefixAccumulated;
        private static float _prefixDelta;
        private static int _runsThisUpdate;
        private static bool _insideUpdate;
        private static MainMenuManager.MainMenuSettings _guardedSettings;
        private static bool _savedHistoryDisabled;
        private static bool _savedRewindDisabled;

        internal static long Updates;
        internal static long RunFrames;
        internal static long Presentations;
        internal static double UpdateMilliseconds;
        internal static double RunFrameMilliseconds;
        internal static double PresentationMilliseconds;
        internal static double MaxUpdateMilliseconds;
        internal static double MaxRunFrameMilliseconds;
        internal static double MaxPresentationMilliseconds;
        internal static double MaxUpdateGapMilliseconds;
        internal static readonly long[] RunHistogram = new long[7];
        internal static long BacklogRecoveries;
        // A retained frame can be charged again while the bounded backlog drains;
        // this is therefore a cumulative scheduler charge, not a unique-frame count.
        internal static long RetainedBacklogFrameCharges;
        internal static long GuardedUpdates;
        internal static long InjectedStalls;
        internal static long Errors;

        internal static void Initialize(ManualLogSource log, string statusPath, int statusEvery,
            bool recoverBacklog, int maxBacklogFrames, bool disableHistory, bool disableRewind,
            bool gateAtlas, int injectAfterUpdates, int injectMilliseconds)
        {
            _log = log;
            _statusPath = statusPath;
            _statusEvery = statusEvery;
            _recoverBacklog = recoverBacklog;
            _maxBacklogFrames = maxBacklogFrames;
            _disableHistory = disableHistory;
            _disableRewind = disableRewind;
            _gateAtlas = gateAtlas;
            _injectAfterUpdates = injectAfterUpdates;
            _injectMilliseconds = injectMilliseconds;
            _stallRequestPath = Path.Combine(Path.GetDirectoryName(statusPath) ?? string.Empty, "stall.request");
        }

        internal static void BeginUpdate(MasterExecutor executor)
        {
            bool scheduled = _injectAfterUpdates > 0 && Updates >= _injectAfterUpdates;
            bool requested = _injectAfterUpdates == 0 && _injectMilliseconds > 0 &&
                !string.IsNullOrEmpty(_stallRequestPath) && File.Exists(_stallRequestPath);
            if (_injectMilliseconds > 0 && InjectedStalls == 0 && (scheduled || requested))
            {
                if (requested)
                {
                    try { File.Delete(_stallRequestPath); } catch { }
                }
                InjectedStalls++;
                Thread.Sleep(_injectMilliseconds);
            }
            long now = Stopwatch.GetTimestamp();
            if (_lastUpdateStart != 0)
                MaxUpdateGapMilliseconds = Math.Max(MaxUpdateGapMilliseconds, Milliseconds(now - _lastUpdateStart));
            _lastUpdateStart = now;
            _updateStart = now;
            _runsThisUpdate = 0;
            _insideUpdate = true;
            _prefixAccumulated = executor?._accumulatedDT ?? 0f;
            _prefixDelta = Time.deltaTime;
            ApplyServiceGuard();
        }

        internal static void EndUpdate(MasterExecutor executor)
        {
            try
            {
                RecoverBacklog(executor);
            }
            catch (Exception exception)
            {
                Errors++;
                _log?.LogWarning("Backlog recovery skipped: " + exception.Message);
            }
            finally
            {
                RestoreServiceGuard();
                _insideUpdate = false;
            }

            double elapsed = Milliseconds(Stopwatch.GetTimestamp() - _updateStart);
            Updates++;
            UpdateMilliseconds += elapsed;
            MaxUpdateMilliseconds = Math.Max(MaxUpdateMilliseconds, elapsed);
            int bucket = Math.Min(Math.Max(_runsThisUpdate, 0), RunHistogram.Length - 1);
            RunHistogram[bucket]++;
            if ((Updates % _statusEvery) == 0) WriteStatus("active");
        }

        internal static void BeginRunFrame()
        {
            _runFrameStart = Stopwatch.GetTimestamp();
            if (_insideUpdate) _runsThisUpdate++;
        }

        internal static void EndRunFrame()
        {
            double elapsed = Milliseconds(Stopwatch.GetTimestamp() - _runFrameStart);
            RunFrames++;
            RunFrameMilliseconds += elapsed;
            MaxRunFrameMilliseconds = Math.Max(MaxRunFrameMilliseconds, elapsed);
        }

        internal static void BeginPresentation()
        {
            _presentationStart = Stopwatch.GetTimestamp();
        }

        internal static void EndPresentation()
        {
            double elapsed = Milliseconds(Stopwatch.GetTimestamp() - _presentationStart);
            Presentations++;
            PresentationMilliseconds += elapsed;
            MaxPresentationMilliseconds = Math.Max(MaxPresentationMilliseconds, elapsed);
        }

        private static void ApplyServiceGuard()
        {
            if (!_disableHistory && !_disableRewind) return;
            MainMenuManager manager = MainMenuManager.Instance;
            MainMenuManager.MainMenuSettings settings = manager?.mainMenuSettings;
            if (settings == null) return;
            _guardedSettings = settings;
            _savedHistoryDisabled = settings.historyDisabled;
            _savedRewindDisabled = settings.rewindDisabled;
            if (_disableHistory) settings.historyDisabled = true;
            if (_disableRewind) settings.rewindDisabled = true;
            GuardedUpdates++;
        }

        private static void RestoreServiceGuard()
        {
            MainMenuManager.MainMenuSettings settings = _guardedSettings;
            _guardedSettings = null;
            if (settings == null) return;
            if (_disableHistory) settings.historyDisabled = _savedHistoryDisabled;
            if (_disableRewind) settings.rewindDisabled = _savedRewindDisabled;
        }

        private static void RecoverBacklog(MasterExecutor executor)
        {
            if (!_recoverBacklog || executor == null || _runsThisUpdate != 5) return;
            float hz = executor.IsNTSC ? 60f : 50f;
            float period = 1f / hz;
            double elapsedAtEntry = _prefixAccumulated + _prefixDelta;
            int due = elapsedAtEntry > 0 ? (int)(elapsedAtEntry / period) : 0;
            if (due <= 5) return;

            int missing = due - 5;
            float corrected = executor._accumulatedDT + missing * period;
            if (_maxBacklogFrames > 0)
                corrected = Math.Min(corrected, _maxBacklogFrames * period);
            executor._accumulatedDT = Math.Max(0f, corrected);
            BacklogRecoveries++;
            RetainedBacklogFrameCharges += missing;
        }

        internal static void WriteFailure(Exception exception)
        {
            Errors++;
            WriteStatus("failed-closed", exception.ToString());
        }

        internal static void WriteStatus(string state, string error = "")
        {
            if (string.IsNullOrEmpty(_statusPath)) return;
            try
            {
                Process process = Process.GetCurrentProcess();
                double updateDivisor = Updates == 0 ? 1 : Updates;
                double frameDivisor = RunFrames == 0 ? 1 : RunFrames;
                double presentationDivisor = Presentations == 0 ? 1 : Presentations;
                StringBuilder json = new StringBuilder(1200);
                json.Append('{');
                Append(json, "version", PerformanceSuitePlugin.PluginVersion).Append(',');
                Append(json, "state", state).Append(',');
                Append(json, "recoverDroppedBacklog", _recoverBacklog).Append(',');
                Append(json, "disableHistoryCapture", _disableHistory).Append(',');
                Append(json, "disableRewindCapture", _disableRewind).Append(',');
                Append(json, "gateAtlasUploads", _gateAtlas).Append(',');
                Append(json, "updates", Updates).Append(',');
                Append(json, "runFrames", RunFrames).Append(',');
                Append(json, "presentations", Presentations).Append(',');
                Append(json, "averageUpdateMs", UpdateMilliseconds / updateDivisor).Append(',');
                Append(json, "averageRunFrameMs", RunFrameMilliseconds / frameDivisor).Append(',');
                Append(json, "averagePresentationMs", PresentationMilliseconds / presentationDivisor).Append(',');
                Append(json, "maxUpdateMs", MaxUpdateMilliseconds).Append(',');
                Append(json, "maxRunFrameMs", MaxRunFrameMilliseconds).Append(',');
                Append(json, "maxPresentationMs", MaxPresentationMilliseconds).Append(',');
                Append(json, "maxUpdateGapMs", MaxUpdateGapMilliseconds).Append(',');
                json.Append("\"runFramesPerUpdate\":[");
                for (int i = 0; i < RunHistogram.Length; i++)
                {
                    if (i != 0) json.Append(',');
                    json.Append(RunHistogram[i]);
                }
                json.Append("],");
                Append(json, "backlogRecoveries", BacklogRecoveries).Append(',');
                Append(json, "retainedBacklogFrameCharges", RetainedBacklogFrameCharges).Append(',');
                Append(json, "guardedUpdates", GuardedUpdates).Append(',');
                Append(json, "injectedStalls", InjectedStalls).Append(',');
                Append(json, "atlasSuppressedPages", AtlasUploadGate.SuppressedPages).Append(',');
                Append(json, "atlasDirtyPages", AtlasUploadGate.DirtyPages).Append(',');
                Append(json, "sampleStopwatchTicks", Stopwatch.GetTimestamp()).Append(',');
                Append(json, "stopwatchFrequency", Frequency).Append(',');
                Append(json, "processCpuSeconds", process.TotalProcessorTime.TotalSeconds).Append(',');
                Append(json, "workingSetBytes", process.WorkingSet64).Append(',');
                Append(json, "privateBytes", process.PrivateMemorySize64).Append(',');
                Append(json, "qualityVSyncCount", QualitySettings.vSyncCount).Append(',');
                Append(json, "targetFrameRate", Application.targetFrameRate).Append(',');
                Append(json, "errors", Errors).Append(',');
                Append(json, "error", error);
                json.Append('}');
                string temporary = _statusPath + ".tmp";
                File.WriteAllText(temporary, json.ToString());
                File.Move(temporary, _statusPath, true);
            }
            catch (Exception exception)
            {
                Errors++;
                _log?.LogWarning("Could not write performance status: " + exception.Message);
            }
        }

        private static StringBuilder Append(StringBuilder json, string key, string value)
        {
            return json.Append('"').Append(Escape(key)).Append("\":\"").Append(Escape(value)).Append('"');
        }

        private static StringBuilder Append(StringBuilder json, string key, bool value)
        {
            return json.Append('"').Append(key).Append("\":").Append(value ? "true" : "false");
        }

        private static StringBuilder Append(StringBuilder json, string key, long value)
        {
            return json.Append('"').Append(key).Append("\":").Append(value);
        }

        private static StringBuilder Append(StringBuilder json, string key, int value)
        {
            return Append(json, key, (long)value);
        }

        private static StringBuilder Append(StringBuilder json, string key, double value)
        {
            return json.Append('"').Append(key).Append("\":")
                .Append(value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static double Milliseconds(long ticks)
        {
            return ticks * 1000.0 / Frequency;
        }
    }

    internal static class AtlasUploadGate
    {
        private static readonly bool[] Seen2 = new bool[16];
        private static readonly bool[] Seen4 = new bool[8];
        private static readonly bool[] Seen8 = new bool[4];
        internal static long SuppressedPages;
        internal static long DirtyPages;

        internal static void StartFramePrefix()
        {
            Array.Clear(Seen2, 0, Seen2.Length);
            Array.Clear(Seen4, 0, Seen4.Length);
            Array.Clear(Seen8, 0, Seen8.Length);
        }

        internal static void Get2Prefix(TileTextureGen __instance, int addr)
        {
            Observe(__instance, addr, 4, Seen2, 0);
        }

        internal static void Get2Postfix(TileTextureGen __instance, int addr)
        {
            Suppress(__instance, addr, 12, Seen2, 0);
        }

        internal static void Get4Prefix(TileTextureGen __instance, int addr)
        {
            Observe(__instance, addr, 5, Seen4, 1);
        }

        internal static void Get4Postfix(TileTextureGen __instance, int addr)
        {
            Suppress(__instance, addr, 13, Seen4, 1);
        }

        internal static void Get8Prefix(TileTextureGen __instance, int addr)
        {
            Observe(__instance, addr, 6, Seen8, 2);
        }

        internal static void Get8Postfix(TileTextureGen __instance, int addr)
        {
            Suppress(__instance, addr, 14, Seen8, 2);
        }

        private static void Observe(TileTextureGen generator, int addr, int tileShift,
            bool[] seen, int kind)
        {
            try
            {
                int tile = (addr & 0xFFFF) >> tileShift;
                int page = (addr & 0xFFFF) >> (tileShift + 8);
                bool dirty = kind == 0 ? generator.snesPPU._dirty2bpp[tile] != 0 :
                    kind == 1 ? generator.snesPPU._dirty4bpp[tile] != 0 :
                    generator.snesPPU._dirty8bpp[tile] != 0;
                if (dirty && !seen[page])
                {
                    seen[page] = true;
                    DirtyPages++;
                }
            }
            catch
            {
                PerformanceState.Errors++;
            }
        }

        private static void Suppress(TileTextureGen generator, int addr, int pageShift,
            bool[] seen, int kind)
        {
            try
            {
                int page = (addr & 0xFFFF) >> pageShift;
                if (seen[page]) return;
                bool wasSet;
                if (kind == 0)
                {
                    wasSet = generator.texture2bitDirty[page];
                    generator.texture2bitDirty[page] = false;
                }
                else if (kind == 1)
                {
                    wasSet = generator.texture4bitDirty[page];
                    generator.texture4bitDirty[page] = false;
                }
                else
                {
                    wasSet = generator.texture8bitDirty[page];
                    generator.texture8bitDirty[page] = false;
                }
                if (wasSet) SuppressedPages++;
            }
            catch
            {
                PerformanceState.Errors++;
            }
        }
    }
}
