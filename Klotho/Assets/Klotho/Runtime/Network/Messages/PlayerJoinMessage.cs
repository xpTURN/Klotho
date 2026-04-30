using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.PlayerJoin)]
    public partial class PlayerJoinMessage : NetworkMessageBase
    {
        [KlothoOrder] public string DeviceId;
    }
}
