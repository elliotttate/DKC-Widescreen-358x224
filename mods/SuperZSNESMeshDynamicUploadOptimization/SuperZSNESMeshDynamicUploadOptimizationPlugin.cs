using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESMeshDynamicUploadOptimization
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESMeshDynamicUploadOptimizationPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.meshdynamicuploadoptimization";
        public const string PluginName = "SuperZSNES Mesh Dynamic Upload Optimization";
        public const string PluginVersion = "0.1.0";

        private Harmony _harmony;

        private void Awake()
        {
            var enabled = Config.Bind("Optimization", "Enabled", false,
                "Call Mesh.MarkDynamic before the first GenerateNewMesh data upload. False applies no patch.");
            if (!enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion + " is disabled; no Harmony patch was applied.");
                return;
            }

            var renderer = AccessTools.TypeByName("PPURenderer");
            var target = renderer == null
                ? null
                : renderer.GetMethods(AccessTools.all).SingleOrDefault(method =>
                    method.Name == "GenerateNewMesh" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));
            if (target == null)
                throw new MissingMethodException("SuperZSNES v0.230 PPURenderer.GenerateNewMesh(int) was not found.");

            GenerateNewMeshPatch.TransformCount = 0;
            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(target, transpiler: new HarmonyMethod(
                AccessTools.Method(typeof(GenerateNewMeshPatch), nameof(GenerateNewMeshPatch.Transpiler))));
            if (GenerateNewMeshPatch.TransformCount != 1)
                throw new InvalidOperationException("Expected exactly one GenerateNewMesh initialization rewrite, got " +
                                                    GenerateNewMeshPatch.TransformCount + ".");

            Logger.LogInfo("Moved Mesh.MarkDynamic before the initial vertex upload in memory; on-disk Assembly-CSharp.dll is unchanged.");
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
        }
    }

    internal static class GenerateNewMeshPatch
    {
        internal static int TransformCount;

        // This helper consumes exactly the same (Mesh, Vector3[]) stack values as
        // Mesh.vertices.set, while applying the existing dynamic-buffer hint first.
        public static void SetInitialVerticesAfterMarkDynamic(Mesh mesh, Vector3[] vertices)
        {
            mesh.MarkDynamic();
            mesh.vertices = vertices;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var verticesSetter = AccessTools.PropertySetter(typeof(Mesh), nameof(Mesh.vertices));
            var markDynamic = AccessTools.Method(typeof(Mesh), nameof(Mesh.MarkDynamic), Type.EmptyTypes);
            var replacement = AccessTools.Method(typeof(GenerateNewMeshPatch), nameof(SetInitialVerticesAfterMarkDynamic));
            if (verticesSetter == null || markDynamic == null || replacement == null)
                throw new MissingMethodException("Unity 6000.3 mesh initialization methods were not found.");

            var vertexCalls = code.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.Calls(verticesSetter)).Select(item => item.index).ToList();
            var dynamicCalls = code.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.Calls(markDynamic)).Select(item => item.index).ToList();
            if (vertexCalls.Count != 1 || dynamicCalls.Count != 1 || vertexCalls[0] >= dynamicCalls[0])
                throw new InvalidOperationException("Unexpected GenerateNewMesh IL shape: vertices setters=" +
                    vertexCalls.Count + ", MarkDynamic calls=" + dynamicCalls.Count + ".");

            var vertexIndex = vertexCalls[0];
            var dynamicIndex = dynamicCalls[0];
            if (code[vertexIndex].labels.Count != 0 || code[vertexIndex].blocks.Count != 0 ||
                code[dynamicIndex].labels.Count != 0 || code[dynamicIndex].blocks.Count != 0)
                throw new InvalidOperationException("Mesh initialization calls contain an unexpected branch target or exception boundary.");

            code[vertexIndex] = new CodeInstruction(OpCodes.Call, replacement);
            // The original receiver load remains; pop consumes it where the late
            // MarkDynamic call used to be, preserving labels and instruction count.
            code[dynamicIndex] = new CodeInstruction(OpCodes.Pop);
            TransformCount++;
            return code;
        }
    }
}
