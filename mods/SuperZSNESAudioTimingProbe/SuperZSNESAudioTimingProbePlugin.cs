using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESAudioTimingProbe
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESAudioTimingProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.audiotimingprobe";
        public const string PluginName = "SuperZSNES Audio Timing Probe";
        public const string PluginVersion = "0.2.0";

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<float> _windowSeconds;
        private ConfigEntry<KeyCode> _pauseKey;
        private ConfigEntry<KeyCode> _snapshotKey;
        private Harmony _harmony;
        private bool _armed;
        private bool _collecting;
        private float _nextFlush;
        private string _rootDirectory;
        private string _sessionDirectory;
        private StreamWriter _jsonWriter;
        private StreamWriter _csvWriter;

        private void Awake()
        {
            _enabled = Config.Bind(
                "Probe", "Enabled", false,
                "Arm and start the measurement probe at process startup. Enabling requires an emulator restart; false installs no Harmony patches.");
            _windowSeconds = Config.Bind(
                "Probe", "WindowSeconds", 5f,
                "Low-frequency aggregation/output interval in seconds (clamped to 1-60).");
            _pauseKey = Config.Bind(
                "Controls", "PauseResumeKey", KeyCode.F10,
                "Pause/resume collection after the probe was armed at startup. Does not patch an unarmed process.");
            _snapshotKey = Config.Bind(
                "Controls", "SnapshotKey", KeyCode.F11,
                "Flush the current partial aggregation window immediately.");

            _rootDirectory = Path.Combine(Paths.BepInExRootPath, "AudioTimingProbe");
            Directory.CreateDirectory(_rootDirectory);
            WriteStatus("loaded-disabled", null);

            if (!_enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion +
                               " loaded disabled. Set Probe.Enabled=true and restart to arm it; no target methods were patched.");
                return;
            }

            try
            {
                Arm();
                SetCollecting(true);
            }
            catch (Exception ex)
            {
                ProbeRuntime.SetCollecting(false);
                try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
                try { _jsonWriter?.Dispose(); } catch { }
                try { _csvWriter?.Dispose(); } catch { }
                _jsonWriter = null;
                _csvWriter = null;
                _armed = false;
                WriteStatus("arm-failed", ex.Message);
                Logger.LogError("Audio timing probe failed closed; no collection is active: " + ex);
            }
        }

        private void Arm()
        {
            var masterType = FindType("MasterExecutor");
            var dspType = FindType("DSPAudio");
            var masterUpdate = masterType == null ? null : AccessTools.Method(masterType, "Update", Type.EmptyTypes);
            var runFrame = masterType == null ? null : AccessTools.Method(masterType, "RunFrame", Type.EmptyTypes);
            var audioCallback = dspType == null
                ? null
                : AccessTools.Method(dspType, "OnAudioFilterRead", new[] { typeof(float[]), typeof(int) });
            var audioCycle = dspType == null
                ? null
                : AccessTools.Method(dspType, "AudioCycle", new[] { typeof(bool) });
            if (masterUpdate == null || runFrame == null || audioCallback == null || audioCycle == null)
                throw new MissingMethodException("Expected SuperZSNES v0.230 timing targets were not found.");

            AudioCycleLockTranspiler.TransformCount = 0;
            UpdateSchedulerTranspiler.TransformCount = 0;
            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(
                masterUpdate,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.MasterUpdatePrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.MasterUpdatePostfix))),
                transpiler: new HarmonyMethod(AccessTools.Method(
                    typeof(UpdateSchedulerTranspiler), nameof(UpdateSchedulerTranspiler.Transpiler))));
            _harmony.Patch(
                runFrame,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.RunFramePrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.RunFramePostfix))));
            _harmony.Patch(
                audioCallback,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.AudioCallbackPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.AudioCallbackPostfix))));
            _harmony.Patch(
                audioCycle,
                transpiler: new HarmonyMethod(AccessTools.Method(
                    typeof(AudioCycleLockTranspiler), nameof(AudioCycleLockTranspiler.Transpiler))));

            if (AudioCycleLockTranspiler.TransformCount != 4)
                throw new InvalidOperationException(
                    "AudioCycle lock instrumentation expected exactly four Monitor.Enter sites, got " +
                    AudioCycleLockTranspiler.TransformCount + ".");
            if (UpdateSchedulerTranspiler.TransformCount != 1)
                throw new InvalidOperationException(
                    "MasterExecutor.Update scheduler instrumentation expected exactly one decision site, got " +
                    UpdateSchedulerTranspiler.TransformCount + ".");

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            _sessionDirectory = Path.Combine(_rootDirectory, "session-" + stamp);
            Directory.CreateDirectory(_sessionDirectory);
            _jsonWriter = new StreamWriter(Path.Combine(_sessionDirectory, "windows.jsonl"), false);
            _csvWriter = new StreamWriter(Path.Combine(_sessionDirectory, "windows.csv"), false);
            _csvWriter.WriteLine(WindowSnapshot.CsvHeader);
            _jsonWriter.AutoFlush = true;
            _csvWriter.AutoFlush = true;
            ProbeRuntime.ResetAll();
            _armed = true;
            _nextFlush = Time.unscaledTime + ClampedWindowSeconds();
            WriteStatus("armed", null);
            Logger.LogInfo(PluginName + " armed. Output: " + _sessionDirectory);
        }

        private void Update()
        {
            if (!_armed) return;

            if (Input.GetKeyDown(_pauseKey.Value))
            {
                if (_collecting) Flush("pause");
                SetCollecting(!_collecting);
            }
            if (Input.GetKeyDown(_snapshotKey.Value)) Flush("manual");

            if (_collecting && Time.unscaledTime >= _nextFlush)
                Flush("interval");
        }

        private void SetCollecting(bool value)
        {
            _collecting = value;
            ProbeRuntime.SetCollecting(value);
            _nextFlush = Time.unscaledTime + ClampedWindowSeconds();
            WriteStatus(value ? "collecting" : "paused", null);
            Logger.LogInfo("Audio timing collection " + (value ? "started/resumed." : "paused."));
        }

        private void Flush(string reason)
        {
            if (!_armed || _jsonWriter == null || _csvWriter == null) return;
            var snapshot = ProbeRuntime.SnapshotAndReset(reason);
            if (!snapshot.HasData && reason == "interval")
            {
                _nextFlush = Time.unscaledTime + ClampedWindowSeconds();
                return;
            }
            _jsonWriter.WriteLine(snapshot.ToJson());
            _csvWriter.WriteLine(snapshot.ToCsv());
            _nextFlush = Time.unscaledTime + ClampedWindowSeconds();
            WriteStatus(_collecting ? "collecting" : "paused", null, snapshot);
        }

        private float ClampedWindowSeconds()
        {
            return Mathf.Clamp(_windowSeconds.Value, 1f, 60f);
        }

        private void OnDestroy()
        {
            ProbeRuntime.SetCollecting(false);
            _collecting = false;
            try { if (_armed) Flush("shutdown"); } catch { }
            try { _jsonWriter?.Dispose(); } catch { }
            try { _csvWriter?.Dispose(); } catch { }
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            try { WriteStatus("shutdown", null); } catch { }
        }

        private void WriteStatus(string state, string error, WindowSnapshot latest = null)
        {
            try
            {
                Directory.CreateDirectory(_rootDirectory);
                var json = "{\"pluginVersion\":\"" + PluginVersion + "\",\"state\":\"" + Json(state) +
                           "\",\"configuredEnabled\":" + (_enabled != null && _enabled.Value ? "true" : "false") +
                           ",\"armed\":" + (_armed ? "true" : "false") +
                           ",\"collecting\":" + (_collecting ? "true" : "false") +
                           ",\"audioCycleLockTransforms\":" + AudioCycleLockTranspiler.TransformCount +
                           ",\"updateSchedulerTransforms\":" + UpdateSchedulerTranspiler.TransformCount +
                           ",\"sessionDirectory\":" + (string.IsNullOrEmpty(_sessionDirectory) ? "null" : "\"" + Json(_sessionDirectory) + "\"") +
                           ",\"lastWindowUtc\":" + (latest == null ? "null" : "\"" + latest.Utc + "\"") +
                           ",\"error\":" + (string.IsNullOrEmpty(error) ? "null" : "\"" + Json(error) + "\"") + "}";
                File.WriteAllText(Path.Combine(_rootDirectory, "status.json"), json);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not write audio timing probe status: " + ex.Message);
            }
        }

        private static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            return null;
        }

        internal static string Json(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    internal static class ProbeHooks
    {
        public static void MasterUpdatePrefix(out long __state)
        {
            if (!ProbeRuntime.IsCollecting)
            {
                __state = 0;
                return;
            }
            __state = Stopwatch.GetTimestamp();
            ProbeRuntime.MasterUpdateStarted(__state);
        }

        public static void MasterUpdatePostfix(long __state)
        {
            if (__state == 0) return;
            ProbeRuntime.MasterUpdateFinished(__state, Stopwatch.GetTimestamp());
        }

        public static void RunFramePrefix(out long __state)
        {
            if (!ProbeRuntime.IsCollecting)
            {
                __state = 0;
                return;
            }
            __state = Stopwatch.GetTimestamp();
            ProbeRuntime.RunFrameStarted(__state);
        }

        public static void RunFramePostfix(long __state)
        {
            if (__state == 0) return;
            ProbeRuntime.RecordRunFrame(__state, Stopwatch.GetTimestamp());
        }

        public static void AudioCallbackPrefix(out long __state)
        {
            if (!ProbeRuntime.IsCollecting)
            {
                __state = 0;
                return;
            }
            __state = Stopwatch.GetTimestamp();
            ProbeRuntime.AudioCallbackStarted(__state);
        }

        public static void AudioCallbackPostfix(long __state)
        {
            if (__state == 0) return;
            ProbeRuntime.AudioCallbackFinished(__state, Stopwatch.GetTimestamp());
        }

        public static void EnterVoiceClear(object sync, ref bool lockTaken)
        {
            TimedEnter(sync, ref lockTaken, 0);
        }

        public static void EnterKeyOn(object sync, ref bool lockTaken)
        {
            TimedEnter(sync, ref lockTaken, 1);
        }

        public static void EnterKeyOnStart(object sync, ref bool lockTaken)
        {
            TimedEnter(sync, ref lockTaken, 2);
        }

        public static void EnterOutputCommit(object sync, ref bool lockTaken)
        {
            TimedEnter(sync, ref lockTaken, 3);
        }

        private static void TimedEnter(object sync, ref bool lockTaken, int site)
        {
            if (!ProbeRuntime.IsCollecting)
            {
                Monitor.Enter(sync, ref lockTaken);
                return;
            }

            ProbeRuntime.RecordBufferAttempt(site);
            if (Monitor.TryEnter(sync))
            {
                lockTaken = true;
                return;
            }

            var start = Stopwatch.GetTimestamp();
            Monitor.Enter(sync, ref lockTaken);
            ProbeRuntime.RecordBufferContention(site, Stopwatch.GetTimestamp() - start);
        }
    }

    internal static class UpdateSchedulerTranspiler
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var accumulatedField = AccessTools.Field(AccessTools.TypeByName("MasterExecutor"), "_accumulatedDT");
            var mathMin = AccessTools.Method(typeof(Mathf), nameof(Mathf.Min), new[] { typeof(int), typeof(int) });
            var record = AccessTools.Method(typeof(ProbeRuntime), nameof(ProbeRuntime.RecordSchedulerDecision));
            if (accumulatedField == null || mathMin == null || record == null)
                throw new MissingMemberException("MasterExecutor.Update scheduler instrumentation dependencies were not found.");

            var dueStore = -1;
            var minCall = -1;
            for (var index = 7; index < code.Count - 4; index++)
            {
                if (!IsStoreLocal(code[index].opcode) || code[index - 1].opcode != OpCodes.Conv_I4 ||
                    code[index - 6].operand as FieldInfo != accumulatedField ||
                    !IsLoadLocal(code[index - 4].opcode) || code[index + 1].opcode != OpCodes.Ldc_I4_0)
                    continue;
                for (var probe = index + 1; probe < Math.Min(code.Count, index + 30); probe++)
                {
                    if (code[probe].Calls(mathMin))
                    {
                        dueStore = index;
                        minCall = probe;
                        break;
                    }
                }
                if (dueStore >= 0) break;
            }

            if (dueStore < 0 || minCall < 3 || !IsLoadLocal(code[minCall - 1].opcode) ||
                !IsLoadLocal(code[minCall - 2].opcode))
                throw new InvalidOperationException("SuperZSNES v0.230 MasterExecutor.Update due/cap IL pattern was not found.");

            var dueLocal = code[dueStore].operand;
            var targetHzLocal = code[dueStore - 4].operand;
            var capLocal = code[minCall - 1].operand;
            if (!SameLocal(dueLocal, code[minCall - 2].operand))
                throw new InvalidOperationException("MasterExecutor.Update due-frame local did not reach Mathf.Min as expected.");

            code.InsertRange(dueStore + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, accumulatedField),
                new CodeInstruction(OpCodes.Ldloc, targetHzLocal),
                new CodeInstruction(OpCodes.Ldloc, dueLocal),
                new CodeInstruction(OpCodes.Ldloc, capLocal),
                new CodeInstruction(OpCodes.Call, record)
            });
            TransformCount++;
            return code;
        }

        private static bool IsLoadLocal(OpCode opcode)
        {
            return opcode == OpCodes.Ldloc || opcode == OpCodes.Ldloc_S || opcode == OpCodes.Ldloc_0 ||
                   opcode == OpCodes.Ldloc_1 || opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3;
        }

        private static bool IsStoreLocal(OpCode opcode)
        {
            return opcode == OpCodes.Stloc || opcode == OpCodes.Stloc_S || opcode == OpCodes.Stloc_0 ||
                   opcode == OpCodes.Stloc_1 || opcode == OpCodes.Stloc_2 || opcode == OpCodes.Stloc_3;
        }

        private static bool SameLocal(object left, object right)
        {
            if (ReferenceEquals(left, right)) return true;
            return left != null && right != null && left.Equals(right);
        }
    }

    internal static class AudioCycleLockTranspiler
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var monitorEnter = AccessTools.Method(
                typeof(Monitor), nameof(Monitor.Enter), new[] { typeof(object), typeof(bool).MakeByRefType() });
            var monitorExit = AccessTools.Method(typeof(Monitor), nameof(Monitor.Exit), new[] { typeof(object) });
            var replacements = new[]
            {
                AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.EnterVoiceClear)),
                AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.EnterKeyOn)),
                AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.EnterKeyOnStart)),
                AccessTools.Method(typeof(ProbeHooks), nameof(ProbeHooks.EnterOutputCommit))
            };

            var enterIndices = new List<int>();
            var exitCount = 0;
            for (var index = 0; index < code.Count; index++)
            {
                if (code[index].Calls(monitorEnter)) enterIndices.Add(index);
                if (code[index].Calls(monitorExit)) exitCount++;
            }
            if (enterIndices.Count != 4 || exitCount != 4)
                throw new InvalidOperationException(
                    "Unexpected DSPAudio.AudioCycle lock IL shape (Monitor.Enter=" + enterIndices.Count +
                    ", Monitor.Exit=" + exitCount + ").");

            for (var site = 0; site < enterIndices.Count; site++)
            {
                var index = enterIndices[site];
                if (index < 6 || code[index - 1].opcode != OpCodes.Ldloca_S ||
                    !HasBufferLockLoad(code, index - 6, index))
                    throw new InvalidOperationException("AudioCycle lock site " + site + " did not match the v0.230 bufferLock pattern.");
                code[index].opcode = OpCodes.Call;
                code[index].operand = replacements[site];
                TransformCount++;
            }
            return code;
        }

        private static bool HasBufferLockLoad(List<CodeInstruction> code, int start, int end)
        {
            start = Math.Max(0, start);
            for (var index = start; index < end; index++)
            {
                var field = code[index].operand as FieldInfo;
                if (field != null && field.Name == "bufferLock" && field.FieldType == typeof(object)) return true;
            }
            return false;
        }
    }

    internal static class ProbeRuntime
    {
        private static readonly TimingMetric RunFrame = new TimingMetric();
        private static readonly TimingMetric RunFrameCadence = new TimingMetric();
        private static readonly TimingMetric MasterUpdate = new TimingMetric();
        private static readonly TimingMetric MasterUpdateCadence = new TimingMetric();
        private static readonly TimingMetric AudioDuration = new TimingMetric();
        private static readonly TimingMetric AudioCadence = new TimingMetric();
        private static readonly TimingMetric BufferWait = new TimingMetric();
        private static readonly long[] BufferAttempts = new long[4];
        private static readonly long[] BufferContentions = new long[4];
        private static readonly long NearTicks = Math.Max(1L, Stopwatch.Frequency / 1000L);
        private static readonly long SlowFrame16Ticks = Math.Max(1L, Stopwatch.Frequency / 60L);
        private static readonly long SlowFrame33Ticks = Math.Max(1L, Stopwatch.Frequency / 30L);
        private static readonly long FrameGap25Ticks = MillisecondsToTicks(25.0);
        private static readonly long FrameGap33Ticks = MillisecondsToTicks(100.0 / 3.0);
        private static readonly long FrameGap50Ticks = MillisecondsToTicks(50.0);
        private static readonly long FrameGap100Ticks = MillisecondsToTicks(100.0);
        private static readonly long[] RunFramesPerUpdate = new long[6];
        private static readonly long[] SchedulerDueBuckets = new long[6];
        private static readonly long[] SchedulerCapBuckets = new long[6];
        private static int _collecting;
        private static int _audioDepth;
        [ThreadStatic] private static int _masterUpdateDepth;
        [ThreadStatic] private static int _runFramesThisUpdate;
        private static long _lastAudioStart;
        private static long _lastAudioEnd;
        private static long _lastRunFrameStart;
        private static long _lastMasterUpdateStart;
        private static long _windowStart;
        private static long _frameAudioOverlap;
        private static long _frameNearAudio;
        private static long _slowFrame16;
        private static long _slowFrame33;
        private static long _slowFrameAudioOverlap;
        private static long _consecutiveSlowFrames;
        private static long _maxConsecutiveSlowFrames;
        private static long _frameGap25;
        private static long _frameGap33;
        private static long _frameGap50;
        private static long _frameGap100;
        private static long _consecutiveMissedCadence;
        private static long _maxConsecutiveMissedCadence;
        private static long _schedulerDecisionCount;
        private static long _schedulerDueFrames;
        private static long _schedulerScheduledFrames;
        private static long _schedulerDropEvents;
        private static long _schedulerDroppedFrames;
        private static long _schedulerMaxDroppedFrames;
        private static long _schedulerAccumulatedUs;
        private static long _schedulerMaxAccumulatedUs;
        private static long _schedulerNtscDecisions;
        private static long _schedulerPalDecisions;

        internal static bool IsCollecting => Volatile.Read(ref _collecting) != 0;

        internal static void SetCollecting(bool value)
        {
            Volatile.Write(ref _collecting, value ? 1 : 0);
        }

        internal static void ResetAll()
        {
            RunFrame.Reset();
            RunFrameCadence.Reset();
            MasterUpdate.Reset();
            MasterUpdateCadence.Reset();
            AudioDuration.Reset();
            AudioCadence.Reset();
            BufferWait.Reset();
            for (var index = 0; index < 4; index++)
            {
                Interlocked.Exchange(ref BufferAttempts[index], 0);
                Interlocked.Exchange(ref BufferContentions[index], 0);
            }
            for (var index = 0; index < RunFramesPerUpdate.Length; index++)
            {
                RunFramesPerUpdate[index] = 0;
                SchedulerDueBuckets[index] = 0;
                SchedulerCapBuckets[index] = 0;
            }
            Interlocked.Exchange(ref _frameAudioOverlap, 0);
            Interlocked.Exchange(ref _frameNearAudio, 0);
            Interlocked.Exchange(ref _lastAudioStart, 0);
            Interlocked.Exchange(ref _lastAudioEnd, 0);
            Interlocked.Exchange(ref _lastRunFrameStart, 0);
            Interlocked.Exchange(ref _lastMasterUpdateStart, 0);
            Interlocked.Exchange(ref _slowFrame16, 0);
            Interlocked.Exchange(ref _slowFrame33, 0);
            Interlocked.Exchange(ref _slowFrameAudioOverlap, 0);
            Interlocked.Exchange(ref _consecutiveSlowFrames, 0);
            Interlocked.Exchange(ref _maxConsecutiveSlowFrames, 0);
            Interlocked.Exchange(ref _frameGap25, 0);
            Interlocked.Exchange(ref _frameGap33, 0);
            Interlocked.Exchange(ref _frameGap50, 0);
            Interlocked.Exchange(ref _frameGap100, 0);
            Interlocked.Exchange(ref _consecutiveMissedCadence, 0);
            Interlocked.Exchange(ref _maxConsecutiveMissedCadence, 0);
            Interlocked.Exchange(ref _schedulerDecisionCount, 0);
            Interlocked.Exchange(ref _schedulerDueFrames, 0);
            Interlocked.Exchange(ref _schedulerScheduledFrames, 0);
            Interlocked.Exchange(ref _schedulerDropEvents, 0);
            Interlocked.Exchange(ref _schedulerDroppedFrames, 0);
            Interlocked.Exchange(ref _schedulerMaxDroppedFrames, 0);
            Interlocked.Exchange(ref _schedulerAccumulatedUs, 0);
            Interlocked.Exchange(ref _schedulerMaxAccumulatedUs, 0);
            Interlocked.Exchange(ref _schedulerNtscDecisions, 0);
            Interlocked.Exchange(ref _schedulerPalDecisions, 0);
            _masterUpdateDepth = 0;
            _runFramesThisUpdate = 0;
            Volatile.Write(ref _audioDepth, 0);
            Interlocked.Exchange(ref _windowStart, Stopwatch.GetTimestamp());
        }

        internal static void RunFrameStarted(long now)
        {
            var previous = Interlocked.Exchange(ref _lastRunFrameStart, now);
            if (_masterUpdateDepth > 0) _runFramesThisUpdate++;
            if (previous == 0 || now <= previous) return;

            var gap = now - previous;
            RunFrameCadence.Record(gap);
            if (gap > FrameGap25Ticks)
            {
                Interlocked.Increment(ref _frameGap25);
                var sequence = Interlocked.Increment(ref _consecutiveMissedCadence);
                UpdateMaximum(ref _maxConsecutiveMissedCadence, sequence);
            }
            else
            {
                Interlocked.Exchange(ref _consecutiveMissedCadence, 0);
            }
            if (gap > FrameGap33Ticks) Interlocked.Increment(ref _frameGap33);
            if (gap > FrameGap50Ticks) Interlocked.Increment(ref _frameGap50);
            if (gap > FrameGap100Ticks) Interlocked.Increment(ref _frameGap100);
        }

        internal static void MasterUpdateStarted(long now)
        {
            var previous = Interlocked.Exchange(ref _lastMasterUpdateStart, now);
            if (previous != 0 && now > previous) MasterUpdateCadence.Record(now - previous);
            _masterUpdateDepth++;
            if (_masterUpdateDepth == 1) _runFramesThisUpdate = 0;
        }

        internal static void MasterUpdateFinished(long start, long end)
        {
            MasterUpdate.Record(end - start);
            if (_masterUpdateDepth <= 0) return;
            _masterUpdateDepth--;
            if (_masterUpdateDepth != 0) return;
            var bucket = Math.Min(Math.Max(_runFramesThisUpdate, 0), RunFramesPerUpdate.Length - 1);
            RunFramesPerUpdate[bucket]++;
        }

        internal static void RecordSchedulerDecision(float accumulatedSeconds, float targetHz, int dueFrames, int cap)
        {
            if (!IsCollecting) return;
            var nonnegativeDue = Math.Max(0, dueFrames);
            var nonnegativeCap = Math.Max(0, cap);
            var scheduled = Math.Min(nonnegativeDue, nonnegativeCap);
            var dropped = Math.Max(0, nonnegativeDue - scheduled);
            var accumulatedUs = (long)Math.Max(0.0, accumulatedSeconds * 1000000.0);

            _schedulerDecisionCount++;
            _schedulerDueFrames += nonnegativeDue;
            _schedulerScheduledFrames += scheduled;
            SchedulerDueBuckets[Math.Min(nonnegativeDue, SchedulerDueBuckets.Length - 1)]++;
            SchedulerCapBuckets[Math.Min(nonnegativeCap, SchedulerCapBuckets.Length - 1)]++;
            _schedulerAccumulatedUs += accumulatedUs;
            if (accumulatedUs > _schedulerMaxAccumulatedUs) _schedulerMaxAccumulatedUs = accumulatedUs;
            if (targetHz >= 55f) _schedulerNtscDecisions++;
            else _schedulerPalDecisions++;
            if (dropped <= 0) return;
            _schedulerDropEvents++;
            _schedulerDroppedFrames += dropped;
            if (dropped > _schedulerMaxDroppedFrames) _schedulerMaxDroppedFrames = dropped;
        }

        internal static void RecordRunFrame(long start, long end)
        {
            var duration = end - start;
            RunFrame.Record(duration);
            var audioStart = Interlocked.Read(ref _lastAudioStart);
            var audioEnd = Interlocked.Read(ref _lastAudioEnd);
            var audioActive = Volatile.Read(ref _audioDepth) > 0;
            var overlap = false;
            if (audioStart <= end && audioStart != 0 && (audioEnd >= start || audioActive))
            {
                Interlocked.Increment(ref _frameAudioOverlap);
                overlap = true;
            }
            else if ((audioEnd != 0 && Math.Abs(start - audioEnd) <= NearTicks) ||
                     (audioStart != 0 && Math.Abs(audioStart - end) <= NearTicks))
                Interlocked.Increment(ref _frameNearAudio);

            if (duration >= SlowFrame16Ticks)
            {
                Interlocked.Increment(ref _slowFrame16);
                if (overlap) Interlocked.Increment(ref _slowFrameAudioOverlap);
                var sequence = Interlocked.Increment(ref _consecutiveSlowFrames);
                UpdateMaximum(ref _maxConsecutiveSlowFrames, sequence);
            }
            else
            {
                Interlocked.Exchange(ref _consecutiveSlowFrames, 0);
            }
            if (duration >= SlowFrame33Ticks) Interlocked.Increment(ref _slowFrame33);
        }

        internal static void AudioCallbackStarted(long now)
        {
            var previous = Interlocked.Exchange(ref _lastAudioStart, now);
            if (previous != 0 && now > previous) AudioCadence.Record(now - previous);
            Interlocked.Increment(ref _audioDepth);
        }

        internal static void AudioCallbackFinished(long start, long end)
        {
            AudioDuration.Record(end - start);
            Interlocked.Exchange(ref _lastAudioEnd, end);
            Interlocked.Decrement(ref _audioDepth);
        }

        internal static void RecordBufferAttempt(int site)
        {
            // AudioCycle is reached only from UpdateAudioDSP inside RunFrame's
            // main-thread scanline loops in v0.230. Keep this 32 kHz-path count
            // free of an interlocked operation; Update/Flush cannot run in the
            // middle of the same Unity main-thread call.
            BufferAttempts[site]++;
        }

        internal static void RecordBufferContention(int site, long ticks)
        {
            BufferContentions[site]++;
            BufferWait.Record(ticks);
        }

        internal static WindowSnapshot SnapshotAndReset(string reason)
        {
            var now = Stopwatch.GetTimestamp();
            var start = Interlocked.Exchange(ref _windowStart, now);
            var attempts = new long[4];
            var contentions = new long[4];
            var updateBatches = new long[RunFramesPerUpdate.Length];
            var dueBuckets = new long[SchedulerDueBuckets.Length];
            var capBuckets = new long[SchedulerCapBuckets.Length];
            for (var index = 0; index < 4; index++)
            {
                attempts[index] = BufferAttempts[index];
                contentions[index] = BufferContentions[index];
                BufferAttempts[index] = 0;
                BufferContentions[index] = 0;
            }
            for (var index = 0; index < RunFramesPerUpdate.Length; index++)
            {
                updateBatches[index] = RunFramesPerUpdate[index];
                dueBuckets[index] = SchedulerDueBuckets[index];
                capBuckets[index] = SchedulerCapBuckets[index];
                RunFramesPerUpdate[index] = 0;
                SchedulerDueBuckets[index] = 0;
                SchedulerCapBuckets[index] = 0;
            }
            Interlocked.Exchange(ref _consecutiveSlowFrames, 0);
            Interlocked.Exchange(ref _consecutiveMissedCadence, 0);
            var schedulerDecisions = Interlocked.Exchange(ref _schedulerDecisionCount, 0);
            var schedulerAccumulatedUs = Interlocked.Exchange(ref _schedulerAccumulatedUs, 0);
            var host = new HostUpdateSnapshot(
                MasterUpdate.SnapshotAndReset(),
                MasterUpdateCadence.SnapshotAndReset(),
                RunFrameCadence.SnapshotAndReset(),
                updateBatches,
                Interlocked.Exchange(ref _frameGap25, 0),
                Interlocked.Exchange(ref _frameGap33, 0),
                Interlocked.Exchange(ref _frameGap50, 0),
                Interlocked.Exchange(ref _frameGap100, 0),
                Interlocked.Exchange(ref _maxConsecutiveMissedCadence, 0),
                schedulerDecisions,
                Interlocked.Exchange(ref _schedulerDueFrames, 0),
                Interlocked.Exchange(ref _schedulerScheduledFrames, 0),
                Interlocked.Exchange(ref _schedulerDropEvents, 0),
                Interlocked.Exchange(ref _schedulerDroppedFrames, 0),
                Interlocked.Exchange(ref _schedulerMaxDroppedFrames, 0),
                schedulerDecisions == 0 ? 0 : (double)schedulerAccumulatedUs / schedulerDecisions,
                Interlocked.Exchange(ref _schedulerMaxAccumulatedUs, 0),
                Interlocked.Exchange(ref _schedulerNtscDecisions, 0),
                Interlocked.Exchange(ref _schedulerPalDecisions, 0),
                dueBuckets,
                capBuckets);
            return new WindowSnapshot(
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                reason,
                start == 0 ? 0 : TicksToMilliseconds(now - start),
                RunFrame.SnapshotAndReset(),
                host,
                AudioDuration.SnapshotAndReset(),
                AudioCadence.SnapshotAndReset(),
                BufferWait.SnapshotAndReset(),
                attempts,
                contentions,
                Interlocked.Exchange(ref _frameAudioOverlap, 0),
                Interlocked.Exchange(ref _frameNearAudio, 0),
                Interlocked.Exchange(ref _slowFrame16, 0),
                Interlocked.Exchange(ref _slowFrame33, 0),
                Interlocked.Exchange(ref _slowFrameAudioOverlap, 0),
                Interlocked.Exchange(ref _maxConsecutiveSlowFrames, 0));
        }

        internal static double TicksToMicroseconds(long ticks)
        {
            return (double)ticks * 1000000.0 / Stopwatch.Frequency;
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return (double)ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static long MillisecondsToTicks(double milliseconds)
        {
            return Math.Max(1L, (long)Math.Round(Stopwatch.Frequency * milliseconds / 1000.0));
        }

        private static void UpdateMaximum(ref long target, long value)
        {
            var observed = Interlocked.Read(ref target);
            while (value > observed)
            {
                var prior = Interlocked.CompareExchange(ref target, value, observed);
                if (prior == observed) break;
                observed = prior;
            }
        }
    }

    internal sealed class TimingMetric
    {
        private static readonly double[] BucketUpperMicroseconds =
        {
            10, 25, 50, 100, 250, 500, 1000, 2000, 4000, 8000, 16000, 33000, 66000, 100000, 250000, 1000000
        };

        private readonly long[] _buckets = new long[BucketUpperMicroseconds.Length + 1];
        private long _count;
        private long _totalTicks;
        private long _maxTicks;

        internal void Record(long ticks)
        {
            if (ticks < 0) return;
            var microseconds = ProbeRuntime.TicksToMicroseconds(ticks);
            var bucket = 0;
            while (bucket < BucketUpperMicroseconds.Length && microseconds > BucketUpperMicroseconds[bucket]) bucket++;
            Interlocked.Increment(ref _buckets[bucket]);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _totalTicks, ticks);
            var observed = Interlocked.Read(ref _maxTicks);
            while (ticks > observed)
            {
                var prior = Interlocked.CompareExchange(ref _maxTicks, ticks, observed);
                if (prior == observed) break;
                observed = prior;
            }
        }

        internal MetricSnapshot SnapshotAndReset()
        {
            var buckets = new long[_buckets.Length];
            for (var index = 0; index < buckets.Length; index++)
                buckets[index] = Interlocked.Exchange(ref _buckets[index], 0);
            var count = Interlocked.Exchange(ref _count, 0);
            var total = Interlocked.Exchange(ref _totalTicks, 0);
            var max = Interlocked.Exchange(ref _maxTicks, 0);
            return new MetricSnapshot(count, total, max, buckets, BucketUpperMicroseconds);
        }

        internal void Reset()
        {
            SnapshotAndReset();
        }
    }

    internal sealed class MetricSnapshot
    {
        internal readonly long Count;
        internal readonly double AverageUs;
        internal readonly double MaximumUs;
        internal readonly double P50Us;
        internal readonly double P95Us;
        internal readonly double P99Us;

        internal MetricSnapshot(long count, long totalTicks, long maxTicks, long[] buckets, double[] bounds)
        {
            Count = count;
            AverageUs = count == 0 ? 0 : ProbeRuntime.TicksToMicroseconds(totalTicks) / count;
            MaximumUs = ProbeRuntime.TicksToMicroseconds(maxTicks);
            P50Us = Percentile(count, buckets, bounds, 0.50);
            P95Us = Percentile(count, buckets, bounds, 0.95);
            P99Us = Percentile(count, buckets, bounds, 0.99);
        }

        private static double Percentile(long count, long[] buckets, double[] bounds, double quantile)
        {
            if (count <= 0) return 0;
            var target = (long)Math.Ceiling(count * quantile);
            long seen = 0;
            for (var index = 0; index < buckets.Length; index++)
            {
                seen += buckets[index];
                if (seen >= target) return index < bounds.Length ? bounds[index] : bounds[bounds.Length - 1];
            }
            return bounds[bounds.Length - 1];
        }

        internal string Json()
        {
            return "{\"count\":" + Count + ",\"avgUs\":" + N(AverageUs) + ",\"p50Us\":" + N(P50Us) +
                   ",\"p95Us\":" + N(P95Us) + ",\"p99Us\":" + N(P99Us) + ",\"maxUs\":" + N(MaximumUs) + "}";
        }

        internal static string N(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class HostUpdateSnapshot
    {
        internal const string CsvHeader =
            "masterUpdateCount,masterUpdateAvgUs,masterUpdateP95Us,masterUpdateP99Us,masterUpdateMaxUs," +
            "masterUpdateCadenceCount,masterUpdateCadenceAvgUs,masterUpdateCadenceP95Us,masterUpdateCadenceMaxUs," +
            "updatesWith0RunFrames,updatesWith1RunFrame,updatesWith2RunFrames,updatesWith3RunFrames," +
            "updatesWith4RunFrames,updatesWith5PlusRunFrames,frameGapGt25Ms,frameGapGt33Ms,frameGapGt50Ms," +
            "frameGapGt100Ms,maxConsecutiveMissedCadence,schedulerDecisions,schedulerDueFrames," +
            "schedulerScheduledFrames,schedulerDropEvents,schedulerDroppedFrames,schedulerMaxDroppedPerUpdate," +
            "schedulerAccumulatedAvgUs,schedulerAccumulatedMaxUs,schedulerNtscDecisions,schedulerPalDecisions," +
            "schedulerDue0,schedulerDue1,schedulerDue2,schedulerDue3,schedulerDue4,schedulerDue5Plus," +
            "schedulerCap0,schedulerCap1,schedulerCap2,schedulerCap3,schedulerCap4,schedulerCap5Plus";

        internal readonly MetricSnapshot UpdateDuration;
        internal readonly MetricSnapshot UpdateCadence;
        internal readonly MetricSnapshot FrameStartCadence;
        internal readonly long[] RunFramesPerUpdate;
        internal readonly long Gap25;
        internal readonly long Gap33;
        internal readonly long Gap50;
        internal readonly long Gap100;
        internal readonly long MaxConsecutiveMissedCadence;
        internal readonly long SchedulerDecisions;
        internal readonly long SchedulerDueFrames;
        internal readonly long SchedulerScheduledFrames;
        internal readonly long SchedulerDropEvents;
        internal readonly long SchedulerDroppedFrames;
        internal readonly long SchedulerMaxDroppedFrames;
        internal readonly double SchedulerAccumulatedAverageUs;
        internal readonly long SchedulerAccumulatedMaximumUs;
        internal readonly long SchedulerNtscDecisions;
        internal readonly long SchedulerPalDecisions;
        internal readonly long[] SchedulerDueBuckets;
        internal readonly long[] SchedulerCapBuckets;

        internal HostUpdateSnapshot(MetricSnapshot updateDuration, MetricSnapshot updateCadence,
            MetricSnapshot frameStartCadence, long[] runFramesPerUpdate, long gap25, long gap33, long gap50,
            long gap100, long maxConsecutiveMissedCadence, long schedulerDecisions, long schedulerDueFrames,
            long schedulerScheduledFrames, long schedulerDropEvents, long schedulerDroppedFrames,
            long schedulerMaxDroppedFrames, double schedulerAccumulatedAverageUs, long schedulerAccumulatedMaximumUs,
            long schedulerNtscDecisions, long schedulerPalDecisions, long[] schedulerDueBuckets, long[] schedulerCapBuckets)
        {
            UpdateDuration = updateDuration;
            UpdateCadence = updateCadence;
            FrameStartCadence = frameStartCadence;
            RunFramesPerUpdate = runFramesPerUpdate;
            Gap25 = gap25;
            Gap33 = gap33;
            Gap50 = gap50;
            Gap100 = gap100;
            MaxConsecutiveMissedCadence = maxConsecutiveMissedCadence;
            SchedulerDecisions = schedulerDecisions;
            SchedulerDueFrames = schedulerDueFrames;
            SchedulerScheduledFrames = schedulerScheduledFrames;
            SchedulerDropEvents = schedulerDropEvents;
            SchedulerDroppedFrames = schedulerDroppedFrames;
            SchedulerMaxDroppedFrames = schedulerMaxDroppedFrames;
            SchedulerAccumulatedAverageUs = schedulerAccumulatedAverageUs;
            SchedulerAccumulatedMaximumUs = schedulerAccumulatedMaximumUs;
            SchedulerNtscDecisions = schedulerNtscDecisions;
            SchedulerPalDecisions = schedulerPalDecisions;
            SchedulerDueBuckets = schedulerDueBuckets;
            SchedulerCapBuckets = schedulerCapBuckets;
        }

        internal string ToJson()
        {
            return "{\"duration\":" + UpdateDuration.Json() + ",\"cadence\":" + UpdateCadence.Json() +
                   ",\"runFramesPerUpdate\":{\"0\":" + RunFramesPerUpdate[0] + ",\"1\":" + RunFramesPerUpdate[1] +
                   ",\"2\":" + RunFramesPerUpdate[2] + ",\"3\":" + RunFramesPerUpdate[3] +
                   ",\"4\":" + RunFramesPerUpdate[4] + ",\"5Plus\":" + RunFramesPerUpdate[5] +
                   "},\"frameStartGaps\":{\"gt25Ms\":" + Gap25 + ",\"gt33_3Ms\":" + Gap33 +
                   ",\"gt50Ms\":" + Gap50 + ",\"gt100Ms\":" + Gap100 +
                   ",\"maxConsecutiveGt25Ms\":" + MaxConsecutiveMissedCadence + "},\"scheduler\":{\"decisions\":" +
                   SchedulerDecisions + ",\"dueFrames\":" + SchedulerDueFrames + ",\"scheduledFrames\":" +
                   SchedulerScheduledFrames + ",\"dropEvents\":" + SchedulerDropEvents + ",\"droppedFrames\":" +
                   SchedulerDroppedFrames + ",\"maxDroppedPerUpdate\":" + SchedulerMaxDroppedFrames +
                   ",\"accumulatedAvgUs\":" + MetricSnapshot.N(SchedulerAccumulatedAverageUs) +
                   ",\"accumulatedMaxUs\":" + SchedulerAccumulatedMaximumUs + ",\"ntscDecisions\":" +
                   SchedulerNtscDecisions + ",\"palDecisions\":" + SchedulerPalDecisions +
                   ",\"dueHistogram\":" + HistogramJson(SchedulerDueBuckets) +
                   ",\"capHistogram\":" + HistogramJson(SchedulerCapBuckets) + "}}";
        }

        internal string ToCsv()
        {
            return UpdateDuration.Count + "," + MetricSnapshot.N(UpdateDuration.AverageUs) + "," +
                   MetricSnapshot.N(UpdateDuration.P95Us) + "," + MetricSnapshot.N(UpdateDuration.P99Us) + "," +
                   MetricSnapshot.N(UpdateDuration.MaximumUs) + "," + UpdateCadence.Count + "," +
                   MetricSnapshot.N(UpdateCadence.AverageUs) + "," + MetricSnapshot.N(UpdateCadence.P95Us) + "," +
                   MetricSnapshot.N(UpdateCadence.MaximumUs) + "," + Join(RunFramesPerUpdate) + "," + Gap25 + "," +
                   Gap33 + "," + Gap50 + "," + Gap100 + "," + MaxConsecutiveMissedCadence + "," +
                   SchedulerDecisions + "," + SchedulerDueFrames + "," + SchedulerScheduledFrames + "," +
                   SchedulerDropEvents + "," + SchedulerDroppedFrames + "," + SchedulerMaxDroppedFrames + "," +
                   MetricSnapshot.N(SchedulerAccumulatedAverageUs) + "," + SchedulerAccumulatedMaximumUs + "," +
                   SchedulerNtscDecisions + "," + SchedulerPalDecisions + "," + Join(SchedulerDueBuckets) + "," +
                   Join(SchedulerCapBuckets);
        }

        private static string HistogramJson(long[] values)
        {
            return "{\"0\":" + values[0] + ",\"1\":" + values[1] + ",\"2\":" + values[2] +
                   ",\"3\":" + values[3] + ",\"4\":" + values[4] + ",\"5Plus\":" + values[5] + "}";
        }

        private static string Join(long[] values)
        {
            return string.Join(",", Array.ConvertAll(values, value => value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    internal sealed class WindowSnapshot
    {
        internal const string CsvHeader =
            "utc,reason,windowMs,runFrameCount,runFrameAvgUs,runFrameP95Us,runFrameP99Us,runFrameMaxUs," +
            "runFrameCadenceCount,runFrameCadenceAvgUs,runFrameCadenceP95Us,runFrameCadenceMaxUs," +
            HostUpdateSnapshot.CsvHeader + "," +
            "audioCount,audioAvgUs,audioP95Us,audioP99Us,audioMaxUs,cadenceCount,cadenceAvgUs,cadenceP95Us,cadenceMaxUs," +
            "bufferAttempts,bufferContentions,bufferContentionPct,bufferWaitAvgUs,bufferWaitP95Us,bufferWaitP99Us,bufferWaitMaxUs," +
            "voiceClearAttempts,voiceClearContentions,keyOnAttempts,keyOnContentions,keyOnStartAttempts,keyOnStartContentions," +
            "outputCommitAttempts,outputCommitContentions,frameAudioOverlap,frameAudioOverlapPct,frameNearAudio," +
            "slowFrame16Count,slowFrame33Count,maxConsecutiveSlowFrames,slowFrameAudioOverlap,slowFrameAudioOverlapPct";

        internal readonly string Utc;
        private readonly string _reason;
        private readonly double _windowMs;
        private readonly MetricSnapshot _runFrame;
        private readonly HostUpdateSnapshot _host;
        private readonly MetricSnapshot _audio;
        private readonly MetricSnapshot _cadence;
        private readonly MetricSnapshot _bufferWait;
        private readonly long[] _attempts;
        private readonly long[] _contentions;
        private readonly long _overlap;
        private readonly long _near;
        private readonly long _slow16;
        private readonly long _slow33;
        private readonly long _slowAudioOverlap;
        private readonly long _maxConsecutiveSlow;

        internal WindowSnapshot(string utc, string reason, double windowMs, MetricSnapshot runFrame, HostUpdateSnapshot host,
            MetricSnapshot audio, MetricSnapshot cadence, MetricSnapshot bufferWait, long[] attempts, long[] contentions,
            long overlap, long near, long slow16, long slow33, long slowAudioOverlap, long maxConsecutiveSlow)
        {
            Utc = utc;
            _reason = reason;
            _windowMs = windowMs;
            _runFrame = runFrame;
            _host = host;
            _audio = audio;
            _cadence = cadence;
            _bufferWait = bufferWait;
            _attempts = attempts;
            _contentions = contentions;
            _overlap = overlap;
            _near = near;
            _slow16 = slow16;
            _slow33 = slow33;
            _slowAudioOverlap = slowAudioOverlap;
            _maxConsecutiveSlow = maxConsecutiveSlow;
        }

        internal bool HasData => _runFrame.Count != 0 || _host.UpdateDuration.Count != 0 || _audio.Count != 0 || Sum(_attempts) != 0;

        internal string ToJson()
        {
            var attempts = Sum(_attempts);
            var contentions = Sum(_contentions);
            return "{\"utc\":\"" + Utc + "\",\"reason\":\"" + SuperZSNESAudioTimingProbePlugin.Json(_reason) +
                   "\",\"windowMs\":" + MetricSnapshot.N(_windowMs) + ",\"runFrame\":" + _runFrame.Json() +
                   ",\"runFrameCadence\":" + _host.FrameStartCadence.Json() +
                   ",\"hostUpdate\":" + _host.ToJson() +
                   ",\"audioCallback\":" + _audio.Json() + ",\"audioCadence\":" + _cadence.Json() +
                   ",\"bufferLock\":{\"attempts\":" + attempts + ",\"contentions\":" + contentions +
                   ",\"contentionPct\":" + MetricSnapshot.N(Percent(contentions, attempts)) + ",\"wait\":" + _bufferWait.Json() +
                   ",\"sites\":[" + SiteJson(0, "voiceClear") + "," + SiteJson(1, "keyOn") + "," +
                   SiteJson(2, "keyOnStart") + "," + SiteJson(3, "outputCommit") + "]},\"correlation\":{\"frameAudioOverlap\":" +
                   _overlap + ",\"frameAudioOverlapPct\":" + MetricSnapshot.N(Percent(_overlap, _runFrame.Count)) +
                   ",\"frameNearAudio\":" + _near + ",\"slowFrame16Count\":" + _slow16 +
                   ",\"slowFrame33Count\":" + _slow33 + ",\"maxConsecutiveSlowFrames\":" + _maxConsecutiveSlow +
                   ",\"slowFrameAudioOverlap\":" + _slowAudioOverlap + ",\"slowFrameAudioOverlapPct\":" +
                   MetricSnapshot.N(Percent(_slowAudioOverlap, _slow16)) + "}}";
        }

        internal string ToCsv()
        {
            var attempts = Sum(_attempts);
            var contentions = Sum(_contentions);
            return Utc + "," + _reason + "," + MetricSnapshot.N(_windowMs) + "," +
                   _runFrame.Count + "," + MetricSnapshot.N(_runFrame.AverageUs) + "," + MetricSnapshot.N(_runFrame.P95Us) + "," +
                   MetricSnapshot.N(_runFrame.P99Us) + "," + MetricSnapshot.N(_runFrame.MaximumUs) + "," +
                   _host.FrameStartCadence.Count + "," + MetricSnapshot.N(_host.FrameStartCadence.AverageUs) + "," +
                   MetricSnapshot.N(_host.FrameStartCadence.P95Us) + "," + MetricSnapshot.N(_host.FrameStartCadence.MaximumUs) + "," +
                   _host.ToCsv() + "," +
                   _audio.Count + "," + MetricSnapshot.N(_audio.AverageUs) + "," + MetricSnapshot.N(_audio.P95Us) + "," +
                   MetricSnapshot.N(_audio.P99Us) + "," + MetricSnapshot.N(_audio.MaximumUs) + "," +
                   _cadence.Count + "," + MetricSnapshot.N(_cadence.AverageUs) + "," + MetricSnapshot.N(_cadence.P95Us) + "," +
                   MetricSnapshot.N(_cadence.MaximumUs) + "," + attempts + "," + contentions + "," +
                   MetricSnapshot.N(Percent(contentions, attempts)) + "," + MetricSnapshot.N(_bufferWait.AverageUs) + "," +
                   MetricSnapshot.N(_bufferWait.P95Us) + "," + MetricSnapshot.N(_bufferWait.P99Us) + "," +
                   MetricSnapshot.N(_bufferWait.MaximumUs) + "," + _attempts[0] + "," + _contentions[0] + "," +
                   _attempts[1] + "," + _contentions[1] + "," + _attempts[2] + "," + _contentions[2] + "," +
                   _attempts[3] + "," + _contentions[3] + "," + _overlap + "," +
                   MetricSnapshot.N(Percent(_overlap, _runFrame.Count)) + "," + _near + "," + _slow16 + "," +
                   _slow33 + "," + _maxConsecutiveSlow + "," + _slowAudioOverlap + "," +
                   MetricSnapshot.N(Percent(_slowAudioOverlap, _slow16));
        }

        private string SiteJson(int index, string name)
        {
            return "{\"name\":\"" + name + "\",\"attempts\":" + _attempts[index] +
                   ",\"contentions\":" + _contentions[index] + "}";
        }

        private static long Sum(long[] values)
        {
            long result = 0;
            for (var index = 0; index < values.Length; index++) result += values[index];
            return result;
        }

        private static double Percent(long numerator, long denominator)
        {
            return denominator == 0 ? 0 : 100.0 * numerator / denominator;
        }
    }
}
