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

        // Diagnostic fingerprint of the sender's static environment (static colliders folded with
        // the navigation fingerprint). The receiver compares it against its own to
        // surface a static/navmesh mismatch at join time (neither is in StateHash).
        // 0 means "not provided" — both producers avoid 0, so the receiver skips it.
        [KlothoOrder]
        public long StaticFingerprint;

        public FullStateKind KindEnum
        {
            get => (FullStateKind)Kind;
            set => Kind = (byte)value;
        }
    }
}
