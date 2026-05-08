using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Tests.Network
{
    /// <summary>
    /// SD command rejection feedback — unit tests for InputCollector.OnCommandRejected event firing
    /// (3 transport-level reject branches), CommandRejectedSimEvent payload, CommandRejectedMessage
    /// wire round-trip, and RejectionReason enum wire stability.
    /// </summary>
    [TestFixture]
    public class CommandRejectionFeedbackTests
    {
        private MessageSerializer _serializer;

        [SetUp]
        public void SetUp()
        {
            _serializer = new MessageSerializer();
        }

        // ── Layer 1: InputCollector reject branches fire OnCommandRejected ──────────────

        [Test]
        public void InputCollector_PeerMismatch_FiresOnCommandRejected()
        {
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(0, peerMap);
            collector.AddPlayer(10);

            (int peerId, int tick, int typeId, RejectionReason reason)? captured = null;
            collector.OnCommandRejected += (p, t, id, r) => captured = (p, t, id, r);

            // peerId 1 → playerId 10 expected, sent with playerId 99 → mismatch.
            var cmd = new EmptyCommand(99, 0);
            Assert.IsFalse(collector.TryAcceptInput(1, 0, 99, cmd));
            Assert.IsTrue(captured.HasValue);
            Assert.AreEqual(1, captured.Value.peerId);
            Assert.AreEqual(0, captured.Value.tick);
            Assert.AreEqual(EmptyCommand.TYPE_ID, captured.Value.typeId);
            Assert.AreEqual(RejectionReason.PeerMismatch, captured.Value.reason);
        }

        [Test]
        public void InputCollector_PastTick_FiresOnCommandRejected()
        {
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(0, peerMap);
            collector.AddPlayer(10);

            collector.CollectTickInputs(0); // _lastExecutedTick → 0

            RejectionReason? capturedReason = null;
            collector.OnCommandRejected += (_, _, _, r) => capturedReason = r;

            var cmd = new EmptyCommand(10, 0);
            Assert.IsFalse(collector.TryAcceptInput(1, 0, 10, cmd));
            Assert.AreEqual(RejectionReason.PastTick, capturedReason);
        }

        [Test]
        public void InputCollector_ToleranceExceeded_FiresOnCommandRejected()
        {
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(100, peerMap); // 100ms tolerance
            collector.AddPlayer(10);

            long now = 1000;
            collector.SetTimeProvider(() => now);
            collector.BeginTick(0, now);

            now = 1200; // 200ms elapsed > 100ms tolerance

            RejectionReason? capturedReason = null;
            collector.OnCommandRejected += (_, _, _, r) => capturedReason = r;

            var cmd = new EmptyCommand(10, 0);
            Assert.IsFalse(collector.TryAcceptInput(1, 0, 10, cmd));
            Assert.AreEqual(RejectionReason.ToleranceExceeded, capturedReason);
        }

        [Test]
        public void InputCollector_BootstrapRedirect_DoesNotFireOnCommandRejected()
        {
            // Bootstrap redirect path *accepts* the input (returns true) — must NOT fire OnCommandRejected.
            var collector = new ServerInputCollector();
            var peerMap = new Dictionary<int, int> { { 1, 10 } };
            collector.Configure(0, peerMap);
            collector.AddPlayer(10);
            collector.SetBootstrapPending(true);

            int rejectFiredCount = 0;
            collector.OnCommandRejected += (_, _, _, _) => rejectFiredCount++;

            var cmd = new EmptyCommand(10, -1);
            Assert.IsTrue(collector.TryAcceptInput(1, -1, 10, cmd));
            Assert.AreEqual(0, rejectFiredCount, "Redirect must not fire reject event");
        }

        // ── Layer 2: CommandRejectedSimEvent payload + Synced mode ──────────────

        [Test]
        public void CommandRejectedSimEvent_Mode_IsSynced()
        {
            var evt = new CommandRejectedSimEvent();
            Assert.AreEqual(EventMode.Synced, evt.Mode,
                "Game-layer rejects must be Synced so verified-only dispatch holds");
        }

        [Test]
        public void CommandRejectedSimEvent_ReasonEnum_RoundTripsThroughByte()
        {
            var evt = new CommandRejectedSimEvent();
            evt.ReasonEnum = RejectionReason.Duplicate;
            Assert.AreEqual((byte)RejectionReason.Duplicate, evt.Reason);
            Assert.AreEqual(RejectionReason.Duplicate, evt.ReasonEnum);
        }

        // ── Wire round-trip ────────────────────────────────

        [Test]
        public void CommandRejectedMessage_SerializeDeserialize_Roundtrip()
        {
            var original = new CommandRejectedMessage
            {
                Tick = 42,
                CommandTypeId = 103,
            };
            original.ReasonEnum = RejectionReason.Duplicate;

            var bytes = _serializer.Serialize(original);
            var deserialized = _serializer.Deserialize(bytes) as CommandRejectedMessage;

            Assert.IsNotNull(deserialized);
            Assert.AreEqual(42, deserialized.Tick);
            Assert.AreEqual(103, deserialized.CommandTypeId);
            Assert.AreEqual(RejectionReason.Duplicate, deserialized.ReasonEnum);
        }

        // ── Wire stability ─────────────────────────────────

        [Test]
        public void RejectionReason_NumericValues_AreAppendOnlyStable()
        {
            // Renumbering breaks cross-version clients. Keep this assertion in lockstep with the enum
            // — adding a new value is fine; changing an existing value should fail this test.
            Assert.AreEqual(0, (byte)RejectionReason.PeerMismatch);
            Assert.AreEqual(1, (byte)RejectionReason.PastTick);
            Assert.AreEqual(2, (byte)RejectionReason.ToleranceExceeded);
            Assert.AreEqual(10, (byte)RejectionReason.Duplicate);
        }
    }
}
