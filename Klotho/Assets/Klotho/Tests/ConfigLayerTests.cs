using System.Collections.Generic;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Tests
{
    [TestFixture]
    public class ConfigLayerTests
    {
        private KlothoTestHarness _harness;
        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("ConfigLayerTests");
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

        // ── SimulationConfig ──────────────────────

        [Test]
        public void Engine_SimulationConfig_TickIntervalMs_MatchesCreationValue()
        {
            var simConfig = new SimulationConfig { TickIntervalMs = 33 };
            var sessionConfig = new SessionConfig();
            var engine = new KlothoEngine(simConfig, sessionConfig);

            Assert.AreEqual(33, engine.SimulationConfig.TickIntervalMs);
        }

        [Test]
        public void Engine_SessionConfig_RandomSeed_MatchesGameStartMessage()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();

            int hostSeed = _harness.Host.Engine.SessionConfig != null
                ? _harness.Host.NetworkService.RandomSeed
                : 0;

            // Verify that Host and Guest RandomSeed match
            foreach (var guest in _harness.Guests)
            {
                Assert.AreEqual(hostSeed, guest.NetworkService.RandomSeed,
                    "Guest RandomSeed must match host RandomSeed from GameStartMessage");
            }
        }

        // ── SimulationConfigMessage serialization round-trip ──

        [Test]
        public void SimulationConfigMessage_RoundTrip_AllFieldsPreserved()
        {
            var original = new SimulationConfigMessage
            {
                TickIntervalMs = 20,
                InputDelayTicks = 3,
                MaxRollbackTicks = 60,
                SyncCheckInterval = 15,
                UsePrediction = false,
                MaxEntities = 512,
                Mode = (int)NetworkMode.P2P,
                HardToleranceMs = 10,
                InputResendIntervalMs = 75,
                MaxUnackedInputs = 25,
                ServerSnapshotRetentionTicks = 5,
                EventDispatchWarnMs = 8,
                TickDriftWarnMultiplier = 3,
            };

            var restored = RoundTrip<SimulationConfigMessage>(original);

            Assert.AreEqual(original.TickIntervalMs, restored.TickIntervalMs);
            Assert.AreEqual(original.InputDelayTicks, restored.InputDelayTicks);
            Assert.AreEqual(original.MaxRollbackTicks, restored.MaxRollbackTicks);
            Assert.AreEqual(original.SyncCheckInterval, restored.SyncCheckInterval);
            Assert.AreEqual(original.UsePrediction, restored.UsePrediction);
            Assert.AreEqual(original.MaxEntities, restored.MaxEntities);
            Assert.AreEqual(original.Mode, restored.Mode);
            Assert.AreEqual(original.HardToleranceMs, restored.HardToleranceMs);
            Assert.AreEqual(original.InputResendIntervalMs, restored.InputResendIntervalMs);
            Assert.AreEqual(original.MaxUnackedInputs, restored.MaxUnackedInputs);
            Assert.AreEqual(original.ServerSnapshotRetentionTicks, restored.ServerSnapshotRetentionTicks);
            Assert.AreEqual(original.EventDispatchWarnMs, restored.EventDispatchWarnMs);
            Assert.AreEqual(original.TickDriftWarnMultiplier, restored.TickDriftWarnMultiplier);
        }

        // ── GameStartMessage serialization round-trip ──

        [Test]
        public void GameStartMessage_RoundTrip_SessionConfigFieldsPreserved()
        {
            var original = new GameStartMessage
            {
                StartTime = 123456789L,
                RandomSeed = 42,
                MaxPlayers = 4,
                MinPlayers = 2,
                AllowLateJoin = true,
                ReconnectTimeoutMs = 15000,
                ReconnectMaxRetries = 5,
                LateJoinDelayTicks = 8,
                ResyncMaxRetries = 2,
                DesyncThresholdForResync = 4,
                CountdownDurationMs = 5000,
                CatchupMaxTicksPerFrame = 150,
                PlayerIds = new List<int> { 0, 1, 2 },
            };

            var restored = RoundTrip<GameStartMessage>(original);

            Assert.AreEqual(original.RandomSeed, restored.RandomSeed);
            Assert.AreEqual(original.MaxPlayers, restored.MaxPlayers);
            Assert.AreEqual(original.MinPlayers, restored.MinPlayers, "MinPlayers roundtrip");
            Assert.AreEqual(original.AllowLateJoin, restored.AllowLateJoin);
            Assert.AreEqual(original.ReconnectTimeoutMs, restored.ReconnectTimeoutMs);
            Assert.AreEqual(original.ReconnectMaxRetries, restored.ReconnectMaxRetries);
            Assert.AreEqual(original.LateJoinDelayTicks, restored.LateJoinDelayTicks);
            Assert.AreEqual(original.ResyncMaxRetries, restored.ResyncMaxRetries);
            Assert.AreEqual(original.DesyncThresholdForResync, restored.DesyncThresholdForResync);
            Assert.AreEqual(original.CountdownDurationMs, restored.CountdownDurationMs);
            Assert.AreEqual(original.CatchupMaxTicksPerFrame, restored.CatchupMaxTicksPerFrame);
            Assert.AreEqual(original.StartTime, restored.StartTime);
            Assert.AreEqual(original.PlayerIds.Count, restored.PlayerIds.Count);
        }

        [Test]
        public void GameStartMessage_DoesNotContain_TickInterval_Or_InputDelay()
        {
            // GameStartMessage must not contain SimulationConfig fields
            var type = typeof(GameStartMessage);
            Assert.IsNull(type.GetField("TickIntervalMs"),
                "GameStartMessage must not contain TickIntervalMs (moved to SimulationConfigMessage)");
            Assert.IsNull(type.GetField("InputDelayTicks"),
                "GameStartMessage must not contain InputDelayTicks (moved to SimulationConfigMessage)");
        }

        // ── CopyFrom result matches ──

        [Test]
        public void SimulationConfigMessage_CopyFrom_MatchesSourceConfig()
        {
            var config = new SimulationConfig
            {
                TickIntervalMs = 16,
                InputDelayTicks = 2,
                MaxRollbackTicks = 40,
                SyncCheckInterval = 20,
                UsePrediction = true,
                MaxEntities = 128,
                Mode = NetworkMode.ServerDriven,
                HardToleranceMs = 30,
                InputResendIntervalMs = 60,
                MaxUnackedInputs = 10,
                ServerSnapshotRetentionTicks = 3,
                EventDispatchWarnMs = 6,
                TickDriftWarnMultiplier = 4,
            };

            var msg = new SimulationConfigMessage();
            msg.CopyFrom(config);

            Assert.AreEqual(config.TickIntervalMs, msg.TickIntervalMs);
            Assert.AreEqual(config.InputDelayTicks, msg.InputDelayTicks);
            Assert.AreEqual(config.MaxRollbackTicks, msg.MaxRollbackTicks);
            Assert.AreEqual(config.SyncCheckInterval, msg.SyncCheckInterval);
            Assert.AreEqual(config.UsePrediction, msg.UsePrediction);
            Assert.AreEqual(config.MaxEntities, msg.MaxEntities);
            Assert.AreEqual((int)config.Mode, msg.Mode);
            Assert.AreEqual(config.HardToleranceMs, msg.HardToleranceMs);
            Assert.AreEqual(config.InputResendIntervalMs, msg.InputResendIntervalMs);
            Assert.AreEqual(config.MaxUnackedInputs, msg.MaxUnackedInputs);
            Assert.AreEqual(config.ServerSnapshotRetentionTicks, msg.ServerSnapshotRetentionTicks);
            Assert.AreEqual(config.EventDispatchWarnMs, msg.EventDispatchWarnMs);
            Assert.AreEqual(config.TickDriftWarnMultiplier, msg.TickDriftWarnMultiplier);
        }

        // ── Helpers ──

        private static T RoundTrip<T>(T message) where T : NetworkMessageBase, new()
        {
            int size = message.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            message.Serialize(ref writer);

            var restored = new T();
            var reader = new SpanReader(new System.ReadOnlySpan<byte>(buf, 0, writer.Position));
            restored.Deserialize(ref reader);
            return restored;
        }
    }
}
