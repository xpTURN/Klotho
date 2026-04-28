using System;
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    public static class DataAssetWriter
    {
        internal const int Magic = 0x58504441; // "XPDA"
        internal const int Version = 1;
        internal const int HeaderSize = 8; // Magic(4) + Version(4)

        private static void WriteHeader(ref SpanWriter writer)
        {
            writer.WriteInt32(Magic);
            writer.WriteInt32(Version);
        }

        // --- bytes serialization ---

        public static byte[] SerializeToBytes<T>(T asset) where T : IDataAssetSerializable
        {
            int size = HeaderSize + asset.GetSerializedSize();
            var buffer = new byte[size];
            var writer = new SpanWriter(buffer);
            WriteHeader(ref writer);
            asset.Serialize(ref writer);
            return buffer;
        }

        public static byte[] SerializeCollectionToBytes<T>(IReadOnlyList<T> assets)
            where T : IDataAssetSerializable
        {
            int totalSize = HeaderSize + 4;
            for (int i = 0; i < assets.Count; i++)
                totalSize += 4 + assets[i].GetSerializedSize();

            var buffer = new byte[totalSize];
            var writer = new SpanWriter(buffer);
            WriteHeader(ref writer);
            writer.WriteInt32(assets.Count);
            for (int i = 0; i < assets.Count; i++)
            {
                writer.WriteInt32(assets[i].GetSerializedSize());
                assets[i].Serialize(ref writer);
            }
            return buffer;
        }

        // --- Mixed type bytes (TypeId based) ---

        public static void SaveMixedCollectionToFile(string path, IReadOnlyList<IDataAssetSerializable> assets)
        {
            var bytes = SerializeMixedCollectionToBytes(assets);
            System.IO.File.WriteAllBytes(path, bytes);
        }

        public static void SaveToFile(string path, byte[] bytes)
        {
            System.IO.File.WriteAllBytes(path, bytes);
        }

        public static byte[] SerializeMixedCollectionToBytes(IReadOnlyList<IDataAssetSerializable> assets)
        {
            int totalSize = HeaderSize + 4;
            for (int i = 0; i < assets.Count; i++)
                totalSize += 4 + 4 + assets[i].GetSerializedSize();

            var buffer = new byte[totalSize];
            var writer = new SpanWriter(buffer);
            WriteHeader(ref writer);
            writer.WriteInt32(assets.Count);
            for (int i = 0; i < assets.Count; i++)
            {
                writer.WriteInt32(DataAssetTypeRegistry.GetTypeId(assets[i].GetType()));
                writer.WriteInt32(assets[i].GetSerializedSize());
                assets[i].Serialize(ref writer);
            }
            return buffer;
        }
    }
}
