using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    [KlothoSerializable(MessageTypeId = NetworkMessageType.FullState)]
    public partial class FullStateResponseMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Tick;

        [KlothoOrder]
        public long StateHash;

        [KlothoOrder]
        public byte[] StateData;
    }
}
