using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace SuperZSNESRendererTimingProbe
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class SuperZSNESRendererTimingProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.local.superzsnes.renderertimingprobe";
        public const string PluginName = "SuperZSNES Renderer Timing Probe";
        public const string PluginVersion = "0.1.0";

        internal static SuperZSNESRendererTimingProbePlugin Instance;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _hotPath;
        private ConfigEntry<int> _windowSeconds;
        private ConfigEntry<bool> _dirtyUploadGate;
        private Harmony _harmony;
        private RendererLayout _layout;
        private StreamWriter _writer;
        private string _outputDirectory;
        private long _windowStarted;
        private long _lastStatusWrite;
        private bool _faulted;

        private void Awake()
        {
            Instance = this;
            _enabled = Config.Bind(
                "Probe", "Enabled", false,
                "Install renderer timing instrumentation. False applies no profiler Harmony patches and writes no samples.");
            _hotPath = Config.Bind(
                "Probe", "EnableHotPathInstrumentation", false,
                "Also time DrawLines, ProcessMaterial, Process2DTiles, and texture/material lookups. Diagnostic only: these methods are frequent and the added Harmony calls perturb timing.");
            _windowSeconds = Config.Bind(
                "Probe", "WindowSeconds", 5,
                new ConfigDescription("Aggregation window in seconds.", new AcceptableValueRange<int>(2, 30)));
            _dirtyUploadGate = Config.Bind(
                "Optimizations", "GateTextureUploadsOnActualTileDirty", false,
                "Move each 2/4/8-bpp texture-bank upload flag under the corresponding SNES tile-dirty branch. Runtime-only, exact-IL-verified, and disabled by default.");

            _outputDirectory = Path.Combine(Paths.BepInExRootPath, "RendererTimingProbe");
            Directory.CreateDirectory(_outputDirectory);
            if (!_enabled.Value && !_dirtyUploadGate.Value)
            {
                WriteStatus("disabled", null);
                Logger.LogInfo(PluginName + " " + PluginVersion + " is disabled; no Harmony patches were applied.");
                return;
            }

            try
            {
                _layout = RendererLayout.ResolveAndVerify();
                _harmony = new Harmony(PluginGuid);

                if (_dirtyUploadGate.Value)
                {
                    DirtyUploadGate.TransformCount = 0;
                    foreach (var method in _layout.TextureGetters)
                        _harmony.Patch(method, transpiler: new HarmonyMethod(
                            AccessTools.Method(typeof(DirtyUploadGate), nameof(DirtyUploadGate.Transpiler))));
                    if (DirtyUploadGate.TransformCount != 3)
                        throw new InvalidOperationException("Expected three texture dirty-gate transforms, got " +
                                                            DirtyUploadGate.TransformCount + ".");
                }

                if (_enabled.Value)
                {
                    TimingHooks.Configure(_layout, _hotPath.Value);
                    PatchTiming(_layout.CoarseTimingMethods);
                    if (_hotPath.Value) PatchTiming(_layout.HotTimingMethods);
                    _writer = new StreamWriter(Path.Combine(_outputDirectory,
                        "renderer-timing-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".jsonl"),
                        false, new UTF8Encoding(false)) { AutoFlush = true };
                    _windowStarted = Stopwatch.GetTimestamp();
                }

                WriteStatus("attached", null);
                Logger.LogInfo(PluginName + " " + PluginVersion + " attached. probe=" + _enabled.Value +
                               ", hotPath=" + _hotPath.Value + ", dirtyUploadGate=" + _dirtyUploadGate.Value +
                               ", transforms=" + DirtyUploadGate.TransformCount + ".");
            }
            catch (Exception ex)
            {
                _faulted = true;
                try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
                WriteStatus("attach-failed", ex.Message);
                Logger.LogError("Renderer probe failed closed; stock methods remain active: " + ex);
            }
        }

        private void PatchTiming(IEnumerable<MethodInfo> methods)
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(TimingHooks), nameof(TimingHooks.Prefix)));
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(TimingHooks), nameof(TimingHooks.Postfix)));
            foreach (var method in methods) _harmony.Patch(method, prefix: prefix, postfix: postfix);
        }

        private void Update()
        {
            if (!_enabled.Value || _faulted || _windowStarted == 0) return;
            var now = Stopwatch.GetTimestamp();
            if (TicksToMilliseconds(now - _windowStarted) < _windowSeconds.Value * 1000.0) return;
            try
            {
                var snapshot = TimingHooks.SnapshotAndReset(_layout);
                snapshot.ElapsedMilliseconds = TicksToMilliseconds(now - _windowStarted);
                _writer.WriteLine(snapshot.ToJson());
                _windowStarted = now;
                if (TicksToMilliseconds(now - _lastStatusWrite) >= 30000.0)
                {
                    WriteStatus("attached", null);
                    _lastStatusWrite = now;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Renderer timing sample failed: " + ex);
            }
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private void WriteStatus(string state, string error)
        {
            try
            {
                var json = "{\"pluginVersion\":\"" + PluginVersion + "\",\"state\":\"" + Escape(state) +
                           "\",\"probeEnabled\":" + (_enabled != null && _enabled.Value ? "true" : "false") +
                           ",\"hotPathInstrumentation\":" + (_hotPath != null && _hotPath.Value ? "true" : "false") +
                           ",\"dirtyUploadGate\":" + (_dirtyUploadGate != null && _dirtyUploadGate.Value ? "true" : "false") +
                           ",\"dirtyUploadTransforms\":" + DirtyUploadGate.TransformCount +
                           ",\"error\":" + (string.IsNullOrEmpty(error) ? "null" : "\"" + Escape(error) + "\"") + "}";
                File.WriteAllText(Path.Combine(_outputDirectory, "status.json"), json);
            }
            catch (Exception ex) { Logger.LogWarning("Could not write renderer probe status: " + ex.Message); }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private void OnDestroy()
        {
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            try { if (_writer != null) _writer.Dispose(); } catch { }
            if (ReferenceEquals(Instance, this)) Instance = null;
        }
    }

    internal static class TimingHooks
    {
        private static readonly Dictionary<RuntimeMethodHandle, string> Names = new Dictionary<RuntimeMethodHandle, string>();
        private static readonly Dictionary<string, Metric> Metrics = new Dictionary<string, Metric>(StringComparer.Ordinal);
        private static RendererLayout _layout;
        private static object _lastRenderer;

        internal static void Configure(RendererLayout layout, bool hot)
        {
            _layout = layout;
            Names.Clear();
            Metrics.Clear();
            foreach (var method in layout.CoarseTimingMethods) Add(method);
            if (hot) foreach (var method in layout.HotTimingMethods) Add(method);
        }

        private static void Add(MethodInfo method)
        {
            var name = method.DeclaringType.Name + "." + method.Name;
            Names.Add(method.MethodHandle, name);
            Metrics[name] = new Metric();
        }

        public static void Prefix(MethodBase __originalMethod, object __instance, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (_layout != null && __originalMethod == _layout.GenerateBackgrounds) _lastRenderer = __instance;
        }

        public static void Postfix(MethodBase __originalMethod, long __state)
        {
            var elapsed = Stopwatch.GetTimestamp() - __state;
            string name;
            Metric metric;
            if (Names.TryGetValue(__originalMethod.MethodHandle, out name) && Metrics.TryGetValue(name, out metric))
                metric.Add(elapsed);
        }

        internal static TimingSnapshot SnapshotAndReset(RendererLayout layout)
        {
            var snapshot = new TimingSnapshot();
            foreach (var pair in Metrics) snapshot.Metrics[pair.Key] = pair.Value.Take();
            snapshot.Cache = CacheCardinality.Capture(_lastRenderer, layout);
            return snapshot;
        }
    }

    internal sealed class Metric
    {
        // Millisecond upper bounds: .05, .1, .25, .5, 1, 2, 4, 8, 16, 33, +inf.
        private static readonly double[] Limits = { 0.05, 0.1, 0.25, 0.5, 1, 2, 4, 8, 16, 33 };
        private long _count;
        private long _totalTicks;
        private long _maxTicks;
        private readonly long[] _buckets = new long[Limits.Length + 1];

        internal void Add(long ticks)
        {
            _count++;
            _totalTicks += ticks;
            if (ticks > _maxTicks) _maxTicks = ticks;
            var ms = ticks * 1000.0 / Stopwatch.Frequency;
            var bucket = 0;
            while (bucket < Limits.Length && ms > Limits[bucket]) bucket++;
            _buckets[bucket]++;
        }

        internal MetricSnapshot Take()
        {
            var result = new MetricSnapshot(_count, _totalTicks, _maxTicks, (long[])_buckets.Clone());
            _count = 0;
            _totalTicks = 0;
            _maxTicks = 0;
            Array.Clear(_buckets, 0, _buckets.Length);
            return result;
        }
    }

    internal sealed class MetricSnapshot
    {
        internal readonly long Count;
        internal readonly long TotalTicks;
        internal readonly long MaxTicks;
        internal readonly long[] Buckets;

        internal MetricSnapshot(long count, long totalTicks, long maxTicks, long[] buckets)
        {
            Count = count;
            TotalTicks = totalTicks;
            MaxTicks = maxTicks;
            Buckets = buckets;
        }

        internal string ToJson()
        {
            var total = TotalTicks * 1000.0 / Stopwatch.Frequency;
            var max = MaxTicks * 1000.0 / Stopwatch.Frequency;
            var average = Count == 0 ? 0 : total / Count;
            return "{\"count\":" + Count + ",\"totalMs\":" + F(total) + ",\"avgMs\":" + F(average) +
                   ",\"maxMs\":" + F(max) + ",\"buckets\":[" + string.Join(",", Buckets) + "]}";
        }

        private static string F(double value) { return value.ToString("0.000000", CultureInfo.InvariantCulture); }
    }

    internal sealed class TimingSnapshot
    {
        internal double ElapsedMilliseconds;
        internal readonly SortedDictionary<string, MetricSnapshot> Metrics =
            new SortedDictionary<string, MetricSnapshot>(StringComparer.Ordinal);
        internal CacheCardinality Cache;

        internal string ToJson()
        {
            var builder = new StringBuilder(2048);
            builder.Append("{\"utc\":\"").Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append("\",\"kind\":\"window\",\"elapsedMs\":")
                .Append(ElapsedMilliseconds.ToString("0.000", CultureInfo.InvariantCulture))
                .Append(",\"histogramUpperMs\":[0.05,0.1,0.25,0.5,1,2,4,8,16,33,null],\"metrics\":{");
            var first = true;
            foreach (var pair in Metrics)
            {
                if (!first) builder.Append(',');
                first = false;
                builder.Append('\"').Append(pair.Key).Append("\":").Append(pair.Value.ToJson());
            }
            builder.Append("},\"cache\":").Append(Cache == null ? "null" : Cache.ToJson()).Append('}');
            return builder.ToString();
        }
    }

    internal sealed class CacheCardinality
    {
        internal int ScratchKeys;
        internal int ScratchTiles;
        internal int UsedMaterials;
        internal int PropertyBlocks;
        internal int TileMaterials;
        internal int FreeTileMaterials;
        internal int PaletteTextures;

        internal static CacheCardinality Capture(object renderer, RendererLayout layout)
        {
            if (renderer == null || layout == null) return null;
            try
            {
                var scratch = layout.ScratchMap.GetValue(renderer) as IDictionary;
                var textureGen = layout.TextureGeneratorField.GetValue(renderer);
                if (scratch == null || textureGen == null) return null;
                var tiles = 0;
                foreach (DictionaryEntry entry in scratch)
                    if (entry.Value is ICollection collection) tiles += collection.Count;
                return new CacheCardinality
                {
                    ScratchKeys = scratch.Count,
                    ScratchTiles = tiles,
                    UsedMaterials = Count(layout.UsedMaterials.GetValue(renderer)),
                    PropertyBlocks = Count(layout.PropertyBlocks.GetValue(renderer)),
                    TileMaterials = Count(layout.TileMaterials.GetValue(textureGen)),
                    FreeTileMaterials = Count(layout.FreeTileMaterials.GetValue(textureGen)),
                    PaletteTextures = Count(layout.PaletteTextures.GetValue(textureGen))
                };
            }
            catch { return null; }
        }

        internal string ToJson()
        {
            return "{\"scratchKeys\":" + ScratchKeys + ",\"scratchTiles\":" + ScratchTiles +
                   ",\"usedMaterials\":" + UsedMaterials + ",\"propertyBlocks\":" + PropertyBlocks +
                   ",\"tileMaterials\":" + TileMaterials + ",\"freeTileMaterials\":" + FreeTileMaterials +
                   ",\"paletteTextures\":" + PaletteTextures + "}";
        }

        private static int Count(object value)
        {
            if (value is ICollection collection) return collection.Count;
            var property = value == null ? null : value.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            return property == null ? -1 : Convert.ToInt32(property.GetValue(value, null), CultureInfo.InvariantCulture);
        }
    }

    internal sealed class RendererLayout
    {
        internal MethodInfo MasterUpdate;
        internal MethodInfo RunFrame;
        internal MethodInfo GenerateBackgrounds;
        internal MethodInfo GenerateBackground;
        internal MethodInfo RefreshTextures;
        internal MethodInfo SetupZPositions;
        internal MethodInfo CalculateCurSceneData;
        internal MethodInfo StartFrame;
        internal MethodInfo GenerateTextures;
        internal MethodInfo CheckMaterialList;
        internal MethodInfo CalculatePalTexture;
        internal MethodInfo DrawLines;
        internal MethodInfo ProcessMaterial;
        internal MethodInfo Process2DTiles;
        internal MethodInfo GetTileMaterial;
        internal MethodInfo[] TextureGetters;
        internal List<MethodInfo> CoarseTimingMethods;
        internal List<MethodInfo> HotTimingMethods;
        internal FieldInfo ScratchMap;
        internal FieldInfo UsedMaterials;
        internal FieldInfo PropertyBlocks;
        internal FieldInfo TextureGeneratorField;
        internal FieldInfo TileMaterials;
        internal FieldInfo FreeTileMaterials;
        internal FieldInfo PaletteTextures;

        internal static RendererLayout ResolveAndVerify()
        {
            var master = RequiredType("MasterExecutor");
            var renderer = RequiredType("PPURenderer");
            var textureGen = RequiredType("TileTextureGen");
            var ui = RequiredType("EmuUIInterface");
            var result = new RendererLayout
            {
                MasterUpdate = RequiredMethod(master, "Update"),
                RunFrame = RequiredMethod(master, "RunFrame"),
                GenerateBackgrounds = RequiredMethod(renderer, "GenerateBackgrounds"),
                GenerateBackground = RequiredMethod(renderer, "GenerateBackground"),
                RefreshTextures = RequiredMethod(renderer, "RefreshTextures"),
                SetupZPositions = RequiredMethod(renderer, "SetupZPositions"),
                CalculateCurSceneData = RequiredMethod(ui, "CalculateCurSceneData"),
                StartFrame = RequiredMethod(textureGen, "StartFrame"),
                GenerateTextures = RequiredMethod(textureGen, "GenerateTextures"),
                CheckMaterialList = RequiredMethod(textureGen, "CheckMaterialList"),
                CalculatePalTexture = RequiredMethod(textureGen, "CalculatePalTexture"),
                DrawLines = RequiredMethod(renderer, "DrawLines"),
                ProcessMaterial = RequiredMethod(renderer, "ProcessMaterial"),
                Process2DTiles = RequiredMethod(renderer, "Process2DTiles"),
                GetTileMaterial = RequiredMethod(textureGen, "GetTileMaterial"),
                TextureGetters = new[]
                {
                    RequiredMethod(textureGen, "Get2bppTexture"), RequiredMethod(textureGen, "Get4bppTexture"),
                    RequiredMethod(textureGen, "Get8bppTexture")
                },
                ScratchMap = RequiredField(renderer, "tileAddrToMat"),
                UsedMaterials = RequiredField(renderer, "usedMaterials"),
                PropertyBlocks = RequiredField(renderer, "matPropBlocks"),
                TextureGeneratorField = RequiredField(renderer, "textGen"),
                TileMaterials = RequiredField(textureGen, "tileMaterials"),
                FreeTileMaterials = RequiredField(textureGen, "freeTileMaterials"),
                PaletteTextures = RequiredField(textureGen, "paletteTextures")
            };

            VerifyCalls(result.GenerateBackgrounds, result.RefreshTextures, 1);
            VerifyCalls(result.GenerateBackgrounds, result.CalculateCurSceneData, 1);
            VerifyCalls(result.GenerateBackgrounds, result.StartFrame, 1);
            VerifyCalls(result.GenerateBackgrounds, result.SetupZPositions, 1);
            VerifyCalls(result.GenerateBackgrounds, result.GenerateBackground, 1);
            VerifyCalls(result.GenerateBackgrounds, result.GenerateTextures, 1);
            VerifyCalls(result.GenerateBackgrounds, result.CheckMaterialList, 1);
            VerifyCalls(result.GenerateBackground, result.DrawLines, 4);
            VerifyCalls(result.GenerateBackground, result.Process2DTiles, 5);
            VerifyCalls(result.DrawLines, result.ProcessMaterial, 2);
            foreach (var getter in result.TextureGetters) DirtyUploadGate.VerifyShape(getter);

            result.CoarseTimingMethods = new List<MethodInfo>
            {
                result.MasterUpdate, result.RunFrame, result.GenerateBackgrounds, result.GenerateBackground,
                result.RefreshTextures, result.CalculateCurSceneData, result.StartFrame, result.SetupZPositions,
                result.GenerateTextures, result.CheckMaterialList, result.CalculatePalTexture
            };
            result.HotTimingMethods = new List<MethodInfo>
            {
                result.DrawLines, result.ProcessMaterial, result.Process2DTiles, result.GetTileMaterial,
                result.TextureGetters[0], result.TextureGetters[1], result.TextureGetters[2]
            };
            return result;
        }

        private static Type RequiredType(string name)
        {
            var type = AccessTools.TypeByName(name);
            if (type == null) throw new TypeLoadException(name + " was not found.");
            return type;
        }

        private static MethodInfo RequiredMethod(Type type, string name)
        {
            var method = AccessTools.Method(type, name);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private static FieldInfo RequiredField(Type type, string name)
        {
            var field = AccessTools.Field(type, name);
            if (field == null) throw new MissingFieldException(type.FullName, name);
            return field;
        }

        private static void VerifyCalls(MethodInfo caller, MethodInfo callee, int expected)
        {
            var count = 0;
            foreach (var instruction in PatchProcessor.GetOriginalInstructions(caller))
                if (instruction.Calls(callee)) count++;
            if (count != expected)
                throw new InvalidOperationException(caller.DeclaringType.Name + "." + caller.Name + " expected " +
                                                    expected + " call(s) to " + callee.Name + ", found " + count + ".");
        }
    }

    internal static class DirtyUploadGate
    {
        internal static int TransformCount;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            var code = new List<CodeInstruction>(input);
            var shape = FindShape(code, __originalMethod);
            var moved = new List<CodeInstruction>();
            for (var index = shape.MarkStart; index <= shape.MarkEnd; index++) moved.Add(new CodeInstruction(code[index]));
            for (var index = shape.MarkStart; index <= shape.MarkEnd; index++)
                if (code[index].labels.Count != 0 || code[index].blocks.Count != 0)
                    throw new InvalidOperationException(__originalMethod.Name + " unconditional bank-dirty mark has an unexpected branch target/block.");

            code.RemoveRange(shape.MarkStart, shape.MarkEnd - shape.MarkStart + 1);
            var removed = shape.MarkEnd - shape.MarkStart + 1;
            var insertAt = shape.DirtyBranch + 1 - removed;
            if (insertAt < 0 || insertAt >= code.Count)
                throw new InvalidOperationException(__originalMethod.Name + " dirty branch relocation index is invalid.");
            moved[0].labels.AddRange(code[insertAt].labels);
            moved[0].blocks.AddRange(code[insertAt].blocks);
            code[insertAt].labels.Clear();
            code[insertAt].blocks.Clear();
            code.InsertRange(insertAt, moved);
            TransformCount++;
            return code;
        }

        internal static void VerifyShape(MethodInfo method)
        {
            FindShape(new List<CodeInstruction>(PatchProcessor.GetOriginalInstructions(method)), method);
        }

        private static DirtyShape FindShape(List<CodeInstruction> code, MethodBase method)
        {
            var suffix = method.Name.Substring(3, 1); // 2, 4, or 8 from GetNbppTexture.
            var textureGen = method.DeclaringType;
            var ppu = AccessTools.TypeByName("SNESPPU");
            var bankDirty = AccessTools.Field(textureGen, "texture" + suffix + "bitDirty");
            var tileDirty = AccessTools.PropertyGetter(ppu, "_dirty" + suffix + "bpp");
            if (bankDirty == null || tileDirty == null)
                throw new MissingMemberException(method.Name + " dirty-array field/property was not found.");

            var bankMatches = FindFieldLoads(code, bankDirty);
            var tileMatches = new List<int>();
            for (var index = 0; index + 3 < code.Count; index++)
                if (code[index].Calls(tileDirty) && IsLoadLocal(code[index + 1], 1) &&
                    code[index + 2].opcode == OpCodes.Ldelem_U1 && IsBranchFalse(code[index + 3].opcode))
                    tileMatches.Add(index);
            if (bankMatches.Count != 1 || tileMatches.Count != 1)
                throw new InvalidOperationException(method.Name + " expected one bank mark and one tile-dirty branch; found " +
                                                    bankMatches.Count + "/" + tileMatches.Count + ".");
            var bank = bankMatches[0];
            var start = bank - 1;
            var end = bank + 3;
            if (start < 0 || end >= code.Count || code[start].opcode != OpCodes.Ldarg_0 ||
                !IsLoadLocal(code[bank + 1], 0) || code[bank + 2].opcode != OpCodes.Ldc_I4_1 ||
                code[bank + 3].opcode != OpCodes.Stelem_I1)
                throw new InvalidOperationException(method.Name + " unconditional texture-bank dirty mark changed shape.");

            var tile = tileMatches[0];
            if (tile <= end)
                throw new InvalidOperationException(method.Name + " dirty test no longer follows its unconditional bank mark.");
            return new DirtyShape(start, end, tile + 3);
        }

        private static List<int> FindFieldLoads(List<CodeInstruction> code, FieldInfo field)
        {
            var result = new List<int>();
            for (var index = 0; index < code.Count; index++)
                if (code[index].opcode == OpCodes.Ldfld && Equals(code[index].operand, field)) result.Add(index);
            return result;
        }

        private static bool IsLoadLocal(CodeInstruction instruction, int index)
        {
            if (index == 0 && instruction.opcode == OpCodes.Ldloc_0) return true;
            if (index == 1 && instruction.opcode == OpCodes.Ldloc_1) return true;
            if (instruction.opcode != OpCodes.Ldloc && instruction.opcode != OpCodes.Ldloc_S) return false;
            if (instruction.operand is LocalBuilder local) return local.LocalIndex == index;
            return Convert.ToInt32(instruction.operand, CultureInfo.InvariantCulture) == index;
        }

        private static bool IsBranchFalse(OpCode opcode)
        {
            return opcode == OpCodes.Brfalse || opcode == OpCodes.Brfalse_S;
        }

        private readonly struct DirtyShape
        {
            internal readonly int MarkStart;
            internal readonly int MarkEnd;
            internal readonly int DirtyBranch;
            internal DirtyShape(int markStart, int markEnd, int dirtyBranch)
            {
                MarkStart = markStart;
                MarkEnd = markEnd;
                DirtyBranch = dirtyBranch;
            }
        }
    }
}
