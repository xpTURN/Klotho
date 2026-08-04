using System;

using NUnit.Framework;

using xpTURN.Klotho.Network; // ServerNetworkService

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Deterministic unit tests for the routed/connected-but-not-joined peer timeout.
    /// A peer that connects (HandlePeerConnected → _pendingPeers) but never sends its first app message
    /// must be evicted after PENDING_PEER_TIMEOUT_MS so the held transport slot is freed. The clock is
    /// injected via SetNowProviderForTest so the sweep is testable without sleeping. PENDING_PEER_TIMEOUT_MS
    /// is internal (10000) — the test references the value indirectly through behavior at the boundary.
    /// </summary>
    public sealed class PendingPeerTimeoutTests
    {
        private const long Timeout = 10000; // mirrors ServerNetworkService.PENDING_PEER_TIMEOUT_MS

        private static (ServerNetworkService svc, FakeTransport tx) NewServer(Func<long> clock)
        {
            var tx = new FakeTransport();
            var svc = new ServerNetworkService();
            svc.Initialize(tx, null, null);
            svc.SetNowProviderForTest(clock);   // test seam (R-A): overrides the sweep clock
            svc.CreateRoom("test", 4);
            return (svc, tx);
        }

        [Test] // connected, never joins → not evicted before the timeout, evicted strictly after, slot freed
        public void PendingPeer_NoFirstMessage_EvictedAfterTimeout()
        {
            long now = 1_000_000;
            var (svc, tx) = NewServer(() => now);

            tx.RaiseConnect(7);                       // HandlePeerConnected → _pendingPeers[7] = now (t0)
            Assert.AreEqual(1, svc.PendingPeerCount);

            svc.Update();                             // now == t0 → age 0, no evict
            Assert.AreEqual(1, svc.PendingPeerCount);
            Assert.IsEmpty(tx.Disconnected);

            now = 1_000_000 + Timeout;                // exactly at the boundary (strict '>' → not yet)
            svc.Update();
            Assert.AreEqual(1, svc.PendingPeerCount);
            Assert.IsEmpty(tx.Disconnected);

            now = 1_000_000 + Timeout + 1;            // one ms past → evict
            svc.Update();
            Assert.AreEqual(0, svc.PendingPeerCount);    // slot freed
            XAssert.Contains(7, tx.Disconnected);      // disconnect issued
        }

        [Test] // a peer removed from _pendingPeers before the timeout (here: disconnect) is NOT swept/redisconnected
        public void PendingPeer_GoneBeforeTimeout_NotEvicted()
        {
            long now = 5_000_000;
            var (svc, tx) = NewServer(() => now);

            tx.RaiseConnect(3);
            Assert.AreEqual(1, svc.PendingPeerCount);

            tx.RaiseDisconnect(3);                    // HandlePeerDisconnected removes it from _pendingPeers
            Assert.AreEqual(0, svc.PendingPeerCount);

            now = 5_000_000 + Timeout + 5000;         // well past the timeout
            svc.Update();                             // sweep finds nothing to evict
            Assert.AreEqual(0, svc.PendingPeerCount);
            Assert.IsEmpty(tx.Disconnected);            // no spurious disconnect from the sweep
        }

        [Test] // multiple pending peers with staggered connect times evict independently at their own deadlines
        public void PendingPeers_EvictIndependentlyByConnectTime()
        {
            long now = 2_000_000;
            var (svc, tx) = NewServer(() => now);

            tx.RaiseConnect(1);                       // t0
            now = 2_000_000 + 4000;
            tx.RaiseConnect(2);                       // t0 + 4s
            Assert.AreEqual(2, svc.PendingPeerCount);

            now = 2_000_000 + Timeout + 1;            // peer 1 expired (age 10001), peer 2 not (age 6001)
            svc.Update();
            Assert.AreEqual(1, svc.PendingPeerCount);
            XAssert.Contains(1, tx.Disconnected);
            XAssert.DoesNotContain(2, tx.Disconnected);

            now = 2_000_000 + 4000 + Timeout + 1;     // peer 2 now expired too
            svc.Update();
            Assert.AreEqual(0, svc.PendingPeerCount);
            XAssert.Contains(2, tx.Disconnected);
        }
    }
}
