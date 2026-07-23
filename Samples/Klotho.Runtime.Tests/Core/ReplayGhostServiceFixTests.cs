using System;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Replay-session ghost network service removal + server RTT staleness guard.
    ///
    /// Pins:
    ///   (1) An IsReplay session creates NO network service (the old path followed the replay
    ///       metadata's Mode down the host branch and subscribed a full service to the live
    ///       main transport) and survives Stop() — which every replay reaches automatically
    ///       via the recorded MatchEnd event driving the auto-shutdown scheduler.
    ///   (2) The new network-less engine Initialize overload preserves the sim/view callbacks
    ///       (losing them would silently degrade replay view updates) and shares the re-init
    ///       guard with the other overloads.
    ///   (3) IsStaleRttSample: pongs older than two ping intervals are discarded from the
    ///       smoother/push path — the threshold is staleness (2000ms), NOT RttSanityMaxMs
    ///       (240ms — overlaps legitimate long-haul RTT; discarding there would starve
    ///       high-RTT players of delay pushes).
    ///   (4) PlayerRttSmoother median context: a single spike is already rejected, a burst
    ///       (window majority) shifts the median wholesale — the exact misfire the staleness
    ///       guard exists to block.
    /// </summary>
    [TestFixture]
    public class ReplayGhostServiceFixTests
    {
        private sealed class StubViewCallbacks : IViewCallbacks
        {
            public void OnGameStart(IKlothoEngine engine) { }
            public void OnTickExecuted(int tick) { }
            public void OnLateJoinActivated(IKlothoEngine engine) { }
        }

        private sealed class StubSimulationCallbacks : ISimulationCallbacks
        {
            public void RegisterSystems(EcsSimulation simulation) { }
            public void OnInitializeWorld(IKlothoEngine engine) { }
            public void OnPollInput(int playerId, int tick, ICommandSender sender) { }
            public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId) { } // no per-join world state to seed
        }

        private static readonly FieldInfo _viewCallbacksField =
            typeof(KlothoEngine).GetField("_viewCallbacks", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _simulationCallbacksField =
            typeof(KlothoEngine).GetField("_simulationCallbacks", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            _logger = factory.CreateLogger("ReplayGhostServiceFixTests");
        }

        private static SimulationConfig MakeSDConfig()
            => new SimulationConfig
            {
                Mode = NetworkMode.ServerDriven,
                TickIntervalMs = 25,
                MaxRollbackTicks = 50,
            };

        // ── (1) Replay session shape ────────────────────────────────────

        [Test]
        public void ReplaySession_Create_HasNoNetworkService_AndStopDoesNotThrow()
        {
            var session = KlothoSession.Create(new KlothoSessionSetup
            {
                Logger = _logger,
                SimulationConfig = MakeSDConfig(), // replay metadata Mode — used to spawn the SD ghost service
                SimulationCallbacks = new StubSimulationCallbacks(),
                ViewCallbacks = new StubViewCallbacks(),
                IsReplay = true,
            });

            Assert.IsNull(session.NetworkService,
                "A replay session must not create a network service (ghost-service fix)");
            Assert.IsNotNull(session.Engine);

            // Every replay reaches Stop automatically (recorded MatchEnd → auto-shutdown
            // scheduler) — the non-spectator branch must tolerate NetworkService == null.
            Assert.DoesNotThrow(() => session.Stop(saveReplay: false));
        }

        [Test]
        public void ReplaySession_Create_PreservesEngineCallbacks()
        {
            var viewCallbacks = new StubViewCallbacks();
            var simCallbacks = new StubSimulationCallbacks();

            var session = KlothoSession.Create(new KlothoSessionSetup
            {
                Logger = _logger,
                SimulationConfig = MakeSDConfig(),
                SimulationCallbacks = simCallbacks,
                ViewCallbacks = viewCallbacks,
                IsReplay = true,
            });

            Assert.AreSame(viewCallbacks, _viewCallbacksField.GetValue(session.Engine),
                "Replay engine must keep the view callbacks (network-less overload)");
            Assert.AreSame(simCallbacks, _simulationCallbacksField.GetValue(session.Engine),
                "Replay engine must keep the simulation callbacks");

            session.Stop(saveReplay: false);
        }

        // ── (2) Network-less overload re-init guard ─────────────────────

        [Test]
        public void NetworklessInitializeOverload_Twice_Throws()
        {
            var engine = new KlothoEngine(MakeSDConfig(), new SessionConfig());
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            engine.Initialize(sim, _logger, new StubSimulationCallbacks(), new StubViewCallbacks());

            Assert.Throws<InvalidOperationException>(
                () => engine.Initialize(sim, _logger, new StubSimulationCallbacks()),
                "The network-less overload chains into the guarded 2-arg body (re-init guard)");
        }

        // ── (3) Staleness threshold ─────────────────────────────────────

        private static bool IsStale(long rttMs)
        {
            var method = typeof(ServerNetworkService).GetMethod(
                "IsStaleRttSample", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "ServerNetworkService.IsStaleRttSample not found");
            return (bool)method.Invoke(null, new object[] { rttMs });
        }

        [Test]
        public void IsStaleRttSample_LegitimateHighRtt_NotStale()
        {
            // 240ms~2000ms is legitimate long-haul territory — must keep feeding the
            // smoother (and the calculator clamp), NOT be discarded (the rejected
            // RttSanityMaxMs-based criterion would have starved these players of pushes).
            Assert.IsFalse(IsStale(240));
            Assert.IsFalse(IsStale(1000));
            Assert.IsFalse(IsStale(2000), "Boundary: exactly 2x ping interval is still accepted");
        }

        [Test]
        public void IsStaleRttSample_BacklogFlush_IsStale()
        {
            Assert.IsTrue(IsStale(2001), "Past 2x ping interval the measurement is stale");
            Assert.IsTrue(IsStale(19903), "The observed replay backlog-flush sample (2026-06-11 log)");
        }

        // ── (4) Smoother median context ─────────────────────────────────

        private static (object smoother, MethodInfo onSample, MethodInfo tryGet) MakeSmoother()
        {
            var type = typeof(ServerNetworkService).Assembly.GetType("xpTURN.Klotho.Network.PlayerRttSmoother");
            Assert.IsNotNull(type, "PlayerRttSmoother not found");
            var smoother = Activator.CreateInstance(type, nonPublic: true);
            return (smoother,
                type.GetMethod("OnSample"),
                type.GetMethod("TryGetSmoothedRtt"));
        }

        private static int Median(object smoother, MethodInfo tryGet)
        {
            var args = new object[] { 0 };
            Assert.IsTrue((bool)tryGet.Invoke(smoother, args), "Smoother below MIN_SAMPLES");
            return (int)args[0];
        }

        [Test]
        public void PlayerRttSmoother_SingleSpikeRejected_BurstShiftsMedian()
        {
            var (smoother, onSample, tryGet) = MakeSmoother();

            foreach (var s in new[] { 100, 110, 105 })
                onSample.Invoke(smoother, new object[] { s });
            int baseline = Median(smoother, tryGet);
            Assert.LessOrEqual(baseline, 110, "Baseline median from normal samples");

            // Single spike: 5-sample sliding median rejects it — existing behavior.
            onSample.Invoke(smoother, new object[] { 19903 });
            Assert.LessOrEqual(Median(smoother, tryGet), 110,
                "A single stale spike must not move the median (pre-existing median rejection)");

            // Burst (window majority): the median shifts wholesale — the exact misfire the
            // staleness guard prevents by never feeding such samples.
            onSample.Invoke(smoother, new object[] { 19903 });
            onSample.Invoke(smoother, new object[] { 19903 });
            Assert.GreaterOrEqual(Median(smoother, tryGet), 19903,
                "A stale burst occupying the window majority shifts the median — why the guard exists");
        }
    }
}
