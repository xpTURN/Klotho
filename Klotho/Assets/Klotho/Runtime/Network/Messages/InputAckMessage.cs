using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Server → client: input receipt acknowledgement (Unreliable).
    /// Auxiliary channel that lets the client prune its resend queue early.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.InputAck)]
    public partial class InputAckMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int AckedTick;

        public override int GetSerializedSize()
        {
            return 1 + 4;
        }

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(AckedTick);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            AckedTick = reader.ReadInt32();
        }
    }
}
