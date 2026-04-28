using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Sync request message (Host → Client, handshake)
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SyncRequest)]
    public partial class SyncRequestMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Magic;

        [KlothoOrder]
        public int Sequence;

        [KlothoOrder]
        public int Attempt;

        [KlothoOrder]
        public long HostTime;
    }
}
