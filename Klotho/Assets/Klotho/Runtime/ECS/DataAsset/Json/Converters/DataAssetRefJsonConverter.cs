using System;
using Newtonsoft.Json;

namespace xpTURN.Klotho.ECS.Json
{
    public sealed class DataAssetRefJsonConverter : JsonConverter<DataAssetRef>
    {
        public override DataAssetRef ReadJson(JsonReader reader, Type objectType, DataAssetRef existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return new DataAssetRef(Convert.ToInt32(reader.Value));
        }

        public override void WriteJson(JsonWriter writer, DataAssetRef value, JsonSerializer serializer)
        {
            writer.WriteValue(value.Id);
        }
    }
}
