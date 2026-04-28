using System.Text;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SpectatorJoin)]
    public partial class SpectatorJoinMessage : NetworkMessageBase
    {
        public string SpectatorName;

        public override int GetSerializedSize()
            => base.GetSerializedSize() + 4 + Encoding.UTF8.GetByteCount(SpectatorName ?? string.Empty);

        protected override void SerializeData(ref SpanWriter writer)
            => writer.WriteString(SpectatorName);

        protected override void DeserializeData(ref SpanReader reader)
            => SpectatorName = reader.ReadString();
    }
}
