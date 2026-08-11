using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SuperZSNESDrawLinesCacheGate
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(RendererFastPathsGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SuperZSNESDrawLinesCacheGatePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.drawlinescachegate";
        public const string PluginName = "SuperZSNES DrawLines Cache Gate";
        public const string PluginVersion = "0.1.0";
        public const string RendererFastPathsGuid = "dev.local.superzsnes.rendererfastpaths";

        private ConfigEntry<bool> _enabled;
        private Harmony _harmony;

        private void Awake()
        {
            _enabled = Config.Bind(
                "Optimization", "Enabled", false,
                "Check DrawLines.matDict before ProcessMaterial. Cache hits skip ProcessMaterial and use the retained TryGetValue result.");
            if (!_enabled.Value)
            {
                Logger.LogInfo("DrawLines cache gate is disabled; no method was patched.");
                WriteStatus("disabled", null);
                return;
            }

            _harmony = new Harmony(PluginGuid);
            try
            {
                CacheGateOptimization.TransformCount = 0;
                CacheGateOptimization.StockInputCount = 0;
                CacheGateOptimization.RendererFastPathInputCount = 0;
                var original = AccessTools.Method(typeof(PPURenderer), "DrawLines");
                if (original == null)
                    throw new MissingMethodException(typeof(PPURenderer).FullName, "DrawLines");
                _harmony.Patch(original, transpiler: CacheGateOptimization.CreateHarmonyMethod());
                if (CacheGateOptimization.TransformCount != 2)
                    throw new InvalidOperationException("DrawLines cache gate expected exactly two call-site rewrites, got " +
                                                        CacheGateOptimization.TransformCount + ".");
                Logger.LogInfo("Applied two DrawLines cache gates after RendererFastPaths normalization; stock sites=" +
                               CacheGateOptimization.StockInputCount + ", normalized sites=" +
                               CacheGateOptimization.RendererFastPathInputCount + ".");
                WriteStatus("attached", null);
            }
            catch (Exception exception)
            {
                try { _harmony.UnpatchSelf(); } catch { }
                _harmony = null;
                WriteStatus("attach-failed", exception.Message);
                throw;
            }
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private void WriteStatus(string state, string error)
        {
            try
            {
                var directory = Path.Combine(Paths.PluginPath, "SuperZSNESDrawLinesCacheGate");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "status.json"),
                    "{\"pluginVersion\":\"" + PluginVersion + "\",\"state\":\"" + Escape(state) +
                    "\",\"enabled\":" + (_enabled != null && _enabled.Value ? "true" : "false") +
                    ",\"transforms\":" + CacheGateOptimization.TransformCount +
                    ",\"stockInputs\":" + CacheGateOptimization.StockInputCount +
                    ",\"rendererFastPathInputs\":" + CacheGateOptimization.RendererFastPathInputCount +
                    ",\"error\":" + (error == null ? "null" : "\"" + Escape(error) + "\"") + "}");
            }
            catch (Exception exception)
            {
                Logger.LogWarning("Could not write DrawLines cache-gate status: " + exception.Message);
            }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    internal static class CacheGateOptimization
    {
        internal static int TransformCount;
        internal static int StockInputCount;
        internal static int RendererFastPathInputCount;

        internal static HarmonyMethod CreateHarmonyMethod()
        {
            return new HarmonyMethod(AccessTools.Method(typeof(CacheGateOptimization), nameof(Transpiler)))
            {
                priority = Priority.Last,
                after = new[] { SuperZSNESDrawLinesCacheGatePlugin.RendererFastPathsGuid }
            };
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(input);
            var renderer = typeof(PPURenderer);
            var processMaterial = AccessTools.Method(renderer, "ProcessMaterial");
            var cacheField = AccessTools.Field(renderer, "matDict");
            if (processMaterial == null || cacheField == null)
                throw new MissingMemberException("PPURenderer.DrawLines cache-gate dependencies were not found.");
            var dictionaryType = cacheField.FieldType;
            var arguments = dictionaryType.GetGenericArguments();
            var keyType = arguments[0];
            var valueType = arguments[1];
            var containsKey = AccessTools.Method(dictionaryType, "ContainsKey", new[] { keyType });
            var indexer = AccessTools.PropertyGetter(dictionaryType, "Item");
            var tryGetValue = AccessTools.Method(dictionaryType, "TryGetValue",
                new[] { keyType, valueType.MakeByRefType() });

            var processIndices = code.Select((instruction, index) => new { instruction, index })
                .Where(item => item.instruction.Calls(processMaterial)).Select(item => item.index).ToList();
            if (processIndices.Count != 2)
                throw new InvalidOperationException("SuperZSNES v0.230 DrawLines expected two ProcessMaterial calls, got " +
                                                    processIndices.Count + ".");

            for (var match = processIndices.Count - 1; match >= 0; match--)
            {
                var processIndex = processIndices[match];
                var callStart = processIndex - 11;
                if (!ValidateCallLoads(code, callStart, processIndex, cacheField))
                    throw new InvalidOperationException("DrawLines ProcessMaterial argument-load shape changed at IL index " + processIndex + ".");

                if (IsRendererFastPathShape(code, processIndex, cacheField, tryGetValue))
                {
                    RewriteNormalizedSite(code, generator, callStart, processIndex, cacheField, indexer);
                    RendererFastPathInputCount++;
                }
                else if (IsStockShape(code, processIndex, cacheField, containsKey, indexer))
                {
                    RewriteStockSite(code, generator, callStart, processIndex, cacheField, tryGetValue);
                    StockInputCount++;
                }
                else
                {
                    throw new InvalidOperationException("DrawLines material lookup after ProcessMaterial is neither stock nor RendererFastPaths-normalized at IL index " +
                                                        processIndex + ".");
                }
                TransformCount++;
            }

            return code;
        }

        private static void RewriteNormalizedSite(List<CodeInstruction> code, ILGenerator generator, int callStart,
            int processIndex, FieldInfo cacheField, MethodInfo indexer)
        {
            // RendererFastPaths v0.1 input:
            // ProcessMaterial(...); dict.TryGetValue(key, out value); brfalse bodyEnd; body...
            var valueLocal = code[processIndex + 4].operand;
            var bodyIndex = processIndex + 7;
            var success = generator.DefineLabel();
            code[bodyIndex].labels.Add(success);

            ValidateMovable(code, callStart, processIndex);
            ValidateMovable(code, processIndex + 1, processIndex + 5);

            var replacement = new List<CodeInstruction>();
            for (var index = processIndex + 1; index <= processIndex + 5; index++)
                replacement.Add(new CodeInstruction(code[index]));
            var hitBranch = new CodeInstruction(OpCodes.Brtrue, success);
            MoveMetadata(code[processIndex + 6], hitBranch);
            replacement.Add(hitBranch);
            for (var index = callStart; index <= processIndex; index++)
                replacement.Add(new CodeInstruction(code[index]));
            replacement.Add(new CodeInstruction(code[processIndex + 1]));
            replacement.Add(new CodeInstruction(code[processIndex + 2]));
            replacement.Add(new CodeInstruction(code[processIndex + 3]));
            replacement.Add(new CodeInstruction(OpCodes.Callvirt, indexer));
            replacement.Add(new CodeInstruction(OpCodes.Stloc, valueLocal));

            code.RemoveRange(callStart, processIndex + 6 - callStart + 1);
            code.InsertRange(callStart, replacement);
        }

        private static void RewriteStockSite(List<CodeInstruction> code, ILGenerator generator, int callStart,
            int processIndex, FieldInfo cacheField, MethodInfo tryGetValue)
        {
            // Stock input:
            // ProcessMaterial(...); if (dict.ContainsKey(key)) { value = dict[key]; body... }
            var valueLocal = code[processIndex + 10].operand;
            var bodyIndex = processIndex + 11;
            var success = generator.DefineLabel();
            code[bodyIndex].labels.Add(success);

            ValidateMovable(code, callStart, processIndex);
            ValidateMovable(code, processIndex + 1, processIndex + 4);
            ValidateMovable(code, processIndex + 6, processIndex + 10);

            var replacement = new List<CodeInstruction>
            {
                new CodeInstruction(code[processIndex + 1]),
                new CodeInstruction(code[processIndex + 2]),
                new CodeInstruction(code[processIndex + 3]),
                new CodeInstruction(OpCodes.Ldloca, valueLocal),
                new CodeInstruction(OpCodes.Callvirt, tryGetValue)
            };
            MoveMetadata(code[processIndex + 4], replacement[3]);
            var hitBranch = new CodeInstruction(OpCodes.Brtrue, success);
            MoveMetadata(code[processIndex + 5], hitBranch);
            replacement.Add(hitBranch);
            for (var index = callStart; index <= processIndex; index++)
                replacement.Add(new CodeInstruction(code[index]));
            for (var index = processIndex + 6; index <= processIndex + 10; index++)
                replacement.Add(new CodeInstruction(code[index]));

            code.RemoveRange(callStart, processIndex + 10 - callStart + 1);
            code.InsertRange(callStart, replacement);
        }

        private static bool ValidateCallLoads(IReadOnlyList<CodeInstruction> code, int start, int callIndex,
            FieldInfo cacheField)
        {
            if (start < 0 || callIndex - start != 11 || code[start].opcode != OpCodes.Ldarg_0)
                return false;
            for (var index = start; index < callIndex; index++)
                if (!IsArgumentOrLocalLoad(code[index].opcode) || code[index].labels.Count != 0 || code[index].blocks.Count != 0)
                    return false;
            // keyData is the third stack item: instance, i, keyData, ...
            return SameLoad(code[start + 2], code[callIndex + 3]);
        }

        private static bool IsRendererFastPathShape(IReadOnlyList<CodeInstruction> code, int processIndex,
            FieldInfo field, MethodInfo tryGetValue)
        {
            return processIndex + 7 < code.Count && code[processIndex + 1].opcode == OpCodes.Ldarg_0 &&
                   Equals(code[processIndex + 2].operand, field) && SameLoad(code[processIndex - 9], code[processIndex + 3]) &&
                   IsLoadLocalAddress(code[processIndex + 4].opcode) && code[processIndex + 5].Calls(tryGetValue) &&
                   IsConditionalFalseBranch(code[processIndex + 6].opcode);
        }

        private static bool IsStockShape(IReadOnlyList<CodeInstruction> code, int processIndex, FieldInfo field,
            MethodInfo containsKey, MethodInfo indexer)
        {
            return processIndex + 11 < code.Count && code[processIndex + 1].opcode == OpCodes.Ldarg_0 &&
                   Equals(code[processIndex + 2].operand, field) && SameLoad(code[processIndex - 9], code[processIndex + 3]) &&
                   code[processIndex + 4].Calls(containsKey) && IsConditionalFalseBranch(code[processIndex + 5].opcode) &&
                   code[processIndex + 6].opcode == OpCodes.Ldarg_0 && Equals(code[processIndex + 7].operand, field) &&
                   SameLoad(code[processIndex + 3], code[processIndex + 8]) && code[processIndex + 9].Calls(indexer) &&
                   IsStoreLocal(code[processIndex + 10].opcode);
        }

        private static void ValidateMovable(IReadOnlyList<CodeInstruction> code, int start, int end)
        {
            for (var index = start; index <= end; index++)
            {
                if (code[index].labels.Count != 0 || code[index].blocks.Count != 0)
                    throw new InvalidOperationException("DrawLines cache-gate move crosses a branch target or exception boundary at IL index " + index + ".");
            }
        }

        private static bool IsArgumentOrLocalLoad(OpCode opcode)
        {
            return opcode == OpCodes.Ldarg || opcode == OpCodes.Ldarg_S || opcode == OpCodes.Ldarg_0 ||
                   opcode == OpCodes.Ldarg_1 || opcode == OpCodes.Ldarg_2 || opcode == OpCodes.Ldarg_3 ||
                   opcode == OpCodes.Ldloc || opcode == OpCodes.Ldloc_S || opcode == OpCodes.Ldloc_0 ||
                   opcode == OpCodes.Ldloc_1 || opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3;
        }

        private static bool IsLoadLocalAddress(OpCode opcode)
        {
            return opcode == OpCodes.Ldloca || opcode == OpCodes.Ldloca_S;
        }

        private static bool IsStoreLocal(OpCode opcode)
        {
            return opcode == OpCodes.Stloc || opcode == OpCodes.Stloc_S || opcode == OpCodes.Stloc_0 ||
                   opcode == OpCodes.Stloc_1 || opcode == OpCodes.Stloc_2 || opcode == OpCodes.Stloc_3;
        }

        private static bool IsConditionalFalseBranch(OpCode opcode)
        {
            return opcode == OpCodes.Brfalse || opcode == OpCodes.Brfalse_S;
        }

        private static bool SameLoad(CodeInstruction first, CodeInstruction second)
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
