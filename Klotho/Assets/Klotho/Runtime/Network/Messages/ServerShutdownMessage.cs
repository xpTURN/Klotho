using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.Disconnect)]
    public partial class ServerShutdownMessage : NetworkMessageBase
    {
        [KlothoOrder] public byte Reason;
        // 0=Unknown, 1=ServerClosing, 2=RoomOverloaded, 3=Maintenance
    }
}
