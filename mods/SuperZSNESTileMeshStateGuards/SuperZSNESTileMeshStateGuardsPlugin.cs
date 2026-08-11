using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SuperZSNESTileMeshStateGuards
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESTileMeshStateGuardsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.tilemeshstateguards";
        public const string PluginName = "SuperZSNES Tile Mesh State Guards";
        public const string PluginVersion = "0.2.0";
        private Harmony _harmony;

        private void Awake()
        {
            var enabled = Config.Bind("Optimization", "Enabled", false,
                "Skip redundant Unity state setters in PPURenderer.Process2DTiles. False applies no patch.");
            var useSharedMeshSetter = Config.Bind("Optimization", "UseSharedMeshSetter", false,
                "Assign the existing pooled Mesh through MeshFilter.sharedMesh instead of MeshFilter.mesh. Independent of the rejected equality guards.");
            if (!enabled.Value && !useSharedMeshSetter.Value)
            {
                Logger.LogInfo(PluginName + " is disabled; no method was patched.");
                return;
            }

            var renderer = AccessTools.TypeByName("PPURenderer");
            var target = renderer == null ? null : renderer.GetMethods(AccessTools.all)
                .SingleOrDefault(method => method.Name == "Process2DTiles" && method.GetParameters().Length == 12);
            if (target == null)
                throw new MissingMethodException("SuperZSNES v0.230 PPURenderer.Process2DTiles was not found.");

            _harmony = new Harmony(PluginGuid);
            if (enabled.Value)
            {
                Process2DTilesStatePatch.ResetCounts();
                _harmony.Patch(target, transpiler: new HarmonyMethod(
                    AccessTools.Method(typeof(Process2DTilesStatePatch), nameof(Process2DTilesStatePatch.Transpiler))));
                Process2DTilesStatePatch.RequireExactCounts();
            }
            if (useSharedMeshSetter.Value)
            {
                SharedMeshSetterPatch.TransformCount = 0;
                _harmony.Patch(target, transpiler: new HarmonyMethod(
                    AccessTools.Method(typeof(SharedMeshSetterPatch), nameof(SharedMeshSetterPatch.Transpiler))));
                if (SharedMeshSetterPatch.TransformCount != 1)
                    throw new InvalidOperationException("Expected one MeshFilter.mesh -> sharedMesh rewrite, got " +
                                                        SharedMeshSetterPatch.TransformCount + ".");
            }
            Logger.LogInfo("Applied tile-mesh options: equality guards=" + enabled.Value +
                           ", sharedMesh setter=" + useSharedMeshSetter.Value +
                           "; Assembly-CSharp.dll is unchanged.");
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
        }
    }

    internal static class SharedMeshSetterPatch
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = input.Select(instruction => new CodeInstruction(instruction)).ToList();
            var meshSetter = AccessTools.PropertySetter(typeof(MeshFilter), nameof(MeshFilter.mesh));
            var sharedSetter = AccessTools.PropertySetter(typeof(MeshFilter), nameof(MeshFilter.sharedMesh));
            if (meshSetter == null || sharedSetter == null)
                throw new MissingMethodException("MeshFilter mesh setters were not found.");
            foreach (var instruction in code)
            {
                if (!instruction.Calls(meshSetter)) continue;
                instruction.operand = sharedSetter;
                TransformCount++;
            }
            return code;
        }
    }

    internal static class Process2DTilesStatePatch
    {
        private static int _active, _position, _scale, _layer, _material, _mesh;

        internal static void ResetCounts() => _active = _position = _scale = _layer = _material = _mesh = 0;

        internal static void RequireExactCounts()
        {
            if (_active != 1 || _position != 1 || _scale != 1 || _layer != 1 || _material != 1 || _mesh != 1)
                throw new InvalidOperationException("Unexpected Process2DTiles state-setter shape: active=" + _active +
                    ", position=" + _position + ", scale=" + _scale + ", layer=" + _layer +
                    ", material=" + _material + ", mesh=" + _mesh + ".");
        }

        public static void SetActiveIfChanged(GameObject gameObject, bool value)
        {
            if (gameObject.activeSelf != value) gameObject.SetActive(value);
        }

        public static void SetPositionIfChanged(Transform transform, Vector3 value)
        {
            if (transform.position != value) transform.position = value;
        }

        public static void SetScaleIfChanged(Transform transform, Vector3 value)
        {
            if (transform.localScale != value) transform.localScale = value;
        }

        public static void SetLayerIfChanged(GameObject gameObject, int value)
        {
            if (gameObject.layer != value) gameObject.layer = value;
        }

        public static void SetMaterialIfChanged(Renderer renderer, Material value)
        {
            if (!ReferenceEquals(renderer.sharedMaterial, value)) renderer.sharedMaterial = value;
        }

        public static void SetMeshIfChanged(MeshFilter filter, Mesh value)
        {
            if (!ReferenceEquals(filter.sharedMesh, value)) filter.mesh = value;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            Replace(code, AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive), new[] { typeof(bool) }),
                AccessTools.Method(typeof(Process2DTilesStatePatch), nameof(SetActiveIfChanged)), ref _active);
            Replace(code, AccessTools.PropertySetter(typeof(Transform), nameof(Transform.position)),
                AccessTools.Method(typeof(Process2DTilesStatePatch), nameof(SetPositionIfChanged)), ref _position);
            Replace(code, AccessTools.PropertySetter(typeof(Transform), nameof(Transform.localScale)),
                AccessTools.Method(typeof(Process2DTilesStatePatch), nameof(SetScaleIfChanged)), ref _scale);
            Replace(code, AccessTools.PropertySetter(typeof(GameObject), nameof(GameObject.layer)),
                AccessTools.Method(typeof(Process2DTilesStatePatch), nameof(SetLayerIfChanged)), ref _layer);
            Replace(code, AccessTools.PropertySetter(typeof(Renderer), nameof(Renderer.sharedMaterial)),
                AccessTools.Method(typeof(Process2DTilesStatePatch), nameof(SetMaterialIfChanged)), ref _material);
            Replace(code, AccessTools.PropertySetter(typeof(MeshFilter), nameof(MeshFilter.mesh)),
                AccessTools.Method(typeof(Process2DTilesStatePatch), nameof(SetMeshIfChanged)), ref _mesh);
            return code;
        }

        private static void Replace(List<CodeInstruction> code, System.Reflection.MethodInfo original,
            System.Reflection.MethodInfo replacement, ref int count)
        {
            if (original == null || replacement == null) throw new MissingMethodException("Unity state setter was not found.");
            foreach (var instruction in code)
            {
                if (!instruction.Calls(original)) continue;
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = replacement;
                count++;
            }
        }
    }
}
