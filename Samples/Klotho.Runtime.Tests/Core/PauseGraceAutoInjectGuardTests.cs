using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;


using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Input;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Verifies the Pause-grace auto-inject guard in KlothoEngine.cs:815-839.
    /// When EndGracePolicy.Pause is active after match-end, the engine emits StopCommand and
    /// must skip the subsequent auto-inject EmptyCommand. Without the guard, when
    /// _recommendedExtraDelay > 0 the empty would overwrite the stop at the shifted slot.
    /// </summary>
    [TestFixture]
    public class PauseGraceAutoInjectGuardTests
    {
        private static readonly FieldInfo _matchEndedDispatchedField = typeof(KlothoEngine)
            .GetField("_matchEndedDispatched", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _inputBufferField = typeof(KlothoEngine)
            .GetField("_inputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);

        private KlothoTestHarness _harness;
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger(nameof(PauseGraceAutoInjectGuardTests));
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
        public void TearDown()
        {
            _harness.Reset();
        }

        [Test]
        public void PauseGrace_With_RecommendedExtraDelay_DoesNotOverwriteStop()
        {
            var peer = _harness.Guests[0];
            var engine = peer.Engine;

            ((SessionConfig)engine.SessionConfig).EndGracePolicy = EndGracePolicy.Pause;
            _matchEndedDispatchedField.SetValue(engine, true);
            peer.Simulation.MatchEnded = true; // gate now also requires the current state to be ended

            const int extraDelay = 5;
            engine.ApplyExtraDelay(extraDelay, ExtraDelaySource.DynamicPush);

            int tickBefore = engine.CurrentTick;
            _harness.Tick();
            _harness.PumpMessages();

            int expectedSlot = tickBefore + engine.SimulationConfig.InputDelayTicks + extraDelay;
            var inputBuffer = (InputBuffer)_inputBufferField.GetValue(engine);
            var cmd = inputBuffer.GetCommand(expectedSlot, peer.LocalPlayerId);

            Assert.IsNotNull(cmd, $"Expected StopCommand at slot {expectedSlot}, found null");
            Assert.IsInstanceOf<StopCommand>(cmd,
                $"Pause grace should emit StopCommand; auto-inject EmptyCommand must not overwrite. Got {cmd.GetType().Name}");
        }

        [Test]
        public void PauseGrace_Baseline_NoExtraDelay_StopInDefaultSlot()
        {
            var peer = _harness.Guests[0];
            var engine = peer.Engine;

            ((SessionConfig)engine.SessionConfig).EndGracePolicy = EndGracePolicy.Pause;
            _matchEndedDispatchedField.SetValue(engine, true);
            peer.Simulation.MatchEnded = true; // gate now also requires the current state to be ended

            int tickBefore = engine.CurrentTick;
            _harness.Tick();
            _harness.PumpMessages();

            int expectedSlot = tickBefore + engine.SimulationConfig.InputDelayTicks;
            var inputBuffer = (InputBuffer)_inputBufferField.GetValue(engine);
            var cmd = inputBuffer.GetCommand(expectedSlot, peer.LocalPlayerId);

            Assert.IsInstanceOf<StopCommand>(cmd,
                $"Baseline (_recommendedExtraDelay=0) must hold StopCommand at inputTick slot");
        }

        [Test]
        public void EndGracePolicy_Continue_AutoInjectStillFires()
        {
            var peer = _harness.Guests[0];
            var engine = peer.Engine;

            ((SessionConfig)engine.SessionConfig).EndGracePolicy = EndGracePolicy.Continue;
            _matchEndedDispatchedField.SetValue(engine, true);

            int tickBefore = engine.CurrentTick;
            _harness.Tick();
            _harness.PumpMessages();

            int expectedSlot = tickBefore + engine.SimulationConfig.InputDelayTicks;
            var inputBuffer = (InputBuffer)_inputBufferField.GetValue(engine);
            var cmd = inputBuffer.GetCommand(expectedSlot, peer.LocalPlayerId);

            Assert.IsInstanceOf<EmptyCommand>(cmd,
                $"Continue policy must not enter Pause branch; auto-inject EmptyCommand should fill the slot");
        }
    }
}
