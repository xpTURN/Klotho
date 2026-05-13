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
        public long Magic;

        [KlothoOrder]
        public int PlayerId;

        [KlothoOrder]
        public long SharedEpoch;

        [KlothoOrder]
        public long ClockOffset;

        // Server-recommended extra InputDelay ticks for normal-join player seed.
        // Trailing field — backward-compat with older clients via deserialize underrun (server-first deploy assumption).
        [KlothoOrder]
        public int RecommendedExtraDelay;
    }
}
