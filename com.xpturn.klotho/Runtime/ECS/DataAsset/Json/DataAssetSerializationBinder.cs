using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace xpTURN.Klotho.ECS.Json
{
    public sealed class DataAssetSerializationBinder : ISerializationBinder
    {
        private readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
        private readonly Dictionary<string, Type> _typeNameCache = new Dictionary<string, Type>();
        private bool _scanned;

        public Type BindToType(string assemblyName, string typeName)
        {
            var key = $"{typeName}, {assemblyName}";
            if (_typeCache.TryGetValue(key, out var cached))
                return cached;

            EnsureScanned();

            if (_typeCache.TryGetValue(key, out cached))
                return cached;

            // Fallback: match by type name only — assembly name may differ across projects.
            if (_typeNameCache.TryGetValue(typeName, out cached))
                return cached;

            throw new JsonSerializationException(
                $"Type '{typeName}, {assemblyName}' is not a registered IDataAsset implementation.");
        }

        public void BindToName(Type serializedType, out string assemblyName, out string typeName)
        {
            assemblyName = serializedType.Assembly.GetName().Name;
            typeName = serializedType.FullName;
        }

        private void EnsureScanned()
        {
            if (_scanned) return;
            _scanned = true;

            var iDataAsset = typeof(IDataAsset);
#pragma warning disable UAC0005 // Unity's Mono editor returns the same list as CurrentAssemblies.GetLoadedAssemblies(), which this engine-free assembly cannot call. Revisit on a CoreCLR editor.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
#pragma warning restore UAC0005
            {
                var asmName = asm.GetName().Name;
                if (asmName.StartsWith("System") || asmName.StartsWith("Microsoft")
                    || asmName.StartsWith("Unity") || asmName.StartsWith("mscorlib"))
                    continue;

                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (!type.IsAbstract && !type.IsInterface && iDataAsset.IsAssignableFrom(type))
                        {
                            _typeCache[$"{type.FullName}, {asmName}"] = type;
                            _typeNameCache.TryAdd(type.FullName, type);
                        }
                    }
                }
                catch (System.Reflection.ReflectionTypeLoadException)
                {
                }
            }
        }
    }
}
