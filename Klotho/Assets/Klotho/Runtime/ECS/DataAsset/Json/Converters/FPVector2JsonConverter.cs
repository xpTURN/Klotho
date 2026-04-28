using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Json
{
    public sealed class FPVector2JsonConverter : JsonConverter<FPVector2>
    {
        public override FPVector2 ReadJson(JsonReader reader, Type objectType, FPVector2 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var obj = JObject.Load(reader);
            var x = FP64.FromDouble((double)(obj["x"] ?? obj["X"] ?? 0));
            var y = FP64.FromDouble((double)(obj["y"] ?? obj["Y"] ?? 0));
            return new FPVector2(x, y);
        }

        public override void WriteJson(JsonWriter writer, FPVector2 value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("x");
            writer.WriteValue(value.x.ToDouble());
            writer.WritePropertyName("y");
            writer.WriteValue(value.y.ToDouble());
            writer.WriteEndObject();
        }
    }
}
