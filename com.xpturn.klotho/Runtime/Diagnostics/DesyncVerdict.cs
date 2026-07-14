namespace xpTURN.Klotho.Diagnostics
{
    /// <summary>
    /// What kind of divergence produced a desync. Wire-stable byte values — append-only
    /// (<c>DesyncVerdictReportMessage.Class</c>).
    /// </summary>
    public enum DesyncClass : byte
    {
        /// <summary>The peers executed different command sets at the diverged tick.</summary>
        Input = 0,
        /// <summary>Same commands, different result — a determinism violation in game logic.</summary>
        State = 1,
        /// <summary>Static (non-simulated) geometry differs; not covered by the state hash.</summary>
        Static = 2,
        /// <summary>
        /// Classification unavailable: the responder could not supply its command digest for the
        /// diverged tick (the tick fell outside its input-buffer retention, e.g. after a resync
        /// cleared the buffer). The layer is still reported — only the input-vs-state call is withheld.
        /// P2P-local only: an SD client always holds both digests at detection, so this value never
        /// travels on the wire and the server drops a verdict carrying it.
        /// </summary>
        Unknown = 3,
    }

    /// <summary>
    /// Which layer of the hash fold the divergence was localized to. Wire-stable byte values —
    /// append-only (<c>DesyncVerdictReportMessage.Layer</c>).
    /// </summary>
    public enum DesyncLayer : byte
    {
        /// <summary>Not localized (probe failed, timed out, or the remote had no history).</summary>
        None = 0,
        /// <summary>A component type diverged — the index carries its registry typeId.</summary>
        Component = 1,
        /// <summary>A snapshot participant (system) diverged — the index carries its participant position.</summary>
        System = 2,
    }

    /// <summary>
    /// The conclusion of a desync diagnosis: which tick diverged, whether inputs or state caused
    /// it, and which component type / system it was localized to. Produced by the requester after it
    /// diffs its captured breakdown against the responder's; consumed as a structured log line (P2P)
    /// or serialized into a <c>DesyncVerdictReportMessage</c> (SD client → server).
    ///
    /// <para>Diagnostic-only by construction: it is derived from history values captured BEFORE any
    /// recovery ran, and no consumer feeds it back into the recovery ladder.</para>
    /// </summary>
    public struct DesyncVerdict
    {
        /// <summary>First tick whose state hash differed between the two peers.</summary>
        public int DivergedTick;

        public DesyncClass Class;

        public DesyncLayer Layer;

        /// <summary>
        /// Meaning depends on <see cref="Layer"/>: component typeId (Component), snapshot-participant
        /// index (System), or -1 (None). Never a raw array index for Component — the positional
        /// breakdown index is resolved to a registry typeId at diff time so the value survives the wire.
        /// </summary>
        public int TypeIdOrParticipantIdx;

        /// <summary>Requester's state hash at <see cref="DivergedTick"/> — diagnostic only.</summary>
        public long LocalHash;

        /// <summary>Responder's state hash at <see cref="DivergedTick"/> — diagnostic only.</summary>
        public long RemoteHash;
    }
}
