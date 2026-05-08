using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Server → client: signals that all bootstrap-ready acks have been collected (or timed out)
    /// and the first server tick is about to start. Carries firstTick and tickStartTimeMs so the
    /// client can align its accumulator with the server's actual tick start. ReliableOrdered.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.BootstrapBegin)]
    public partial class BootstrapBeginMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int FirstTick;

        [KlothoOrder]
        public long TickStartTimeMs;

        public override int GetSerializedSize()
        {
            return 1 + 4 + 8;
        }

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(FirstTick);
            writer.WriteInt64(TickStartTimeMs);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            FirstTick = reader.ReadInt32();
            TickStartTimeMs = reader.ReadInt64();
        }
    }
}
