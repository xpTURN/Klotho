using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Response message acknowledging receipt of a player's command for a specific tick.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.CommandAck)]
    public partial class CommandAckMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Tick;

        [KlothoOrder]
        public int PlayerId;
    }
}
