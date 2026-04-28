using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.ReconnectReject)]
    public partial class ReconnectRejectMessage : NetworkMessageBase
    {
        [KlothoOrder] public byte Reason; // 0=Unknown, 1=InvalidMagic, 2=InvalidPlayer, 3=Timeout, 4=AlreadyConnected
    }
}
