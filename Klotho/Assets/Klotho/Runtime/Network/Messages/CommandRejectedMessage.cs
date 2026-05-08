using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Server → originating client: notification that a command was rejected.
    /// Unreliable — feedback is a hint, not a determinism requirement; loss costs at most one cooldown
    /// of latch persistence, and game-layer fallback (state-driven query) recovers naturally.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.CommandRejected)]
    public partial class CommandRejectedMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Tick;

        [KlothoOrder]
        public int CommandTypeId;

        [KlothoOrder]
        public byte Reason;

        public RejectionReason ReasonEnum
        {
            get => (RejectionReason)Reason;
            set => Reason = (byte)value;
        }

        public override int GetSerializedSize()
        {
            return 1 + 4 + 4 + 1;
        }

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(Tick);
            writer.WriteInt32(CommandTypeId);
            writer.WriteByte(Reason);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            Tick = reader.ReadInt32();
            CommandTypeId = reader.ReadInt32();
            Reason = reader.ReadByte();
        }
    }
}
