using System;
using System.Linq;
using System.Reflection;

namespace DKCDebugInvincibility
{
    internal static class Reflect
    {
        public static Type Type(string shortName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try
                {
                    type = assembly.GetType(shortName, false)
                        ?? assembly.GetTypes().FirstOrDefault(candidate => candidate.Name == shortName);
                }
                catch (ReflectionTypeLoadException ex)
                {
                    type = ex.Types.FirstOrDefault(candidate => candidate != null && candidate.Name == shortName);
                }
                catch { }
                if (type != null) return type;
            }
            return null;
        }

        public static object Static(string typeName, string member)
        {
            var type = Type(typeName);
            return type == null ? null : Get(type, null, member, BindingFlags.Static);
        }

        public static object Get(object instance, string member)
        {
            return instance == null ? null : Get(instance.GetType(), instance, member, BindingFlags.Instance);
        }

        private static object Get(Type type, object instance, string member, BindingFlags scope)
        {
            const BindingFlags common = BindingFlags.Public | BindingFlags.NonPublic;
            var property = type.GetProperty(member, scope | common);
            if (property != null) return property.GetValue(instance, null);
            var field = type.GetField(member, scope | common);
            return field == null ? null : field.GetValue(instance);
        }

        public static object TryCall(object instance, string method)
        {
            if (instance == null) return null;
            try
            {
                var target = instance.GetType().GetMethod(method,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, System.Type.EmptyTypes, null);
                return target == null ? null : target.Invoke(instance, null);
            }
            catch { return null; }
        }
    }
}
