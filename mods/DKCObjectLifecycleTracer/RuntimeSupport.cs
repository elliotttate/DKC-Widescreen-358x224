using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace DKCObjectLifecycleTracer
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
            foreach (var candidate in instance.GetType().GetMethods(AnyInstance).Where(m => m.Name == method && m.GetParameters().Length == args.Length))
            {
                var parameters = candidate.GetParameters();
                var converted = new object[args.Length];
                var compatible = true;
                for (var i = 0; i < args.Length; i++)
                {
                    try { converted[i] = ConvertFor(args[i], parameters[i].ParameterType); }
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
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
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
            return "{" + string.Join(",", values.Select(p => Escape(p.Key) + ":" + Value(p.Value))) + "}";
        }
    }

    internal sealed class TraceOutput : IDisposable
    {
        private readonly object _gate = new object();
        private readonly StreamWriter _events;
        private readonly StreamWriter _writes;
        private readonly StreamWriter _scanner;
        public string Root { get; private set; }

        public TraceOutput(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
            _events = Open("events.jsonl");
            _writes = Open("writes.jsonl");
            _scanner = Open("scanner.jsonl");
        }

        private StreamWriter Open(string name)
        {
            return new StreamWriter(new FileStream(Path.Combine(Root, name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        }

        public void Event(IDictionary<string, object> data) { Write(_events, data); }
        public void Write(IDictionary<string, object> data) { Write(_writes, data); }
        public void Scanner(IDictionary<string, object> data) { Write(_scanner, data); }

        private void Write(StreamWriter writer, IDictionary<string, object> data)
        {
            lock (_gate) writer.WriteLine(Json.Object(data));
        }

        public void Current(IDictionary<string, object> data)
        {
            AtomicWrite(Path.Combine(Root, "current.json"), Json.Object(data));
        }

        public string Capture(IDictionary<string, object> data, string reason)
        {
            var safe = new string((reason ?? "capture").Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-').ToArray());
            if (safe.Length > 48) safe = safe.Substring(0, 48);
            var path = Path.Combine(Root, "capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + safe + ".json");
            AtomicWrite(path, Json.Object(data));
            return path;
        }

        private static void AtomicWrite(string path, string content)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        public void Dispose()
        {
            lock (_gate) { _events.Dispose(); _writes.Dispose(); _scanner.Dispose(); }
        }
    }

    internal sealed class ReflectionSnesReader : ISnesMemoryReader
    {
        private readonly object _memory;
        public ReflectionSnesReader(object memory) { _memory = memory; }
        public byte ReadByte(uint address) { return Convert.ToByte(Reflect.Call(_memory, "ReadMem", address & 0xFFFFFF), CultureInfo.InvariantCulture); }
    }
}
