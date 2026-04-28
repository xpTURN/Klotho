using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.ReconnectRequest)]
    public partial class ReconnectRequestMessage : NetworkMessageBase
    {
        [KlothoOrder] public int SessionMagic;
        [KlothoOrder] public int PlayerId;
    }
}
