using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.JoinReject)]
    public partial class JoinRejectMessage : NetworkMessageBase
    {
        [KlothoOrder] public byte Reason;
        // Wire reason codes (also the disconnect-payload byte; mapped by JoinFailReason.FromJoinReject):
        // 0=Unknown, 1=RoomNotFound, 2=RoomFull, 3=ServerFull, 4=LateJoinDisabled, 5=RoomClosing,
        // 6=IdentityInvalid, 7=IdentityExpired, 8=IdentitySessionMismatch, 9=IdentityRejected,
        // 10=IdentityRequired, 11=IdentityValidationFailed, 12=LayoutMismatch.
    }
}
