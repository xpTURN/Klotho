using System;
using Newtonsoft.Json;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS.Json
{
    public sealed class FP64JsonConverter : JsonConverter<FP64>
    {
        public override FP64 ReadJson(JsonReader reader, Type objectType, FP64 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Float || reader.TokenType == JsonToken.Integer)
                return FP64.FromDouble(Convert.ToDouble(reader.Value));

            if (reader.TokenType == JsonToken.String)
            {
                var str = (string)reader.Value;
                if (str.StartsWith("raw:") && long.TryParse(str.Substring(4), out var raw))
                    return FP64.FromRaw(raw);
                if (double.TryParse(str, out var d))
                    return FP64.FromDouble(d);
            }

            throw new JsonSerializationException($"Cannot convert {reader.TokenType} to FP64");
        }

        public override void WriteJson(JsonWriter writer, FP64 value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToDouble());
        }
    }
}
