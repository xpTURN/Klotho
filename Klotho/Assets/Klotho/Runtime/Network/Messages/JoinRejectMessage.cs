using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.JoinReject)]
    public partial class JoinRejectMessage : NetworkMessageBase
    {
        [KlothoOrder] public byte Reason;
        // 0=Unknown, 1=RoomNotFound, 2=RoomFull, 3=ServerFull, 4=LateJoinDisabled, 5=RoomClosing
    }
}
