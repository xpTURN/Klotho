using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Integration.Tests
{
    /// <summary>
    /// Replay SeekReplay regressions:
    ///   - the seek re-simulation loop raised events without draining the collector, leaking
    ///     them to the EventPool (BeginTick + pool-return drop fixes it).
    ///   - a backward seek did not lower the Synced dispatch watermark (Synced went silent on
    ///     rewound playback) nor reset the event-buffer ring-wrap slot markers (ClearTick dev
    ///     guard false-fired). The watermark cascade + ClearAll fix it.
    ///
    /// All three reproduce only via the replay seek path (live netcode unaffected). The test sim is
    /// non-ECS, so the engine does NOT auto-wire its event collector as the raiser — the tests wire
    /// `replaySim.EventRaiser = engine._eventCollector` by reflection (mirror EventDispatchTests),
    /// otherwise OnAfterTickRaise raises into a disconnected raiser and nothing reproduces.
    /// </summary>
    [TestFixture]
    public class ReplaySeekTests
    {
        private static readonly FieldInfo _engineEventCollectorField = typeof(KlothoEngine)
            .GetField("_eventCollector", BindingFlags.NonPublic | BindingFlags.Instance);

        private KlothoTestHarness _harness;
        private IKLogger _logger;

        private sealed class TestSyncedEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 90001;
            public override EventMode Mode => EventMode.Synced;
            public override long GetContentHash() => Payload;
        }

        private sealed class TestRegularEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 90002;
            public override EventMode Mode => EventMode.Regular;
            public override long GetContentHash() => Payload;
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Trace);
                b.AddUnityDebug();
            });
            _logger = factory.CreateLogger("ReplaySeekTests");
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

        /// <summary>Records a short 2-player match and returns its ReplayData (≈100 ticks).</summary>
        private IReplayData RecordReplay()
        {
            _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.StartPlaying();
            _harness.AdvanceAllToTick(100);
            _harness.Host.Engine.Stop();
            var replayData = _harness.Host.Engine.GetCurrentReplayData();
            Assert.IsNotNull(replayData, "ReplayData should not be null");
            Assert.Greater(replayData.Metadata.TotalTicks, 60, "Replay must be long enough for seek tests");
            return replayData;
        }

        private static KlothoEngine BuildReplayEngine(TestSimulation replaySim, IKLogger logger, int maxRollbackTicks)
        {
            var simConfig = new SimulationConfig { MaxRollbackTicks = maxRollbackTicks };
            var engine = new KlothoEngine(simConfig, new SessionConfig());
            engine.Initialize(replaySim, logger);
            engine.SetCommandFactory(new CommandFactory());
            return engine;
        }

        private static void WireEventRaiser(KlothoEngine engine, TestSimulation sim)
        {
            sim.EventRaiser = (ISimulationEventRaiser)_engineEventCollectorField.GetValue(engine);
        }

        private static void DriveUpdates(KlothoEngine engine, IReplayData replayData, int count)
        {
            // ReplayPlayer.Update treats deltaTime as seconds (accumulator += deltaTime * 1000),
            // so feed TickIntervalMs/1000 to advance exactly one replay tick per call. (Passing
            // TickIntervalMs directly would add 25_000ms and finish the whole replay in one call.)
            float deltaSeconds = replayData.Metadata.TickIntervalMs / 1000f;
            for (int i = 0; i < count; i++)
            {
                if (engine.State.IsEnded())
                    break;
                engine.Update(deltaSeconds);
            }
        }

        // ── seek re-simulation loop returns raised events to the pool (no leak) ──

        [Test]
        public void Replay_SeekReSimulationLoop_RaisedEventsReturnedToPool_NoLeak()
        {
            var replayData = RecordReplay();

            var replaySim = new TestSimulation { UseDeterministicHash = true };
            replaySim.SetPlayerCount(2);
            // Raise one event on every re-simulated tick so the seek loop [0, target) has events to
            // leak. (sim.CurrentTick is post-increment, so this fires inside every Tick.)
            replaySim.OnAfterTickRaise = (tick, raiser) =>
            {
                if (raiser != null)
                    raiser.RaiseEvent(new TestRegularEvent { Payload = tick });
            };

            var engine = BuildReplayEngine(replaySim, _logger, maxRollbackTicks: 50);
            WireEventRaiser(engine, replaySim);
            engine.StartReplay(replayData);

            // StartReplay leaves a snapshot only at tick 0, so SeekReplay(T) re-simulates [0, T) —
            // a non-empty loop that raises (and, pre-fix, leaks) one event per tick.
            int baseline = EventPool.GetOutstandingCount();

            // Soak: repeated seeks. Pre-fix this grows monotonically (delta > 0); post-fix it is flat.
            for (int s = 0; s < 5; s++)
            {
                engine.SeekReplay(30);
                engine.SeekReplay(10);
            }

            int delta = EventPool.GetOutstandingCount() - baseline;
            Assert.AreEqual(0, delta,
                "Seek re-simulation must drop its raised events back to the pool (E-5b leak). " +
                $"Outstanding grew by {delta} across repeated seeks.");
        }

        // ── backward seek re-dispatches Synced events on resumed playback ──

        [Test]
        public void Replay_BackwardSeek_ReDispatchesSyncedEvents()
        {
            var replayData = RecordReplay();

            const int syncedAtTick = 5;
            var replaySim = new TestSimulation { UseDeterministicHash = true };
            replaySim.SetPlayerCount(2);
            // sim.CurrentTick is post-increment, so checking (syncedAtTick + 1) lands the event on
            // engine replay-tick syncedAtTick (mirror EventDispatchTests).
            replaySim.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == syncedAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 7 });
            };

            int syncedDispatchCount = 0;
            var engine = BuildReplayEngine(replaySim, _logger, maxRollbackTicks: 50);
            WireEventRaiser(engine, replaySim);
            engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent)
                    syncedDispatchCount++;
            };

            engine.StartReplay(replayData);

            // Forward to past the Synced tick → it dispatches once and raises the watermark.
            DriveUpdates(engine, replayData, syncedAtTick + 10);
            Assert.AreEqual(1, syncedDispatchCount,
                "Synced event should dispatch exactly once on initial forward playback");

            // Rewind below the Synced tick, then resume past it.
            engine.SeekReplay(syncedAtTick - 2);
            DriveUpdates(engine, replayData, 10);

            Assert.AreEqual(2, syncedDispatchCount,
                "Backward seek must lower the watermark so the resumed playback re-dispatches the " +
                "Synced event (E-6 symptom ①). Pre-fix it stays silent (count remains 1).");
        }

        // ── long backward seek does not false-fire the ClearTick dev guard ──

        [Test]
        public void Replay_LongBackwardSeek_NoClearTickDevGuardKError()
        {
            var replayData = RecordReplay();

            // Small rollback window so the event buffer ring (cap = MaxRollbackTicks + 2 = 10) wraps
            // quickly: a backward seek of distance >= 10 maps the resume tick's slot onto a newer
            // occupant, which pre-fix trips the ClearTick newer-occupant dev guard.
            const int maxRollback = 8;
            var replaySim = new TestSimulation { UseDeterministicHash = true };
            replaySim.SetPlayerCount(2);
            replaySim.OnAfterTickRaise = (tick, raiser) =>
            {
                if (raiser != null)
                    raiser.RaiseEvent(new TestRegularEvent { Payload = tick });
            };

            var logCapture = new LogCapture();
            var engine = BuildReplayEngine(replaySim, logCapture, maxRollbackTicks: maxRollback);
            WireEventRaiser(engine, replaySim);
            engine.StartReplay(replayData);

            // Forward well past the ring capacity so slots hold newer-tick markers.
            DriveUpdates(engine, replayData, 25);
            logCapture.Clear();

            // Backward seek of distance >> capacity, then resume so HandleReplayTick runs ClearTick.
            engine.SeekReplay(0);
            DriveUpdates(engine, replayData, 5);

            Assert.IsFalse(logCapture.Contains(KLogLevel.Error, "destroys a NEWER occupant"),
                "A long backward seek must reset the ring-wrap slot markers (ClearAll) so the " +
                "resumed ClearTick does not false-fire the dev guard (E-6 symptom ②).");
        }
    }
}
