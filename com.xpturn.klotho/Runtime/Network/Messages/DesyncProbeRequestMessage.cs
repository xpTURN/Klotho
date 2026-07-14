using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Which slice of the responder's diagnostic history a probe asks for.
    /// Wire-stable byte values — append-only.
    /// </summary>
    public enum DesyncProbeLevel : byte
    {
        /// <summary>Per-tick total hashes over a window — used to narrow the divergence to one tick.</summary>
        TickHashes = 1,
        /// <summary>The layered breakdown at a single tick — used to localize the diverging layer.</summary>
        Breakdown = 2,
    }

    /// <summary>
    /// Requester → responder (ReliableOrdered): asks the peer this machine disagrees with for its own
    /// recorded hashes around the divergence. The responder serves from its diagnostic hash-history
    /// ring and NEVER rewinds or re-simulates to answer — a miss is answered with an empty
    /// payload, not by reconstructing the past.
    ///
    /// <para>The round trip is two sequential requests: Level 1 returns the per-tick totals over
    /// [FromTick..ToTick] so the requester can find the first diverging tick T₀, then Level 2 returns
    /// the breakdown at T₀ so it can name the diverging component type / system.</para>
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.DesyncProbeRequest)]
    public partial class DesyncProbeRequestMessage : NetworkMessageBase
    {
        /// <summary>Requester-assigned; echoed by the response so the requester can match the round trip.</summary>
        [KlothoOrder]
        public int CorrelationId;

        /// <summary>DesyncProbeLevel.</summary>
        [KlothoOrder]
        public byte Level;

        /// <summary>L1: window start. L2: the target tick (== ToTick).</summary>
        [KlothoOrder]
        public int FromTick;

        /// <summary>L1: window end. L2: the target tick.</summary>
        [KlothoOrder]
        public int ToTick;

        /// <summary>
        /// Logging only — NEVER used to route the response. The responder replies to the transport
        /// peerId the request arrived on; honouring this field would let a forged value redirect a
        /// response at another peer (information leak + amplification).
        /// </summary>
        [KlothoOrder]
        public int RequesterPlayerId;

        public DesyncProbeLevel LevelEnum
        {
            get => (DesyncProbeLevel)Level;
            set => Level = (byte)value;
        }
    }
}
