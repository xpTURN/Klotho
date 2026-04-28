namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Marker interface for ECS components.
    /// All components must be unmanaged structs that implement this interface.
    /// </summary>
    public interface IComponent
    {
        ulong GetHash(ulong hash);
        void Serialize(ref Serialization.SpanWriter writer);
        void Deserialize(ref Serialization.SpanReader reader);
        int GetSerializedSize();
    }
}
