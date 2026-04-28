using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.FullStateRequest)]
    public partial class FullStateRequestMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int RequestTick;
    }
}
