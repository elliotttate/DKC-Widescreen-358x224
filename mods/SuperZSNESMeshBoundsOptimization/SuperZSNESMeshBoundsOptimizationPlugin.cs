using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace SuperZSNESMeshBoundsOptimization
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESMeshBoundsOptimizationPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.meshboundsoptimization";
        public const string PluginName = "SuperZSNES Mesh Bounds Optimization";
        public const string PluginVersion = "0.2.0";

        private Harmony _harmony;

        private void Awake()
        {
            var enabled = Config.Bind("Optimization", "Enabled", false,
                "Use fixed conservative local bounds for 2D tile meshes and skip per-mesh RecalculateBounds. False applies no patch.");
            var halfExtent = Config.Bind("Optimization", "BoundsHalfExtent", 2048f,
                new ConfigDescription(
                    "Half-extent of the fixed local-space cube. 2048 is intentionally far larger than the roughly +/-24 x/y and 0..13 z used by a 7-tile widescreen frame.",
                    new AcceptableValueRange<float>(64f, 32768f)));
            var batchMeshNotifications = Config.Bind("Optimization", "BatchMeshNotifications", false,
                "Upload vertices and UVs with DontNotifyMeshUsers, then notify once with MarkModified. Experimental and disabled by default.");
            if (!enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion + " is disabled; no Harmony patch was applied.");
                return;
            }

            var renderer = AccessTools.TypeByName("PPURenderer");
            var target = renderer == null
                ? null
                : renderer.GetMethods(AccessTools.all)
                    .SingleOrDefault(method => method.Name == "Process2DTiles" && method.GetParameters().Length == 12);
            if (target == null)
                throw new MissingMethodException("SuperZSNES v0.230 PPURenderer.Process2DTiles was not found.");

            Process2DTilesPatch.Configure(halfExtent.Value, batchMeshNotifications.Value);
            Process2DTilesPatch.TransformCount = 0;
            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(target, transpiler: new HarmonyMethod(
                AccessTools.Method(typeof(Process2DTilesPatch), nameof(Process2DTilesPatch.Transpiler))));
            if (Process2DTilesPatch.TransformCount != 1)
                throw new InvalidOperationException("Expected exactly one Process2DTiles mesh upload rewrite, got " +
                                                    Process2DTilesPatch.TransformCount + ".");

            Logger.LogInfo("Applied in-memory 2D mesh bounds optimization. Half-extent=" + halfExtent.Value +
                           ", batched notifications=" + batchMeshNotifications.Value +
                           "; on-disk Assembly-CSharp.dll is unchanged.");
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
        }
    }

    internal static class Process2DTilesPatch
    {
        internal static int TransformCount;
        private static Bounds _fixedBounds = new Bounds(Vector3.zero, Vector3.one * 4096f);
        private static bool _batchMeshNotifications;

        internal static void Configure(float halfExtent, bool batchMeshNotifications)
        {
            var safe = Mathf.Clamp(halfExtent, 64f, 32768f);
            _fixedBounds = new Bounds(Vector3.zero, Vector3.one * (safe * 2f));
            _batchMeshNotifications = batchMeshNotifications;
        }

        public static void UploadVerticesWithFixedBounds(Mesh mesh, Vector3[] vertices)
        {
            var flags = MeshUpdateFlags.DontRecalculateBounds;
            if (_batchMeshNotifications)
                flags |= MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
            mesh.SetVertices(vertices, 0, vertices.Length, flags);
            if (!_batchMeshNotifications) mesh.bounds = _fixedBounds;
        }

        public static void UploadUvsAndNotify(Mesh mesh, int channel, Vector2[] uvs)
        {
            mesh.SetUVs(channel, uvs, 0, uvs.Length,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices |
                MeshUpdateFlags.DontNotifyMeshUsers);
            mesh.bounds = _fixedBounds;
            mesh.MarkModified();
        }

        public static bool ContainsCore(float x, float y, float z, float halfExtent)
        {
            return Math.Abs(x) <= halfExtent && Math.Abs(y) <= halfExtent && Math.Abs(z) <= halfExtent;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var setVertices = AccessTools.Method(typeof(Mesh), nameof(Mesh.SetVertices), new[] { typeof(Vector3[]) });
            var setUvs = AccessTools.Method(typeof(Mesh), nameof(Mesh.SetUVs), new[] { typeof(int), typeof(Vector2[]) });
            var recalculate = AccessTools.Method(typeof(Mesh), nameof(Mesh.RecalculateBounds), Type.EmptyTypes);
            var replacement = AccessTools.Method(typeof(Process2DTilesPatch), nameof(UploadVerticesWithFixedBounds));
            var uvReplacement = AccessTools.Method(typeof(Process2DTilesPatch), nameof(UploadUvsAndNotify));
            if (setVertices == null || setUvs == null || recalculate == null || replacement == null || uvReplacement == null)
                throw new MissingMethodException("Unity 6000.3 mesh upload methods were not found.");

            var vertexCalls = code.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.Calls(setVertices)).Select(item => item.index).ToList();
            var uvCalls = code.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.Calls(setUvs)).Select(item => item.index).ToList();
            var boundsCalls = code.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.Calls(recalculate)).Select(item => item.index).ToList();
            if (vertexCalls.Count != 1 || uvCalls.Count != 1 || boundsCalls.Count != 1 ||
                !(vertexCalls[0] < uvCalls[0] && uvCalls[0] < boundsCalls[0]))
                throw new InvalidOperationException("Unexpected Process2DTiles mesh IL shape: SetVertices=" +
                    vertexCalls.Count + ", SetUVs=" + uvCalls.Count + ", RecalculateBounds=" + boundsCalls.Count + ".");

            var vertexIndex = vertexCalls[0];
            var boundsIndex = boundsCalls[0];
            if (code[vertexIndex].labels.Count != 0 || code[vertexIndex].blocks.Count != 0 ||
                code[boundsIndex].labels.Count != 0 || code[boundsIndex].blocks.Count != 0)
                throw new InvalidOperationException("Mesh calls contain an unexpected branch target or exception boundary.");

            // The helper has the same (Mesh, Vector3[]) stack inputs as the instance
            // call. Replacing RecalculateBounds with pop consumes its already-loaded
            // Mesh receiver while preserving instruction labels/count and all UV work.
            code[vertexIndex] = new CodeInstruction(OpCodes.Call, replacement);
            if (_batchMeshNotifications)
                code[uvCalls[0]] = new CodeInstruction(OpCodes.Call, uvReplacement);
            code[boundsIndex] = new CodeInstruction(OpCodes.Pop);
            TransformCount++;
            return code;
        }
    }
}
