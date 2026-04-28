using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.RoomHandshake)]
    public partial class RoomHandshakeMessage : NetworkMessageBase
    {
        [KlothoOrder] public int RoomId;
    }
}
