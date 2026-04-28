using System.Collections.Generic;

namespace xpTURN.Klotho.ECS
{
    public static class DataAssetRegistryExtensions
    {
        // --- bytes (single type) ---

        public static T LoadAndRegister<T>(this IDataAssetRegistryBuilder builder, byte[] data)
            where T : IDataAssetSerializable
        {
            var asset = DataAssetReader.LoadFromBytes<T>(data);
            builder.Register(asset);
            return asset;
        }

        public static List<T> LoadCollectionAndRegister<T>(this IDataAssetRegistryBuilder builder, byte[] data)
            where T : IDataAssetSerializable
        {
            var assets = DataAssetReader.LoadCollectionFromBytes<T>(data);
            foreach (var asset in assets)
                builder.Register(asset);
            return assets;
        }

        // --- bytes (mixed type) ---

        public static List<IDataAsset> LoadMixedAndRegister(this IDataAssetRegistryBuilder builder, string path)
        {
            var assets = DataAssetReader.LoadMixedCollectionFromBytes(path);
            return LoadMixedAndRegister(builder, assets);
        }

        public static List<IDataAsset> LoadMixedAndRegister(this IDataAssetRegistryBuilder builder, byte[] data)
        {
            var assets = DataAssetReader.LoadMixedCollectionFromBytes(data);
            return LoadMixedAndRegister(builder, assets);
        }

        public static List<IDataAsset> LoadMixedAndRegister(this IDataAssetRegistryBuilder builder, List<IDataAsset> assets)
        {
            builder.RegisterRange(assets);
            return assets;
        }
    }
}
