using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.State;

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

        // ── Path (c) — stub IStateSnapshot for RingSnapshotManager injection ──

        private sealed class StubSnapshot : IStateSnapshot
        {
            public int Tick { get; set; }
            public byte[] Serialize() => Array.Empty<byte>();
            public void Deserialize(byte[] data) { }
            public ulong CalculateHash() => 0;
        }

        // ── Reflection handles ──────────────────────────────────────────

        private static readonly FieldInfo _engineEventCollectorField = typeof(KlothoEngine)
            .GetField("_eventCollector", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _engineSnapshotManagerField = typeof(KlothoEngine)
            .GetField("_snapshotManager", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _engineDispatcherField = typeof(KlothoEngine)
            .GetField("_dispatcher", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _spectatorPredictionStartTickField = typeof(KlothoEngine)
            .GetField("_spectatorPredictionStartTick", BindingFlags.NonPublic | BindingFlags.Instance);

        // The 2-arg Initialize(simulation, logger) overload does not construct _dispatcher
        // (only the 3-arg overload that takes networkService does). DispatchTickEvents and
        // the Spectator inline Synced dispatch path both call _dispatcher.Dispatch — leaving
        // it null causes NRE on the first event-bearing tick. Inject one for test isolation.
        private static void InjectDispatcher(KlothoEngine engine, ILogger logger)
        {
            if (_engineDispatcherField.GetValue(engine) != null) return;
            _engineDispatcherField.SetValue(engine, new EventDispatcher(logger, warnMs: int.MaxValue));
        }

        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                b.AddZLoggerUnityDebug();
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

        private static IStateSnapshotManager ReadSnapshotManager(KlothoEngine engine)
            => (IStateSnapshotManager)_engineSnapshotManagerField.GetValue(engine);

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

            DriveSpectatorUntilPredictionStarts(engine);

            int predStart = ReadSpectatorPredictionStartTick(engine);
            Assert.LessOrEqual(predStart, raiseAtTick,
                $"Test setup invariant: prediction must start at or before raiseAtTick ({raiseAtTick}) — got predStart={predStart}");

            Assert.AreEqual(0, dispatchedCount,
                "Synced events buffered during Predicted ticks must not dispatch before promotion-to-verified");

            // _snapshotManager.GetSnapshot(predStart) must return non-null for the snapshot
            // branch to proceed. Engine code never populates RingSnapshotManager itself, so
            // inject a stub at predStart that Rollback.cs treats as a valid restore point.
            ReadSnapshotManager(engine).SaveSnapshot(predStart, new StubSnapshot { Tick = predStart });

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

            DriveSpectatorUntilPredictionStarts(engine);

            int initialPredicted = predictedCount;
            Assert.GreaterOrEqual(initialPredicted, 1,
                "Regular event must fire OnEventPredicted during Predicted tick execution");

            int predStart = ReadSpectatorPredictionStartTick(engine);
            ReadSnapshotManager(engine).SaveSnapshot(predStart, new StubSnapshot { Tick = predStart });

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
