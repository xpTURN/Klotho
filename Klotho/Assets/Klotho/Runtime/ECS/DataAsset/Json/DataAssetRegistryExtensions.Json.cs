using System.Collections.Generic;

namespace xpTURN.Klotho.ECS.Json
{
    public static class DataAssetRegistryJsonExtensions
    {
        public static T LoadFromJsonAndRegister<T>(this IDataAssetRegistryBuilder builder, string json)
            where T : IDataAsset
        {
            var asset = DataAssetJsonSerializer.Deserialize<T>(json);
            builder.Register(asset);
            return asset;
        }

        public static List<T> LoadCollectionFromJsonAndRegister<T>(this IDataAssetRegistryBuilder builder, string json)
            where T : IDataAsset
        {
            var assets = DataAssetJsonSerializer.DeserializeCollection<T>(json);
            foreach (var asset in assets)
                builder.Register(asset);
            return assets;
        }

        public static List<IDataAsset> LoadMixedFromJsonAndRegister(this IDataAssetRegistryBuilder builder, string json)
        {
            var assets = DataAssetJsonSerializer.DeserializeMixedCollection(json);
            builder.RegisterRange(assets);
            return assets;
        }
    }
}
