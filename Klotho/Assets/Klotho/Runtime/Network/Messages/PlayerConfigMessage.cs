using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Per-player custom data transmission message.
    /// Flow: client → host → broadcast to all peers.
    /// ConfigData carries the serialized PlayerConfigBase bytes.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.PlayerConfig)]
    public partial class PlayerConfigMessage : NetworkMessageBase
    {
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public byte[] ConfigData;
    }
}
