namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Shared FullState serving policy. The P2P host (<see cref="KlothoNetworkService"/>) and the
    /// SD server (<see cref="ServerNetworkService"/>) apply the same per-peer cooldown, so the
    /// value lives here rather than being duplicated per service.
    /// </summary>
    internal static class ResyncPolicy
    {
        /// <summary>
        /// Per-peer cooldown before the authority serves another FullState to the same peer.
        /// A throttled request is dropped silently — the requester re-sends after its own
        /// resync timeout.
        /// <para>
        /// INVARIANT: must stay BELOW the requester's retry interval
        /// (KlothoEngine.RESYNC_TIMEOUT_MS). Otherwise a legitimate retry lands inside the
        /// cooldown window every time and resync starves. Pinned by
        /// ResyncPolicyInvariantTests.
        /// </para>
        /// </summary>
        internal const long RESYNC_RESPONSE_COOLDOWN_MS = 2000;
    }
}
