using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Client → server: signals that Initial FullState has been applied and the client is ready
    /// for the first server tick. Sent right after HandleInitialFullStateReceived. ReliableOrdered.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.PlayerBootstrapReady)]
    public partial class PlayerBootstrapReadyMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int PlayerId;

        public override int GetSerializedSize()
        {
            return 1 + 4;
        }

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(PlayerId);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            PlayerId = reader.ReadInt32();
        }
    }
}
