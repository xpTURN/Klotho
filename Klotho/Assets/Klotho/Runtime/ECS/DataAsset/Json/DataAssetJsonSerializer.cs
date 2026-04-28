using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace xpTURN.Klotho.ECS.Json
{
    public static class DataAssetJsonSerializer
    {
        private static readonly JsonSerializerSettings Settings;

        static DataAssetJsonSerializer()
        {
            Settings = new JsonSerializerSettings
            {
                ContractResolver = new DataAssetContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                TypeNameHandling = TypeNameHandling.Auto,
                SerializationBinder = new DataAssetSerializationBinder(),
            };

            Settings.Converters.Add(new FP64JsonConverter());
            Settings.Converters.Add(new FPVector2JsonConverter());
            Settings.Converters.Add(new FPVector3JsonConverter());
            Settings.Converters.Add(new DataAssetRefJsonConverter());
        }

        public static T Deserialize<T>(string json) where T : IDataAsset
            => JsonConvert.DeserializeObject<T>(json, Settings);

        public static List<T> DeserializeCollection<T>(string json) where T : IDataAsset
            => JsonConvert.DeserializeObject<List<T>>(json, Settings);

        public static IDataAsset Deserialize(string json)
            => JsonConvert.DeserializeObject<IDataAsset>(json, Settings);

        public static List<IDataAsset> DeserializeMixedCollection(string json)
            => JsonConvert.DeserializeObject<List<IDataAsset>>(json, Settings);

        public static string Serialize<T>(T asset) where T : IDataAsset
            => JsonConvert.SerializeObject(asset, Formatting.Indented, Settings);

        public static string SerializeCollection<T>(IReadOnlyList<T> assets) where T : IDataAsset
            => JsonConvert.SerializeObject(assets, Formatting.Indented, Settings);

        public static string SerializeMixedCollection(IReadOnlyList<IDataAsset> assets)
            => JsonConvert.SerializeObject(assets, Formatting.Indented, Settings);
    }
}
