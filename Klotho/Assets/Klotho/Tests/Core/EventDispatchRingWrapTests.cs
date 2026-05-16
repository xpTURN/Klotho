using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Ring-wrap under-fire: when _lastVerifiedTick stalls (guest input absent) while CurrentTick
    /// advances by SnapshotCapacity (= MaxRollbackTicks + 2), tick T and tick T+capacity share the
    /// same EventBuffer slot. ExecuteTick calls ClearTick(CurrentTick) before AddEvent — this
    /// silently wipes the still-pending event at the earlier tick occupying the same slot.
    ///
    /// (a) Immediate pattern — ExecuteTick ClearTick wipes a buffered Synced event.
    /// (c) Predicted event rollback diff cascade — ring wrap removes Predicted event from its slot;
    ///     DiffRollbackEvents sees it as old-only → spurious OnEventCanceled.
    ///
    /// MaxRollbackTicks=4 → SnapshotCapacity=6. Stalling the chain by withholding guest input
    /// prevents CanAdvanceTick from succeeding, so _lastVerifiedTick stays put naturally while
    /// CurrentTick advances. Tick raiseAtTick=3 and raiseAtTick+6=9 share slot 3 % 6 = 3.
    /// </summary>
    [TestFixture]
    public class EventDispatchRingWrapTests
    {
        private sealed class TestSyncedEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_301;
            public override EventMode Mode => EventMode.Synced;
        }

        private sealed class TestRegularEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_302;
            public override long GetContentHash() => ((long)EventTypeId << 32) | (uint)Payload;
        }

        private static readonly FieldInfo _engineEventCollectorField = typeof(KlothoEngine)
            .GetField("_eventCollector", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _lastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _engineEventBufferField = typeof(KlothoEngine)
            .GetField("_eventBuffer", BindingFlags.NonPublic | BindingFlags.Instance);

        private ILogger _logger;
        private KlothoTestHarness _harness;

        // MaxRollbackTicks=4 → SnapshotCapacity=6. Tick T and T+6 share the same EventBuffer slot.
        private const int TestMaxRollbackTicks = 4;
        private const int SnapshotCapacity = TestMaxRollbackTicks + 2; // 6
        private const int RaiseAtTick = 3;
        private const int WrapTick = RaiseAtTick + SnapshotCapacity; // 9

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                b.AddZLoggerUnityDebug();
            });
            _logger = factory.CreateLogger("EventDispatchRingWrapTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _harness = new KlothoTestHarness(_logger)
                .WithSimulationConfig(new SimulationConfig { MaxRollbackTicks = TestMaxRollbackTicks, SyncCheckInterval = TestMaxRollbackTicks });
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        private static void WireEventRaiserFromEngine(TestPeer peer)
        {
            var collector = (ISimulationEventRaiser)_engineEventCollectorField.GetValue(peer.Engine);
            peer.Simulation.EventRaiser = collector;
        }

        private int GuestPlayerId => _harness.Guests[0].LocalPlayerId;

        /// <summary>
        /// (a) Immediate pattern: a Synced event raised at tick T is wiped when ExecuteTick
        /// reaches tick T+SnapshotCapacity and calls ClearTick — same slot as T. The event is
        /// silently lost; OnSyncedEvent never fires.
        ///
        /// Guest input is withheld to stall CanAdvanceTick naturally — _lastVerifiedTick stays
        /// below RaiseAtTick while CurrentTick advances past WrapTick. This is the
        /// constant 8-tick under-fire window invariant. The test locks in the current behavior.
        /// </summary>
        [Test]
        public void ImmediatePattern_SyncedEvent_WipedByRingWrapClearTick_NeverDispatched()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            int syncedDispatchCount = 0;
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent) syncedDispatchCount++;
            };

            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == RaiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
            };

            // Advance both peers to just past RaiseAtTick so the event is buffered.
            _harness.AdvanceAllToTick(RaiseAtTick + 1);
            int dispatchAfterRaise = syncedDispatchCount;

            // Stall chain by withholding guest input — _lastVerifiedTick cannot pass RaiseAtTick.
            // Host CurrentTick advances alone until WrapTick, where ClearTick overwrites the slot.
            _harness.AdvanceWithStalledPeer(WrapTick + 2, GuestPlayerId);

            // The Synced event at RaiseAtTick shares slot (RaiseAtTick % SnapshotCapacity) with
            // WrapTick. ExecuteTick(WrapTick) calls ClearTick(WrapTick) first, wiping the pending
            // event. If later the chain does resume, no residual event exists at that slot.
            Assert.AreEqual(dispatchAfterRaise, syncedDispatchCount,
                $"Synced event at tick {RaiseAtTick} must not dispatch after ring wrap at tick " +
                $"{WrapTick} ({RaiseAtTick} % {SnapshotCapacity} == {WrapTick} % {SnapshotCapacity}). " +
                "This is the under-fire window: the slot was silently wiped by ClearTick.");
        }

        /// <summary>
        /// (c) Ring wrap slot overwrite: a Regular event raised at tick T occupies EventBuffer
        /// slot (T % SnapshotCapacity). When CurrentTick reaches T+SnapshotCapacity, ExecuteTick
        /// calls ClearTick on the same slot before AddEvent — the buffered event at T is silently
        /// wiped. GetEvents(T) returns empty after the wrap.
        ///
        /// This test locks in the buffer state so that a capacity guard or fix is observable
        /// as a change in the slot-empty assertion.
        /// </summary>
        [Test]
        public void PredictedCascade_RingWrap_SlotOverwrite_EventGoneAfterWrap()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            int confirmedCount = 0;
            _harness.Host.Engine.OnEventConfirmed += (tick, evt) =>
            {
                if (evt is TestRegularEvent) confirmedCount++;
            };

            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == RaiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestRegularEvent { Payload = 1 });
            };

            // Advance to just past RaiseAtTick — both peers exchange input so CanAdvanceTick
            // succeeds: Regular event fires OnEventConfirmed (verified path).
            _harness.AdvanceAllToTick(RaiseAtTick + 1);
            Assert.GreaterOrEqual(confirmedCount, 1,
                "Regular event must fire OnEventConfirmed on the verified path before ring wrap");

            // Confirm event is present in EventBuffer at RaiseAtTick before wrap.
            var eventBuffer = (EventBuffer)_engineEventBufferField.GetValue(_harness.Host.Engine);
            int countBeforeWrap = eventBuffer.GetEvents(RaiseAtTick).Count;
            Assert.GreaterOrEqual(countBeforeWrap, 1,
                $"EventBuffer must hold the Regular event at tick {RaiseAtTick} before ring wrap");

            // Stall chain — host alone advances to WrapTick. ExecuteTick(WrapTick) calls
            // ClearTick(WrapTick) which maps to slot (WrapTick % SnapshotCapacity) =
            // (RaiseAtTick % SnapshotCapacity) — same slot, wiping the earlier event.
            _harness.AdvanceWithStalledPeer(WrapTick + 1, GuestPlayerId);

            // After ring wrap, the slot shared by RaiseAtTick and WrapTick has been overwritten.
            // GetEvents(RaiseAtTick) returns the WrapTick data (or empty if WrapTick also cleared).
            // Either way, the original RaiseAtTick event is gone — documents the slot collision.
            int countAfterWrap = eventBuffer.GetEvents(RaiseAtTick).Count;
            Assert.AreEqual(0, countAfterWrap,
                $"EventBuffer slot at tick {RaiseAtTick} must be empty after ring wrap at tick " +
                $"{WrapTick} ({RaiseAtTick} % {SnapshotCapacity} == {WrapTick} % {SnapshotCapacity}). " +
                "This is the slot overwrite: ClearTick(WrapTick) wiped the RaiseAtTick event.");
        }
    }
}
