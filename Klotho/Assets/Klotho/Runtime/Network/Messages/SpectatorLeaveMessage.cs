using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SpectatorLeave)]
    public partial class SpectatorLeaveMessage : NetworkMessageBase
    {
        [KlothoOrder] public int SpectatorId;
    }
}
