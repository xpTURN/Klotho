using System.Collections.Generic;
using System.Linq;

namespace xpTURN.Klotho.ECS.Json
{
    public static class DataAssetJsonConverter
    {
        // --- JSON loading (generic — single type) ---

        public static T LoadFromJson<T>(string json) where T : IDataAsset
            => DataAssetJsonSerializer.Deserialize<T>(json);

        public static List<T> LoadCollectionFromJson<T>(string json) where T : IDataAsset
            => DataAssetJsonSerializer.DeserializeCollection<T>(json);

        // --- JSON loading (non-generic — $type polymorphism) ---

        public static IDataAsset LoadFromJson(string json)
            => DataAssetJsonSerializer.Deserialize(json);

        public static List<IDataAsset> LoadMixedCollectionFromJson(string json)
            => DataAssetJsonSerializer.DeserializeMixedCollection(json);

        // --- Conversion: JSON → bytes (single type) ---

        public static byte[] ConvertJsonToBytes<T>(string json) where T : IDataAssetSerializable
        {
            var asset = DataAssetJsonSerializer.Deserialize<T>(json);
            return DataAssetWriter.SerializeToBytes(asset);
        }

        public static byte[] ConvertJsonCollectionToBytes<T>(string json) where T : IDataAssetSerializable
        {
            var assets = DataAssetJsonSerializer.DeserializeCollection<T>(json);
            return DataAssetWriter.SerializeCollectionToBytes(assets);
        }

        // --- Conversion: JSON → bytes (mixed type) ---

        public static byte[] ConvertMixedJsonToBytes(string json)
        {
            var assets = DataAssetJsonSerializer.DeserializeMixedCollection(json);
            var serializable = new List<IDataAssetSerializable>(assets.Count);
            foreach (var asset in assets)
                serializable.Add((IDataAssetSerializable)asset);
            return DataAssetWriter.SerializeMixedCollectionToBytes(serializable);
        }
    }
}
