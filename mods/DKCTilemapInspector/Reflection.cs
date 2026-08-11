using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DKCTilemapInspector
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
            var field = instance.GetType().GetField(member, AnyInstance);
            if (field != null) return field.GetValue(instance);
            var property = instance.GetType().GetProperty(member, AnyInstance);
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
            catch { return fallback; }
        }

        public static object Call(object instance, string method, params object[] args)
        {
            if (instance == null) return null;
            args = args ?? Array.Empty<object>();
            foreach (var candidate in instance.GetType().GetMethods(AnyInstance)
                .Where(m => m.Name == method && m.GetParameters().Length == args.Length))
            {
                try { return candidate.Invoke(instance, args); }
                catch (ArgumentException) { }
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

        public static byte[] BytesCall(object instance, string method)
        {
            return TryCall(instance, method) as byte[];
        }
    }
}
