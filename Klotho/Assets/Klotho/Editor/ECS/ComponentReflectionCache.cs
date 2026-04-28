using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Editor.ECS
{
    /// <summary>
    /// Component field reflection cache. Caches the FieldInfo array and fixed-size field names.
    /// </summary>
    internal static class ComponentReflectionCache
    {
        static readonly Dictionary<Type, FieldInfo[]> s_fields = new();
        static readonly Dictionary<Type, HashSet<string>> s_fixedFieldNames = new();
        static readonly Dictionary<Type, bool> s_isPrimitive = new();

        public static FieldInfo[] GetFields(Type componentType)
        {
            if (s_fields.TryGetValue(componentType, out var fields)) return fields;

            fields = componentType.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            var fixedSet = new HashSet<string>();
            foreach (var fi in fields)
            {
                if (fi.GetCustomAttribute<FixedBufferAttribute>() != null)
                    fixedSet.Add(fi.Name);
            }
            s_fields[componentType] = fields;
            s_fixedFieldNames[componentType] = fixedSet;
            return fields;
        }

        public static bool IsFixedField(Type componentType, string fieldName)
            => s_fixedFieldNames.TryGetValue(componentType, out var set) && set.Contains(fieldName);

        public static bool HasPublicInstanceFields(Type type)
        {
            return type.GetFields(BindingFlags.Public | BindingFlags.Instance).Length > 0;
        }

        public static bool IsPrimitive(Type type)
        {
            if (s_isPrimitive.TryGetValue(type, out var cached)) return cached;
            var result = type.GetCustomAttribute<PrimitiveAttribute>() != null;
            s_isPrimitive[type] = result;
            return result;
        }
    }
}
