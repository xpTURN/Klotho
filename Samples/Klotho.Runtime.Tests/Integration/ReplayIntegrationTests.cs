using System.IO;
using System.Buffers.Binary;
using NUnit.Framework;
using xpTURN.Klotho.Logging;


using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Integration.Tests
{
    [TestFixture]
    public class ReplayIntegrationTests
    {
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

        #region 45. Replay file round-trip (uncompressed — no LZ4)

        // Replays are written uncompressed, self-framed by the RPLY magic header (the bundled LZ4
        // compressor was removed). This guards the SaveReplayToFile → LoadFromFile file path — the
        // other replay tests are in-memory only — and asserts a lossless round-trip.
        [Test]
        public void Replay_SaveLoadFile_Uncompressed_RoundTrips()
        {
            const uint RPLY_MAGIC = 0x52504C59;
            string path = Path.Combine(Path.GetTempPath(), "klotho_replay_roundtrip_imp73.rpl");
            if (File.Exists(path)) File.Delete(path);

            try
            {
                // ── Record ──
                _harness.CreateHost(4);
                _harness.AddGuest();
                _harness.StartPlaying();
                _harness.AdvanceAllToTick(60);

                long recordedHash = _harness.Host.Simulation.GetStateHash();

                _harness.Host.Engine.Stop();
                var recorded = _harness.Host.Engine.GetCurrentReplayData();
                Assert.IsNotNull(recorded, "Recorded ReplayData should not be null");
                Assert.Greater(recorded.Metadata.TotalTicks, 0, "Recorded replay should have ticks");

                // ── Save to file ──
                _harness.Host.Engine.SaveReplayToFile(path);
                Assert.IsTrue(File.Exists(path), "Replay file should be written");

                // Uncompressed: the file must begin with the RPLY magic (not an LZ4 frame header).
                byte[] head = File.ReadAllBytes(path);
                Assert.GreaterOrEqual(head.Length, 4, "Replay file too small");
                Assert.AreEqual(RPLY_MAGIC, BinaryPrimitives.ReadUInt32LittleEndian(head),
                    "Replay file must be uncompressed (RPLY magic header)");

                // ── Load from file ──
                var loader = new ReplaySystem(new CommandFactory(), _logger);
                loader.LoadFromFile(path);
                var loaded = loader.CurrentReplayData;
                Assert.IsNotNull(loaded, "Loaded ReplayData should not be null");
                Assert.AreEqual(recorded.Metadata.TotalTicks, loaded.Metadata.TotalTicks,
                    "Loaded TotalTicks should match recorded");

                // ── Playback the loaded replay → state hash must match the recorded one ──
                var replaySim = new TestSimulation();
                replaySim.SetPlayerCount(2);
                var replayEngine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
                replayEngine.Initialize(replaySim, _logger);
                replayEngine.SetCommandFactory(new CommandFactory());
                replayEngine.StartReplay(loaded);

                int maxIterations = loaded.Metadata.TotalTicks * 2;
                for (int i = 0; i < maxIterations; i++)
                {
                    if (replayEngine.State.IsEnded()) break;
                    replayEngine.Update(loaded.Metadata.TickIntervalMs);
                }

                Assert.AreEqual(recordedHash, replaySim.GetStateHash(),
                    "Replay loaded from uncompressed file should reproduce the recorded StateHash");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        #endregion
    }
}
