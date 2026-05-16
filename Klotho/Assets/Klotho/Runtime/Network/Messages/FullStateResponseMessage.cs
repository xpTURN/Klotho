using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    public enum FullStateKind : byte
    {
        Unicast = 0,
        CorrectiveReset,
        InitialState,
    }

    [KlothoSerializable(MessageTypeId = NetworkMessageType.FullState)]
    public partial class FullStateResponseMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Tick;

        [KlothoOrder]
        public long StateHash;

        [KlothoOrder]
        public byte[] StateData;

        [KlothoOrder]
        public byte Kind;

        public FullStateKind KindEnum
        {
            get => (FullStateKind)Kind;
            set => Kind = (byte)value;
        }
    }
}
