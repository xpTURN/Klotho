using System;
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    public static class DataAssetReader
    {
        private static void ValidateHeader(ref SpanReader reader)
        {
            int magic = reader.ReadInt32();
            if (magic != DataAssetWriter.Magic)
                throw new InvalidOperationException(
                    $"Invalid DataAsset file: expected magic 0x{DataAssetWriter.Magic:X8}, got 0x{magic:X8}");
            int version = reader.ReadInt32();
            if (version != DataAssetWriter.Version)
                throw new InvalidOperationException(
                    $"Unsupported DataAsset version: {version}");
        }

        // --- Delegate cache ---

        public delegate T DeserializeDelegate<T>(ref SpanReader reader) where T : IDataAssetSerializable;

        private static class DeserializerCache<T> where T : IDataAssetSerializable
        {
            public static readonly DeserializeDelegate<T> Func;

            static DeserializerCache()
            {
                var method = typeof(T).GetMethod("Deserialize",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    new[] { typeof(SpanReader).MakeByRefType() },
                    null);

                if (method == null)
                    throw new InvalidOperationException(
                        $"Type {typeof(T).Name} does not have a generated static Deserialize method. " +
                        $"Ensure it has [KlothoDataAsset] attribute and is partial.");

                Func = (DeserializeDelegate<T>)method.CreateDelegate(typeof(DeserializeDelegate<T>));
            }
        }

        // --- bytes loading (single type) ---

        public static T LoadFromBytes<T>(byte[] data) where T : IDataAssetSerializable
        {
            var reader = new SpanReader(data, 0, data.Length);
            ValidateHeader(ref reader);
            return DeserializerCache<T>.Func(ref reader);
        }

        public static T LoadFromBytes<T>(ReadOnlySpan<byte> data) where T : IDataAssetSerializable
        {
            var reader = new SpanReader(data);
            ValidateHeader(ref reader);
            return DeserializerCache<T>.Func(ref reader);
        }

        public static List<T> LoadCollectionFromBytes<T>(byte[] data) where T : IDataAssetSerializable
        {
            var reader = new SpanReader(data, 0, data.Length);
            ValidateHeader(ref reader);
            int count = reader.ReadInt32();
            var result = new List<T>(count);
            for (int i = 0; i < count; i++)
            {
                int size = reader.ReadInt32();
                int startPos = reader.Position;
                result.Add(DeserializerCache<T>.Func(ref reader));
                int readBytes = reader.Position - startPos;
                if (readBytes != size)
                    throw new InvalidOperationException(
                        $"DataAsset size mismatch: header={size}, actual={readBytes}, type={typeof(T).Name}");
            }
            return result;
        }

        // --- Mixed type bytes (TypeId based) ---

        public static List<IDataAsset> LoadMixedCollectionFromBytes(string path)
        {
            var buffer = System.IO.File.ReadAllBytes(path);
            return LoadMixedCollectionFromBytes(buffer);
        }

        public static List<IDataAsset> LoadMixedCollectionFromBytes(byte[] data)
        {
            return LoadMixedCollectionFromBytes(data.AsSpan());
        }

        public static List<IDataAsset> LoadMixedCollectionFromBytes(ReadOnlySpan<byte> buffer)
        {
            DataAssetTypeRegistry.EnsureInitialized();

            var reader = new SpanReader(buffer);
            ValidateHeader(ref reader);
            int count = reader.ReadInt32();
            var result = new List<IDataAsset>(count);
            for (int i = 0; i < count; i++)
            {
                int typeId = reader.ReadInt32();
                int size = reader.ReadInt32();
                if (DataAssetTypeRegistry.IsRegistered(typeId))
                {
                    result.Add(DataAssetTypeRegistry.Deserialize(typeId, ref reader));
                }
                else
                {
                    reader.Skip(size);
                }
            }
            return result;
        }

    }
}
