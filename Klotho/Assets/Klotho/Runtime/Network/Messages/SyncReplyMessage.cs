using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Sync reply message (client → host, handshake)
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SyncReply)]
    public partial class SyncReplyMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Magic;

        [KlothoOrder]
        public int Sequence;

        [KlothoOrder]
        public int Attempt;

        [KlothoOrder]
        public long ClientTime;
    }
}
