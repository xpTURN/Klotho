using System;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Input;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// match-end divergence policy: fire-forward backstop and
    /// state-based Pause-grace StopCommand gate.
    ///   - Backstop: ApplyFullState into a restored match-ended state with no prior OnMatchEnded fires
    ///     OnMatchEnded exactly once (covers ring-wipe under-fire and forward-jump loss); a
    ///     non-ended restore does not fire; a second resync does not re-fire (exactly-once).
    ///   - Gate: Pause-grace injects StopCommand only when (latch START) && (current state ended, RELEASE)
    ///     — a predicted-only match-end (latch false) must NOT inject (determinism/glitch guard), and a
    ///     removed match-end (state false) releases the gate without un-firing the latch.
    ///   - TestSimulation match-end state round-trips through serialize/restore (the backstop's premise).
    /// </summary>
    [TestFixture]
    public class MatchEndDivergenceBackstopTests
    {
        private static readonly FieldInfo _matchEndedDispatchedField = typeof(KlothoEngine)
            .GetField("_matchEndedDispatched", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _inputBufferField = typeof(KlothoEngine)
            .GetField("_inputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo _applyFullStateMethod = typeof(KlothoEngine)
            .GetMethod("ApplyFullState", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Type _applyReasonType =
            _applyFullStateMethod.GetParameters()[3].ParameterType;

        private IKLogger _logger;
        private KlothoTestHarness _harness;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Error));
            _logger = factory.CreateLogger(nameof(MatchEndDivergenceBackstopTests));
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _harness = new KlothoTestHarness(_logger);
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
        }

        [TearDown]
        public void TearDown() => _harness.Reset();

        private object ApplyReason(string name) => Enum.Parse(_applyReasonType, name);

        private object InvokeApplyFullState(KlothoEngine engine, int tick, byte[] data, long hash, string reason)
            => _applyFullStateMethod.Invoke(engine, new object[] { tick, data, hash, ApplyReason(reason) });

        // ── Backstop ───────────────────────────────────────────────

        [Test]
        public void Backstop_ResyncIntoEndedState_FiresOnMatchEndedOnce()
        {
            var peer = _harness.Guests[0];
            var engine = peer.Engine;

            int matchEndedCount = 0;
            int firedTick = -1;
            engine.OnMatchEnded += (t, e) => { matchEndedCount++; firedTick = t; };

            // No prior OnMatchEnded (latch false), but the restored state is match-ended.
            _matchEndedDispatchedField.SetValue(engine, false);
            peer.Simulation.MatchEnded = true;
            peer.Simulation.MatchWinnerId = 1;
            var (data, hash) = peer.Simulation.SerializeFullStateWithHash();

            int tick = engine.CurrentTick + 10; // forward-jump resync shape
            InvokeApplyFullState(engine, tick, data, hash, "ResyncRequest");

            Assert.AreEqual(1, matchEndedCount, "backstop must fire OnMatchEnded exactly once on resync into an ended state");
            Assert.AreEqual(tick, firedTick, "backstop fires at the restored tick");
            Assert.IsTrue(engine.IsMatchEnded, "latch set by backstop");
        }

        [Test]
        public void Backstop_NotEndedState_DoesNotFire()
        {
            var peer = _harness.Guests[0];
            var engine = peer.Engine;

            int matchEndedCount = 0;
            engine.OnMatchEnded += (t, e) => matchEndedCount++;

            _matchEndedDispatchedField.SetValue(engine, false);
            peer.Simulation.MatchEnded = false; // restored state is NOT ended
            var (data, hash) = peer.Simulation.SerializeFullStateWithHash();

            InvokeApplyFullState(engine, engine.CurrentTick + 10, data, hash, "ResyncRequest");

            Assert.AreEqual(0, matchEndedCount, "backstop must not fire when the restored state is not ended");
            Assert.IsFalse(engine.IsMatchEnded);
        }

        [Test]
        public void Backstop_SecondResync_NoReFire()
        {
            var peer = _harness.Guests[0];
            var engine = peer.Engine;

            int matchEndedCount = 0;
            engine.OnMatchEnded += (t, e) => matchEndedCount++;

            _matchEndedDispatchedField.SetValue(engine, false);
            peer.Simulation.MatchEnded = true;
            peer.Simulation.MatchWinnerId = 1;
            var (data, hash) = peer.Simulation.SerializeFullStateWithHash();

            InvokeApplyFullState(engine, engine.CurrentTick + 10, data, hash, "ResyncRequest");
            InvokeApplyFullState(engine, engine.CurrentTick + 10, data, hash, "ResyncRequest");

            Assert.AreEqual(1, matchEndedCount, "exactly-once: latch guard blocks a re-fire on a second resync into an ended state");
        }

        // ── State-based StopCommand gate ──────────────────────────────────

        [Test]
        public void Gate_PredictedOnlyMatchEnd_NoStopInjection()
        {
            // Latch false (not yet verified) but state ended (predicted): the latch term must block
            // injection — a state-only gate would inject StopCommand during prediction (glitch regression).
            var peer = _harness.Guests[0];
            var engine = peer.Engine;

            ((SessionConfig)engine.SessionConfig).EndGracePolicy = EndGracePolicy.Pause;
            _matchEndedDispatchedField.SetValue(engine, false);
            peer.Simulation.MatchEnded = true;

            int tickBefore = engine.CurrentTick;
            _harness.Tick();
            _harness.PumpMessages();

            int slot = tickBefore + engine.SimulationConfig.InputDelayTicks;
            var inputBuffer = (InputBuffer)_inputBufferField.GetValue(engine);
            var cmd = inputBuffer.GetCommand(slot, peer.LocalPlayerId);

            Assert.IsInstanceOf<EmptyCommand>(cmd,
                "predicted-only match-end (latch false) must not inject StopCommand — auto-inject fills the slot");
        }

        [Test]
        public void Gate_RemovedMatchEnd_ReleasesStopCommand()
        {
            var peer = _harness.Guests[0];
            var engine = peer.Engine;
            ((SessionConfig)engine.SessionConfig).EndGracePolicy = EndGracePolicy.Pause;
            var inputBuffer = (InputBuffer)_inputBufferField.GetValue(engine);

            // Verified match-end: latch true + state ended → StopCommand injected.
            _matchEndedDispatchedField.SetValue(engine, true);
            peer.Simulation.MatchEnded = true;
            int t0 = engine.CurrentTick;
            _harness.Tick();
            _harness.PumpMessages();
            var endedCmd = inputBuffer.GetCommand(t0 + engine.SimulationConfig.InputDelayTicks, peer.LocalPlayerId);
            Assert.IsInstanceOf<StopCommand>(endedCmd, "verified match-end under Pause injects StopCommand");

            // Removed (state restored to non-ended) — latch stays true (never un-fire) but the gate releases.
            peer.Simulation.MatchEnded = false;
            int t1 = engine.CurrentTick;
            _harness.Tick();
            _harness.PumpMessages();
            var releasedCmd = inputBuffer.GetCommand(t1 + engine.SimulationConfig.InputDelayTicks, peer.LocalPlayerId);

            Assert.IsInstanceOf<EmptyCommand>(releasedCmd,
                "removed match-end releases the gate (OnPollInput/auto-inject resumes) without un-firing the latch");
            Assert.IsTrue(engine.IsMatchEnded, "latch is NOT un-fired (one-way notification contract)");
        }

        // ── Hook round-trip (backstop premise) ──────────────────────────────────

        [Test]
        public void MatchEndState_RoundTripsThroughSerializeRestore()
        {
            var src = new TestSimulation();
            src.Initialize();
            Assert.IsFalse(src.IsMatchEndedState, "fresh sim is not ended (absence = false)");

            src.MatchEnded = true;
            src.MatchWinnerId = 2;
            var data = src.SerializeFullState();

            var dst = new TestSimulation();
            dst.Initialize();
            dst.RestoreFromFullState(data);

            Assert.IsTrue(dst.IsMatchEndedState, "restored state reports ended (premise of the resync backstop)");
            Assert.AreEqual(2, dst.GetActiveMatchEnd().WinnerPlayerId, "winner round-trips");
        }
    }
}
