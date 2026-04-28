using System;
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    public static class DataAssetTypeRegistry
    {
        public delegate IDataAsset DeserializeFunc(ref SpanReader reader);

        private static readonly Dictionary<int, DeserializeFunc> _deserializers = new Dictionary<int, DeserializeFunc>();
        private static readonly Dictionary<int, Type> _types = new Dictionary<int, Type>();
        private static readonly Dictionary<Type, int> _typeIds = new Dictionary<Type, int>();

        public static void Register(int typeId, Type assetType, DeserializeFunc deserializer)
        {
            if (_deserializers.ContainsKey(typeId))
                throw new InvalidOperationException(
                    $"Duplicate DataAsset TypeId: {typeId} (existing: {_types[typeId].Name}, new: {assetType.Name})");
            _deserializers[typeId] = deserializer;
            _types[typeId] = assetType;
            _typeIds[assetType] = typeId;
        }

        /// <summary>
        /// Forces execution of [ModuleInitializer] in all assemblies to
        /// guarantee DataAsset Registrar registration.
        /// Must be called before deserialization such as LoadMixedCollectionFromBytes.
        /// </summary>
        public static void EnsureInitialized()
        {
            Core.ModuleInitializerHelper.EnsureAll();
        }

        public static IDataAsset Deserialize(int typeId, ref SpanReader reader)
        {
            if (!_deserializers.TryGetValue(typeId, out var func))
                throw new KeyNotFoundException($"DataAsset TypeId not found: {typeId}");
            return func(ref reader);
        }

        public static bool IsRegistered(int typeId) => _deserializers.ContainsKey(typeId);

        public static int GetTypeId<T>() where T : IDataAssetSerializable => _typeIds[typeof(T)];

        public static int GetTypeId(Type assetType) => _typeIds[assetType];
    }
}
