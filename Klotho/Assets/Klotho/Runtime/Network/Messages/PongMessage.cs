using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Response message to Ping. Returns the received Timestamp and Sequence as-is.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.Pong)]
    public partial class PongMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public long Timestamp;

        [KlothoOrder]
        public int Sequence;
    }
}
