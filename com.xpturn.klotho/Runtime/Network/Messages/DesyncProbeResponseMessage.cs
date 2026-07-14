using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Responder → requester (ReliableOrdered): the requested slice of this peer's diagnostic hash
    /// history. Served straight out of the history ring — no rewind, no re-simulation.
    ///
    /// <para>An empty <see cref="Payload"/> (entry count 0) is the "diagnostics unavailable" answer:
    /// history is disabled on this peer, or the asked-for ticks have already been overwritten. The
    /// requester degrades immediately instead of waiting out its timeout.</para>
    /// </summary>
    [KlothoSerializable(MessageTypeId = NetworkMessageType.DesyncProbeResponse)]
    public partial class DesyncProbeResponseMessage : NetworkMessageBase
    {
        /// <summary>Echo of the request's CorrelationId.</summary>
        [KlothoOrder]
        public int CorrelationId;

        /// <summary>DesyncProbeLevel — echo of the request's Level.</summary>
        [KlothoOrder]
        public byte Level;

        /// <summary>L1: the window start actually served. L2: the target tick.</summary>
        [KlothoOrder]
        public int BaseTick;

        /// <summary>
        /// P2P L2 only: the responder's command digest at the target tick, recomputed from its input
        /// buffer at serve time. It is what lets the requester tell input divergence from state
        /// divergence — the requester cannot derive the other peer's command set from its own.
        /// <para>0 = unavailable (the tick is outside the responder's input-buffer retention, or the
        /// buffer was cleared by a resync). 0 is a safe sentinel: the digest of an empty command set
        /// is the FNV offset basis, never 0. Unused (0) by SD, which classifies locally at detection.</para>
        /// </summary>
        [KlothoOrder]
        public long CmdHashAtTick;

        /// <summary>
        /// Level-dependent hand-packed blob — see <see cref="DesyncProbePayload"/>. Hand-packed rather
        /// than declared as typed arrays so the wire layout is explicit and shared with the offline
        /// dump reader.
        /// </summary>
        [KlothoOrder]
        public byte[] Payload;

        public DesyncProbeLevel LevelEnum
        {
            get => (DesyncProbeLevel)Level;
            set => Level = (byte)value;
        }
    }
}
