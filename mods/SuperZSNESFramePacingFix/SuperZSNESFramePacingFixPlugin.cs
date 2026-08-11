using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESFramePacingFix
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESFramePacingFixPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.framepacingfix";
        public const string PluginName = "SuperZSNES Frame Pacing Fix";
        public const string PluginVersion = "0.3.0";

        private Harmony _harmony;

        private void Awake()
        {
            FramePacingPatch.Enabled = Config.Bind(
                "FramePacing",
                "Enabled",
                false,
                "Apply the normal-speed accumulator fix so only frames actually scheduled by the five-frame cap are consumed.");
            FramePacingPatch.EmergencyMaxBacklogFrames = Config.Bind(
                "FramePacing",
                "EmergencyMaxBacklogFrames",
                120,
                new ConfigDescription(
                    "Emergency ceiling for normal-speed backlog remaining after the current update executes. 120 is two NTSC seconds and does not affect a 1.045-second stall. Zero is unbounded.",
                    new AcceptableValueRange<int>(0, 36000)));
            CadenceController.Enabled = Config.Bind(
                "PresentationCadence",
                "Enabled",
                true,
                "At normal speed, disable VSync and request a higher Unity update/presentation cadence so one Unity update is available for each emulated frame. The plugin master Enabled switch must also be true.");
            CadenceController.TargetFrameRate = Config.Bind(
                "PresentationCadence",
                "TargetFrameRate",
                120,
                new ConfigDescription(
                    "Unity software frame-rate ceiling while normal-speed emulation is active. This is cadence headroom, not the SNES emulation rate. Use at least 90; 120 is recommended.",
                    new AcceptableValueRange<int>(61, 480)));
            CadenceController.RestoreDuringFastForward = Config.Bind(
                "PresentationCadence",
                "RestoreDuringFastForward",
                true,
                "Restore the original VSync and Application.targetFrameRate while fast-forward is active, preserving its cadence-dependent stock behavior." );

            if (!FramePacingPatch.Enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion + " is disabled; no Harmony patch was applied.");
                return;
            }

            CadenceController.Initialize(Logger);

            var masterExecutor = AccessTools.TypeByName("MasterExecutor");
            var update = masterExecutor == null ? null : AccessTools.Method(masterExecutor, "Update", Type.EmptyTypes);
            if (update == null)
                throw new MissingMethodException("SuperZSNES v0.230 MasterExecutor.Update() was not found.");

            FramePacingPatch.TransformCount = 0;
            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(update, transpiler: new HarmonyMethod(
                AccessTools.Method(typeof(FramePacingPatch), nameof(FramePacingPatch.Transpiler))));
            if (FramePacingPatch.TransformCount != 1)
                throw new InvalidOperationException("Expected exactly one frame-accumulator IL replacement, got " +
                                                    FramePacingPatch.TransformCount + ".");

            Logger.LogInfo("Applied normal-speed frame backlog fix in memory. On-disk Assembly-CSharp.dll is unchanged; emergency backlog ceiling=" +
                           (FramePacingPatch.EmergencyMaxBacklogFrames.Value == 0
                               ? "unbounded"
                               : FramePacingPatch.EmergencyMaxBacklogFrames.Value + " frames") +
                           "; presentation cadence lift=" + CadenceController.Enabled.Value +
                           (CadenceController.Enabled.Value
                               ? " (VSync off, target " + CadenceController.TargetFrameRate.Value + " Hz)"
                               : string.Empty) + ".");
        }

        private void OnDestroy()
        {
            try
            {
                CadenceController.RestoreOriginal();
                if (_harmony != null) _harmony.UnpatchSelf();
            }
            catch
            {
                // Unity may already be tearing down the managed runtime.
            }
        }
    }

    internal static class FramePacingPatch
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> EmergencyMaxBacklogFrames;
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var masterExecutor = AccessTools.TypeByName("MasterExecutor");
            var accumulated = masterExecutor == null ? null : AccessTools.Field(masterExecutor, "_accumulatedDT");
            var integerMin = AccessTools.Method(typeof(Mathf), nameof(Mathf.Min), new[] { typeof(int), typeof(int) });
            var consume = AccessTools.Method(typeof(FramePacingPatch), nameof(ConsumeElapsed));
            if (accumulated == null || integerMin == null || consume == null)
                throw new InvalidOperationException("Required SuperZSNES frame-pacing members were not found.");

            var start = -1;
            for (var index = 0; index + 10 < code.Count; index++)
            {
                if (code[index].opcode != OpCodes.Ldarg_0 ||
                    code[index + 1].opcode != OpCodes.Ldarg_0 ||
                    !code[index + 2].LoadsField(accumulated) ||
                    !IsLoadLocal(code[index + 3]) ||
                    code[index + 4].opcode != OpCodes.Conv_R4 ||
                    code[index + 5].opcode != OpCodes.Ldc_R4 ||
                    !Equals(code[index + 5].operand, 1f) ||
                    !IsLoadLocal(code[index + 6]) ||
                    code[index + 7].opcode != OpCodes.Div ||
                    code[index + 8].opcode != OpCodes.Mul ||
                    code[index + 9].opcode != OpCodes.Sub ||
                    !code[index + 10].StoresField(accumulated))
                    continue;

                if (start != -1)
                    throw new InvalidOperationException("More than one MasterExecutor accumulator-charge IL pattern matched.");
                start = index;
            }

            if (start < 0)
                throw new InvalidOperationException("SuperZSNES v0.230 accumulator-charge IL pattern was not found.");

            var dueLoad = code[start + 3];
            var targetHzLoad = code[start + 6];
            var minIndex = -1;
            for (var index = start - 1; index >= Math.Max(2, start - 24); index--)
            {
                if (!code[index].Calls(integerMin)) continue;
                if (!IsLoadLocal(code[index - 2]) || !IsLoadLocal(code[index - 1])) continue;
                if (LocalIndex(code[index - 2]) != LocalIndex(dueLoad)) continue;
                minIndex = index;
                break;
            }
            if (minIndex < 0)
                throw new InvalidOperationException("The loop's Mathf.Min(dueFrames, cap) IL pattern was not found.");

            var capLoad = code[minIndex - 1];
            for (var index = start + 1; index <= start + 10; index++)
            {
                if (code[index].labels.Count != 0 || code[index].blocks.Count != 0)
                    throw new InvalidOperationException("Accumulator arithmetic contains an unexpected branch target or exception boundary.");
            }

            var replacement = new List<CodeInstruction>
            {
                new CodeInstruction(code[start]),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, accumulated),
                CloneWithoutMetadata(dueLoad),
                CloneWithoutMetadata(capLoad),
                CloneWithoutMetadata(targetHzLoad),
                new CodeInstruction(OpCodes.Call, consume),
                new CodeInstruction(OpCodes.Stfld, accumulated)
            };

            code.RemoveRange(start, 11);
            code.InsertRange(start, replacement);
            TransformCount++;
            return code;
        }

        // Normal speed uses cap=5. Fast-forward deliberately uses cap=1..4 and takes
        // the stock arithmetic path, including its charge-all-due-frames behavior.
        public static float ConsumeElapsed(float accumulated, int dueFrames, int cap, float targetHz)
        {
            CadenceController.ObserveExecutionCap(cap);
            var emergencyMaximum = EmergencyMaxBacklogFrames == null ? 120 : EmergencyMaxBacklogFrames.Value;
            return ConsumeElapsedCore(accumulated, dueFrames, cap, targetHz, emergencyMaximum);
        }

        public static float ConsumeElapsedCore(float accumulated, int dueFrames, int cap, float targetHz,
                                               int emergencyMaxBacklogFrames)
        {
            var period = 1f / targetHz;
            if (cap != 5)
                return accumulated - (float)dueFrames * period;

            var scheduledFrames = Math.Min(Math.Max(dueFrames, 0), cap);
            var remaining = accumulated - (float)scheduledFrames * period;
            if (!(remaining > 0f)) return 0f;

            // The ceiling is an emergency guard, not the normal catch-up policy. It is
            // applied after this update's scheduled frames are charged so the current
            // five-frame batch is never retroactively changed. Zero means unbounded.
            if (emergencyMaxBacklogFrames > 0)
            {
                var maximumSeconds = (float)emergencyMaxBacklogFrames * period;
                if (remaining > maximumSeconds) return maximumSeconds;
            }
            return remaining;
        }

        private static CodeInstruction CloneWithoutMetadata(CodeInstruction instruction)
        {
            return new CodeInstruction(instruction.opcode, instruction.operand);
        }

        private static bool IsLoadLocal(CodeInstruction instruction)
        {
            var opcode = instruction.opcode;
            return opcode == OpCodes.Ldloc || opcode == OpCodes.Ldloc_S ||
                   opcode == OpCodes.Ldloc_0 || opcode == OpCodes.Ldloc_1 ||
                   opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3;
        }

        private static int LocalIndex(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Ldloc_0) return 0;
            if (instruction.opcode == OpCodes.Ldloc_1) return 1;
            if (instruction.opcode == OpCodes.Ldloc_2) return 2;
            if (instruction.opcode == OpCodes.Ldloc_3) return 3;
            if (instruction.operand is LocalBuilder builder) return builder.LocalIndex;
            if (instruction.operand is LocalVariableInfo variable) return variable.LocalIndex;
            if (instruction.operand is byte byteIndex) return byteIndex;
            if (instruction.operand is int intIndex) return intIndex;
            return -1;
        }
    }

    // SuperZSNES only calls GenerateBackgrounds once per Unity Update, after all due
    // emulation frames have run. A two-frame batch therefore advances over one SNES
    // image without presenting it. The only scheduler-level way to prevent that at
    // full speed is to provide at least 60 Unity presentation opportunities per
    // second. This controller raises the normal-speed Unity cadence; it does not
    // change the 60/50 Hz emulation accumulator or predict frames early.
    internal static class CadenceController
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> TargetFrameRate;
        internal static ConfigEntry<bool> RestoreDuringFastForward;

        private static BepInEx.Logging.ManualLogSource _logger;
        private static int _originalVSyncCount;
        private static int _originalTargetFrameRate;
        private static bool _initialized;
        private static bool _usingHighCadence;

        internal static void Initialize(BepInEx.Logging.ManualLogSource logger)
        {
            if (_initialized) return;
            _logger = logger;
            _originalVSyncCount = QualitySettings.vSyncCount;
            _originalTargetFrameRate = Application.targetFrameRate;
            _initialized = true;
            _usingHighCadence = false;
        }

        internal static void ObserveExecutionCap(int cap)
        {
            if (!_initialized || Enabled == null || !Enabled.Value) return;

            // MasterExecutor uses cap=5 only for normal play and cap=1..4 for
            // fast-forward. Switching after this update changes the next Unity
            // cadence but leaves the current stock fast-forward arithmetic intact.
            if (cap == 5 || RestoreDuringFastForward == null || !RestoreDuringFastForward.Value)
                ApplyHighCadence();
            else
                RestoreOriginal();
        }

        internal static void ApplyHighCadence()
        {
            if (!_initialized || Enabled == null || !Enabled.Value) return;
            var target = TargetFrameRate == null ? 120 : TargetFrameRate.Value;
            if (_usingHighCadence && QualitySettings.vSyncCount == 0 &&
                Application.targetFrameRate == target) return;

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = target;
            if (!_usingHighCadence && _logger != null)
                _logger.LogInfo("Normal-speed presentation cadence lift active: VSync=0, targetFrameRate=" + target + ".");
            _usingHighCadence = true;
        }

        internal static void RestoreOriginal()
        {
            if (!_initialized) return;
            if (!_usingHighCadence && QualitySettings.vSyncCount == _originalVSyncCount &&
                Application.targetFrameRate == _originalTargetFrameRate) return;

            QualitySettings.vSyncCount = _originalVSyncCount;
            Application.targetFrameRate = _originalTargetFrameRate;
            if (_usingHighCadence && _logger != null)
                _logger.LogInfo("Restored original presentation cadence: VSync=" + _originalVSyncCount +
                                ", targetFrameRate=" + _originalTargetFrameRate + ".");
            _usingHighCadence = false;
        }

        // Pure helpers used by the offline verifier. They document the mode
        // discriminator without requiring a running Unity player.
        public static bool WantsHighCadenceCore(int cap, bool enabled, bool restoreDuringFastForward)
        {
            return enabled && (cap == 5 || !restoreDuringFastForward);
        }
    }
}
