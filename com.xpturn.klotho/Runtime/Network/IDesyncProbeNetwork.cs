using System;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Transport surface for the online desync diagnostic round trip. Kept OFF
    /// <see cref="IKlothoNetworkService"/> deliberately: this is a diagnostic side channel, not part of
    /// the lockstep contract, so a service (or a test double) that does not implement it simply has no
    /// probe — the engine treats a null cast as "diagnostics unavailable" and degrades to the local
    /// per-peer logs. Adding it to the core interface would instead force every implementer to carry
    /// dead no-op stubs.
    ///
    /// <para>The methods are role-agnostic; each implementation routes by its own topology.
    /// P2P host → the guest's peerId; P2P guest → the host (peer 0); SD client → the server (peer 0);
    /// SD server → the requesting client. The roles that never originate a given message implement it
    /// as a no-op (the SD server never requests; the SD client never serves; P2P never sends a verdict).</para>
    ///
    /// <para>Rate limiting lives HERE, at the edge: a request that fails the serve budget is dropped
    /// without ever reaching the engine, so a flood cannot cost a ring lookup or a payload build. The
    /// engine owns everything past the edge — pending round trips, captures, the diff, the verdict.</para>
    /// </summary>
    public interface IDesyncProbeNetwork
    {
        void SendDesyncProbeRequest(int targetPeerId, DesyncProbeRequestMessage msg);

        void SendDesyncProbeResponse(int targetPeerId, DesyncProbeResponseMessage msg);

        /// <summary>SD client → server. No-op in every other role.</summary>
        void SendDesyncVerdict(DesyncVerdictReportMessage msg);

        /// <summary>
        /// Resolves the peer to probe for a desync reported against <paramref name="playerId"/>. The
        /// detection event carries a playerId but the transport routes by peerId. A P2P host resolves
        /// it through its authoritative peer↔player binding; a guest / SD client always answers peer 0
        /// (host / server — the only peer whose hashes it ever sees). False when it cannot be resolved,
        /// which skips the probe.
        /// </summary>
        bool TryResolveProbePeer(int playerId, out int peerId);

        /// <summary>
        /// (fromPeerId, msg) — a probe request that PASSED the serve budget. The engine answers it from
        /// its hash-history ring.
        /// </summary>
        event Action<int, DesyncProbeRequestMessage> OnDesyncProbeRequested;

        /// <summary>
        /// (fromPeerId, msg) — a probe response. fromPeerId is the transport's, so the engine can
        /// verify it against the peer it actually asked before accepting the payload.
        /// </summary>
        event Action<int, DesyncProbeResponseMessage> OnDesyncProbeResponse;

        /// <summary>
        /// (fromPeerId, msg) — SD server only: a client reported its diagnosis. Untrusted: the engine
        /// validates and logs it, and stores nothing.
        /// </summary>
        event Action<int, DesyncVerdictReportMessage> OnDesyncVerdictReported;
    }
}
