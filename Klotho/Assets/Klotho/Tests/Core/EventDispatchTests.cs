using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.State;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Covers two invariants:
    ///   (a) A single Synced event raised by the simulation on a tick that later verifies
    ///       results in exactly one OnSyncedEvent dispatch from KlothoEngine.
    ///   (b) EventCollector does not dedup — identical events raised twice within a tick
    ///       produce two collected entries. Dedup, if needed, is the simulation's responsibility.
    /// </summary>
    [TestFixture]
    public class EventDispatchTests
    {
        private sealed class TestSyncedEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_101;
            public override EventMode Mode => EventMode.Synced;
        }

        // ── (b) EventCollector unit invariants ──

        [Test]
        public void EventCollector_RaiseEvent_AppendsWithoutDedup()
        {
            var collector = new EventCollector();
            collector.BeginTick(7);

            var e1 = new TestSyncedEvent { Payload = 1 };
            var e2 = new TestSyncedEvent { Payload = 1 };
            collector.RaiseEvent(e1);
            collector.RaiseEvent(e2);

            Assert.AreEqual(2, collector.Count,
                "EventCollector must not dedup — same-content events raised twice produce two entries");
            Assert.AreSame(e1, collector.Collected[0]);
            Assert.AreSame(e2, collector.Collected[1]);
        }

        [Test]
        public void EventCollector_BeginTick_ClearsPriorCollection()
        {
            var collector = new EventCollector();
            collector.BeginTick(1);
            collector.RaiseEvent(new TestSyncedEvent());
            Assert.AreEqual(1, collector.Count);

            collector.BeginTick(2);
            Assert.AreEqual(0, collector.Count,
                "BeginTick must clear the prior tick's collected entries");
        }

        [Test]
        public void EventCollector_RaiseEvent_StampsEventTickFromActiveBeginTick()
        {
            var collector = new EventCollector();
            collector.BeginTick(42);

            var evt = new TestSyncedEvent();
            collector.RaiseEvent(evt);

            Assert.AreEqual(42, evt.Tick,
                "RaiseEvent must stamp evt.Tick from the most recent BeginTick");
        }

        // ── (a) KlothoEngine OnSyncedEvent single dispatch ──

        private sealed class StubSnapshot : IStateSnapshot
        {
            public int Tick { get; set; }
            public byte[] Serialize() => Array.Empty<byte>();
            public void Deserialize(byte[] data) { }
            public ulong CalculateHash() => 0;
        }

        private static readonly FieldInfo _engineEventCollectorField = typeof(KlothoEngine)
            .GetField("_eventCollector", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _engineSnapshotManagerField = typeof(KlothoEngine)
            .GetField("_snapshotManager", BindingFlags.NonPublic | BindingFlags.Instance);

        private static IStateSnapshotManager ReadSnapshotManager(KlothoEngine engine)
            => (IStateSnapshotManager)_engineSnapshotManagerField.GetValue(engine);

        private ILogger _logger;
        private KlothoTestHarness _harness;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                b.AddZLoggerUnityDebug();
            });
            _logger = factory.CreateLogger("EventDispatchTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _harness = new KlothoTestHarness(_logger);
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

        [Test]
        public void Engine_SyncedEventOnVerifiedTick_DispatchesExactlyOnce()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 5;
            int dispatchedCount = 0;
            int dispatchedTick = -1;
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent)
                {
                    dispatchedCount++;
                    dispatchedTick = tick;
                }
            };

            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 99 });
            };

            _harness.AdvanceAllToTick(raiseAtTick + 10);

            Assert.AreEqual(1, dispatchedCount,
                "A single Synced event raised on a tick that later verifies must dispatch exactly once");
            Assert.AreEqual(raiseAtTick, dispatchedTick,
                "Dispatched tick must equal the tick the event was raised at");
        }

        [Test]
        public void Engine_SyncedEventRaisedTwiceOnSameTick_DispatchesTwice()
        {
            // Mirror of the dedup-absent invariant at the engine level: simulation-side
            // duplicate raises cascade through the pipeline as two dispatches. If the project
            // ever adopts a dedup policy, this test pins down where it must be enforced.
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 5;
            int dispatchedCount = 0;
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent)
                    dispatchedCount++;
            };

            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                {
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
                }
            };

            _harness.AdvanceAllToTick(raiseAtTick + 10);

            Assert.AreEqual(2, dispatchedCount,
                "Pipeline has no dedup — two raises at the same tick must produce two dispatches");
        }

        // ── P2P Rollback outer-loop pattern (Rollback.cs:157 → 191) ──

        private sealed class TestRegularEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_102;
            public override long GetContentHash() => ((long)EventTypeId << 32) ^ (uint)Payload;
        }

        [Test]
        public void Rollback_SyncedEvent_TickArgumentInvariant_AcrossResim()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 10;
            var dispatches = new List<(int callbackTick, int evtTick)>();
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent)
                    dispatches.Add((tick, evt.Tick));
            };

            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
            };

            _harness.AdvanceAllToTick(raiseAtTick + 5);
            Assert.GreaterOrEqual(dispatches.Count, 1, "Initial verified-tick dispatch");

            // Trigger the Rollback.cs outer-loop ClearTick path. Resim re-runs Tick(raiseAtTick),
            // raising the event again through the same BeginTick(t) / AddEvent(t) machinery.
            ReadSnapshotManager(_harness.Host.Engine)
                .SaveSnapshot(raiseAtTick - 1, new StubSnapshot { Tick = raiseAtTick - 1 });
            _harness.Host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 10);

            Assert.GreaterOrEqual(dispatches.Count, 1, "Dispatches should still be observable post-rollback");
            for (int i = 0; i < dispatches.Count; i++)
            {
                var (callbackTick, evtTick) = dispatches[i];
                Assert.AreEqual(callbackTick, evtTick,
                    $"BeginTick(t) / AddEvent(t) invariant violated at dispatch #{i}: " +
                    $"OnSyncedEvent callback tick {callbackTick} != evt.Tick {evtTick}");
            }
        }

        [Test]
        public void Rollback_RegularEvent_MatchedResim_NoDiffCascade()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 10;
            int predictedCount = 0, confirmedCount = 0, canceledCount = 0;
            _harness.Host.Engine.OnEventPredicted += (t, e) =>
            { if (e is TestRegularEvent) predictedCount++; };
            _harness.Host.Engine.OnEventConfirmed += (t, e) =>
            { if (e is TestRegularEvent) confirmedCount++; };
            _harness.Host.Engine.OnEventCanceled += (t, e) =>
            { if (e is TestRegularEvent) canceledCount++; };

            // Stable payload — resim produces identical content hash → matched in DiffRollbackEvents.
            const int payload = 7;
            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestRegularEvent { Payload = payload });
            };

            _harness.AdvanceAllToTick(raiseAtTick + 5);
            int predictedAfterInitial = predictedCount;
            int confirmedAfterInitial = confirmedCount;
            int canceledAfterInitial = canceledCount;

            ReadSnapshotManager(_harness.Host.Engine)
                .SaveSnapshot(raiseAtTick - 1, new StubSnapshot { Tick = raiseAtTick - 1 });
            _harness.Host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 10);

            // Matched event: DiffRollbackEvents finds identical (Tick, EventTypeId, GetContentHash)
            // in old + new → no Cancel, no new-only Confirm/Predict dispatch.
            Assert.AreEqual(canceledAfterInitial, canceledCount,
                "Matched event: no OnEventCanceled should fire");
            Assert.AreEqual(confirmedAfterInitial, confirmedCount,
                "Matched event: no additional OnEventConfirmed should fire");
            Assert.AreEqual(predictedAfterInitial, predictedCount,
                "Matched event: no additional OnEventPredicted should fire");
        }

        [Test]
        public void Rollback_RegularEvent_DivergedResim_OldCanceled_NewDispatched()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 10;
            int predictedCount = 0, confirmedCount = 0, canceledCount = 0;
            _harness.Host.Engine.OnEventPredicted += (t, e) =>
            { if (e is TestRegularEvent) predictedCount++; };
            _harness.Host.Engine.OnEventConfirmed += (t, e) =>
            { if (e is TestRegularEvent) confirmedCount++; };
            _harness.Host.Engine.OnEventCanceled += (t, e) =>
            { if (e is TestRegularEvent) canceledCount++; };

            int currentPayload = 1;
            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestRegularEvent { Payload = currentPayload });
            };

            _harness.AdvanceAllToTick(raiseAtTick + 5);
            int predictedAfterInitial = predictedCount;
            int confirmedAfterInitial = confirmedCount;
            int canceledAfterInitial = canceledCount;

            // Diverge: resim raises Payload=2 → different content hash → unmatched in DiffRollbackEvents.
            currentPayload = 2;
            ReadSnapshotManager(_harness.Host.Engine)
                .SaveSnapshot(raiseAtTick - 1, new StubSnapshot { Tick = raiseAtTick - 1 });
            _harness.Host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 10);

            // Old-only (Payload=1) → OnEventCanceled. New-only (Payload=2) → OnEventConfirmed or
            // OnEventPredicted depending on tick vs _lastVerifiedTick at diff time. Either path
            // counts as a "new dispatch", so check the union.
            Assert.AreEqual(canceledAfterInitial + 1, canceledCount,
                "Diverged event: old-only must dispatch as OnEventCanceled");
            int newDispatches = (predictedCount - predictedAfterInitial) + (confirmedCount - confirmedAfterInitial);
            Assert.AreEqual(1, newDispatches,
                "Diverged event: new-only must dispatch exactly once (Confirmed or Predicted)");
        }
    }
}
