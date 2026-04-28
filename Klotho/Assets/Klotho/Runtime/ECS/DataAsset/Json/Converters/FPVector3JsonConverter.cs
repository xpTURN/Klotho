using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Json
{
    public sealed class FPVector3JsonConverter : JsonConverter<FPVector3>
    {
        public override FPVector3 ReadJson(JsonReader reader, Type objectType, FPVector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var obj = JObject.Load(reader);
            var x = FP64.FromDouble((double)(obj["x"] ?? obj["X"] ?? 0));
            var y = FP64.FromDouble((double)(obj["y"] ?? obj["Y"] ?? 0));
            var z = FP64.FromDouble((double)(obj["z"] ?? obj["Z"] ?? 0));
            return new FPVector3(x, y, z);
        }

        public override void WriteJson(JsonWriter writer, FPVector3 value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("x");
            writer.WriteValue(value.x.ToDouble());
            writer.WritePropertyName("y");
            writer.WriteValue(value.y.ToDouble());
            writer.WritePropertyName("z");
            writer.WriteValue(value.z.ToDouble());
            writer.WriteEndObject();
        }
    }
}
