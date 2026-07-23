using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;


using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Spectator outer-loop rollback paths (SpectatorHandleConfirmedInput in
    /// KlothoEngine.Spectator.cs):
    ///   (b) EcsSim branch — when _simulation is EcsSimulation. Outer ClearTick range
    ///       at lines 249-256, resim loop at lines 262-283 with inline Synced dispatch
    ///       (promotion-to-verified), DiffRollbackEvents at line 285.
    ///   (c) Snapshot branch — non-Ecs simulation, uses _snapshotManager.GetSnapshot.
    ///       Outer ClearTick range at lines 302-309, resim loop at lines 315-336 with
    ///       inline Synced dispatch, DiffRollbackEvents at line 338.
    ///
    /// Asserts per path:
    ///   (1) OnSyncedEvent dispatched exactly once across prediction → rollback → resim.
    ///       Buffered during Predicted ticks (DispatchTickEvents skips Synced when not
    ///       Verified) and fired inline on promotion-to-verified during resim.
    ///   (2) evt.Tick (stamped by BeginTick) equals the OnSyncedEvent callback tick
    ///       (passed by the inline _dispatcher.Dispatch call) — tick-argument invariant.
    ///   (3) Regular event diff cascade — old-only variant fires OnEventCanceled,
    ///       new-only variant fires OnEventConfirmed via DiffRollbackEvents.
    /// </summary>
    [TestFixture]
    public class EventDispatchSpectatorTests
    {
        // ── Shared test events ───────────────────────────────────────────

        private sealed class TestSyncedEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_301;
            public override EventMode Mode => EventMode.Synced;
            public override long GetContentHash() => ((long)EventTypeId << 32) | (uint)Payload;
        }

        private sealed class TestRegularEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_302;
            public override long GetContentHash() => ((long)EventTypeId << 32) | (uint)Payload;
        }

        // ── Path (b) — custom ECS system raises events from inside Tick ──

        private sealed class EventRaiserSystem : ISystem
        {
            public int RaiseAtTick = -1;
            public Func<SimulationEvent> Factory;

            public void Update(ref Frame frame)
            {
                if (frame.Tick != RaiseAtTick) return;
                if (frame.EventRaiser == null) return;
                if (Factory == null) return;
                frame.EventRaiser.RaiseEvent(Factory());
            }
        }

        // ── Path (c) — non-ECS spectator rollback resolves via ISimulation.GetNearestRollbackTick
        //    (TestSimulation stateless mode restores any tick — no injection needed) ──


        // ── Reflection handles ──────────────────────────────────────────

        private static readonly FieldInfo _engineEventCollectorField = typeof(KlothoEngine)
            .GetField("_eventCollector", BindingFlags.NonPublic | BindingFlags.Instance);


        private static readonly FieldInfo _engineDispatcherField = typeof(KlothoEngine)
            .GetField("_dispatcher", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _spectatorPredictionStartTickField = typeof(KlothoEngine)
            .GetField("_spectatorPredictionStartTick", BindingFlags.NonPublic | BindingFlags.Instance);

        // The 2-arg Initialize(simulation, logger) overload does not construct _dispatcher
        // (only the 3-arg overload that takes networkService does). DispatchTickEvents and
        // the Spectator inline Synced dispatch path both call _dispatcher.Dispatch — leaving
        // it null causes NRE on the first event-bearing tick. Inject one for test isolation.
        private static void InjectDispatcher(KlothoEngine engine, IKLogger logger)
        {
            if (_engineDispatcherField.GetValue(engine) != null) return;
            _engineDispatcherField.SetValue(engine, new EventDispatcher(logger, warnMs: int.MaxValue));
        }

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            _logger = factory.CreateLogger("EventDispatchSpectatorTests");
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static SpectatorStartInfo MakeStartInfo()
            => new SpectatorStartInfo
            {
                PlayerCount = 1,
                RandomSeed = 42,
                TickInterval = 25,
                PlayerIds = new List<int> { 0 },
            };

        private static int ReadSpectatorPredictionStartTick(KlothoEngine engine)
            => (int)_spectatorPredictionStartTickField.GetValue(engine);


        // Drive the spectator forward by Update calls until prediction has populated
        // _spectatorPredictionStartTick (initialized lazily inside ExecuteSpectatorPredictedTick).
        // Uses small Update slices to avoid one-shot over-accumulation surprises.
        private static void DriveSpectatorUntilPredictionStarts(KlothoEngine engine, int maxSlices = 16)
        {
            for (int i = 0; i < maxSlices; i++)
            {
                engine.Update(0.05f);
                if (ReadSpectatorPredictionStartTick(engine) >= 0)
                    return;
            }
            Assert.Fail("Spectator did not enter prediction within driver budget — setup error");
        }

        // ── (b) EcsSim path ─────────────────────────────────────────────

        [Test]
        public void Spectator_EcsSimRollback_SyncedEvent_DispatchAcrossPredictionResim_SingleFire()
        {
            const int raiseAtTick = 1;
            int dispatchedCount = 0;
            int lastCallbackTick = -1;
            int lastEvtTick = -1;

            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            var raiser = new EventRaiserSystem
            {
                RaiseAtTick = raiseAtTick,
                Factory = () => new TestSyncedEvent { Payload = 1 },
            };
            sim.AddSystem(raiser, SystemPhase.Update);

            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);

            engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent)
                {
                    dispatchedCount++;
                    lastCallbackTick = tick;
                    lastEvtTick = evt.Tick;
                }
            };

            engine.StartSpectator(MakeStartInfo());
            engine.ResetToTick(0);

            DriveSpectatorUntilPredictionStarts(engine);

            int predStart = ReadSpectatorPredictionStartTick(engine);
            Assert.LessOrEqual(predStart, raiseAtTick,
                $"Test setup invariant: prediction must start at or before raiseAtTick ({raiseAtTick}) — got predStart={predStart}");

            Assert.AreEqual(0, dispatchedCount,
                "Synced events buffered during Predicted ticks must not dispatch before promotion-to-verified");

            engine.ConfirmSpectatorTick(raiseAtTick + 1);
            engine.Update(0.01f);

            Assert.AreEqual(1, dispatchedCount,
                $"Synced event must dispatch exactly once on resim promotion-to-verified (EcsSim path). Got {dispatchedCount}. " +
                "If > 1, the inline dispatch at Spectator.cs:273-279 fired multiple times or DiffRollbackEvents did not skip Synced.");
            Assert.AreEqual(raiseAtTick, lastCallbackTick,
                "OnSyncedEvent callback tick must equal the tick at which the event was raised");
            Assert.AreEqual(raiseAtTick, lastEvtTick,
                "evt.Tick (BeginTick stamp) must match the dispatch callback tick — tick-argument invariant");
        }

        [Test]
        public void Spectator_EcsSimRollback_RegularEvent_DiffCascade_OldOnlyCancels_NewOnlyConfirms()
        {
            const int raiseAtTick = 1;
            int predictedCount = 0;
            int confirmedCount = 0;
            int canceledCount = 0;

            int variant = 1;
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            var raiser = new EventRaiserSystem
            {
                RaiseAtTick = raiseAtTick,
                Factory = () => new TestRegularEvent { Payload = variant },
            };
            sim.AddSystem(raiser, SystemPhase.Update);

            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);

            engine.OnEventPredicted += (tick, evt) => { if (evt is TestRegularEvent) predictedCount++; };
            engine.OnEventConfirmed += (tick, evt) => { if (evt is TestRegularEvent) confirmedCount++; };
            engine.OnEventCanceled  += (tick, evt) => { if (evt is TestRegularEvent) canceledCount++; };

            engine.StartSpectator(MakeStartInfo());
            engine.ResetToTick(0);

            DriveSpectatorUntilPredictionStarts(engine);

            int initialPredicted = predictedCount;
            Assert.GreaterOrEqual(initialPredicted, 1,
                "Regular event must fire OnEventPredicted during Predicted tick execution");

            // Switch variant so resim produces a different content hash → diff cascade triggers.
            variant = 2;

            int baselineConfirmed = confirmedCount;
            int baselineCanceled = canceledCount;

            engine.ConfirmSpectatorTick(raiseAtTick + 1);
            engine.Update(0.01f);

            int newCanceled = canceledCount - baselineCanceled;
            int newConfirmed = confirmedCount - baselineConfirmed;

            Assert.AreEqual(1, newCanceled,
                $"Old-only Regular event (variant 1) must be canceled exactly once via DiffRollbackEvents. Got {newCanceled}.");
            Assert.AreEqual(1, newConfirmed,
                $"New-only Regular event (variant 2) must be confirmed exactly once via DiffRollbackEvents on verified tick. Got {newConfirmed}.");
        }

        // ── (b) EcsSim path — truncated prediction tail ──

        // A Regular event predicted on a tick BEYOND the
        // confirmed tick is the "truncated tail" — re-sim halts at the confirmed tick, shrinking
        // CurrentTick below the old-cache bound. Pre-fix, DiffRollbackEvents fired a spurious
        // OnEventCanceled for the tail; with the guard (oldEvt.Tick >= CurrentTick → skip) it must not.
        // The tail is re-fired by subsequent re-prediction (Predicted), not canceled.
        [Test]
        public void Spectator_EcsSimRollback_TruncatedTailRegular_NotCanceled_IMP60_30_E4()
        {
            // SPECTATOR_INPUT_INTERVAL = 2 → MAX_SPECTATOR_PREDICTION_TICKS = 4. With confirmed at 0 the
            // prediction head can reach tick 4, so a tick-3 event sits in the lead. Confirming to tick 2
            // halts re-sim at 2 (CurrentTick → 3), leaving the tick-3 event in the truncated tail.
            const int raiseAtTick = 3;
            const int confirmTick = 2;
            int predictedCount = 0;
            int canceledCount = 0;

            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            var raiser = new EventRaiserSystem
            {
                RaiseAtTick = raiseAtTick,
                Factory = () => new TestRegularEvent { Payload = 1 },
            };
            sim.AddSystem(raiser, SystemPhase.Update);

            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);

            engine.OnEventPredicted += (tick, evt) => { if (evt is TestRegularEvent) predictedCount++; };
            engine.OnEventCanceled  += (tick, evt) => { if (evt is TestRegularEvent) canceledCount++; };

            engine.StartSpectator(MakeStartInfo());
            engine.ResetToTick(0);

            // Drive prediction forward until the tail event at raiseAtTick has been predicted
            // (guarantees the prediction head advanced past raiseAtTick → CurrentTick > raiseAtTick).
            DriveSpectatorUntilPredictionStarts(engine);
            for (int i = 0; i < 16 && predictedCount < 1; i++)
                engine.Update(0.05f);
            Assert.GreaterOrEqual(predictedCount, 1,
                "Setup invariant: the tail Regular event must be predicted (OnEventPredicted) before confirmation");

            int baselineCanceled = canceledCount;

            // Confirm a tick BELOW the tail event → re-sim halts there, truncating the tail.
            engine.ConfirmSpectatorTick(confirmTick);
            engine.Update(0.01f);

            Assert.AreEqual(0, canceledCount - baselineCanceled,
                "E-4: a truncated-tail Regular event (tick beyond re-sim CurrentTick) must NOT fire OnEventCanceled. " +
                "Pre-fix this was a spurious cancel (flicker). The guard oldEvt.Tick >= CurrentTick skips it; " +
                "re-prediction re-fires it as Predicted instead.");
        }

        // ── (c) Snapshot path ───────────────────────────────────────────

        [Test]
        public void Spectator_SnapshotRollback_SyncedEvent_DispatchAcrossPredictionResim_SingleFire()
        {
            const int raiseAtTick = 1;
            int dispatchedCount = 0;
            int lastCallbackTick = -1;
            int lastEvtTick = -1;

            var sim = new TestSimulation();
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);

            // TestSimulation is not EcsSimulation → engine takes the else branch in
            // SpectatorHandleConfirmedInput (path c). Wire EventRaiser via reflection so
            // OnAfterTickRaise events flow through the engine's _eventCollector.
            sim.EventRaiser = (ISimulationEventRaiser)_engineEventCollectorField.GetValue(engine);

            engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent)
                {
                    dispatchedCount++;
                    lastCallbackTick = tick;
                    lastEvtTick = evt.Tick;
                }
            };

            sim.OnAfterTickRaise = (tick, r) =>
            {
                if (tick == raiseAtTick + 1 && r != null)
                    r.RaiseEvent(new TestSyncedEvent { Payload = 1 });
            };

            engine.StartSpectator(MakeStartInfo());
            engine.ResetToTick(0);

            DriveSpectatorUntilPredictionStarts(engine);

            int predStart = ReadSpectatorPredictionStartTick(engine);
            Assert.LessOrEqual(predStart, raiseAtTick,
                $"Test setup invariant: prediction must start at or before raiseAtTick ({raiseAtTick}) — got predStart={predStart}");

            Assert.AreEqual(0, dispatchedCount,
                "Synced events buffered during Predicted ticks must not dispatch before promotion-to-verified");

            // The snapshot branch resolves via ISimulation.GetNearestRollbackTick —
            // TestSimulation (stateless mode) restores any tick, so no injection is needed.
            engine.ConfirmSpectatorTick(raiseAtTick + 1);
            engine.Update(0.01f);

            Assert.AreEqual(1, dispatchedCount,
                $"Synced event must dispatch exactly once on resim promotion-to-verified (snapshot path). Got {dispatchedCount}. " +
                "If > 1, the inline dispatch at Spectator.cs:326-332 fired multiple times or DiffRollbackEvents did not skip Synced.");
            Assert.AreEqual(raiseAtTick, lastCallbackTick,
                "OnSyncedEvent callback tick must equal the tick at which the event was raised");
            Assert.AreEqual(raiseAtTick, lastEvtTick,
                "evt.Tick (BeginTick stamp) must match the dispatch callback tick — tick-argument invariant");
        }

        [Test]
        public void Spectator_SnapshotRollback_RegularEvent_DiffCascade_OldOnlyCancels_NewOnlyConfirms()
        {
            const int raiseAtTick = 1;
            int predictedCount = 0;
            int confirmedCount = 0;
            int canceledCount = 0;

            int variant = 1;

            var sim = new TestSimulation();
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);
            sim.EventRaiser = (ISimulationEventRaiser)_engineEventCollectorField.GetValue(engine);

            engine.OnEventPredicted += (tick, evt) => { if (evt is TestRegularEvent) predictedCount++; };
            engine.OnEventConfirmed += (tick, evt) => { if (evt is TestRegularEvent) confirmedCount++; };
            engine.OnEventCanceled  += (tick, evt) => { if (evt is TestRegularEvent) canceledCount++; };

            sim.OnAfterTickRaise = (tick, r) =>
            {
                if (tick == raiseAtTick + 1 && r != null)
                    r.RaiseEvent(new TestRegularEvent { Payload = variant });
            };

            engine.StartSpectator(MakeStartInfo());
            engine.ResetToTick(0);

            DriveSpectatorUntilPredictionStarts(engine);

            int initialPredicted = predictedCount;
            Assert.GreaterOrEqual(initialPredicted, 1,
                "Regular event must fire OnEventPredicted during Predicted tick execution");

            int predStart = ReadSpectatorPredictionStartTick(engine);
            variant = 2;

            int baselineConfirmed = confirmedCount;
            int baselineCanceled = canceledCount;

            engine.ConfirmSpectatorTick(raiseAtTick + 1);
            engine.Update(0.01f);

            int newCanceled = canceledCount - baselineCanceled;
            int newConfirmed = confirmedCount - baselineConfirmed;

            Assert.AreEqual(1, newCanceled,
                $"Old-only Regular event (variant 1) must be canceled exactly once via DiffRollbackEvents. Got {newCanceled}.");
            Assert.AreEqual(1, newConfirmed,
                $"New-only Regular event (variant 2) must be confirmed exactly once via DiffRollbackEvents on verified tick. Got {newConfirmed}.");
        }
    }
}
