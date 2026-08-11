using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using HarmonyLib;

namespace SuperZSNESRenderLinesLoopFix
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESRenderLinesLoopFixPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.renderlinesloopfix";
        public const string PluginName = "SuperZSNES RenderLines Loop Fix";
        public const string PluginVersion = "0.1.0";

        private Harmony _harmony;

        private void Awake()
        {
            var enabled = Config.Bind("Optimization", "Enabled", false,
                "Render each of the 128 OAM entries exactly once in PPURenderer.RenderLines. False applies no patch.");
            if (!enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion + " is disabled; no Harmony patch was applied.");
                return;
            }

            var renderer = AccessTools.TypeByName("PPURenderer");
            var target = renderer == null
                ? null
                : renderer.GetMethods(AccessTools.all).SingleOrDefault(IsExactRenderLinesSignature);
            if (target == null)
                throw new MissingMethodException("Exact SuperZSNES v0.230 PPURenderer.RenderLines signature was not found.");

            RenderLinesLoopPatch.TransformCount = 0;
            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(target, transpiler: new HarmonyMethod(
                AccessTools.Method(typeof(RenderLinesLoopPatch), nameof(RenderLinesLoopPatch.Transpiler))));
            if (RenderLinesLoopPatch.TransformCount != 1)
                throw new InvalidOperationException("Expected exactly one RenderLines terminal loop-bound rewrite, got " +
                                                    RenderLinesLoopPatch.TransformCount + ".");

            Logger.LogInfo("Applied in-memory RenderLines OAM loop fix: terminal inclusive bound 128 -> 127. " +
                           "The on-disk game assembly is unchanged.");
        }

        private static bool IsExactRenderLinesSignature(MethodInfo method)
        {
            if (method.Name != "RenderLines" || method.ReturnType != typeof(void)) return false;
            var parameters = method.GetParameters();
            return parameters.Length == 7 &&
                   parameters[0].ParameterType == typeof(byte[]) &&
                   parameters.Skip(1).Take(4).All(parameter => parameter.ParameterType == typeof(int)) &&
                   parameters[5].ParameterType == typeof(int).MakeByRefType() &&
                   parameters[6].ParameterType == typeof(int).MakeByRefType();
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
        }
    }

    internal static class RenderLinesLoopPatch
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var candidates = new List<int>();

            for (var i = 5; i + 2 < code.Count; i++)
            {
                if (LoadsInt(code[i], 128) &&
                    code[i + 1].opcode == OpCodes.Ble &&
                    code[i + 2].opcode == OpCodes.Ret &&
                    code[i - 5].opcode == OpCodes.Ldloc_2 &&
                    code[i - 4].opcode == OpCodes.Ldc_I4_1 &&
                    code[i - 3].opcode == OpCodes.Add &&
                    code[i - 2].opcode == OpCodes.Stloc_2 &&
                    code[i - 1].opcode == OpCodes.Ldloc_2)
                {
                    candidates.Add(i);
                }
            }

            if (candidates.Count != 1)
                throw new InvalidOperationException("Unexpected RenderLines v0.230 terminal loop shape: found " +
                                                    candidates.Count + " exact <= 128 bounds.");

            var boundIndex = candidates[0];
            var branchTarget = code[boundIndex + 1].operand;
            if (!(branchTarget is Label) ||
                code.Count(instruction => instruction.labels.Contains((Label)branchTarget)) != 1)
                throw new InvalidOperationException("RenderLines terminal BLE does not have one valid loop-head target.");

            var oldBound = code[boundIndex];
            if (oldBound.labels.Count != 0 || oldBound.blocks.Count != 0)
                throw new InvalidOperationException("RenderLines terminal bound contains an unexpected label or exception boundary.");

            code[boundIndex] = new CodeInstruction(OpCodes.Ldc_I4, 127);
            TransformCount++;
            return code;
        }

        internal static bool LoadsInt(CodeInstruction instruction, int value)
        {
            if (instruction.opcode == OpCodes.Ldc_I4) return instruction.operand is int i && i == value;
            if (instruction.opcode == OpCodes.Ldc_I4_S) return Convert.ToInt32(instruction.operand) == value;
            if (value == -1) return instruction.opcode == OpCodes.Ldc_I4_M1;
            if (value >= 0 && value <= 8)
                return instruction.opcode == new[]
                {
                    OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_2,
                    OpCodes.Ldc_I4_3, OpCodes.Ldc_I4_4, OpCodes.Ldc_I4_5,
                    OpCodes.Ldc_I4_6, OpCodes.Ldc_I4_7, OpCodes.Ldc_I4_8
                }[value];
            return false;
        }
    }
}
