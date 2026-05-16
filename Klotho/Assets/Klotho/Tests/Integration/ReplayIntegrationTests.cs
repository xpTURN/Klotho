using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Integration.Tests
{
    [TestFixture]
    public class ReplayIntegrationTests
    {
        private KlothoTestHarness _harness;
        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("ReplayIntegrationTests");
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

        #region 44. Replay PlayerJoinCommand playback

        [Test]
        public void Replay_PlayerJoinCommand_ReproducesCorrectState()
        {
            // ── Recording stage ──

            // 1. Host + Guest1 → Playing (2 players, recording starts automatically)
            _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.StartPlaying();

            Assert.IsTrue(_harness.Host.Engine.IsRecording, "Recording should be active");

            // 2. Advance the game
            _harness.AdvanceAllToTick(50);

            // 3. Late Join Guest connects
            var lateJoinGuest = _harness.AddLateJoinGuest();

            // 4. Handshake + catchup completed
            _harness.PumpMessages(20);
            _harness.AdvanceAllToTick(100);

            // Capture state during recording
            long recordedHash = _harness.Host.Simulation.GetStateHash();
            int recordedPlayerCount = _harness.Host.NetworkService.PlayerCount;

            // 5. Stop recording + obtain ReplayData
            _harness.Host.Engine.Stop();
            var replayData = _harness.Host.Engine.GetCurrentReplayData();

            Assert.IsNotNull(replayData, "ReplayData should not be null");
            Assert.Greater(replayData.Metadata.TotalTicks, 0, "ReplayData should have ticks");

            // ── Playback stage ──

            // 6. Create new Engine + Simulation (without network)
            var replaySim = new TestSimulation();
            replaySim.SetPlayerCount(2); // 2 players at recording start

            var replayEngine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            var commandFactory = new CommandFactory();
            replayEngine.Initialize(replaySim, _logger);
            replayEngine.SetCommandFactory(commandFactory);

            // 7-8. Play replay
            replayEngine.StartReplay(replayData);

            Assert.AreEqual(KlothoState.Running, replayEngine.State, "Replay should be Running");

            // Tick playback — consume all ticks via enough Update calls
            int maxIterations = replayData.Metadata.TotalTicks * 2;
            for (int i = 0; i < maxIterations; i++)
            {
                if (replayEngine.State.IsEnded())
                    break;
                replayEngine.Update(replayData.Metadata.TickIntervalMs);
            }

            // 9. Verify
            Assert.AreEqual(recordedHash, replaySim.GetStateHash(),
                "Replay StateHash should match recorded StateHash");
        }

        #endregion
    }
}
