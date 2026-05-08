using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Tests.Network
{
    /// <summary>
    /// SD bootstrap handshake — unit tests for InputCollector window flag, redirect guard,
    /// and wire-level round trips of PlayerBootstrapReadyMessage / BootstrapBeginMessage.
    /// Higher-level ack-completion / timeout flows are exercised by integration tests.
    /// </summary>
    [TestFixture]
    public class BootstrapHandshakeTests
    {
        private MessageSerializer _serializer;

        [SetUp]
        public void SetUp()
        {
            _serializer = new MessageSerializer();
        }

        // ── Redirect guard ──────────────────────────────────

        [Test]
        public void InputCollector_BootstrapPending_RedirectsNegativeTickToFirstScheduled()
        {
            // Defensive redirect path: in BootstrapPending and no tick yet executed,
            // a stray negative-tick input is rerouted to FIRST_SCHEDULED_TICK (= 0) instead of rejected.
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(0, peerMap);
            collector.AddPlayer(10);
            collector.SetBootstrapPending(true);

            var cmd = new EmptyCommand(10, /*ignored*/ -5);
            // tick=-1 satisfies tick <= _lastExecutedTick(=-1) AND _bootstrapPending — must redirect, return true.
            Assert.IsTrue(collector.TryAcceptInput(1, -1, 10, cmd),
                "Bootstrap redirect path should accept early-arriving past-tick input");
        }

        [Test]
        public void InputCollector_BootstrapPending_DoesNotRedirectOnceTickExecuted()
        {
            // Once the first real tick has executed, redirect must NOT fire even if _bootstrapPending is still set —
            // silently relabeling an already-executed tick destroys determinism. Belt-and-suspenders guard.
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(0, peerMap);
            collector.AddPlayer(10);
            collector.SetBootstrapPending(true);

            collector.CollectTickInputs(0); // _lastExecutedTick → 0

            var cmd = new EmptyCommand(10, 0);
            Assert.IsFalse(collector.TryAcceptInput(1, 0, 10, cmd),
                "Redirect must not relabel after tick already executed");
        }

        [Test]
        public void InputCollector_NotBootstrapPending_PastTickRejected()
        {
            // Steady-state path: _bootstrapPending=false → past-tick always rejected.
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(0, peerMap);
            collector.AddPlayer(10);
            // _bootstrapPending defaults to false

            var cmd = new EmptyCommand(10, -1);
            Assert.IsFalse(collector.TryAcceptInput(1, -1, 10, cmd),
                "Steady-state past-tick must reject without redirect");
        }

        [Test]
        public void InputCollector_Reset_ClearsBootstrapPending()
        {
            // Reset() is invoked from ServerNetworkService.LeaveRoom — must clear _bootstrapPending so that
            // a subsequent session reuse does not inherit stale redirect behavior (matches matrix row 4).
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(0, peerMap);
            collector.AddPlayer(10);
            collector.SetBootstrapPending(true);
            collector.Reset();

            // After Reset _bootstrapPending must be false → past-tick rejection rules apply.
            var cmd = new EmptyCommand(10, -1);
            Assert.IsFalse(collector.TryAcceptInput(1, -1, 10, cmd),
                "Reset must clear bootstrap flag");
        }

        // ── Message round-trips ─────────────────────────────

        [Test]
        public void PlayerBootstrapReadyMessage_SerializeDeserialize_Roundtrip()
        {
            var original = new PlayerBootstrapReadyMessage { PlayerId = 7 };

            var bytes = _serializer.Serialize(original);
            var deserialized = _serializer.Deserialize(bytes) as PlayerBootstrapReadyMessage;

            Assert.IsNotNull(deserialized);
            Assert.AreEqual(7, deserialized.PlayerId);
        }

        [Test]
        public void BootstrapBeginMessage_SerializeDeserialize_Roundtrip()
        {
            var original = new BootstrapBeginMessage
            {
                FirstTick = 0,
                TickStartTimeMs = 1234567890123L,
            };

            var bytes = _serializer.Serialize(original);
            var deserialized = _serializer.Deserialize(bytes) as BootstrapBeginMessage;

            Assert.IsNotNull(deserialized);
            Assert.AreEqual(0, deserialized.FirstTick);
            Assert.AreEqual(1234567890123L, deserialized.TickStartTimeMs);
        }

        // ── State enum ──────────────────────────────────────

        [Test]
        public void KlothoState_BootstrapPending_OrderedBetweenWaitingAndRunning()
        {
            // Stable enum order — BootstrapPending sits between WaitingForPlayers and Running so existing
            // gates (State != Running) naturally block UpdateServerTick during the bootstrap window.
            Assert.Less((int)KlothoState.WaitingForPlayers, (int)KlothoState.BootstrapPending);
            Assert.Less((int)KlothoState.BootstrapPending, (int)KlothoState.Running);
        }
    }
}
