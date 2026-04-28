using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.ECS
{
    public interface IDataAssetSerializable : IDataAsset
    {
        int GetSerializedSize();
        void Serialize(ref SpanWriter writer);
    }
}
