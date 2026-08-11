using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SuperZSNESCoreOptimizations
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESCoreOptimizationsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.coreoptimizations";
        public const string PluginName = "SuperZSNES Core Optimizations";
        public const string PluginVersion = "0.2.0";

        private ConfigEntry<bool> _readMemCheatFastPath;
        private ConfigEntry<bool> _tileMaterialCacheFastPath;
        private Harmony _harmony;

        private void Awake()
        {
            _readMemCheatFastPath = Config.Bind(
                "Optimizations", "ReadMemCheatFastPath", false,
                "Replace ReadMem's ContainsKey+indexer cheat lookup with an empty-dictionary Count guard and one TryGetValue lookup. Cheats remain supported.");
            _tileMaterialCacheFastPath = Config.Bind(
                "Optimizations", "TileMaterialCacheFastPath", false,
                "Replace GetTileMaterial's ContainsKey plus five repeated tuple indexer lookups with one TryGetValue result local.");

            _harmony = new Harmony(PluginGuid);
            if (_readMemCheatFastPath.Value)
            {
                ReadMemOptimization.TransformCount = 0;
                var original = AccessTools.Method(typeof(MainMemoryMap), nameof(MainMemoryMap.ReadMem), new[] { typeof(uint) });
                var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ReadMemOptimization), nameof(ReadMemOptimization.Transpiler)));
                _harmony.Patch(original, transpiler: transpiler);
                if (ReadMemOptimization.TransformCount != 1)
                    throw new InvalidOperationException("ReadMem optimization expected exactly one IL replacement, got " + ReadMemOptimization.TransformCount + ".");
                Logger.LogInfo("Applied ReadMem cheat fast path: Count guard + TryGetValue; on-disk Assembly-CSharp.dll unchanged.");
            }
            if (_tileMaterialCacheFastPath.Value)
            {
                TileMaterialOptimization.TransformCount = 0;
                var original = AccessTools.Method(typeof(TileTextureGen), nameof(TileTextureGen.GetTileMaterial));
                var transpiler = new HarmonyMethod(AccessTools.Method(typeof(TileMaterialOptimization), nameof(TileMaterialOptimization.Transpiler)));
                _harmony.Patch(original, transpiler: transpiler);
                if (TileMaterialOptimization.TransformCount != 1)
                    throw new InvalidOperationException("Tile material optimization expected exactly one IL replacement, got " + TileMaterialOptimization.TransformCount + ".");
                Logger.LogInfo("Applied GetTileMaterial cache fast path: one TryGetValue local replaces repeated tuple hashes.");
            }
            WriteStatus();
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
        }

        private void WriteStatus()
        {
            try
            {
                var directory = Path.Combine(Paths.PluginPath, "SuperZSNESCoreOptimizations");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "status.json"),
                    "{\"pluginVersion\":\"" + PluginVersion + "\",\"readMemCheatFastPath\":" +
                    (_readMemCheatFastPath.Value ? "true" : "false") + ",\"readMemTransforms\":" +
                    ReadMemOptimization.TransformCount + ",\"tileMaterialCacheFastPath\":" +
                    (_tileMaterialCacheFastPath.Value ? "true" : "false") + ",\"tileMaterialTransforms\":" +
                    TileMaterialOptimization.TransformCount + "}");
            }
            catch (Exception ex) { Logger.LogWarning("Could not write core optimization status: " + ex.Message); }
        }
    }

    internal static class ReadMemOptimization
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(input);
            var dictionaryType = typeof(Dictionary<int, byte>);
            var containsKey = AccessTools.Method(dictionaryType, nameof(Dictionary<int, byte>.ContainsKey), new[] { typeof(int) });
            var getItem = AccessTools.PropertyGetter(dictionaryType, "Item");
            var getCount = AccessTools.PropertyGetter(dictionaryType, nameof(Dictionary<int, byte>.Count));
            var tryGetValue = AccessTools.Method(dictionaryType, nameof(Dictionary<int, byte>.TryGetValue),
                new[] { typeof(int), typeof(byte).MakeByRefType() });

            var containsIndex = code.FindIndex(instruction => instruction.Calls(containsKey));
            if (containsIndex < 4 || containsIndex + 7 >= code.Count || !code[containsIndex + 6].Calls(getItem) || code[containsIndex + 7].opcode != OpCodes.Ret)
                throw new InvalidOperationException("SuperZSNES v0.230 ReadMem cheat lookup IL pattern was not found.");

            var start = containsIndex - 4;
            var end = containsIndex + 7;
            for (var index = start + 1; index <= end; index++)
            {
                if (code[index].labels.Count != 0 || code[index].blocks.Count != 0)
                    throw new InvalidOperationException("ReadMem cheat lookup contains an unexpected branch target or exception block.");
            }

            var continueTarget = code[containsIndex + 1].operand;
            var dictionaryLocal = generator.DeclareLocal(dictionaryType);
            var valueLocal = generator.DeclareLocal(typeof(byte));
            var replacement = new List<CodeInstruction>
            {
                new CodeInstruction(code[start]),
                new CodeInstruction(code[start + 1]),
                new CodeInstruction(code[start + 2]),
                new CodeInstruction(OpCodes.Stloc, dictionaryLocal),
                new CodeInstruction(OpCodes.Ldloc, dictionaryLocal),
                new CodeInstruction(OpCodes.Callvirt, getCount),
                new CodeInstruction(OpCodes.Brfalse, continueTarget),
                new CodeInstruction(OpCodes.Ldloc, dictionaryLocal),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldloca, valueLocal),
                new CodeInstruction(OpCodes.Callvirt, tryGetValue),
                new CodeInstruction(OpCodes.Brfalse, continueTarget),
                new CodeInstruction(OpCodes.Ldloc, valueLocal),
                new CodeInstruction(OpCodes.Ret)
            };

            code.RemoveRange(start, end - start + 1);
            code.InsertRange(start, replacement);
            TransformCount++;
            return code;
        }
    }

    internal static class TileMaterialOptimization
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var cacheField = AccessTools.Field(typeof(TileTextureGen), "tileMaterials");
            var dictionaryType = cacheField.FieldType;
            var keyType = dictionaryType.GetGenericArguments()[0];
            var valueType = dictionaryType.GetGenericArguments()[1];
            var containsKey = AccessTools.Method(dictionaryType, "ContainsKey", new[] { keyType });
            var getItem = AccessTools.PropertyGetter(dictionaryType, "Item");
            var tryGetValue = AccessTools.Method(dictionaryType, "TryGetValue", new[] { keyType, valueType.MakeByRefType() });

            var containsIndex = code.FindIndex(instruction => instruction.Calls(containsKey));
            var itemIndices = new List<int>();
            for (var index = 0; index < code.Count; index++) if (code[index].Calls(getItem)) itemIndices.Add(index);
            if (containsIndex < 0 || itemIndices.Count != 5)
                throw new InvalidOperationException("SuperZSNES v0.230 GetTileMaterial cache IL pattern was not found (ContainsKey=" +
                                                    (containsIndex >= 0) + ", indexers=" + itemIndices.Count + ").");

            var containsCall = code[containsIndex];
            var loadOut = new CodeInstruction(OpCodes.Ldloca_S, (byte)1);
            loadOut.labels.AddRange(containsCall.labels);
            loadOut.blocks.AddRange(containsCall.blocks);
            code[containsIndex] = loadOut;
            code.Insert(containsIndex + 1, new CodeInstruction(OpCodes.Callvirt, tryGetValue));

            for (var item = itemIndices.Count - 1; item >= 0; item--)
            {
                var end = itemIndices[item] + 1;
                if (end > containsIndex) end++;
                var start = end - 7;
                if (start < 0 || code[start].opcode != OpCodes.Ldarg_0 || code[start + 1].operand as FieldInfo != cacheField)
                    throw new InvalidOperationException("Unexpected GetTileMaterial indexer load shape at match " + item + ".");
                var replacement = new CodeInstruction(OpCodes.Ldloc_1);
                for (var index = start; index <= end - 1; index++)
                {
                    replacement.labels.AddRange(code[index].labels);
                    replacement.blocks.AddRange(code[index].blocks);
                }
                code.RemoveRange(start, end - start);
                code.Insert(start, replacement);
            }

            TransformCount++;
            return code;
        }
    }
}
