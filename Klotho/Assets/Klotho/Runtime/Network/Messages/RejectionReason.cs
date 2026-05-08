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
        ToleranceExceeded = 2,

        // Game-layer (application) — receiver updates game-layer state (latch clear / UI / cooldown).
        // 10+ append-only.
        Duplicate = 10,
    }
}
