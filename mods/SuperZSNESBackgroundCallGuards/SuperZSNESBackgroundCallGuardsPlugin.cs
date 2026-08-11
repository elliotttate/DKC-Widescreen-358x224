using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SuperZSNESBackgroundCallGuards
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESBackgroundCallGuardsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.backgroundcallguards";
        public const string PluginName = "SuperZSNES Background Call Guards";
        public const string PluginVersion = "0.1.1";

        private ConfigEntry<bool> _emptyScratchClearLoop;
        private ConfigEntry<bool> _noOpProcessCalls;
        private Harmony _harmony;

        private void Awake()
        {
            _emptyScratchClearLoop = Config.Bind(
                "Optimizations", "OptimizeEmptyScratchClearLoop", false,
                "Skip the stock usedMaterials scratch-list clear walk only when tileAddrToMat.Count is already zero.");
            _noOpProcessCalls = Config.Bind(
                "Optimizations", "OptimizeNoOpProcess2DTilesCalls", false,
                "EXPERIMENTAL: skip each Process2DTiles call when the current remainder is below that call's 256/64/16/4/1 threshold. Do not combine with OptimizeEmptyScratchClearLoop; that combination failed visual QA.");

            if (_emptyScratchClearLoop.Value && _noOpProcessCalls.Value)
            {
                const string quarantine = "The combined optimization failed live visual QA and is quarantined. No PPURenderer patch was applied; isolate one option at a time only in a controlled test.";
                Logger.LogError(quarantine);
                WriteStatus("quarantined-combination", quarantine);
                return;
            }

            if (!_emptyScratchClearLoop.Value && !_noOpProcessCalls.Value)
            {
                Logger.LogInfo("Background call guards are disabled; PPURenderer was not patched.");
                WriteStatus("disabled", null);
                return;
            }

            try
            {
                var layout = BackgroundGuardLayout.ResolveAndVerify();
                var original = PatchProcessor.GetOriginalInstructions(layout.GenerateBackground);
                var report = BackgroundCallGuardOptimization.VerifyExact(
                    original, layout, _emptyScratchClearLoop.Value, _noOpProcessCalls.Value);

                BackgroundCallGuardOptimization.Configure(
                    layout, _emptyScratchClearLoop.Value, _noOpProcessCalls.Value, Logger);
                _harmony = new Harmony(PluginGuid);
                _harmony.Patch(
                    layout.GenerateBackground,
                    transpiler: new HarmonyMethod(AccessTools.Method(
                        typeof(BackgroundCallGuardOptimization),
                        nameof(BackgroundCallGuardOptimization.Transpiler))));

                var expectedClear = _emptyScratchClearLoop.Value ? 1 : 0;
                var expectedCalls = _noOpProcessCalls.Value ? 5 : 0;
                if (BackgroundCallGuardOptimization.ClearLoopTransformCount != expectedClear ||
                    BackgroundCallGuardOptimization.ProcessCallTransformCount != expectedCalls)
                {
                    throw new InvalidOperationException(
                        "Runtime Harmony chain did not retain the verified v0.230 shape: clear=" +
                        BackgroundCallGuardOptimization.ClearLoopTransformCount + "/" + expectedClear +
                        ", processCalls=" + BackgroundCallGuardOptimization.ProcessCallTransformCount + "/" +
                        expectedCalls + ".");
                }

                Logger.LogInfo("Applied exact background call guards. " + report);
                WriteStatus("active", report);
            }
            catch (Exception exception)
            {
                try { _harmony?.UnpatchSelf(); } catch { }
                Logger.LogError("Background call guards failed closed; no optimization remains active: " + exception);
                WriteStatus("failed-closed", exception.Message);
            }
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private void WriteStatus(string state, string detail)
        {
            try
            {
                var directory = Path.Combine(Paths.PluginPath, "SuperZSNESBackgroundCallGuards");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "status.json"),
                    "{\"pluginVersion\":\"" + PluginVersion + "\",\"state\":\"" + Escape(state) +
                    "\",\"optimizeEmptyScratchClearLoop\":" + Bool(_emptyScratchClearLoop.Value) +
                    ",\"optimizeNoOpProcess2DTilesCalls\":" + Bool(_noOpProcessCalls.Value) +
                    ",\"clearLoopTransforms\":" + BackgroundCallGuardOptimization.ClearLoopTransformCount +
                    ",\"processCallTransforms\":" + BackgroundCallGuardOptimization.ProcessCallTransformCount +
                    ",\"detail\":\"" + Escape(detail ?? string.Empty) + "\"}");
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Could not write background-call-guard status: " + exception.Message);
            }
        }

        private static string Bool(bool value) => value ? "true" : "false";

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    internal sealed class BackgroundGuardLayout
    {
        internal MethodInfo GenerateBackground;
        internal MethodInfo Process2DTiles;
        internal FieldInfo UsedMaterials;
        internal FieldInfo TileAddrToMat;
        internal MethodInfo UsedMaterialsGetEnumerator;
        internal MethodInfo UsedMaterialsClear;
        internal MethodInfo TileAddrToMatCount;

        internal static BackgroundGuardLayout ResolveAndVerify()
        {
            var renderer = AccessTools.TypeByName("PPURenderer") ??
                           throw new TypeLoadException("PPURenderer was not found.");
            var result = new BackgroundGuardLayout
            {
                GenerateBackground = AccessTools.Method(renderer, "GenerateBackground"),
                Process2DTiles = AccessTools.Method(renderer, "Process2DTiles"),
                UsedMaterials = AccessTools.Field(renderer, "usedMaterials"),
                TileAddrToMat = AccessTools.Field(renderer, "tileAddrToMat")
            };
            if (result.GenerateBackground == null || result.Process2DTiles == null ||
                result.UsedMaterials == null || result.TileAddrToMat == null)
                throw new MissingMemberException("Required PPURenderer v0.230 members were not found.");

            result.UsedMaterialsGetEnumerator = AccessTools.Method(result.UsedMaterials.FieldType, "GetEnumerator", Type.EmptyTypes);
            result.UsedMaterialsClear = AccessTools.Method(result.UsedMaterials.FieldType, "Clear", Type.EmptyTypes);
            result.TileAddrToMatCount = AccessTools.PropertyGetter(result.TileAddrToMat.FieldType, "Count");
            if (result.UsedMaterialsGetEnumerator == null || result.UsedMaterialsClear == null ||
                result.TileAddrToMatCount == null)
                throw new MissingMethodException("Required collection methods were not found.");

            var parameters = result.Process2DTiles.GetParameters();
            if (parameters.Length != 12 || parameters[2].ParameterType != typeof(int) ||
                !parameters[6].ParameterType.IsByRef ||
                parameters[6].ParameterType.GetElementType() != typeof(int))
                throw new InvalidOperationException("PPURenderer.Process2DTiles ABI does not match v0.230.");
            return result;
        }
    }

    internal static class BackgroundCallGuardOptimization
    {
        private sealed class ProcessCallPlan
        {
            internal int Start;
            internal int Call;
            internal int Target;
            internal int Threshold;
            internal object RemainderLocal;
        }

        private sealed class RewritePlan
        {
            internal int ClearStart = -1;
            internal int ClearTarget = -1;
            internal readonly List<ProcessCallPlan> ProcessCalls = new List<ProcessCallPlan>();
        }

        private static BackgroundGuardLayout _layout;
        private static bool _optimizeEmptyClear;
        private static bool _optimizeProcessCalls;
        private static ManualLogSource _log;

        internal static int ClearLoopTransformCount;
        internal static int ProcessCallTransformCount;

        internal static void Configure(BackgroundGuardLayout layout, bool optimizeEmptyClear,
            bool optimizeProcessCalls, ManualLogSource log)
        {
            _layout = layout;
            _optimizeEmptyClear = optimizeEmptyClear;
            _optimizeProcessCalls = optimizeProcessCalls;
            _log = log;
            ClearLoopTransformCount = 0;
            ProcessCallTransformCount = 0;
        }

        internal static string VerifyExact(IEnumerable<CodeInstruction> input, BackgroundGuardLayout layout,
            bool optimizeEmptyClear, bool optimizeProcessCalls)
        {
            var code = input.Select(instruction => new CodeInstruction(instruction)).ToList();
            var plan = Analyze(code, layout, optimizeEmptyClear, optimizeProcessCalls);
            return "verified v0.230 IL: emptyClear=" + (plan.ClearStart >= 0 ? "1/1" : "off") +
                   ", Process2DTiles=" + (optimizeProcessCalls
                       ? string.Join("/", plan.ProcessCalls.Select(call => call.Threshold))
                       : "off") + ".";
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input, ILGenerator generator)
        {
            var code = input.Select(instruction => new CodeInstruction(instruction)).ToList();
            ClearLoopTransformCount = 0;
            ProcessCallTransformCount = 0;
            try
            {
                if (_layout == null)
                    throw new InvalidOperationException("The optimization layout was not configured.");
                var plan = Analyze(code, _layout, _optimizeEmptyClear, _optimizeProcessCalls);
                Apply(code, plan, _layout, generator);
                ClearLoopTransformCount = plan.ClearStart >= 0 ? 1 : 0;
                ProcessCallTransformCount = plan.ProcessCalls.Count;
                return code;
            }
            catch (Exception exception)
            {
                _log?.LogError("GenerateBackground transpiler rejected the runtime IL and emitted stock instructions: " + exception.Message);
                ClearLoopTransformCount = 0;
                ProcessCallTransformCount = 0;
                return input;
            }
        }

        private static RewritePlan Analyze(IReadOnlyList<CodeInstruction> code, BackgroundGuardLayout layout,
            bool optimizeEmptyClear, bool optimizeProcessCalls)
        {
            var plan = new RewritePlan();
            if (optimizeEmptyClear)
                AnalyzeEmptyClear(code, layout, plan);
            if (optimizeProcessCalls)
                AnalyzeProcessCalls(code, layout, plan);
            return plan;
        }

        private static void AnalyzeEmptyClear(IReadOnlyList<CodeInstruction> code,
            BackgroundGuardLayout layout, RewritePlan plan)
        {
            var enumerators = new List<int>();
            var clears = new List<int>();
            for (var index = 2; index < code.Count; index++)
            {
                if (code[index].Calls(layout.UsedMaterialsGetEnumerator) &&
                    IsLdarg0(code[index - 2]) && Equals(code[index - 1].operand, layout.UsedMaterials))
                    enumerators.Add(index - 2);
                if (code[index].Calls(layout.UsedMaterialsClear) &&
                    IsLdarg0(code[index - 2]) && Equals(code[index - 1].operand, layout.UsedMaterials))
                    clears.Add(index - 2);
            }

            if (enumerators.Count != 2 || clears.Count != 1 ||
                enumerators[0] >= clears[0] || enumerators[1] <= clears[0])
                throw new InvalidOperationException(
                    "Expected early-clear/tail usedMaterials enumerators 2 and Clear 1; got enumerators=" +
                    enumerators.Count + ", clears=" + clears.Count + ".");

            plan.ClearStart = enumerators[0];
            plan.ClearTarget = clears[0];
        }

        private static void AnalyzeProcessCalls(IReadOnlyList<CodeInstruction> code,
            BackgroundGuardLayout layout, RewritePlan plan)
        {
            var expected = new[] { 256, 64, 16, 4, 1 };
            for (var callIndex = 0; callIndex < code.Count; callIndex++)
            {
                if (!code[callIndex].Calls(layout.Process2DTiles))
                    continue;
                var start = callIndex - 17;
                if (start < 0 || callIndex + 1 >= code.Count)
                    throw new InvalidOperationException("Process2DTiles call has an incomplete argument sequence.");

                var threshold = ReadI4(code[start + 3]);
                if (!IsLdarg0(code[start]) || !IsLdloc(code[start + 1]) || !IsLdloc(code[start + 2]) ||
                    threshold == null || !IsLdarg2(code[start + 4]) ||
                    !IsLdloca(code[start + 5]) || !IsLdloca(code[start + 6]) || !IsLdloca(code[start + 7]) ||
                    !IsLdarg0(code[start + 8]) || code[start + 9].opcode != OpCodes.Ldflda ||
                    !IsLdarg0(code[start + 10]) || code[start + 11].opcode != OpCodes.Ldfld ||
                    !IsLdarg0(code[start + 12]) || code[start + 13].opcode != OpCodes.Ldfld ||
                    !IsLdarg0(code[start + 14]) || code[start + 15].opcode != OpCodes.Ldfld ||
                    !IsLdarg1(code[start + 16]))
                    throw new InvalidOperationException("Process2DTiles argument sequence does not match exact v0.230 IL.");

                var suffix = threshold.Value.ToString();
                RequireFieldName(code[start + 9], "mesh" + suffix + "Idx");
                RequireFieldName(code[start + 11], "mesh" + suffix);
                RequireFieldName(code[start + 13], "mesh" + suffix + "Vec");
                RequireFieldName(code[start + 15], "mesh" + suffix + "UV2");
                plan.ProcessCalls.Add(new ProcessCallPlan
                {
                    Start = start,
                    Call = callIndex,
                    Target = callIndex + 1,
                    Threshold = threshold.Value,
                    RemainderLocal = code[start + 7].operand
                });
            }

            if (plan.ProcessCalls.Count != expected.Length ||
                !plan.ProcessCalls.Select(call => call.Threshold).SequenceEqual(expected))
                throw new InvalidOperationException("Expected Process2DTiles thresholds 256/64/16/4/1 exactly once.");
            if (plan.ProcessCalls.Any(call => !Equals(call.RemainderLocal, plan.ProcessCalls[0].RemainderLocal)))
                throw new InvalidOperationException("Process2DTiles calls do not share one remainder local.");
        }

        private static void Apply(List<CodeInstruction> code, RewritePlan plan,
            BackgroundGuardLayout layout, ILGenerator generator)
        {
            var insertions = new Dictionary<int, List<CodeInstruction>>();
            if (plan.ClearStart >= 0)
            {
                var target = generator.DefineLabel();
                code[plan.ClearTarget].labels.Add(target);
                insertions.Add(plan.ClearStart, new List<CodeInstruction>
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld, layout.TileAddrToMat),
                    new CodeInstruction(OpCodes.Callvirt, layout.TileAddrToMatCount),
                    new CodeInstruction(OpCodes.Brfalse, target)
                });
            }

            foreach (var call in plan.ProcessCalls)
            {
                var target = generator.DefineLabel();
                code[call.Target].labels.Add(target);
                var localLoad = new CodeInstruction(OpCodes.Ldloc, call.RemainderLocal);
                var thresholdLoad = new CodeInstruction(OpCodes.Ldc_I4, call.Threshold);
                insertions.Add(call.Start, new List<CodeInstruction>
                {
                    localLoad,
                    thresholdLoad,
                    new CodeInstruction(OpCodes.Blt, target)
                });
            }

            foreach (var insertion in insertions)
            {
                MoveMetadata(code[insertion.Key], insertion.Value[0]);
            }
            foreach (var insertion in insertions.OrderByDescending(pair => pair.Key))
                code.InsertRange(insertion.Key, insertion.Value);
        }

        private static void MoveMetadata(CodeInstruction from, CodeInstruction to)
        {
            to.labels.AddRange(from.labels);
            from.labels.Clear();
            to.blocks.AddRange(from.blocks);
            from.blocks.Clear();
        }

        private static void RequireFieldName(CodeInstruction instruction, string expected)
        {
            var field = instruction.operand as FieldInfo;
            if (field == null || field.Name != expected)
                throw new InvalidOperationException("Expected field " + expected + " in Process2DTiles call sequence.");
        }

        private static int? ReadI4(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Ldc_I4_M1) return -1;
            if (instruction.opcode == OpCodes.Ldc_I4_0) return 0;
            if (instruction.opcode == OpCodes.Ldc_I4_1) return 1;
            if (instruction.opcode == OpCodes.Ldc_I4_2) return 2;
            if (instruction.opcode == OpCodes.Ldc_I4_3) return 3;
            if (instruction.opcode == OpCodes.Ldc_I4_4) return 4;
            if (instruction.opcode == OpCodes.Ldc_I4_5) return 5;
            if (instruction.opcode == OpCodes.Ldc_I4_6) return 6;
            if (instruction.opcode == OpCodes.Ldc_I4_7) return 7;
            if (instruction.opcode == OpCodes.Ldc_I4_8) return 8;
            if (instruction.opcode == OpCodes.Ldc_I4_S) return (sbyte)instruction.operand;
            if (instruction.opcode == OpCodes.Ldc_I4) return (int)instruction.operand;
            return null;
        }

        private static bool IsLdarg0(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldarg_0;
        private static bool IsLdarg1(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldarg_1;
        private static bool IsLdarg2(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldarg_2;

        private static bool IsLdloc(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S ||
                   instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Ldloc_1 ||
                   instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Ldloc_3;
        }

        private static bool IsLdloca(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S;
        }
    }
}
