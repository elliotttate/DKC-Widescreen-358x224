using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DKCWidescreenDebugger
{
    internal static class Reflect
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal);

        public static Type Type(string name)
        {
            Type result;
            if (!Types.TryGetValue(name, out result))
            {
                result = AccessTools.TypeByName(name);
                if (result != null) Types[name] = result;
            }
            return result;
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
            var type = instance.GetType();
            var field = type.GetField(member, AnyInstance);
            if (field != null) return field.GetValue(instance);
            var property = type.GetProperty(member, AnyInstance);
            return property == null ? null : property.GetValue(instance, null);
        }

        public static T Get<T>(object instance, string member, T fallback = default(T))
        {
            try
            {
                var value = Get(instance, member);
                if (value == null) return fallback;
                if (value is T) return (T)value;
                return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        public static bool Set(object instance, string member, object value)
        {
            if (instance == null) return false;
            var type = instance.GetType();
            var field = type.GetField(member, AnyInstance);
            if (field != null)
            {
                field.SetValue(instance, ConvertFor(value, field.FieldType));
                return true;
            }
            var property = type.GetProperty(member, AnyInstance);
            if (property != null && property.CanWrite)
            {
                property.SetValue(instance, ConvertFor(value, property.PropertyType), null);
                return true;
            }
            return false;
        }

        public static object Call(object instance, string method, params object[] args)
        {
            if (instance == null) return null;
            args = args ?? Array.Empty<object>();
            var candidates = instance.GetType().GetMethods(AnyInstance)
                .Where(m => m.Name == method && m.GetParameters().Length == args.Length);
            foreach (var candidate in candidates)
            {
                var parameters = candidate.GetParameters();
                var converted = new object[args.Length];
                var compatible = true;
                for (var i = 0; i < args.Length; i++)
                {
                    try { converted[i] = ConvertFor(args[i], parameters[i].ParameterType); }
                    catch { compatible = false; break; }
                }
                if (!compatible) continue;
                return candidate.Invoke(instance, converted);
            }
            throw new MissingMethodException(instance.GetType().FullName, method);
        }

        public static object TryCall(object instance, string method, params object[] args)
        {
            try { return Call(instance, method, args); }
            catch { return null; }
        }

        public static int IntCall(object instance, string method, int fallback = 0)
        {
            try { return Convert.ToInt32(Call(instance, method), CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static uint UIntCall(object instance, string method, uint fallback = 0)
        {
            try { return Convert.ToUInt32(Call(instance, method), CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static byte[] BytesCall(object instance, string method)
        {
            return TryCall(instance, method) as byte[];
        }

        public static string ScalarObjectJson(object instance, params string[] preferredMembers)
        {
            if (instance == null) return "null";
            var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
            if (preferredMembers != null && preferredMembers.Length > 0)
            {
                foreach (var name in preferredMembers)
                {
                    var value = Get(instance, name);
                    if (IsScalar(value)) values[name] = value;
                }
            }
            else
            {
                foreach (var field in instance.GetType().GetFields(AnyInstance))
                {
                    object value;
                    try { value = field.GetValue(instance); } catch { continue; }
                    if (IsScalar(value)) values[field.Name] = value;
                }
                foreach (var property in instance.GetType().GetProperties(AnyInstance))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length != 0 || values.ContainsKey(property.Name)) continue;
                    object value;
                    try { value = property.GetValue(instance, null); } catch { continue; }
                    if (IsScalar(value)) values[property.Name] = value;
                }
            }
            return Json.Object(values);
        }

        private static bool IsScalar(object value)
        {
            if (value == null) return true;
            var type = value.GetType();
            return type.IsPrimitive || type.IsEnum || value is string || value is decimal;
        }

        private static object ConvertFor(object value, Type target)
        {
            if (value == null)
            {
                if (target.IsValueType && Nullable.GetUnderlyingType(target) == null) return Activator.CreateInstance(target);
                return null;
            }
            var unwrapped = Nullable.GetUnderlyingType(target) ?? target;
            if (unwrapped.IsInstanceOfType(value)) return value;
            if (unwrapped.IsEnum)
            {
                if (value is string) return Enum.Parse(unwrapped, (string)value, true);
                return Enum.ToObject(unwrapped, value);
            }
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
}
