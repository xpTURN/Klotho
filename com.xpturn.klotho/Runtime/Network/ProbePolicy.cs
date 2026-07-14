using System.Collections.Generic;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Serving policy for the desync probe, shared by the P2P service and the SD server so the
    /// two roles cannot drift apart.
    ///
    /// <para>Probe serving gets its OWN budget, separate from the FullState cooldown. One desync fires
    /// a FullStateRequest and a ProbeRequest at nearly the same moment; if they shared a window the
    /// recovery request would take it and the probe would be dropped without a trace — the diagnosis
    /// would silently disappear exactly when it is needed.</para>
    /// </summary>
    internal static class ProbePolicy
    {
        /// <summary>
        /// Per-peer serve budget window. Reuses the FullState cooldown length — a probe episode and a
        /// resync episode are triggered by the same event, so aligning the windows keeps their
        /// throttles from interleaving into surprising patterns.
        /// </summary>
        internal const long PROBE_SERVE_WINDOW_MS = ResyncPolicy.RESYNC_RESPONSE_COOLDOWN_MS;

        /// <summary>
        /// Serves allowed per window, per peer.
        /// <para>MUST be at least 2, and that is not a tuning choice: one episode is TWO sequential
        /// serves to the SAME responder (L1, then L2 for the tick L1 identified), and L2 follows L1 by
        /// about one RTT — far inside the window. A one-per-window cooldown would drop the L2 of every
        /// honest episode and the P2P diagnosis would never complete. An honest requester is capped at
        /// one episode per window anyway (one probe in flight at a time), so 2 lets every legitimate
        /// episode through while a flooding peer is throttled after its second serve.</para>
        /// </summary>
        internal const int PROBE_SERVE_BURST = 2;

        /// <summary>
        /// Deadline for one probe round trip (L1 or L2 individually). Sized against RTT, NOT against
        /// the resync retry interval: a probe is an observation running alongside recovery, not a step
        /// of it, and the captured history stays valid however recovery proceeds. On expiry the
        /// pending entry and its capture are dropped with a single log line and nothing is retried —
        /// losing a diagnosis is strictly better than retrying into a peer that is already struggling.
        /// </summary>
        internal const long PROBE_TIMEOUT_MS = 2000;

        /// <summary>
        /// Per-peer serve budget. Dropped serves only bump <see cref="Drops"/>: logging per drop would
        /// hand a flooding peer a string-alloc + log-write path, which is the cost the budget exists to
        /// deny. The accumulated count is surfaced on the next successful serve instead.
        /// </summary>
        internal struct PeerServeState
        {
            public long WindowStartMs;
            public int ServesInWindow;
            public int Drops;
        }

        /// <summary>
        /// Token-bucket admission for one serve. True when the serve is allowed;
        /// <paramref name="throttledSinceLastServe"/> then carries (and clears) the drops accumulated
        /// since the previous allowed serve. False bumps the drop counter and writes nothing.
        /// </summary>
        internal static bool TryConsumeServe(Dictionary<int, PeerServeState> budget, int peerId, long nowMs,
            out int throttledSinceLastServe)
        {
            throttledSinceLastServe = 0;

            if (!budget.TryGetValue(peerId, out var state) || nowMs - state.WindowStartMs >= PROBE_SERVE_WINDOW_MS)
            {
                // First serve, or the previous window has elapsed — open a fresh one.
                throttledSinceLastServe = state.Drops;
                budget[peerId] = new PeerServeState { WindowStartMs = nowMs, ServesInWindow = 1, Drops = 0 };
                return true;
            }

            if (state.ServesInWindow >= PROBE_SERVE_BURST)
            {
                state.Drops++;
                budget[peerId] = state;
                return false;
            }

            state.ServesInWindow++;
            throttledSinceLastServe = state.Drops;
            state.Drops = 0;
            budget[peerId] = state;
            return true;
        }
    }
}
