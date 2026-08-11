using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SuperZSNESRendererFastPaths
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESRendererFastPathsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.rendererfastpaths";
        public const string PluginName = "SuperZSNES Renderer Fast Paths";
        public const string PluginVersion = "0.2.0";

        private ConfigEntry<bool> _drawLinesLookup;
        private ConfigEntry<bool> _usedMaterialsAdd;
        private ConfigEntry<bool> _mode7DataLookup;
        private ConfigEntry<bool> _tileListClearLookup;
        private ConfigEntry<bool> _dynamicFontLookup;
        private Harmony _harmony;

        private void Awake()
        {
            _drawLinesLookup = Config.Bind(
                "Optimizations", "DrawLinesMaterialLookup", false,
                "Replace two ContainsKey+indexer material-cache reads in PPURenderer.DrawLines with one TryGetValue each.");
            _usedMaterialsAdd = Config.Bind(
                "Optimizations", "UsedMaterialsAdd", false,
                "Replace HashSet.Contains followed by HashSet.Add with one unconditional Add; duplicate Add remains a no-op.");
            _mode7DataLookup = Config.Bind(
                "Optimizations", "Mode7DataLookup", false,
                "Replace two mode7data ContainsKey/Add/indexer get-or-create sequences with TryGetValue/Add and a retained list local.");
            _tileListClearLookup = Config.Bind(
                "Optimizations", "TileListClearLookup", false,
                "Replace GenerateBackground's tileAddrToMat ContainsKey+indexer clear with one TryGetValue result.");
            _dynamicFontLookup = Config.Bind(
                "Optimizations", "DynamicFontLookup", false,
                "Replace dynamic-font ContainsKey+indexer reads with TryGetValue and redundant usedDynamicFonts Contains+Add with Add.");

            if (!_drawLinesLookup.Value && !_usedMaterialsAdd.Value && !_mode7DataLookup.Value &&
                !_tileListClearLookup.Value && !_dynamicFontLookup.Value)
            {
                Logger.LogInfo("Renderer fast paths are disabled; no target methods were patched.");
                WriteStatus();
                return;
            }

            _harmony = new Harmony(PluginGuid);
            try
            {
                if (_drawLinesLookup.Value)
                {
                    DrawLinesMaterialLookupOptimization.TransformCount = 0;
                    var original = AccessTools.Method(typeof(PPURenderer), "DrawLines");
                    var transpiler = new HarmonyMethod(AccessTools.Method(
                        typeof(DrawLinesMaterialLookupOptimization), nameof(DrawLinesMaterialLookupOptimization.Transpiler)));
                    if (original == null)
                        throw new MissingMethodException(typeof(PPURenderer).FullName, "DrawLines");
                    _harmony.Patch(original, transpiler: transpiler);
                    if (DrawLinesMaterialLookupOptimization.TransformCount != 2)
                        throw new InvalidOperationException("DrawLines expected exactly two material lookup rewrites, got " +
                                                            DrawLinesMaterialLookupOptimization.TransformCount + ".");
                }

                if (_usedMaterialsAdd.Value)
                {
                    UsedMaterialsAddOptimization.TransformCount = 0;
                    var original = AccessTools.Method(typeof(PPURenderer), "ProcessMaterial");
                    var transpiler = new HarmonyMethod(AccessTools.Method(
                        typeof(UsedMaterialsAddOptimization), nameof(UsedMaterialsAddOptimization.Transpiler)));
                    if (original == null)
                        throw new MissingMethodException(typeof(PPURenderer).FullName, "ProcessMaterial");
                    _harmony.Patch(original, transpiler: transpiler);
                    if (UsedMaterialsAddOptimization.TransformCount != 1)
                        throw new InvalidOperationException("ProcessMaterial expected exactly one used-material rewrite, got " +
                                                            UsedMaterialsAddOptimization.TransformCount + ".");
                }

                if (_mode7DataLookup.Value)
                {
                    Mode7DataLookupOptimization.TransformCount = 0;
                    var transpiler = new HarmonyMethod(AccessTools.Method(
                        typeof(Mode7DataLookupOptimization), nameof(Mode7DataLookupOptimization.Transpiler)));
                    PatchRequired("UpdateMode7Tiles", transpiler);
                    PatchRequired("CalculateBoundsMesh", transpiler);
                    if (Mode7DataLookupOptimization.TransformCount != 2)
                        throw new InvalidOperationException("Mode 7 data lookup expected exactly two rewrites, got " +
                                                            Mode7DataLookupOptimization.TransformCount + ".");
                }

                if (_tileListClearLookup.Value)
                {
                    TileListClearLookupOptimization.TransformCount = 0;
                    var transpiler = new HarmonyMethod(AccessTools.Method(
                        typeof(TileListClearLookupOptimization), nameof(TileListClearLookupOptimization.Transpiler)));
                    PatchRequired("GenerateBackground", transpiler);
                    if (TileListClearLookupOptimization.TransformCount != 1)
                        throw new InvalidOperationException("Tile-list clear lookup expected exactly one rewrite, got " +
                                                            TileListClearLookupOptimization.TransformCount + ".");
                }

                if (_dynamicFontLookup.Value)
                {
                    DynamicFontGetLookupOptimization.TransformCount = 0;
                    DynamicFontGenerateLookupOptimization.DictionaryTransformCount = 0;
                    DynamicFontGenerateLookupOptimization.SetTransformCount = 0;
                    PatchRequired("GetDynamicFontTexture", new HarmonyMethod(AccessTools.Method(
                        typeof(DynamicFontGetLookupOptimization), nameof(DynamicFontGetLookupOptimization.Transpiler))));
                    PatchRequired("GenerateDynamicFontTexture", new HarmonyMethod(AccessTools.Method(
                        typeof(DynamicFontGenerateLookupOptimization), nameof(DynamicFontGenerateLookupOptimization.Transpiler))));
                    if (DynamicFontGetLookupOptimization.TransformCount != 1 ||
                        DynamicFontGenerateLookupOptimization.DictionaryTransformCount != 1 ||
                        DynamicFontGenerateLookupOptimization.SetTransformCount != 1)
                        throw new InvalidOperationException("Dynamic-font lookup expected one getter, one generator dictionary, and one set rewrite.");
                }

                Logger.LogInfo("Renderer fast paths applied: DrawLines=" + _drawLinesLookup.Value +
                               ", UsedMaterialsAdd=" + _usedMaterialsAdd.Value +
                               ", Mode7Data=" + _mode7DataLookup.Value +
                               ", TileListClear=" + _tileListClearLookup.Value +
                               ", DynamicFont=" + _dynamicFontLookup.Value + ".");
                WriteStatus();
            }
            catch
            {
                try { _harmony.UnpatchSelf(); } catch { }
                _harmony = null;
                throw;
            }
        }

        private void PatchRequired(string methodName, HarmonyMethod transpiler)
        {
            var original = AccessTools.Method(typeof(PPURenderer), methodName);
            if (original == null)
                throw new MissingMethodException(typeof(PPURenderer).FullName, methodName);
            _harmony.Patch(original, transpiler: transpiler);
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private void WriteStatus()
        {
            try
            {
                var directory = Path.Combine(Paths.PluginPath, "SuperZSNESRendererFastPaths");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "status.json"),
                    "{\"pluginVersion\":\"" + PluginVersion + "\",\"drawLinesMaterialLookup\":" +
                    (_drawLinesLookup.Value ? "true" : "false") + ",\"drawLinesTransforms\":" +
                    DrawLinesMaterialLookupOptimization.TransformCount + ",\"usedMaterialsAdd\":" +
                    (_usedMaterialsAdd.Value ? "true" : "false") + ",\"usedMaterialsTransforms\":" +
                    UsedMaterialsAddOptimization.TransformCount + ",\"mode7DataLookup\":" +
                    (_mode7DataLookup.Value ? "true" : "false") + ",\"mode7Transforms\":" +
                    Mode7DataLookupOptimization.TransformCount + ",\"tileListClearLookup\":" +
                    (_tileListClearLookup.Value ? "true" : "false") + ",\"tileListTransforms\":" +
                    TileListClearLookupOptimization.TransformCount + ",\"dynamicFontLookup\":" +
                    (_dynamicFontLookup.Value ? "true" : "false") + ",\"dynamicFontDictionaryTransforms\":" +
                    (DynamicFontGetLookupOptimization.TransformCount + DynamicFontGenerateLookupOptimization.DictionaryTransformCount) +
                    ",\"dynamicFontSetTransforms\":" + DynamicFontGenerateLookupOptimization.SetTransformCount + "}");
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Could not write renderer-fast-path status: " + exception.Message);
            }
        }
    }

    internal static class DrawLinesMaterialLookupOptimization
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var cacheField = AccessTools.Field(typeof(PPURenderer), "matDict");
            if (cacheField == null)
                throw new MissingFieldException(typeof(PPURenderer).FullName, "matDict");

            var dictionaryType = cacheField.FieldType;
            var genericArguments = dictionaryType.GetGenericArguments();
            var keyType = genericArguments[0];
            var valueType = genericArguments[1];
            var containsKey = AccessTools.Method(dictionaryType, "ContainsKey", new[] { keyType });
            var indexer = AccessTools.PropertyGetter(dictionaryType, "Item");
            var tryGetValue = AccessTools.Method(dictionaryType, "TryGetValue",
                new[] { keyType, valueType.MakeByRefType() });

            var starts = new List<int>();
            for (var index = 3; index + 7 < code.Count; index++)
            {
                if (!code[index].Calls(containsKey))
                    continue;
                var start = index - 3;
                if (code[start].opcode == OpCodes.Ldarg_0 &&
                    Equals(code[start + 1].operand, cacheField) &&
                    IsConditionalFalseBranch(code[index + 1].opcode) &&
                    code[index + 2].opcode == OpCodes.Ldarg_0 &&
                    Equals(code[index + 3].operand, cacheField) &&
                    SameLocalLoad(code[start + 2], code[index + 4]) &&
                    code[index + 5].Calls(indexer) &&
                    code[index + 6].opcode == OpCodes.Stloc_S)
                {
                    starts.Add(start);
                }
            }

            if (starts.Count != 2)
                throw new InvalidOperationException("SuperZSNES v0.230 DrawLines lookup pattern count was " + starts.Count + ", expected 2.");

            for (var match = starts.Count - 1; match >= 0; match--)
            {
                var start = starts[match];
                var end = start + 9;
                ValidateRemovableMetadata(code, start + 1, end);

                var branch = new CodeInstruction(code[start + 4]);
                var first = new CodeInstruction(code[start]);
                var fieldLoad = new CodeInstruction(code[start + 1]);
                var keyLoad = new CodeInstruction(code[start + 2]);
                var valueLocal = code[end].operand;
                var replacement = new List<CodeInstruction>
                {
                    first,
                    fieldLoad,
                    keyLoad,
                    new CodeInstruction(OpCodes.Ldloca_S, valueLocal),
                    new CodeInstruction(OpCodes.Callvirt, tryGetValue),
                    branch
                };

                code.RemoveRange(start, end - start + 1);
                code.InsertRange(start, replacement);
                TransformCount++;
            }

            return code;
        }

        private static bool IsConditionalFalseBranch(OpCode opcode)
        {
            return opcode == OpCodes.Brfalse || opcode == OpCodes.Brfalse_S;
        }

        private static bool SameLocalLoad(CodeInstruction first, CodeInstruction second)
        {
            if (first.opcode != second.opcode)
                return false;
            return Equals(first.operand, second.operand);
        }

        private static void ValidateRemovableMetadata(IReadOnlyList<CodeInstruction> code, int start, int end)
        {
            for (var index = start; index <= end; index++)
            {
                if (code[index].labels.Count != 0 || code[index].blocks.Count != 0)
                    throw new InvalidOperationException("DrawLines lookup contains an unexpected branch target or exception block at IL index " + index + ".");
            }
        }
    }

    internal static class UsedMaterialsAddOptimization
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input);
            var usedMaterials = AccessTools.Field(typeof(PPURenderer), "usedMaterials");
            if (usedMaterials == null)
                throw new MissingFieldException(typeof(PPURenderer).FullName, "usedMaterials");
            var setType = usedMaterials.FieldType;
            var elementType = setType.GetGenericArguments()[0];
            var contains = AccessTools.Method(setType, "Contains", new[] { elementType });
            var add = AccessTools.Method(setType, "Add", new[] { elementType });

            var containsIndex = code.FindIndex(instruction => instruction.Calls(contains));
            if (containsIndex < 3 || containsIndex + 6 >= code.Count ||
                code[containsIndex - 3].opcode != OpCodes.Ldarg_0 ||
                !Equals(code[containsIndex - 2].operand, usedMaterials) ||
                !IsConditionalTrueBranch(code[containsIndex + 1].opcode) ||
                code[containsIndex + 2].opcode != OpCodes.Ldarg_0 ||
                !Equals(code[containsIndex + 3].operand, usedMaterials) ||
                !SameLocalLoad(code[containsIndex - 1], code[containsIndex + 4]) ||
                !code[containsIndex + 5].Calls(add) || code[containsIndex + 6].opcode != OpCodes.Pop)
            {
                throw new InvalidOperationException("SuperZSNES v0.230 ProcessMaterial used-material pattern was not found.");
            }

            if (code.Count(instruction => instruction.Calls(contains)) != 1)
                throw new InvalidOperationException("ProcessMaterial contains an unexpected number of usedMaterials.Contains calls.");

            var start = containsIndex - 3;
            var end = containsIndex + 1;
            var retained = code[containsIndex + 2];
            for (var index = start; index <= end; index++)
            {
                retained.labels.AddRange(code[index].labels);
                retained.blocks.AddRange(code[index].blocks);
            }
            code.RemoveRange(start, end - start + 1);
            TransformCount++;
            return code;
        }

        private static bool IsConditionalTrueBranch(OpCode opcode)
        {
            return opcode == OpCodes.Brtrue || opcode == OpCodes.Brtrue_S;
        }

        private static bool SameLocalLoad(CodeInstruction first, CodeInstruction second)
        {
            return first.opcode == second.opcode && Equals(first.operand, second.operand);
        }
    }

    internal static class Mode7DataLookupOptimization
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(input);
            var field = AccessTools.Field(typeof(PPURenderer), "mode7data");
            if (field == null)
                throw new MissingFieldException(typeof(PPURenderer).FullName, "mode7data");
            var dictionaryType = field.FieldType;
            var arguments = dictionaryType.GetGenericArguments();
            var keyType = arguments[0];
            var valueType = arguments[1];
            var contains = AccessTools.Method(dictionaryType, "ContainsKey", new[] { keyType });
            var indexer = AccessTools.PropertyGetter(dictionaryType, "Item");
            var add = AccessTools.Method(dictionaryType, "Add", new[] { keyType, valueType });
            var tryGet = AccessTools.Method(dictionaryType, "TryGetValue", new[] { keyType, valueType.MakeByRefType() });
            var constructor = AccessTools.Constructor(valueType, Type.EmptyTypes);

            var containsIndices = code.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.Calls(contains)).Select(item => item.index).ToList();
            if (containsIndices.Count != 1)
                throw new InvalidOperationException("Mode 7 method expected one mode7data.ContainsKey call, got " + containsIndices.Count + ".");

            var containsIndex = containsIndices[0];
            var start = containsIndex - 5;
            var end = containsIndex + 14;
            if (start < 0 || end >= code.Count || code[start].opcode != OpCodes.Ldarg_0 ||
                !Equals(code[start + 1].operand, field) || !IsConditionalTrueBranch(code[containsIndex + 1].opcode) ||
                code[containsIndex + 2].opcode != OpCodes.Ldarg_0 || !Equals(code[containsIndex + 3].operand, field) ||
                !SameRange(code, containsIndex - 3, containsIndex + 4, 3) ||
                !SameRange(code, containsIndex - 3, containsIndex + 11, 3) ||
                code[containsIndex + 7].opcode != OpCodes.Newobj || !Equals(code[containsIndex + 7].operand, constructor) ||
                !code[containsIndex + 8].Calls(add) || code[containsIndex + 9].opcode != OpCodes.Ldarg_0 ||
                !Equals(code[containsIndex + 10].operand, field) || !code[containsIndex + 14].Calls(indexer))
            {
                throw new InvalidOperationException("SuperZSNES v0.230 mode7data get-or-create IL pattern was not found.");
            }

            var valueLocal = generator.DeclareLocal(valueType);
            var replacement = new List<CodeInstruction>();
            for (var index = start; index < containsIndex; index++) replacement.Add(new CodeInstruction(code[index]));

            var outLoad = new CodeInstruction(OpCodes.Ldloca, valueLocal);
            MoveMetadata(code[containsIndex], outLoad);
            replacement.Add(outLoad);
            replacement.Add(new CodeInstruction(OpCodes.Callvirt, tryGet));
            replacement.Add(new CodeInstruction(code[containsIndex + 1]));
            for (var index = containsIndex + 2; index <= containsIndex + 7; index++)
                replacement.Add(new CodeInstruction(code[index]));
            replacement.Add(new CodeInstruction(OpCodes.Dup));
            replacement.Add(new CodeInstruction(OpCodes.Stloc, valueLocal));
            replacement.Add(new CodeInstruction(code[containsIndex + 8]));

            var valueLoad = new CodeInstruction(OpCodes.Ldloc, valueLocal);
            for (var index = containsIndex + 9; index <= end; index++) MoveMetadata(code[index], valueLoad);
            replacement.Add(valueLoad);

            code.RemoveRange(start, end - start + 1);
            code.InsertRange(start, replacement);
            TransformCount++;
            return code;
        }

        private static bool IsConditionalTrueBranch(OpCode opcode)
        {
            return opcode == OpCodes.Brtrue || opcode == OpCodes.Brtrue_S;
        }

        private static bool SameRange(IReadOnlyList<CodeInstruction> code, int first, int second, int count)
        {
            for (var offset = 0; offset < count; offset++)
                if (code[first + offset].opcode != code[second + offset].opcode ||
                    !Equals(code[first + offset].operand, code[second + offset].operand)) return false;
            return true;
        }

        private static void MoveMetadata(CodeInstruction source, CodeInstruction destination)
        {
            destination.labels.AddRange(source.labels);
            destination.blocks.AddRange(source.blocks);
        }
    }

    internal static class TileListClearLookupOptimization
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(input);
            var field = AccessTools.Field(typeof(PPURenderer), "tileAddrToMat");
            if (field == null)
                throw new MissingFieldException(typeof(PPURenderer).FullName, "tileAddrToMat");
            var dictionaryType = field.FieldType;
            var arguments = dictionaryType.GetGenericArguments();
            var keyType = arguments[0];
            var valueType = arguments[1];
            var contains = AccessTools.Method(dictionaryType, "ContainsKey", new[] { keyType });
            var indexer = AccessTools.PropertyGetter(dictionaryType, "Item");
            var tryGet = AccessTools.Method(dictionaryType, "TryGetValue", new[] { keyType, valueType.MakeByRefType() });
            var clear = AccessTools.Method(valueType, "Clear", Type.EmptyTypes);

            var containsIndices = code.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.Calls(contains)).Select(item => item.index).ToList();
            if (containsIndices.Count != 1)
                throw new InvalidOperationException("GenerateBackground expected one tileAddrToMat.ContainsKey call, got " + containsIndices.Count + ".");
            var containsIndex = containsIndices[0];
            var start = containsIndex - 3;
            var end = containsIndex + 6;
            if (start < 0 || end >= code.Count || code[start].opcode != OpCodes.Ldarg_0 ||
                !Equals(code[start + 1].operand, field) || !IsConditionalFalseBranch(code[containsIndex + 1].opcode) ||
                code[containsIndex + 2].opcode != OpCodes.Ldarg_0 || !Equals(code[containsIndex + 3].operand, field) ||
                !SameLocalLoad(code[start + 2], code[containsIndex + 4]) || !code[containsIndex + 5].Calls(indexer) ||
                !code[containsIndex + 6].Calls(clear))
            {
                throw new InvalidOperationException("SuperZSNES v0.230 tile-list clear lookup IL pattern was not found.");
            }

            var valueLocal = generator.DeclareLocal(valueType);
            var replacement = new List<CodeInstruction>
            {
                new CodeInstruction(code[start]),
                new CodeInstruction(code[start + 1]),
                new CodeInstruction(code[start + 2])
            };
            var outLoad = new CodeInstruction(OpCodes.Ldloca, valueLocal);
            MoveMetadata(code[containsIndex], outLoad);
            replacement.Add(outLoad);
            replacement.Add(new CodeInstruction(OpCodes.Callvirt, tryGet));
            replacement.Add(new CodeInstruction(code[containsIndex + 1]));
            var valueLoad = new CodeInstruction(OpCodes.Ldloc, valueLocal);
            for (var index = containsIndex + 2; index <= containsIndex + 5; index++) MoveMetadata(code[index], valueLoad);
            replacement.Add(valueLoad);
            replacement.Add(new CodeInstruction(code[containsIndex + 6]));

            code.RemoveRange(start, end - start + 1);
            code.InsertRange(start, replacement);
            TransformCount++;
            return code;
        }

        private static bool IsConditionalFalseBranch(OpCode opcode)
        {
            return opcode == OpCodes.Brfalse || opcode == OpCodes.Brfalse_S;
        }

        private static bool SameLocalLoad(CodeInstruction first, CodeInstruction second)
        {
            return first.opcode == second.opcode && Equals(first.operand, second.operand);
        }

        private static void MoveMetadata(CodeInstruction source, CodeInstruction destination)
        {
            destination.labels.AddRange(source.labels);
            destination.blocks.AddRange(source.blocks);
        }
    }

    internal static class DynamicFontGetLookupOptimization
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input, ILGenerator generator)
        {
            var code = DynamicFontDictionaryLookupRewriter.Rewrite(new List<CodeInstruction>(input), generator);
            TransformCount++;
            return code;
        }
    }

    internal static class DynamicFontGenerateLookupOptimization
    {
        internal static int DictionaryTransformCount;
        internal static int SetTransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(input);
            var field = AccessTools.Field(typeof(PPURenderer), "usedDynamicFonts");
            if (field == null)
                throw new MissingFieldException(typeof(PPURenderer).FullName, "usedDynamicFonts");
            var setType = field.FieldType;
            var valueType = setType.GetGenericArguments()[0];
            var contains = AccessTools.Method(setType, "Contains", new[] { valueType });
            var add = AccessTools.Method(setType, "Add", new[] { valueType });
            var containsIndex = code.FindIndex(instruction => instruction.Calls(contains));
            if (containsIndex < 3 || containsIndex + 6 >= code.Count ||
                code[containsIndex - 3].opcode != OpCodes.Ldarg_0 || !Equals(code[containsIndex - 2].operand, field) ||
                !SameArgumentLoad(code[containsIndex - 1], code[containsIndex + 4]) ||
                !IsConditionalTrueBranch(code[containsIndex + 1].opcode) || code[containsIndex + 2].opcode != OpCodes.Ldarg_0 ||
                !Equals(code[containsIndex + 3].operand, field) || !code[containsIndex + 5].Calls(add) ||
                code[containsIndex + 6].opcode != OpCodes.Pop || code.Count(instruction => instruction.Calls(contains)) != 1)
            {
                throw new InvalidOperationException("SuperZSNES v0.230 usedDynamicFonts Contains/Add pattern was not found.");
            }

            var start = containsIndex - 3;
            var end = containsIndex + 1;
            var retained = code[containsIndex + 2];
            for (var index = start; index <= end; index++)
            {
                retained.labels.AddRange(code[index].labels);
                retained.blocks.AddRange(code[index].blocks);
            }
            code.RemoveRange(start, end - start + 1);
            SetTransformCount++;

            code = DynamicFontDictionaryLookupRewriter.Rewrite(code, generator);
            DictionaryTransformCount++;
            return code;
        }

        private static bool IsConditionalTrueBranch(OpCode opcode)
        {
            return opcode == OpCodes.Brtrue || opcode == OpCodes.Brtrue_S;
        }

        private static bool SameArgumentLoad(CodeInstruction first, CodeInstruction second)
        {
            return first.opcode == second.opcode && Equals(first.operand, second.operand);
        }
    }

    internal static class DynamicFontDictionaryLookupRewriter
    {
        internal static List<CodeInstruction> Rewrite(List<CodeInstruction> code, ILGenerator generator)
        {
            var field = AccessTools.Field(typeof(PPURenderer), "dynamicFontStorage");
            if (field == null)
                throw new MissingFieldException(typeof(PPURenderer).FullName, "dynamicFontStorage");
            var dictionaryType = field.FieldType;
            var arguments = dictionaryType.GetGenericArguments();
            var keyType = arguments[0];
            var valueType = arguments[1];
            var contains = AccessTools.Method(dictionaryType, "ContainsKey", new[] { keyType });
            var indexer = AccessTools.PropertyGetter(dictionaryType, "Item");
            var tryGet = AccessTools.Method(dictionaryType, "TryGetValue", new[] { keyType, valueType.MakeByRefType() });
            var containsIndices = code.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.Calls(contains)).Select(item => item.index).ToList();
            if (containsIndices.Count != 1)
                throw new InvalidOperationException("Dynamic-font method expected one dynamicFontStorage.ContainsKey call, got " + containsIndices.Count + ".");

            var containsIndex = containsIndices[0];
            var start = containsIndex - 3;
            var end = containsIndex + 5;
            if (start < 0 || end >= code.Count || code[start].opcode != OpCodes.Ldarg_0 ||
                !Equals(code[start + 1].operand, field) || !IsConditionalFalseBranch(code[containsIndex + 1].opcode) ||
                code[containsIndex + 2].opcode != OpCodes.Ldarg_0 || !Equals(code[containsIndex + 3].operand, field) ||
                !SameKeyLoad(code[start + 2], code[containsIndex + 4]) || !code[containsIndex + 5].Calls(indexer))
            {
                throw new InvalidOperationException("SuperZSNES v0.230 dynamic-font dictionary return pattern was not found.");
            }

            var valueLocal = generator.DeclareLocal(valueType);
            var replacement = new List<CodeInstruction>
            {
                new CodeInstruction(code[start]),
                new CodeInstruction(code[start + 1]),
                new CodeInstruction(code[start + 2])
            };
            var outLoad = new CodeInstruction(OpCodes.Ldloca, valueLocal);
            MoveMetadata(code[containsIndex], outLoad);
            replacement.Add(outLoad);
            replacement.Add(new CodeInstruction(OpCodes.Callvirt, tryGet));
            replacement.Add(new CodeInstruction(code[containsIndex + 1]));
            var valueLoad = new CodeInstruction(OpCodes.Ldloc, valueLocal);
            for (var index = containsIndex + 2; index <= end; index++) MoveMetadata(code[index], valueLoad);
            replacement.Add(valueLoad);

            code.RemoveRange(start, end - start + 1);
            code.InsertRange(start, replacement);
            return code;
        }

        private static bool IsConditionalFalseBranch(OpCode opcode)
        {
            return opcode == OpCodes.Brfalse || opcode == OpCodes.Brfalse_S;
        }

        private static bool SameKeyLoad(CodeInstruction first, CodeInstruction second)
        {
            return first.opcode == second.opcode && Equals(first.operand, second.operand);
        }

        private static void MoveMetadata(CodeInstruction source, CodeInstruction destination)
        {
            destination.labels.AddRange(source.labels);
            destination.blocks.AddRange(source.blocks);
        }
    }
}
