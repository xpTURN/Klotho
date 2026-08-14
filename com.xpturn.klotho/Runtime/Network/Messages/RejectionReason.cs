namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Reason a server-side command rejection is reported to the originating client.
    /// Wire-stable: byte underlying type, append-only — never renumber existing values.
    /// Two categories distinguished by name shape (transport-noun vs state-form).
    /// </summary>
    public enum RejectionReason : byte
    {
        // Transport-level (InputCollector) — receiver corrects clock / lead / session integrity.
        // 0..9 reserved for transport-level reasons.
        PeerMismatch = 0,
        PastTick = 1,
        /// <summary>
        /// Deprecated — no longer emitted. The effective input deadline is the
        /// tick's execution moment, covered by <see cref="PastTick"/>. Member retained:
        /// wire-stable byte values must never be renumbered, and handler branches /
        /// recorded replays may still reference it.
        /// </summary>
        ToleranceExceeded = 2,

        // Game-layer (application) — receiver updates game-layer state (latch clear / UI / cooldown).
        // 10+ append-only.
        Duplicate = 10,
        /// <summary>Building placement rejected (overlap / boundary / outside walkable / cap).</summary>
        InvalidPlacement = 11,
    }
}
