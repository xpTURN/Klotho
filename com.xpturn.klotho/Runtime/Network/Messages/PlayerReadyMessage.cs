using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Player ready message
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.PlayerReady)]
    public partial class PlayerReadyMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int PlayerId;

        [KlothoOrder]
        public bool IsReady;

        // Setup fingerprints, appended after the existing fields (order numbers of those stay put —
        // see Docs/Serialization.md). Two fields rather than one fold so the receiver can react
        // differently: a layout difference is state-hash input and fatal from tick 0, an environment
        // difference is outside the state hash. 0 = "not provided" on both, same sentinel the
        // FullState path uses.
        /// <summary>Component-registry layout fingerprint (stage- and rebake-invariant).</summary>
        [KlothoOrder]
        public long LayoutFingerprint;

        /// <summary>Static colliders XOR navmesh XOR game slot, registry excluded. Moves with runtime rebakes.</summary>
        [KlothoOrder]
        public long EnvironmentFingerprint;
    }
}
