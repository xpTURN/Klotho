using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Sync complete message (host → client, handshake complete)
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SyncComplete)]
    public partial class SyncCompleteMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Magic;

        [KlothoOrder]
        public int PlayerId;

        [KlothoOrder]
        public long SharedEpoch;

        [KlothoOrder]
        public long ClockOffset;
    }
}
