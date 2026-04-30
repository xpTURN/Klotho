using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.ReconnectRequest)]
    public partial class ReconnectRequestMessage : NetworkMessageBase
    {
        [KlothoOrder] public long SessionMagic;
        [KlothoOrder] public int PlayerId;
        [KlothoOrder] public string DeviceId;
    }
}
