using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace DKCWramFlightRecorder
{
    internal static class Json
    {
        public static string Value(object value)
        {
            if (value == null) return "null";
            if (value is string || value is char || value is Enum) return Escape(Convert.ToString(value, CultureInfo.InvariantCulture));
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is decimal) return ((decimal)value).ToString(CultureInfo.InvariantCulture);
            if (value is IDictionary)
            {
                var pairs = new List<KeyValuePair<string, object>>();
                foreach (DictionaryEntry entry in (IDictionary)value)
                    pairs.Add(new KeyValuePair<string, object>(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), entry.Value));
                return Object(pairs);
            }
            if (value is IEnumerable && !(value is string))
            {
                var values = new List<string>();
                foreach (var item in (IEnumerable)value) values.Add(Value(item));
                return "[" + string.Join(",", values) + "]";
            }
            if (value.GetType().IsPrimitive) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return Escape(value.ToString());
        }

        public static string Object(IEnumerable<KeyValuePair<string, object>> values)
        {
            return "{" + string.Join(",", values.Select(pair => Escape(pair.Key) + ":" + Value(pair.Value))) + "}";
        }

        public static string Escape(string value)
        {
            if (value == null) return "null";
            var result = new StringBuilder(value.Length + 8).Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\': result.Append("\\\\"); break;
                    case '"': result.Append("\\\""); break;
                    case '\r': result.Append("\\r"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (character < 0x20) result.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                        else result.Append(character);
                        break;
                }
            }
            return result.Append('"').ToString();
        }
    }

    internal static class AtomicFile
    {
        public static void WriteText(string path, string contents)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            if (!File.Exists(path)) { File.Move(temporary, path); return; }
            try { File.Replace(temporary, path, null); }
            catch
            {
                File.Delete(path);
                File.Move(temporary, path);
            }
        }

        public static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(stream));
        }

        public static string Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(bytes));
        }

        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", string.Empty); }
    }

    internal sealed class RuntimeContractResult
    {
        public bool Valid;
        public string Error;
        public RuntimeBinding Binding;
        public IDictionary<string, object> Evidence;
    }

    internal sealed class RuntimeBinding
    {
        public Assembly GameAssembly;
        public MethodInfo ExecuteNextInstruction;
        public MethodInfo WriteMem;
        public MethodInfo GetRam;
        public MethodInfo GetFrameNo;
        public MethodInfo GetLineNo;
        public MethodInfo GetPixelNo;
        public MethodInfo GetDebugOpcodeString;
        public MethodInfo GetPcAddress;
        public FieldInfo MainRam;
        public FieldInfo MemoryMaster;
        public FieldInfo MasterCpu;
        public FieldInfo CpuMaster;
        public FieldInfo RegA;
        public FieldInfo RegX;
        public FieldInfo RegY;
        public FieldInfo RegS;
        public FieldInfo RegD;
        public FieldInfo RegDb;
        public FieldInfo RegPb;
        public FieldInfo RegPc;
        public FieldInfo FlagN;
        public FieldInfo FlagV;
        public FieldInfo FlagM;
        public FieldInfo FlagX;
        public FieldInfo FlagD;
        public FieldInfo FlagI;
        public FieldInfo FlagZ;
        public FieldInfo FlagC;
        public FieldInfo FlagE;
        public FieldInfo TotalCycles;
        public FieldInfo NumCycles;
    }

    internal static class SuperZsnesContract
    {
        internal const string AssemblySha256 = "33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED";
        internal const string ModuleMvid = "11738189-56ff-499d-8e00-b87cfb7f66eb";
        internal const int AssemblyBytes = 612352;
        internal const int CpuToken = 0x060004BB;
        internal const int CpuIlBytes = 14028;
        internal const string CpuIlSha256 = "3931A27E4F8B3C6F5EAEAA192E4DABC053101FA2C3EEDA8B31B838CB08DE172F";
        internal const int WriteToken = 0x0600056E;
        internal const int WriteIlBytes = 209;
        internal const string WriteIlSha256 = "1640D72CEE188DC079AFC641E4AE3EE8755C7DC5499D87B5A5279B83E46F6A9C";

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static RuntimeContractResult Validate()
        {
            var evidence = new Dictionary<string, object>();
            try
            {
                var cpuType = AccessTools.TypeByName("CPU65c816");
                var memoryType = AccessTools.TypeByName("MainMemoryMap");
                var masterType = AccessTools.TypeByName("MasterExecutor");
                Require(cpuType != null && memoryType != null && masterType != null, "Required SuperZSNES types were not found.");
                var assembly = cpuType.Assembly;
                Require(ReferenceEquals(assembly, memoryType.Assembly) && ReferenceEquals(assembly, masterType.Assembly), "Hook types do not come from one game assembly.");
                Require(!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location), "Assembly-CSharp has no readable file location; refusing an unverifiable runtime.");
                var file = new FileInfo(assembly.Location);
                var fileHash = AtomicFile.Sha256File(assembly.Location);
                var mvid = assembly.ManifestModule.ModuleVersionId.ToString("D");
                evidence["assemblyPath"] = assembly.Location;
                evidence["assemblyBytes"] = file.Length;
                evidence["assemblySha256"] = fileHash;
                evidence["mvid"] = mvid;
                Require(file.Length == AssemblyBytes, "Assembly-CSharp size mismatch: " + file.Length + ".");
                Require(string.Equals(fileHash, AssemblySha256, StringComparison.OrdinalIgnoreCase), "Assembly-CSharp SHA-256 mismatch: " + fileHash + ".");
                Require(string.Equals(mvid, ModuleMvid, StringComparison.OrdinalIgnoreCase), "Assembly-CSharp MVID mismatch: " + mvid + ".");

                var cpu = ExactMethod(cpuType, "ExecuteNextInstruction", typeof(void), Type.EmptyTypes);
                var write = ExactMethod(memoryType, "WriteMem", typeof(void), new[] { typeof(uint), typeof(byte) });
                ValidateBody(cpu, CpuToken, CpuIlBytes, CpuIlSha256, "CPU65c816.ExecuteNextInstruction", evidence);
                ValidateBody(write, WriteToken, WriteIlBytes, WriteIlSha256, "MainMemoryMap.WriteMem", evidence);

                var binding = new RuntimeBinding
                {
                    GameAssembly = assembly,
                    ExecuteNextInstruction = cpu,
                    WriteMem = write,
                    GetRam = ExactMethod(memoryType, "GetRam", typeof(byte[]), Type.EmptyTypes),
                    GetFrameNo = ExactMethod(masterType, "GetFrameNo", typeof(int), Type.EmptyTypes),
                    GetLineNo = ExactMethod(masterType, "GetLineNo", typeof(int), Type.EmptyTypes),
                    GetPixelNo = ExactMethod(masterType, "GetPixelNo", typeof(int), Type.EmptyTypes),
                    GetDebugOpcodeString = ExactMethod(cpuType, "GetDebugOpcodeString", typeof(string), Type.EmptyTypes),
                    GetPcAddress = ExactMethod(cpuType, "GetPCAddress", typeof(uint), Type.EmptyTypes),
                    MainRam = ExactField(memoryType, "mainRam", typeof(byte[])),
                    MemoryMaster = ExactField(memoryType, "masterExecutor", masterType),
                    MasterCpu = ExactField(masterType, "CPUCore65c816", cpuType),
                    CpuMaster = ExactField(cpuType, "masterExecutor", masterType),
                    RegA = ExactField(cpuType, "regA", typeof(int)), RegX = ExactField(cpuType, "regX", typeof(int)), RegY = ExactField(cpuType, "regY", typeof(int)),
                    RegS = ExactField(cpuType, "regS", typeof(uint)), RegD = ExactField(cpuType, "regD", typeof(uint)), RegDb = ExactField(cpuType, "regDB", typeof(uint)),
                    RegPb = ExactField(cpuType, "regPB", typeof(uint)), RegPc = ExactField(cpuType, "regPC", typeof(uint)),
                    FlagN = ExactField(cpuType, "flagN", typeof(bool)), FlagV = ExactField(cpuType, "flagV", typeof(bool)), FlagM = ExactField(cpuType, "flagM", typeof(bool)),
                    FlagX = ExactField(cpuType, "flagX", typeof(bool)), FlagD = ExactField(cpuType, "flagD", typeof(bool)), FlagI = ExactField(cpuType, "flagI", typeof(bool)),
                    FlagZ = ExactField(cpuType, "flagZ", typeof(bool)), FlagC = ExactField(cpuType, "flagC", typeof(bool)), FlagE = ExactField(cpuType, "flagE", typeof(bool)),
                    TotalCycles = ExactField(cpuType, "totalCycles", typeof(long)), NumCycles = ExactField(cpuType, "numCycles", typeof(int))
                };
                evidence["valid"] = true;
                evidence["contract"] = "SuperZSNES v0.230 exact Assembly-CSharp/MVID/signature/token/IL gate";
                return new RuntimeContractResult { Valid = true, Binding = binding, Evidence = evidence, Error = string.Empty };
            }
            catch (Exception ex)
            {
                evidence["valid"] = false;
                evidence["error"] = ex.Message;
                return new RuntimeContractResult { Valid = false, Error = ex.Message, Evidence = evidence };
            }
        }

        private static MethodInfo ExactMethod(Type type, string name, Type returnType, Type[] parameters)
        {
            var matches = type.GetMethods(Instance).Where(method => method.Name == name && ParametersEqual(method.GetParameters(), parameters)).ToArray();
            Require(matches.Length == 1, type.FullName + "." + name + " exact overload count was " + matches.Length + ".");
            Require(matches[0].ReturnType == returnType, type.FullName + "." + name + " return type mismatch.");
            return matches[0];
        }

        private static FieldInfo ExactField(Type type, string name, Type fieldType)
        {
            var field = type.GetField(name, Instance);
            Require(field != null && field.FieldType == fieldType, type.FullName + "." + name + " field contract mismatch.");
            return field;
        }

        private static bool ParametersEqual(ParameterInfo[] actual, Type[] expected)
        {
            if (actual.Length != expected.Length) return false;
            for (var index = 0; index < actual.Length; index++) if (actual[index].ParameterType != expected[index]) return false;
            return true;
        }

        private static void ValidateBody(MethodInfo method, int token, int bytes, string hash, string label, IDictionary<string, object> evidence)
        {
            var body = method.GetMethodBody();
            var il = body == null ? null : body.GetILAsByteArray();
            Require(method.MetadataToken == token, label + " metadata token mismatch.");
            Require(il != null && il.Length == bytes, label + " IL length mismatch.");
            var actualHash = AtomicFile.Sha256(il);
            Require(string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase), label + " IL SHA-256 mismatch: " + actualHash + ".");
            evidence[label] = new Dictionary<string, object> { { "metadataToken", "0x" + token.ToString("X8", CultureInfo.InvariantCulture) }, { "ilBytes", il.Length }, { "ilSha256", actualHash } };
        }

        private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    }

    internal sealed class TraceSession : IDisposable
    {
        private readonly StreamWriter _writes;
        private readonly StreamWriter _events;
        private int _dumpNumber;
        public string Root { get; private set; }

        public TraceSession(string root, RangePlan plan, IDictionary<string, object> contract, IDictionary<string, object> settings)
        {
            Root = root;
            Directory.CreateDirectory(root);
            _writes = Writer(Path.Combine(root, "writes.jsonl"));
            _events = Writer(Path.Combine(root, "events.jsonl"));
            AtomicFile.WriteText(Path.Combine(root, "session.json"), Json.Object(new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "tool", "DKCWramFlightRecorder" }, { "version", DKCWramFlightRecorderPlugin.PluginVersion },
                { "startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "ranges", plan.Ranges.Select(range => (object)range.ToData()).ToArray() }, { "contract", contract }, { "settings", settings }
            }));
        }

        public void TargetWrite(TargetWriteCapture capture)
        {
            _writes.WriteLine(Json.Object(capture.ToData()));
        }

        public void Event(string type, IDictionary<string, object> extra)
        {
            var values = new Dictionary<string, object> { { "type", type }, { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) } };
            if (extra != null) foreach (var pair in extra) values[pair.Key] = pair.Value;
            _events.WriteLine(Json.Object(values));
        }

        public string Dump(string reason, bool hasCurrent, InstructionSample current, InstructionSample[] instructions, WriteSample[] writes, long capturedWrites, string evidencePath)
        {
            var number = ++_dumpNumber;
            var path = Path.Combine(Root, "dump-" + number.ToString("D4", CultureInfo.InvariantCulture) + ".json");
            var evidence = new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "tool", "DKCWramFlightRecorder" }, { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "reason", reason ?? string.Empty }, { "capturedTargetWrites", capturedWrites },
                { "watchdogEvidencePath", string.IsNullOrWhiteSpace(evidencePath) ? null : evidencePath },
                { "currentInstruction", hasCurrent ? (object)current.ToData() : null },
                { "precedingInstructions", (instructions ?? Array.Empty<InstructionSample>()).Select(item => (object)item.ToData()).ToArray() },
                { "precedingWrites", (writes ?? Array.Empty<WriteSample>()).Select(item => (object)item.ToData()).ToArray() }
            };
            AtomicFile.WriteText(path, Json.Object(evidence));
            Event("dump_committed", new Dictionary<string, object> { { "path", path }, { "reason", reason ?? string.Empty }, { "watchdogEvidencePath", evidencePath } });
            return path;
        }

        public void Dispose()
        {
            try { _writes.Flush(); _writes.Dispose(); } catch { }
            try { _events.Flush(); _events.Dispose(); } catch { }
        }

        private static StreamWriter Writer(string path)
        {
            return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        }
    }
}
