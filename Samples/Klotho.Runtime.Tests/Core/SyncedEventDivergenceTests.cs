using System;
using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Synced-set changes below the dispatched
    /// watermark are reported via OnSyncedEventDivergence instead of being silently lost:
    ///   - Removed + Added pair when a dispatched Synced event's payload changes under a
    ///     desync-recovery rollback (exactly-once invariant holds: no re-fire);
    ///   - Added-only when a new Synced event appears at an already-dispatched tick;
    ///   - IMatchEndEvent exception: an Added match-end with no prior OnMatchEnded fires
    ///     OnMatchEnded exactly once; a Removed match-end is notification-only;
    ///   - Removed payloads are readable during the callback (pooled old event contract);
    ///   - divergence/diff dispatch runs inside the Resimulate stage on P2P,
    ///     matching the SD batch path's stage semantics.
    /// Harness pattern follows EventDispatchRollbackTests: stateless TestSimulation (any
    /// rollback target resolvable) + OnAfterTickRaise event injection + RequestRollback.
    /// </summary>
    [TestFixture]
    public class SyncedEventDivergenceTests
    {
        private sealed class PayloadSyncedEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_301;
            public override EventMode Mode => EventMode.Synced;
            public override long GetContentHash() => ((long)EventTypeId << 32) | (uint)Payload;
        }

        private sealed class OtherSyncedEvent : SimulationEvent
        {
            public override int EventTypeId => 9_999_302;
            public override EventMode Mode => EventMode.Synced;
            public override long GetContentHash() => (long)EventTypeId << 32;
        }

        private sealed class TestMatchEndEvent : SimulationEvent, IMatchEndEvent
        {
            public override int EventTypeId => 9_999_303;
            public override EventMode Mode => EventMode.Synced;
            public override long GetContentHash() => (long)EventTypeId << 32;
            public int WinnerPlayerId => 1;
            public FixedString32 Reason => default;
        }

        private static readonly System.Reflection.FieldInfo EngineEventCollectorField =
            typeof(KlothoEngine).GetField("_eventCollector",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private IKLogger _logger;
        private KlothoTestHarness _harness;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Error));
            _logger = factory.CreateLogger("SyncedEventDivergenceTests");
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

        private TestPeer SetUpHostWithRaiser()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            var host = _harness.Host;
            host.Simulation.EventRaiser =
                (ISimulationEventRaiser)EngineEventCollectorField.GetValue(host.Engine);
            return host;
        }

        [Test]
        public void PayloadChange_BelowWatermark_ReportsRemovedPlusAdded_NoRefire()
        {
            var host = SetUpHostWithRaiser();
            const int raiseAtTick = 10;

            int syncedFireCount = 0;
            var divergences = new List<(int tick, int payload, SyncedDivergenceKind kind)>();
            host.Engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is PayloadSyncedEvent) syncedFireCount++;
            };
            host.Engine.OnSyncedEventDivergence += (tick, evt, kind) =>
            {
                if (evt is PayloadSyncedEvent p)
                    divergences.Add((tick, p.Payload, kind)); // payload read inside the callback —
                                                              // Removed instances are pooled old
                                                              // events, valid only here.
            };

            int payloadVariant = 1;
            host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new PayloadSyncedEvent { Payload = payloadVariant });
            };

            // Initial pass: event verifies and dispatches once — watermark covers raiseAtTick.
            _harness.AdvanceAllToTick(raiseAtTick + 5);
            Assert.AreEqual(1, syncedFireCount, "Initial verified dispatch fires exactly once");
            Assert.AreEqual(0, divergences.Count, "No divergence on a clean pass");

            // Desync-style rollback below the dispatched tick with a changed payload: the
            // re-simulated Synced set differs at a tick the exactly-once guard blocks.
            payloadVariant = 2;
            host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 12);

            Assert.AreEqual(1, syncedFireCount,
                "Exactly-once invariant: the changed Synced event must NOT re-fire OnSyncedEvent");
            Assert.AreEqual(2, divergences.Count,
                "A changed payload at the same (tick, type) surfaces as a Removed + Added pair");
            Assert.IsTrue(divergences.Contains((raiseAtTick, 1, SyncedDivergenceKind.Removed)),
                "Removed must carry the originally-dispatched payload (valid during the callback)");
            Assert.IsTrue(divergences.Contains((raiseAtTick, 2, SyncedDivergenceKind.Added)),
                "Added must carry the re-simulated payload");
        }

        [Test]
        public void NewSyncedEvent_AtDispatchedTick_ReportsAddedOnly()
        {
            var host = SetUpHostWithRaiser();
            const int raiseAtTick = 10;

            var divergences = new List<(SimulationEvent evt, SyncedDivergenceKind kind)>();
            host.Engine.OnSyncedEventDivergence += (tick, evt, kind) => divergences.Add((evt, kind));

            bool alsoRaiseSecond = false;
            host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                {
                    raiser.RaiseEvent(new OtherSyncedEvent());
                    if (alsoRaiseSecond)
                        raiser.RaiseEvent(new PayloadSyncedEvent { Payload = 7 });
                }
            };

            _harness.AdvanceAllToTick(raiseAtTick + 5);
            Assert.AreEqual(0, divergences.Count);

            // Re-sim adds a second Synced event at the already-dispatched tick: the unchanged
            // one matches old-vs-new (no report); the new one can never fire normally -> Added.
            alsoRaiseSecond = true;
            host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 12);

            Assert.AreEqual(1, divergences.Count, "Only the new event diverges (the unchanged one matches)");
            Assert.AreEqual(SyncedDivergenceKind.Added, divergences[0].kind);
            Assert.IsInstanceOf<PayloadSyncedEvent>(divergences[0].evt);
        }

        [Test]
        public void AddedMatchEnd_BelowWatermark_FiresOnMatchEndedOnce()
        {
            var host = SetUpHostWithRaiser();
            const int raiseAtTick = 10;

            int matchEndedCount = 0;
            int matchEndedTick = -1;
            var divergenceKinds = new List<SyncedDivergenceKind>();
            host.Engine.OnMatchEnded += (tick, evt) => { matchEndedCount++; matchEndedTick = tick; };
            host.Engine.OnSyncedEventDivergence += (tick, evt, kind) =>
            {
                if (evt is TestMatchEndEvent) divergenceKinds.Add(kind);
            };

            bool raiseMatchEnd = false;
            host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                {
                    raiser.RaiseEvent(new OtherSyncedEvent()); // raises the watermark at raiseAtTick
                    if (raiseMatchEnd)
                        raiser.RaiseEvent(new TestMatchEndEvent());
                }
            };

            _harness.AdvanceAllToTick(raiseAtTick + 5);
            Assert.AreEqual(0, matchEndedCount);
            Assert.IsFalse(host.Engine.IsMatchEnded);

            // Re-sim makes a match end appear at the already-dispatched tick. Notification alone
            // would leave the match unendable — OnMatchEnded must fire (guard keeps exactly-once).
            raiseMatchEnd = true;
            host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 12);

            Assert.AreEqual(1, divergenceKinds.Count);
            Assert.AreEqual(SyncedDivergenceKind.Added, divergenceKinds[0]);
            Assert.AreEqual(1, matchEndedCount,
                "An Added IMatchEndEvent with no prior OnMatchEnded must fire OnMatchEnded once");
            Assert.AreEqual(raiseAtTick, matchEndedTick);
            Assert.IsTrue(host.Engine.IsMatchEnded);
        }

        [Test]
        public void RemovedMatchEnd_BelowWatermark_NotificationOnly_NoUnfire()
        {
            var host = SetUpHostWithRaiser();
            const int raiseAtTick = 10;

            int matchEndedCount = 0;
            var divergenceKinds = new List<SyncedDivergenceKind>();
            host.Engine.OnMatchEnded += (tick, evt) => matchEndedCount++;
            host.Engine.OnSyncedEventDivergence += (tick, evt, kind) =>
            {
                if (evt is TestMatchEndEvent) divergenceKinds.Add(kind);
            };

            bool raiseMatchEnd = true;
            host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                {
                    raiser.RaiseEvent(new OtherSyncedEvent());
                    if (raiseMatchEnd)
                        raiser.RaiseEvent(new TestMatchEndEvent());
                }
            };

            // Initial pass dispatches the match end.
            _harness.AdvanceAllToTick(raiseAtTick + 5);
            Assert.AreEqual(1, matchEndedCount);

            // Re-sim erases it: the end flow already ran and cannot be un-fired — the engine
            // reports Removed (AbortMatch is the documented game-layer response, not automatic).
            raiseMatchEnd = false;
            host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 12);

            Assert.AreEqual(1, divergenceKinds.Count);
            Assert.AreEqual(SyncedDivergenceKind.Removed, divergenceKinds[0]);
            Assert.AreEqual(1, matchEndedCount, "A removed match end must not re-fire OnMatchEnded");
            Assert.IsTrue(host.Engine.IsMatchEnded, "The already-run end flow is not reverted");
            Assert.AreEqual(KlothoState.Running, host.Engine.State, "No automatic abort — game policy");
        }

        [Test]
        public void DivergenceDispatch_RunsInsideResimulateStage()
        {
            var host = SetUpHostWithRaiser();
            const int raiseAtTick = 10;

            SimulationStage? stageAtDivergence = null;
            host.Engine.OnSyncedEventDivergence += (tick, evt, kind) =>
            {
                if (stageAtDivergence == null) stageAtDivergence = host.Engine.Stage;
            };

            int payloadVariant = 1;
            host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new PayloadSyncedEvent { Payload = payloadVariant });
            };

            _harness.AdvanceAllToTick(raiseAtTick + 5);
            payloadVariant = 2;
            host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 12);

            // The P2P rollback wraps resim + chain re-advance + event diff in
            // Stage = Resimulate, matching the SD batch path — game-side IsResimulation
            // side-effect suppression must see the same stage in both modes.
            Assert.AreEqual(SimulationStage.Resimulate, stageAtDivergence,
                "Diff-phase dispatch (incl. divergence reports) must run inside the Resimulate stage on P2P");
            Assert.AreEqual(SimulationStage.Forward, host.Engine.Stage,
                "Stage must revert to Forward after the rollback completes");
        }
    }
}
