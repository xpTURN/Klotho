using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Message that carries the simulation hash for a specific tick to verify state synchronization.
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.SyncHash)]
    public partial class SyncHashMessage : NetworkMessageBase
    {
        [KlothoOrder]
        public int Tick;

        [KlothoOrder]
        public long Hash;

        [KlothoOrder]
        public int PlayerId;

        /// <summary>
        /// FNV digest of the tick's canonically-ordered executed command set. Compared alongside
        /// <see cref="Hash"/> to classify a desync: differing CommandHash ⇒ input divergence (propagation
        /// / buffer / ordering — engine-side); equal CommandHash with differing <see cref="Hash"/> ⇒ state
        /// divergence (determinism violation — game-side). Diagnostic-only; never drives recovery.
        /// </summary>
        [KlothoOrder]
        public long CommandHash;
    }
}
