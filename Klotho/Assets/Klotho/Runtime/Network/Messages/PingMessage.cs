using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Ping request message for RTT measurement. Includes Timestamp and Sequence.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.Ping)]
    public partial class PingMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public long Timestamp;

        [KlothoOrder]
        public int Sequence;
    }
}
