using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SuperZSNESMaterialCacheGuard
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESMaterialCacheGuardPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.materialcacheguard";
        public const string PluginName = "SuperZSNES Material Cache Guard";
        public const string PluginVersion = "0.2.0";

        internal static SuperZSNESMaterialCacheGuardPlugin Instance;

        private ConfigEntry<bool> _enableScratchListPool;
        private ConfigEntry<bool> _enableDiagnostics;
        private ConfigEntry<int> _diagnosticInterval;
        private Harmony _harmony;
        private StreamWriter _writer;
        private long _renderCalls;
        private bool _guardFaulted;
        private string _directory;

        private void Awake()
        {
            Instance = this;
            _enableScratchListPool = Config.Bind(
                "ScratchListPool", "EnablePerBackgroundScratchListPool", true,
                "Bound PPURenderer.tileAddrToMat to the current generated background and reuse cleared List<TileInfo> instances. No Unity asset is destroyed.");
            _enableDiagnostics = Config.Bind(
                "Diagnostics", "EnableDiagnostics", false,
                "Write low-frequency scratch-map/list-pool cardinality and process-memory samples after completed GenerateBackgrounds calls.");
            _diagnosticInterval = Config.Bind(
                "Diagnostics", "SampleIntervalRenderCalls", 300,
                new ConfigDescription("Composite-render calls between diagnostic samples.", new AcceptableValueRange<int>(60, 3600)));

            _directory = Path.Combine(Paths.PluginPath, "SuperZSNESMaterialCacheGuard");
            Directory.CreateDirectory(_directory);
            if (!_enableScratchListPool.Value && !_enableDiagnostics.Value)
            {
                WriteStatus("disabled", null);
                Logger.LogInfo(PluginName + " " + PluginVersion + " is disabled; no Harmony patch was applied.");
                return;
            }

            try
            {
                var layout = MaterialCacheLayout.ResolveAndVerify();
                MaterialCacheHooks.Layout = layout;
                if (_enableScratchListPool.Value) ScratchListPool.Initialize(layout.ScratchListType, layout.ScratchListConstructor);

                _harmony = new Harmony(PluginGuid);
                ProcessMaterialTranspiler.TransformCount = 0;
                if (_enableScratchListPool.Value)
                {
                    _harmony.Patch(
                        layout.GenerateBackground,
                        prefix: new HarmonyMethod(AccessTools.Method(
                            typeof(MaterialCacheHooks), nameof(MaterialCacheHooks.GenerateBackgroundPrefix))));
                    _harmony.Patch(
                        layout.ProcessMaterial,
                        transpiler: new HarmonyMethod(AccessTools.Method(
                            typeof(ProcessMaterialTranspiler), nameof(ProcessMaterialTranspiler.Transpiler))));
                    if (ProcessMaterialTranspiler.TransformCount != 1)
                        throw new InvalidOperationException("Expected one ProcessMaterial list-constructor replacement, got " +
                                                            ProcessMaterialTranspiler.TransformCount + ".");
                }

                if (_enableDiagnostics.Value)
                {
                    _harmony.Patch(
                        layout.GenerateBackgrounds,
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(MaterialCacheHooks), nameof(MaterialCacheHooks.GenerateBackgroundsPostfix))));
                    _writer = new StreamWriter(Path.Combine(_directory, "material-cache.jsonl"), true) { AutoFlush = true };
                }
                WriteStatus("attached", null);
                Logger.LogInfo(PluginName + " " + PluginVersion + " attached. Per-background scratch-list pool=" +
                               _enableScratchListPool.Value + ", diagnostics=" + _enableDiagnostics.Value +
                               ", ProcessMaterial transforms=" + ProcessMaterialTranspiler.TransformCount + ".");
            }
            catch (Exception ex)
            {
                _guardFaulted = true;
                ScratchListPool.DisableAndDiscard();
                try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
                WriteStatus("attach-failed", ex.Message);
                Logger.LogError("Material-cache guard failed closed; stock methods remain active: " + ex);
            }
        }

        internal void BeforeGenerateBackground(object renderer)
        {
            if (!_enableScratchListPool.Value || _guardFaulted) return;
            try
            {
                var scratch = MaterialCacheHooks.Layout.RendererScratchMap.GetValue(renderer) as IDictionary;
                if (scratch == null)
                    throw new InvalidOperationException("PPURenderer.tileAddrToMat no longer implements IDictionary.");
                ScratchListPool.HarvestAndClear(scratch);
            }
            catch (Exception ex)
            {
                _guardFaulted = true;
                ScratchListPool.DisableAndDiscard();
                WriteStatus("runtime-fault", ex.Message);
                Logger.LogError("Scratch-list pooling disabled after runtime verifier failure; stock rendering will continue: " + ex);
            }
        }

        internal void AfterGenerateBackgrounds(object renderer)
        {
            _renderCalls++;
            if (!_enableDiagnostics.Value || _renderCalls % _diagnosticInterval.Value != 0) return;
            try
            {
                var snapshot = CacheSnapshot.Capture(renderer, MaterialCacheHooks.Layout, _renderCalls);
                _writer?.WriteLine(snapshot.ToJson());
                WriteStatus(_guardFaulted ? "runtime-fault" : "attached", null);
            }
            catch (Exception ex)
            {
                Logger.LogError("Material-cache diagnostics sample failed: " + ex);
            }
        }

        private void WriteStatus(string state, string error)
        {
            try
            {
                var stats = ScratchListPool.GetStats();
                var json = "{\"pluginVersion\":\"" + PluginVersion + "\",\"state\":\"" + Escape(state) +
                           "\",\"poolConfigured\":" + (_enableScratchListPool != null && _enableScratchListPool.Value ? "true" : "false") +
                           ",\"poolActive\":" + (stats.Enabled ? "true" : "false") +
                           ",\"diagnostics\":" + (_enableDiagnostics != null && _enableDiagnostics.Value ? "true" : "false") +
                           ",\"processMaterialTransforms\":" + ProcessMaterialTranspiler.TransformCount +
                           ",\"poolCount\":" + stats.PoolCount + ",\"totalListAllocations\":" + stats.TotalAllocations +
                           ",\"totalRentals\":" + stats.TotalRentals + ",\"totalReturns\":" + stats.TotalReturns +
                           ",\"framesPrepared\":" + stats.FramesPrepared +
                           ",\"error\":" + (string.IsNullOrEmpty(error) ? "null" : "\"" + Escape(error) + "\"") + "}";
                File.WriteAllText(Path.Combine(_directory, "status.json"), json);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not write material-cache guard status: " + ex.Message);
            }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            try { _writer?.Dispose(); } catch { }
            ScratchListPool.DisableAndDiscard();
            if (ReferenceEquals(Instance, this)) Instance = null;
        }
    }

    internal static class MaterialCacheHooks
    {
        internal static MaterialCacheLayout Layout;

        public static void GenerateBackgroundPrefix(object __instance)
        {
            InstanceOrNull()?.BeforeGenerateBackground(__instance);
        }

        public static void GenerateBackgroundsPostfix(object __instance)
        {
            InstanceOrNull()?.AfterGenerateBackgrounds(__instance);
        }

        private static SuperZSNESMaterialCacheGuardPlugin InstanceOrNull()
        {
            return SuperZSNESMaterialCacheGuardPlugin.Instance;
        }
    }

    internal static class ProcessMaterialTranspiler
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var renderer = AccessTools.TypeByName("PPURenderer");
            var scratchField = renderer == null ? null : AccessTools.Field(renderer, "tileAddrToMat");
            var listType = scratchField == null ? null : scratchField.FieldType.GetGenericArguments()[1];
            var constructor = listType?.GetConstructor(Type.EmptyTypes);
            var rent = AccessTools.Method(typeof(ScratchListPool), nameof(ScratchListPool.RentObject));
            if (scratchField == null || listType == null || constructor == null || rent == null)
                throw new MissingMemberException("ProcessMaterial scratch-list transpiler dependencies were not found.");

            var matches = new List<int>();
            for (var index = 0; index < code.Count; index++)
                if (code[index].opcode == OpCodes.Newobj && Equals(code[index].operand, constructor)) matches.Add(index);
            if (matches.Count != 1)
                throw new InvalidOperationException("Expected one new List<TileInfo>() in ProcessMaterial, found " + matches.Count + ".");

            var match = matches[0];
            if (match < 3 || match + 1 >= code.Count || code[match - 3].opcode != OpCodes.Ldarg_0 ||
                code[match - 2].operand as FieldInfo != scratchField || !IsLoadLocal(code[match - 1].opcode) ||
                !IsScratchDictionaryAdd(code[match + 1], scratchField.FieldType))
                throw new InvalidOperationException("ProcessMaterial List<TileInfo> constructor was not at the sole tileAddrToMat.Add site.");

            var replacement = new CodeInstruction(OpCodes.Call, rent);
            replacement.labels.AddRange(code[match].labels);
            replacement.blocks.AddRange(code[match].blocks);
            code[match] = replacement;
            code.Insert(match + 1, new CodeInstruction(OpCodes.Castclass, listType));
            TransformCount++;
            return code;
        }

        private static bool IsLoadLocal(OpCode opcode)
        {
            return opcode == OpCodes.Ldloc || opcode == OpCodes.Ldloc_S || opcode == OpCodes.Ldloc_0 ||
                   opcode == OpCodes.Ldloc_1 || opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3;
        }

        private static bool IsScratchDictionaryAdd(CodeInstruction instruction, Type dictionaryType)
        {
            var method = instruction.operand as MethodInfo;
            return instruction.opcode == OpCodes.Callvirt && method != null && method.Name == "Add" &&
                   method.DeclaringType == dictionaryType && method.GetParameters().Length == 2;
        }
    }

    internal static class ScratchListPool
    {
        private static readonly Stack<object> FreeLists = new Stack<object>();
        private static Type _listType;
        private static ConstructorInfo _constructor;
        private static bool _enabled;
        private static long _totalAllocations;
        private static long _totalRentals;
        private static long _totalReturns;
        private static long _framesPrepared;
        private static int _poolHighWater;

        internal static void Initialize(Type listType, ConstructorInfo constructor)
        {
            if (listType == null || constructor == null || !typeof(IList).IsAssignableFrom(listType) ||
                !listType.IsGenericType || listType.GetGenericTypeDefinition() != typeof(List<>))
                throw new InvalidOperationException("Scratch-list pool requires the exact List<TileInfo> runtime type and public default constructor.");
            FreeLists.Clear();
            _listType = listType;
            _constructor = constructor;
            _totalAllocations = 0;
            _totalRentals = 0;
            _totalReturns = 0;
            _framesPrepared = 0;
            _poolHighWater = 0;
            _enabled = true;
        }

        internal static void HarvestAndClear(IDictionary scratch)
        {
            if (!_enabled) return;

            // Validate the complete map before mutating it. The second pass is
            // allocation-free with respect to List<TileInfo> instances.
            foreach (DictionaryEntry entry in scratch)
            {
                if (entry.Value == null || entry.Value.GetType() != _listType || !(entry.Value is IList))
                    throw new InvalidOperationException("tileAddrToMat contained an unexpected list value type: " +
                                                        (entry.Value == null ? "null" : entry.Value.GetType().FullName) + ".");
            }
            foreach (DictionaryEntry entry in scratch)
            {
                var list = (IList)entry.Value;
                list.Clear();
                FreeLists.Push(entry.Value);
                _totalReturns++;
            }
            scratch.Clear();
            _framesPrepared++;
            if (FreeLists.Count > _poolHighWater) _poolHighWater = FreeLists.Count;
        }

        public static object RentObject()
        {
            if (_enabled)
            {
                _totalRentals++;
                if (FreeLists.Count != 0) return FreeLists.Pop();
            }
            if (_constructor == null)
                throw new InvalidOperationException("Scratch-list constructor is unavailable.");
            _totalAllocations++;
            return _constructor.Invoke(null);
        }

        internal static void DisableAndDiscard()
        {
            _enabled = false;
            FreeLists.Clear();
        }

        internal static PoolStats GetStats()
        {
            return new PoolStats(_enabled, FreeLists.Count, _poolHighWater, _totalAllocations, _totalRentals,
                _totalReturns, _framesPrepared);
        }
    }

    internal readonly struct PoolStats
    {
        internal readonly bool Enabled;
        internal readonly int PoolCount;
        internal readonly int PoolHighWater;
        internal readonly long TotalAllocations;
        internal readonly long TotalRentals;
        internal readonly long TotalReturns;
        internal readonly long FramesPrepared;

        internal PoolStats(bool enabled, int poolCount, int poolHighWater, long totalAllocations,
            long totalRentals, long totalReturns, long framesPrepared)
        {
            Enabled = enabled;
            PoolCount = poolCount;
            PoolHighWater = poolHighWater;
            TotalAllocations = totalAllocations;
            TotalRentals = totalRentals;
            TotalReturns = totalReturns;
            FramesPrepared = framesPrepared;
        }
    }

    internal sealed class MaterialCacheLayout
    {
        internal MethodInfo GenerateBackgrounds;
        internal MethodInfo GenerateBackground;
        internal MethodInfo ProcessMaterial;
        internal FieldInfo RendererScratchMap;
        internal Type ScratchListType;
        internal ConstructorInfo ScratchListConstructor;
        internal FieldInfo RendererPropertyBlocks;
        internal FieldInfo RendererUsedMaterials;
        internal FieldInfo RendererFrameNo;
        internal FieldInfo RendererTextureGen;
        internal FieldInfo TileMaterials;
        internal FieldInfo LastUsedTileMaterials;
        internal FieldInfo FreeTileMaterials;
        internal FieldInfo PaletteTextures;

        internal static MaterialCacheLayout ResolveAndVerify()
        {
            var renderer = AccessTools.TypeByName("PPURenderer");
            var textureGen = AccessTools.TypeByName("TileTextureGen");
            if (renderer == null || textureGen == null)
                throw new TypeLoadException("SuperZSNES v0.230 PPURenderer/TileTextureGen types were not found.");

            var result = new MaterialCacheLayout
            {
                GenerateBackgrounds = AccessTools.Method(renderer, "GenerateBackgrounds", Type.EmptyTypes),
                GenerateBackground = AccessTools.Method(renderer, "GenerateBackground"),
                ProcessMaterial = AccessTools.Method(renderer, "ProcessMaterial"),
                RendererScratchMap = AccessTools.Field(renderer, "tileAddrToMat"),
                RendererPropertyBlocks = AccessTools.Field(renderer, "matPropBlocks"),
                RendererUsedMaterials = AccessTools.Field(renderer, "usedMaterials"),
                RendererFrameNo = AccessTools.Field(renderer, "frameNo"),
                RendererTextureGen = AccessTools.Field(renderer, "textGen"),
                TileMaterials = AccessTools.Field(textureGen, "tileMaterials"),
                LastUsedTileMaterials = AccessTools.Field(textureGen, "lastUsedTileMaterials"),
                FreeTileMaterials = AccessTools.Field(textureGen, "freeTileMaterials"),
                PaletteTextures = AccessTools.Field(textureGen, "paletteTextures")
            };

            if (result.GenerateBackgrounds == null || result.GenerateBackground == null || result.ProcessMaterial == null ||
                result.RendererScratchMap == null ||
                result.RendererPropertyBlocks == null || result.RendererUsedMaterials == null ||
                result.RendererFrameNo == null || result.RendererTextureGen == null ||
                result.TileMaterials == null || result.LastUsedTileMaterials == null ||
                result.FreeTileMaterials == null || result.PaletteTextures == null)
                throw new MissingMemberException("The expected SuperZSNES v0.230 material-cache layout was not found.");

            var scratchType = result.RendererScratchMap.FieldType;
            var dictionaryArguments = scratchType.IsGenericType ? scratchType.GetGenericArguments() : Type.EmptyTypes;
            if (dictionaryArguments.Length != 2)
                throw new InvalidOperationException("tileAddrToMat is no longer a two-argument generic dictionary.");
            result.ScratchListType = dictionaryArguments[1];
            if (!result.ScratchListType.IsGenericType || result.ScratchListType.GetGenericTypeDefinition() != typeof(List<>) ||
                result.ScratchListType.GetGenericArguments()[0].DeclaringType != renderer ||
                result.ScratchListType.GetGenericArguments()[0].Name != "TileInfo")
                throw new InvalidOperationException("tileAddrToMat value type is not List<PPURenderer.TileInfo>.");
            result.ScratchListConstructor = result.ScratchListType.GetConstructor(Type.EmptyTypes);
            if (result.ScratchListConstructor == null)
                throw new MissingMethodException("List<PPURenderer.TileInfo>() constructor was not found.");

            var checkMaterialList = AccessTools.Method(textureGen, "CheckMaterialList", Type.EmptyTypes);
            if (checkMaterialList == null)
                throw new MissingMethodException("TileTextureGen.CheckMaterialList() was not found.");
            var generateInstructions = PatchProcessor.GetOriginalInstructions(result.GenerateBackgrounds);
            var checkCalls = 0;
            var checkIndex = -1;
            for (var index = 0; index < generateInstructions.Count; index++)
            {
                if (!generateInstructions[index].Calls(checkMaterialList)) continue;
                checkCalls++;
                checkIndex = index;
            }
            if (checkCalls != 1 || checkIndex < generateInstructions.Count - 8)
                throw new InvalidOperationException("Expected one tail CheckMaterialList call in GenerateBackgrounds; found " +
                                                    checkCalls + " at " + checkIndex + " of " + generateInstructions.Count + ".");

            var generateBackgroundCalls = 0;
            foreach (var instruction in generateInstructions)
                if (instruction.Calls(result.GenerateBackground)) generateBackgroundCalls++;
            if (generateBackgroundCalls != 1)
                throw new InvalidOperationException("Expected one loop-body call to singular GenerateBackground; found " +
                                                    generateBackgroundCalls + ".");

            VerifyProcessMaterialShape(result);
            return result;
        }

        private static void VerifyProcessMaterialShape(MaterialCacheLayout layout)
        {
            var instructions = PatchProcessor.GetOriginalInstructions(layout.ProcessMaterial);
            var constructorMatches = 0;
            var exactAddMatches = 0;
            for (var index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].opcode != OpCodes.Newobj ||
                    !Equals(instructions[index].operand, layout.ScratchListConstructor)) continue;
                constructorMatches++;
                if (index >= 3 && index + 1 < instructions.Count && instructions[index - 3].opcode == OpCodes.Ldarg_0 &&
                    instructions[index - 2].operand as FieldInfo == layout.RendererScratchMap &&
                    instructions[index + 1].operand is MethodInfo add && add.Name == "Add" &&
                    add.DeclaringType == layout.RendererScratchMap.FieldType)
                    exactAddMatches++;
            }
            if (constructorMatches != 1 || exactAddMatches != 1)
                throw new InvalidOperationException("ProcessMaterial expected one List<TileInfo> constructor at one tileAddrToMat.Add; constructors=" +
                                                    constructorMatches + ", exact adds=" + exactAddMatches + ".");
        }
    }

    internal sealed class CacheSnapshot
    {
        internal long RenderCalls;
        internal int RendererFrame;
        internal int ScratchMapCount;
        internal long ScratchListCount;
        internal long ScratchListCapacity;
        internal int PropertyBlockCount;
        internal int UsedMaterialCount;
        internal int ActiveTileMaterialCount;
        internal int LastUsedTileMaterialCount;
        internal int FreeTileMaterialCount;
        internal int PaletteTextureCount;
        internal long ManagedBytes;
        internal long PrivateBytes;
        internal long WorkingSetBytes;
        internal PoolStats Pool;

        internal static CacheSnapshot Capture(object renderer, MaterialCacheLayout layout, long renderCalls)
        {
            if (renderer == null || layout == null)
                throw new ArgumentNullException(renderer == null ? "renderer" : "layout");
            var scratch = layout.RendererScratchMap.GetValue(renderer) as IDictionary;
            var propertyBlocks = layout.RendererPropertyBlocks.GetValue(renderer) as ICollection;
            var usedMaterials = layout.RendererUsedMaterials.GetValue(renderer);
            var textureGen = layout.RendererTextureGen.GetValue(renderer);
            if (scratch == null || propertyBlocks == null || usedMaterials == null || textureGen == null)
                throw new InvalidOperationException("One or more renderer cache fields have an unexpected runtime type.");

            long listCount = 0;
            long listCapacity = 0;
            foreach (DictionaryEntry entry in scratch)
            {
                if (entry.Value is ICollection collection) listCount += collection.Count;
                var capacity = entry.Value?.GetType().GetProperty("Capacity", BindingFlags.Instance | BindingFlags.Public);
                if (capacity != null) listCapacity += Convert.ToInt64(capacity.GetValue(entry.Value, null), CultureInfo.InvariantCulture);
            }

            long privateBytes;
            long workingSetBytes;
            using (var process = Process.GetCurrentProcess())
            {
                privateBytes = process.PrivateMemorySize64;
                workingSetBytes = process.WorkingSet64;
            }
            return new CacheSnapshot
            {
                RenderCalls = renderCalls,
                RendererFrame = Convert.ToInt32(layout.RendererFrameNo.GetValue(renderer), CultureInfo.InvariantCulture),
                ScratchMapCount = scratch.Count,
                ScratchListCount = listCount,
                ScratchListCapacity = listCapacity,
                PropertyBlockCount = propertyBlocks.Count,
                UsedMaterialCount = Count(usedMaterials),
                ActiveTileMaterialCount = Count(layout.TileMaterials.GetValue(textureGen)),
                LastUsedTileMaterialCount = Count(layout.LastUsedTileMaterials.GetValue(textureGen)),
                FreeTileMaterialCount = Count(layout.FreeTileMaterials.GetValue(textureGen)),
                PaletteTextureCount = Count(layout.PaletteTextures.GetValue(textureGen)),
                ManagedBytes = GC.GetTotalMemory(false),
                PrivateBytes = privateBytes,
                WorkingSetBytes = workingSetBytes,
                Pool = ScratchListPool.GetStats()
            };
        }

        internal string ToJson()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{{\"utc\":\"{0:O}\",\"kind\":\"sample\",\"renderCalls\":{1},\"rendererFrame\":{2}," +
                "\"scratchMapCount\":{3},\"scratchListCount\":{4},\"scratchListCapacity\":{5}," +
                "\"pooledListCount\":{6},\"poolHighWater\":{7},\"totalListAllocations\":{8}," +
                "\"totalRentals\":{9},\"totalReturns\":{10},\"framesPrepared\":{11}," +
                "\"propertyBlockCount\":{12},\"usedMaterialCount\":{13},\"activeTileMaterialCount\":{14}," +
                "\"lastUsedTileMaterialCount\":{15},\"freeTileMaterialCount\":{16},\"paletteTextureCount\":{17}," +
                "\"managedBytes\":{18},\"privateBytes\":{19},\"workingSetBytes\":{20}}}",
                DateTime.UtcNow, RenderCalls, RendererFrame, ScratchMapCount, ScratchListCount, ScratchListCapacity,
                Pool.PoolCount, Pool.PoolHighWater, Pool.TotalAllocations, Pool.TotalRentals, Pool.TotalReturns,
                Pool.FramesPrepared, PropertyBlockCount, UsedMaterialCount, ActiveTileMaterialCount,
                LastUsedTileMaterialCount, FreeTileMaterialCount, PaletteTextureCount, ManagedBytes, PrivateBytes,
                WorkingSetBytes);
        }

        private static int Count(object value)
        {
            if (value is ICollection collection) return collection.Count;
            var count = value?.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            if (count != null) return Convert.ToInt32(count.GetValue(value, null), CultureInfo.InvariantCulture);
            throw new InvalidOperationException("Expected a countable collection, got " +
                                                (value == null ? "null" : value.GetType().FullName) + ".");
        }
    }
}
