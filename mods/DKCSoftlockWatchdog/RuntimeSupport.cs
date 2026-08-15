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

namespace DKCSoftlockWatchdog
{
    internal static class Reflect
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal);

        public static Type Type(string name)
        {
            Type value;
            if (!Types.TryGetValue(name, out value))
            {
                value = AccessTools.TypeByName(name);
                if (value != null) Types[name] = value;
            }
            return value;
        }

        public static object Static(string typeName, string member)
        {
            var type = Type(typeName);
            if (type == null) return null;
            var field = type.GetField(member, AnyStatic);
            if (field != null) return field.GetValue(null);
            var property = type.GetProperty(member, AnyStatic);
            return property == null ? null : property.GetValue(null, null);
        }

        public static object Get(object instance, string member)
        {
            if (instance == null) return null;
            var field = instance.GetType().GetField(member, AnyInstance);
            if (field != null) return field.GetValue(instance);
            var property = instance.GetType().GetProperty(member, AnyInstance);
            return property == null ? null : property.GetValue(instance, null);
        }

        public static object Call(object instance, string method, params object[] args)
        {
            if (instance == null) return null;
            args = args ?? Array.Empty<object>();
            foreach (var candidate in instance.GetType().GetMethods(AnyInstance).Where(item => item.Name == method && item.GetParameters().Length == args.Length))
            {
                var parameters = candidate.GetParameters();
                var converted = new object[args.Length];
                var compatible = true;
                for (var index = 0; index < args.Length; index++)
                {
                    try { converted[index] = ConvertFor(args[index], parameters[index].ParameterType); }
                    catch { compatible = false; break; }
                }
                if (compatible) return candidate.Invoke(instance, converted);
            }
            throw new MissingMethodException(instance.GetType().FullName, method);
        }

        public static object TryCall(object instance, string method, params object[] args)
        {
            try { return Call(instance, method, args); } catch { return null; }
        }

        public static int IntCall(object instance, string method, int fallback)
        {
            try { return Convert.ToInt32(Call(instance, method), CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        public static uint UIntCall(object instance, string method, uint fallback)
        {
            try { return Convert.ToUInt32(Call(instance, method), CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static object ConvertFor(object value, Type target)
        {
            if (value == null) return target.IsValueType ? Activator.CreateInstance(target) : null;
            var unwrapped = Nullable.GetUnderlyingType(target) ?? target;
            if (unwrapped.IsInstanceOfType(value)) return value;
            if (unwrapped.IsEnum) return Enum.ToObject(unwrapped, value);
            return Convert.ChangeType(value, unwrapped, CultureInfo.InvariantCulture);
        }
    }

    internal static class Json
    {
        public static string Escape(string value)
        {
            if (value == null) return "null";
            var builder = new StringBuilder(value.Length + 8);
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20) builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                        else builder.Append(character);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

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
                var parts = new List<string>();
                foreach (var item in (IEnumerable)value) parts.Add(Value(item));
                return "[" + string.Join(",", parts) + "]";
            }
            if (value.GetType().IsPrimitive) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return Escape(value.ToString());
        }

        public static string Object(IEnumerable<KeyValuePair<string, object>> values)
        {
            return "{" + string.Join(",", values.Select(pair => Escape(pair.Key) + ":" + Value(pair.Value))) + "}";
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
            CommitFile(temporary, path);
        }

        public static bool TryCreateText(string path, string contents)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(path)) return false;
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            try { File.Move(temporary, path); return true; }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                return false;
            }
        }

        private static void CommitFile(string temporary, string path)
        {
            if (!File.Exists(path)) { File.Move(temporary, path); return; }
            try { File.Replace(temporary, path, null); }
            catch
            {
                File.Delete(path);
                File.Move(temporary, path);
            }
        }
    }

    internal sealed class PendingCapture
    {
        public byte[] Wram;
        public IDictionary<string, object> Evidence;
        public string Slug;
        public bool RequestPause;
        public bool RequestExternalCaptures;
    }

    internal sealed class EvidenceWriter : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _triggerRoot;
        private readonly StreamWriter _events;
        public string SessionRoot { get; private set; }

        public EvidenceWriter(string root)
        {
            SessionRoot = root;
            _triggerRoot = Path.Combine(root, "Triggers");
            Directory.CreateDirectory(_triggerRoot);
            _events = new StreamWriter(new FileStream(Path.Combine(root, "events.jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        }

        public string Capture(PendingCapture capture)
        {
            if (capture == null || capture.Wram == null || capture.Wram.Length != DkcRam.WramSize)
                throw new ArgumentException("Capture requires exactly 128 KiB of WRAM.", "capture");
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var safe = Safe(capture.Slug);
            var final = UniquePath(Path.Combine(_triggerRoot, timestamp + "-" + safe));
            var staging = Path.Combine(_triggerRoot, ".tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var hash = Sha256(capture.Wram);
                capture.Evidence["wramFile"] = "wram-7e7f.bin";
                capture.Evidence["wramBytes"] = capture.Wram.Length;
                capture.Evidence["wramSha256"] = hash;
                File.WriteAllBytes(Path.Combine(staging, "wram-7e7f.bin"), capture.Wram);
                File.WriteAllText(Path.Combine(staging, "evidence.json"), Json.Object(capture.Evidence), new UTF8Encoding(false));
                Directory.Move(staging, final);
                lock (_gate)
                {
                    _events.WriteLine(Json.Object(new Dictionary<string, object>
                    {
                        { "type", "capture_committed" }, { "utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                        { "path", final }, { "wramSha256", hash }, { "slug", safe }
                    }));
                }
                return final;
            }
            catch
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                throw;
            }
        }

        public void Event(IDictionary<string, object> data)
        {
            lock (_gate) _events.WriteLine(Json.Object(data));
        }

        public void Status(string path, IDictionary<string, object> data)
        {
            AtomicFile.WriteText(path, Json.Object(data));
        }

        private static string Safe(string value)
        {
            var safe = new string((value ?? "capture").Select(character => char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '-').ToArray());
            if (safe.Length == 0) safe = "capture";
            return safe.Length <= 72 ? safe : safe.Substring(0, 72);
        }

        private static string UniquePath(string path)
        {
            if (!Directory.Exists(path)) return path;
            for (var suffix = 2; suffix < 10000; suffix++)
            {
                var candidate = path + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                if (!Directory.Exists(candidate)) return candidate;
            }
            return path + "-" + Guid.NewGuid().ToString("N");
        }

        private static string Sha256(byte[] data)
        {
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(data)).Replace("-", string.Empty);
        }

        public void Dispose()
        {
            lock (_gate) _events.Dispose();
        }
    }

    internal sealed class ReflectionSnesReader : ISnesMemoryReader
    {
        private readonly object _memory;
        public ReflectionSnesReader(object memory) { _memory = memory; }
        public byte ReadByte(uint address)
        {
            return Convert.ToByte(Reflect.Call(_memory, "ReadMem", address & 0xFFFFFF), CultureInfo.InvariantCulture);
        }
    }
}
