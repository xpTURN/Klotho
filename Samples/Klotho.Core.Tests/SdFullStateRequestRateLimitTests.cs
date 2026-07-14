using Xunit;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// SD server FullStateRequest rate limit. A request is ~5 bytes while the response is the whole
    /// simulation state, so an unthrottled client can amplify its uplink into the server's downlink
    /// and force a re-serialize (plus a heap allocation) on the room worker thread. The P2P host has
    /// always guarded this with a per-peer cooldown; these tests pin the same guard on the SD side.
    ///
    /// Driven headlessly through the FakeTransport: a peer that never completed the connect handshake
    /// is not in _pendingPeers, so its messages bypass the first-message gate and land directly on the
    /// dispatch switch — which is the path under test.
    /// </summary>
    public sealed class SdFullStateRequestRateLimitTests
    {
        [Fact] // a flood collapses to a single serve — this is the amplification vector being closed
        public void RepeatedRequests_WithinCooldown_ServedOnce()
        {
            var (svc, tx, ser) = NewServer();

            int served = 0;
            svc.OnFullStateRequested += (_, _) => served++;

            for (int i = 0; i < 50; i++)
                Feed(tx, ser, peerId: 7, new FullStateRequestMessage { RequestTick = 100 + i });

            Assert.Equal(1, served);
        }

        [Fact] // the cooldown is per-peer: one peer flooding must not starve another peer's resync
        public void Cooldown_IsPerPeer_DoesNotBlockOtherPeers()
        {
            var (svc, tx, ser) = NewServer();

            var servedPeers = new System.Collections.Generic.List<int>();
            svc.OnFullStateRequested += (peerId, _) => servedPeers.Add(peerId);

            for (int i = 0; i < 20; i++)
                Feed(tx, ser, peerId: 7, new FullStateRequestMessage { RequestTick = 100 });

            Feed(tx, ser, peerId: 8, new FullStateRequestMessage { RequestTick = 100 });

            Assert.Equal(new[] { 7, 8 }, servedPeers);
        }

        [Fact] // disconnect clears the cooldown, so a recycled peerId is not served a stale window.
                // Without the cleanup a reconnecting peer (or a spectator handed the same peerId)
                // would have its FIRST request dropped and stall until its own resync timeout.
        public void Disconnect_ClearsCooldown_SoRecycledPeerIdIsServed()
        {
            var (svc, tx, ser) = NewServer();

            int served = 0;
            svc.OnFullStateRequested += (_, _) => served++;

            Feed(tx, ser, peerId: 7, new FullStateRequestMessage { RequestTick = 100 });  // served
            Feed(tx, ser, peerId: 7, new FullStateRequestMessage { RequestTick = 101 });  // throttled
            Assert.Equal(1, served);

            tx.RaiseDisconnect(7);

            // Same peerId, new peer: still inside the 2s cooldown window, but the entry is gone.
            Feed(tx, ser, peerId: 7, new FullStateRequestMessage { RequestTick = 200 });

            Assert.Equal(2, served);
        }

        // ── harness ───────────────────────────────────────────────────

        private static (ServerNetworkService svc, FakeTransport tx, MessageSerializer ser) NewServer()
        {
            var tx = new FakeTransport();
            var svc = new ServerNetworkService();
            svc.Initialize(tx, null, null);
            svc.CreateRoom("test", 4);
            return (svc, tx, new MessageSerializer());
        }

        private static void Feed(FakeTransport tx, MessageSerializer ser, int peerId, NetworkMessageBase msg)
        {
            byte[] bytes = ser.Serialize(msg);
            tx.RaiseData(peerId, bytes, bytes.Length);
        }
    }
}
