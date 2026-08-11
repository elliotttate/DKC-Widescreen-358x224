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
using UnityEngine;

namespace SuperZSNESVariableMaterialBatching
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESVariableMaterialBatchingPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.variablematerialbatching";
        public const string PluginName = "SuperZSNES Variable Material Batching";
        public const string PluginVersion = "0.1.1";

        private Harmony _harmony;
        private bool _active;
        private int _lastStatusFrame;

        private void Awake()
        {
            var enabled = Config.Bind("Prototype", "Enabled", false,
                "QUARANTINED: the v0.1.0 batching experiment failed live visual QA. This setting is retained only to fail closed with an explicit status; no batching patch is applied.");
            var maxQuads = Config.Bind("Prototype", "MaximumCombinedQuads", 4095,
                new ConfigDescription("Lists above this size retain stock 256/64/16/4/1 batching. 4095 keeps 4*quads below the UInt16 index limit.",
                    new AcceptableValueRange<int>(1, 4095)));
            var maxQueue = Config.Bind("Prototype", "MaximumOpaqueRenderQueue", 2500,
                new ConfigDescription("Only RenderType=Opaque materials at or below this queue are combined; later transparent queues retain stock renderer boundaries.",
                    new AcceptableValueRange<int>(2000, 2500)));

            if (!enabled.Value)
            {
                Logger.LogInfo(PluginName + " " + PluginVersion + " is disabled; no Harmony patch was applied.");
                WriteStatus("disabled", null);
                return;
            }

            const string quarantine = "Variable material batching is quarantined after a reproducible live visual failure (tile geometry disappeared). RenderType/queue eligibility is insufficient to prove renderer-boundary equivalence. No Harmony patch was applied.";
            Logger.LogError(quarantine);
            WriteStatus("quarantined-visual-failure", quarantine);
            return;

#pragma warning disable CS0162
            try
            {
                var layout = VariableBatchLayout.ResolveAndVerify();
                VariableBatchTranspilers.VerifyExact(layout);
                VariableBatchRuntime.Configure(maxQuads.Value, maxQueue.Value, Logger);
                VariableBatchTranspilers.Configure(layout, Logger);

                _harmony = new Harmony(PluginGuid);
                var selectPatch = new HarmonyMethod(AccessTools.Method(
                    typeof(VariableBatchTranspilers), nameof(VariableBatchTranspilers.GenerateBackgroundTranspiler)));
                var shapePatch = new HarmonyMethod(AccessTools.Method(
                    typeof(VariableBatchTranspilers), nameof(VariableBatchTranspilers.MeshProcessTranspiler)));
                _harmony.Patch(layout.GenerateBackground, transpiler: selectPatch);
                _harmony.Patch(layout.Process2DTiles, transpiler: shapePatch);
                _harmony.Patch(layout.ProcessTiles, transpiler: shapePatch);

                if (VariableBatchTranspilers.GenerateBackgroundTransformCount != 1 ||
                    VariableBatchTranspilers.MeshProcessTransformCount != 2)
                    throw new InvalidOperationException("Runtime Harmony chain did not retain the verified shape: selector=" +
                        VariableBatchTranspilers.GenerateBackgroundTransformCount + "/1, shape=" +
                        VariableBatchTranspilers.MeshProcessTransformCount + "/2.");

                _active = true;
                _lastStatusFrame = Time.frameCount;
                WriteStatus("active", null);
                Logger.LogWarning("Experimental variable material batching is active. Validate pixel output before using timing results.");
            }
            catch (Exception exception)
            {
                try { _harmony?.UnpatchSelf(); } catch { }
                _active = false;
                Logger.LogError("Variable material batching failed closed; no prototype patch remains active: " + exception);
                WriteStatus("failed-closed", exception.Message);
            }
#pragma warning restore CS0162
        }

        private void Update()
        {
            if (!_active || Time.frameCount - _lastStatusFrame < 300) return;
            _lastStatusFrame = Time.frameCount;
            WriteStatus("active", null);
        }

        private void OnDestroy()
        {
            if (_active) WriteStatus("stopped", null);
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private void WriteStatus(string state, string error)
        {
            try
            {
                var directory = Path.Combine(Paths.PluginPath, "SuperZSNESVariableMaterialBatching");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "status.json"), VariableBatchRuntime.StatusJson(state, error));
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Could not write variable-batching status: " + exception.Message);
            }
        }
    }

    internal sealed class VariableBatchLayout
    {
        internal MethodInfo GenerateBackground;
        internal MethodInfo Process2DTiles;
        internal MethodInfo ProcessTiles;
        internal MethodInfo GenerateNewMesh;
        internal FieldInfo MaterialTupleItem1;

        internal static VariableBatchLayout ResolveAndVerify()
        {
            var renderer = AccessTools.TypeByName("PPURenderer") ?? throw new TypeLoadException("PPURenderer was not found.");
            var result = new VariableBatchLayout
            {
                GenerateBackground = AccessTools.Method(renderer, "GenerateBackground"),
                Process2DTiles = renderer.GetMethods(AccessTools.all).SingleOrDefault(method =>
                    method.Name == "Process2DTiles" && method.GetParameters().Length == 12),
                ProcessTiles = renderer.GetMethods(AccessTools.all).SingleOrDefault(method =>
                    method.Name == "ProcessTiles" && method.GetParameters().Length == 10),
                GenerateNewMesh = AccessTools.Method(renderer, "GenerateNewMesh", new[] { typeof(int) })
            };
            if (result.GenerateBackground == null || result.Process2DTiles == null ||
                result.ProcessTiles == null || result.GenerateNewMesh == null)
                throw new MissingMethodException("Required PPURenderer v0.230 methods were not found.");

            var tupleType = result.Process2DTiles.GetParameters()[1].ParameterType;
            result.MaterialTupleItem1 = AccessTools.Field(tupleType, "Item1");
            if (result.MaterialTupleItem1 == null || result.MaterialTupleItem1.FieldType != typeof(Material))
                throw new MissingFieldException("Process2DTiles material tuple Item1 was not found.");
            if (result.ProcessTiles.GetParameters()[2].ParameterType != typeof(int) ||
                result.Process2DTiles.GetParameters()[2].ParameterType != typeof(int))
                throw new InvalidOperationException("Mesh processor numTiles ABI does not match v0.230.");
            return result;
        }
    }

    internal static class VariableBatchRuntime
    {
        private static readonly Dictionary<int, Stack<Vector3[]>> Vector3Pool = new Dictionary<int, Stack<Vector3[]>>();
        private static readonly Dictionary<int, Stack<Vector2[]>> Vector2Pool = new Dictionary<int, Stack<Vector2[]>>();
        private static readonly Dictionary<int, Stack<int[]>> IntPool = new Dictionary<int, Stack<int[]>>();
        private static int _maxQuads = 4095;
        private static int _maxQueue = 2500;
        private static ManualLogSource _log;

        internal static long ListsSeen;
        internal static long ListsCombined;
        internal static long EmptyLists;
        internal static long StockProjectedMeshes;
        internal static long SelectedProjectedMeshes;
        internal static long ShapeChanges;
        internal static long ShapeFailures;

        internal static void Configure(int maxQuads, int maxQueue, ManualLogSource log)
        {
            _maxQuads = Math.Max(1, Math.Min(4095, maxQuads));
            _maxQueue = Math.Max(2000, Math.Min(2500, maxQueue));
            _log = log;
            ListsSeen = ListsCombined = EmptyLists = StockProjectedMeshes = SelectedProjectedMeshes = 0;
            ShapeChanges = ShapeFailures = 0;
            Vector3Pool.Clear();
            Vector2Pool.Clear();
            IntPool.Clear();
        }

        public static int SelectFirstBatchSize(Material material, int count)
        {
            ListsSeen++;
            var stockMeshes = StockMeshCount(count);
            StockProjectedMeshes += stockMeshes;
            if (count <= 0)
            {
                EmptyLists++;
                return 1;
            }

            var combine = count <= _maxQuads && material != null;
            if (combine)
            {
                try
                {
                    combine = material.renderQueue <= _maxQueue &&
                              string.Equals(material.GetTag("RenderType", false, string.Empty), "Opaque",
                                  StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception exception)
                {
                    combine = false;
                    _log?.LogWarning("Material eligibility check failed; stock batching retained: " + exception.Message);
                }
            }

            if (combine)
            {
                ListsCombined++;
                SelectedProjectedMeshes++;
                return count;
            }

            SelectedProjectedMeshes += stockMeshes;
            return 256;
        }

        public static void EnsureMeshShape(List<Mesh> meshes, List<Vector3[]> positions,
            List<Vector2[]> dynamicUvs, int index, int numTiles)
        {
            if (numTiles <= 0 || index < 0 || index >= meshes.Count ||
                index >= positions.Count || index >= dynamicUvs.Count)
                throw new InvalidOperationException("Variable mesh shape arguments are outside the verified range.");
            var length = checked(numTiles * 4);
            var oldPositions = positions[index];
            var oldUvs = dynamicUvs[index];
            if (oldPositions != null && oldUvs != null &&
                oldPositions.Length == length && oldUvs.Length == length)
                return;

            var newPositions = Rent(Vector3Pool, length);
            var newUvs = Rent(Vector2Pool, length);
            var triangles = Rent(IntPool, checked(numTiles * 6));
            var baseUvs = Rent(Vector2Pool, length);
            var normals = Rent(Vector3Pool, length);
            try
            {
                FillTopology(numTiles, triangles, baseUvs, normals);
                var mesh = meshes[index];
                mesh.Clear(false);
                mesh.vertices = newPositions;
                mesh.triangles = triangles;
                mesh.uv = baseUvs;
                mesh.normals = normals;
                mesh.RecalculateTangents();
                positions[index] = newPositions;
                dynamicUvs[index] = newUvs;
                Return(Vector3Pool, oldPositions);
                Return(Vector2Pool, oldUvs);
                newPositions = null;
                newUvs = null;
                ShapeChanges++;
            }
            catch
            {
                ShapeFailures++;
                throw;
            }
            finally
            {
                Return(IntPool, triangles);
                Return(Vector2Pool, baseUvs);
                Return(Vector3Pool, normals);
                Return(Vector3Pool, newPositions);
                Return(Vector2Pool, newUvs);
            }
        }

        internal static int StockMeshCount(int count)
        {
            if (count <= 0) return 0;
            var result = 0;
            var remaining = count;
            foreach (var size in new[] { 256, 64, 16, 4, 1 })
            {
                result += remaining / size;
                remaining %= size;
            }
            return result;
        }

        private static void FillTopology(int numTiles, int[] triangles, Vector2[] baseUvs, Vector3[] normals)
        {
            for (var index = 0; index < numTiles; index++)
            {
                var vertex = index * 4;
                var triangle = index * 6;
                triangles[triangle] = vertex;
                triangles[triangle + 1] = vertex + 3;
                triangles[triangle + 2] = vertex + 1;
                triangles[triangle + 3] = vertex + 3;
                triangles[triangle + 4] = vertex;
                triangles[triangle + 5] = vertex + 2;
                baseUvs[vertex] = new Vector2(0f, 0f);
                baseUvs[vertex + 1] = new Vector2(1f, 0f);
                baseUvs[vertex + 2] = new Vector2(0f, 1f);
                baseUvs[vertex + 3] = new Vector2(1f, 1f);
                normals[vertex] = normals[vertex + 1] = normals[vertex + 2] = normals[vertex + 3] =
                    new Vector3(0f, 0f, -1f);
            }
        }

        private static T[] Rent<T>(Dictionary<int, Stack<T[]>> pool, int length)
        {
            Stack<T[]> stack;
            return pool.TryGetValue(length, out stack) && stack.Count > 0 ? stack.Pop() : new T[length];
        }

        private static void Return<T>(Dictionary<int, Stack<T[]>> pool, T[] array)
        {
            if (array == null) return;
            Stack<T[]> stack;
            if (!pool.TryGetValue(array.Length, out stack))
                pool.Add(array.Length, stack = new Stack<T[]>());
            stack.Push(array);
        }

        internal static string StatusJson(string state, string error)
        {
            return "{\"pluginVersion\":\"" + SuperZSNESVariableMaterialBatchingPlugin.PluginVersion +
                   "\",\"state\":\"" + Escape(state) + "\",\"maximumCombinedQuads\":" + _maxQuads +
                   ",\"maximumOpaqueRenderQueue\":" + _maxQueue + ",\"listsSeen\":" + ListsSeen +
                   ",\"listsCombined\":" + ListsCombined + ",\"emptyLists\":" + EmptyLists +
                   ",\"stockProjectedMeshes\":" + StockProjectedMeshes +
                   ",\"selectedProjectedMeshes\":" + SelectedProjectedMeshes +
                   ",\"shapeChanges\":" + ShapeChanges + ",\"shapeFailures\":" + ShapeFailures +
                   ",\"error\":\"" + Escape(error ?? string.Empty) + "\"}";
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    internal static class VariableBatchTranspilers
    {
        private sealed class MeshMethodShape
        {
            internal int MeshIndexArgument;
            internal int MeshListArgument;
            internal int PositionListArgument;
            internal int UvListArgument;
        }

        private static VariableBatchLayout _layout;
        private static ManualLogSource _log;
        internal static int GenerateBackgroundTransformCount;
        internal static int MeshProcessTransformCount;

        internal static void Configure(VariableBatchLayout layout, ManualLogSource log)
        {
            _layout = layout;
            _log = log;
            GenerateBackgroundTransformCount = 0;
            MeshProcessTransformCount = 0;
        }

        internal static void VerifyExact(VariableBatchLayout layout)
        {
            AnalyzeGenerateBackground(PatchProcessor.GetOriginalInstructions(layout.GenerateBackground), layout);
            AnalyzeMeshMethod(PatchProcessor.GetOriginalInstructions(layout.Process2DTiles), layout, layout.Process2DTiles);
            AnalyzeMeshMethod(PatchProcessor.GetOriginalInstructions(layout.ProcessTiles), layout, layout.ProcessTiles);
        }

        public static IEnumerable<CodeInstruction> GenerateBackgroundTranspiler(IEnumerable<CodeInstruction> input)
        {
            var original = input.Select(instruction => new CodeInstruction(instruction)).ToList();
            var code = original.Select(instruction => new CodeInstruction(instruction)).ToList();
            try
            {
                var match = AnalyzeGenerateBackground(code, _layout);
                var select = AccessTools.Method(typeof(VariableBatchRuntime), nameof(VariableBatchRuntime.SelectFirstBatchSize));
                var materialLoad = new CodeInstruction(code[match.Start + 2]);
                materialLoad.labels.Clear();
                materialLoad.blocks.Clear();
                var remainderLoad = new CodeInstruction(code[match.Start + 7]) { opcode = OpCodes.Ldloc };
                remainderLoad.labels.Clear();
                remainderLoad.blocks.Clear();
                var replacement = new List<CodeInstruction>
                {
                    materialLoad,
                    new CodeInstruction(OpCodes.Ldfld, _layout.MaterialTupleItem1),
                    remainderLoad,
                    new CodeInstruction(OpCodes.Call, select)
                };
                MoveMetadata(code[match.Start + 3], replacement[0]);
                code.RemoveAt(match.Start + 3);
                code.InsertRange(match.Start + 3, replacement);
                GenerateBackgroundTransformCount++;
                return code;
            }
            catch (Exception exception)
            {
                _log?.LogError("Variable-batch GenerateBackground transpiler rejected runtime IL: " + exception.Message);
                GenerateBackgroundTransformCount = 0;
                return original;
            }
        }

        public static IEnumerable<CodeInstruction> MeshProcessTranspiler(
            IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            var original = input.Select(instruction => new CodeInstruction(instruction)).ToList();
            var code = original.Select(instruction => new CodeInstruction(instruction)).ToList();
            try
            {
                var shape = AnalyzeMeshMethod(code, _layout, __originalMethod);
                var insertionIndex = FindMeshRetrievalStart(code, _layout.GenerateNewMesh);
                var helper = AccessTools.Method(typeof(VariableBatchRuntime), nameof(VariableBatchRuntime.EnsureMeshShape));
                var insertion = new List<CodeInstruction>
                {
                    LoadArgument(shape.MeshListArgument),
                    LoadArgument(shape.PositionListArgument),
                    LoadArgument(shape.UvListArgument),
                    LoadArgument(shape.MeshIndexArgument),
                    new CodeInstruction(OpCodes.Ldind_I4),
                    LoadArgument(3),
                    new CodeInstruction(OpCodes.Call, helper)
                };
                MoveMetadata(code[insertionIndex], insertion[0]);
                code.InsertRange(insertionIndex, insertion);
                MeshProcessTransformCount++;
                return code;
            }
            catch (Exception exception)
            {
                _log?.LogError("Variable-batch mesh-shape transpiler rejected " + __originalMethod?.Name + ": " + exception.Message);
                return original;
            }
        }

        private sealed class GenerateMatch
        {
            internal int Start;
        }

        private static GenerateMatch AnalyzeGenerateBackground(IReadOnlyList<CodeInstruction> code,
            VariableBatchLayout layout)
        {
            if (layout == null) throw new InvalidOperationException("Variable-batch layout was not configured.");
            var calls = new List<int>();
            for (var index = 0; index < code.Count; index++)
                if (code[index].Calls(layout.Process2DTiles)) calls.Add(index);
            if (calls.Count != 5) throw new InvalidOperationException("GenerateBackground Process2DTiles call count was " + calls.Count + ", expected 5.");
            var expected = new[] { 256, 64, 16, 4, 1 };
            for (var index = 0; index < calls.Count; index++)
            {
                var start = calls[index] - 17;
                if (start < 0 || !IsLdarg0(code[start]) || !IsLdloc(code[start + 1]) ||
                    !IsLdloc(code[start + 2]) || ReadI4(code[start + 3]) != expected[index] ||
                    !IsLdarg2(code[start + 4]) || !IsLdloca(code[start + 7]))
                    throw new InvalidOperationException("Process2DTiles call " + index + " does not match exact v0.230 argument IL.");
            }
            return new GenerateMatch { Start = calls[0] - 17 };
        }

        private static MeshMethodShape AnalyzeMeshMethod(IReadOnlyList<CodeInstruction> code,
            VariableBatchLayout layout, MethodBase method)
        {
            if (layout == null || method == null) throw new InvalidOperationException("Mesh method layout was not configured.");
            if (code.Count(instruction => instruction.Calls(layout.GenerateNewMesh)) != 1)
                throw new InvalidOperationException(method.Name + " must call GenerateNewMesh exactly once.");
            var result = Equals(method, layout.Process2DTiles)
                ? new MeshMethodShape { MeshIndexArgument = 8, MeshListArgument = 9, PositionListArgument = 10, UvListArgument = 11 }
                : Equals(method, layout.ProcessTiles)
                    ? new MeshMethodShape { MeshIndexArgument = 7, MeshListArgument = 8, PositionListArgument = 9, UvListArgument = 10 }
                    : throw new InvalidOperationException("Unexpected mesh processor " + method.Name + ".");
            var start = FindMeshRetrievalStart(code, layout.GenerateNewMesh);
            if (!LoadsArgument(code[start], result.MeshListArgument) ||
                !LoadsArgument(code[start + 1], result.MeshIndexArgument) || code[start + 2].opcode != OpCodes.Ldind_I4)
                throw new InvalidOperationException(method.Name + " mesh retrieval ABI does not match v0.230.");
            return result;
        }

        private static int FindMeshRetrievalStart(IReadOnlyList<CodeInstruction> code, MethodInfo generateNewMesh)
        {
            var generatedAt = -1;
            for (var index = 0; index < code.Count; index++)
                if (code[index].Calls(generateNewMesh)) { generatedAt = index; break; }
            for (var index = generatedAt + 1; index + 4 < code.Count; index++)
            {
                if (!IsLoadArgument(code[index]) || !IsLoadArgument(code[index + 1]) ||
                    code[index + 2].opcode != OpCodes.Ldind_I4 ||
                    !(code[index + 3].operand is MethodInfo item) || item.Name != "get_Item" ||
                    !IsStoreLocalZero(code[index + 4])) continue;
                return index;
            }
            throw new InvalidOperationException("Mesh retrieval after GenerateNewMesh was not found.");
        }

        private static CodeInstruction LoadArgument(int index)
        {
            if (index == 0) return new CodeInstruction(OpCodes.Ldarg_0);
            if (index == 1) return new CodeInstruction(OpCodes.Ldarg_1);
            if (index == 2) return new CodeInstruction(OpCodes.Ldarg_2);
            if (index == 3) return new CodeInstruction(OpCodes.Ldarg_3);
            return new CodeInstruction(OpCodes.Ldarg_S, (byte)index);
        }

        private static bool LoadsArgument(CodeInstruction instruction, int index)
        {
            if (index == 0) return instruction.opcode == OpCodes.Ldarg_0;
            if (index == 1) return instruction.opcode == OpCodes.Ldarg_1;
            if (index == 2) return instruction.opcode == OpCodes.Ldarg_2;
            if (index == 3) return instruction.opcode == OpCodes.Ldarg_3;
            return (instruction.opcode == OpCodes.Ldarg || instruction.opcode == OpCodes.Ldarg_S) &&
                   Convert.ToInt32(instruction.operand) == index;
        }

        private static bool IsLoadArgument(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Ldarg || instruction.opcode == OpCodes.Ldarg_S ||
                   instruction.opcode == OpCodes.Ldarg_0 || instruction.opcode == OpCodes.Ldarg_1 ||
                   instruction.opcode == OpCodes.Ldarg_2 || instruction.opcode == OpCodes.Ldarg_3;
        }

        private static bool IsStoreLocalZero(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Stloc_0) return true;
            return (instruction.opcode == OpCodes.Stloc || instruction.opcode == OpCodes.Stloc_S) &&
                   instruction.operand is LocalBuilder local && local.LocalIndex == 0;
        }

        private static void MoveMetadata(CodeInstruction from, CodeInstruction to)
        {
            to.labels.AddRange(from.labels);
            from.labels.Clear();
            to.blocks.AddRange(from.blocks);
            from.blocks.Clear();
        }

        private static int? ReadI4(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Ldc_I4_1) return 1;
            if (instruction.opcode == OpCodes.Ldc_I4_4) return 4;
            if (instruction.opcode == OpCodes.Ldc_I4_S) return (sbyte)instruction.operand;
            if (instruction.opcode == OpCodes.Ldc_I4) return (int)instruction.operand;
            return null;
        }

        private static bool IsLdarg0(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldarg_0;
        private static bool IsLdarg2(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldarg_2;
        private static bool IsLdloc(CodeInstruction instruction) => instruction.opcode.Name.StartsWith("ldloc") && !IsLdloca(instruction);
        private static bool IsLdloca(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S;
    }
}
