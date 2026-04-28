namespace xpTURN.Klotho.Serialization
{
    public interface ISpanSerializable
    {
        void Serialize(ref SpanWriter writer);
        void Deserialize(ref SpanReader reader);
        int GetSerializedSize();
    }
}
