using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DKCTileStreamTracer
{
    internal static class R
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal);

        public static Type Type(string name)
        {
            Type value;
            if (Types.TryGetValue(name, out value)) return value;
            value = AccessTools.TypeByName(name);
            if (value != null) Types[name] = value;
            return value;
        }

        public static object Static(string typeName, string member)
        {
            var type = Type(typeName);
            if (type == null) return null;
            var field = type.GetField(member, StaticFlags);
            if (field != null) return field.GetValue(null);
            var property = type.GetProperty(member, StaticFlags);
            return property == null ? null : property.GetValue(null, null);
        }

        public static object Get(object instance, string member)
        {
            if (instance == null) return null;
            var type = instance.GetType();
            var field = type.GetField(member, InstanceFlags);
            if (field != null) return field.GetValue(instance);
            var property = type.GetProperty(member, InstanceFlags);
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
            foreach (var candidate in instance.GetType().GetMethods(InstanceFlags).Where(m => m.Name == method && m.GetParameters().Length == args.Length))
            {
                try { return candidate.Invoke(instance, args); }
                catch (ArgumentException) { }
            }
            return null;
        }

        public static uint UIntCall(object instance, string method, uint fallback = 0)
        {
            try { return Convert.ToUInt32(Call(instance, method), CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static int IntCall(object instance, string method, int fallback = 0)
        {
            try { return Convert.ToInt32(Call(instance, method), CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static byte[] BytesCall(object instance, string method)
        {
            return Call(instance, method) as byte[];
        }
    }
}
