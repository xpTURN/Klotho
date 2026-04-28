using System;
using System.Collections.Generic;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;
using Brawler;

namespace xpTURN.Klotho.Tests
{
    [TestFixture]
    public class PlayerConfigTests
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
            _logger = loggerFactory.CreateLogger("PlayerConfigTests");
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

        // ── SendPlayerConfig -> broadcast -> GetPlayerConfig ──

        [Test]
        public void SendPlayerConfig_HostToGuest_GuestCanGetConfig()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();

            var config = new BrawlerPlayerConfig { SelectedCharacterClass = 3 };
            _harness.Host.NetworkService.SendPlayerConfig(_harness.Host.LocalPlayerId, config);
            _harness.PumpMessages();

            // Host must also store its own
            var hostResult = _harness.Host.Engine.GetPlayerConfig<BrawlerPlayerConfig>(_harness.Host.LocalPlayerId);
            Assert.IsNotNull(hostResult, "Host must store its own PlayerConfig");
            Assert.AreEqual(3, hostResult.SelectedCharacterClass);

            // Guest must also receive it via broadcast
            var guest = _harness.Guests[0];
            var guestResult = guest.Engine.GetPlayerConfig<BrawlerPlayerConfig>(_harness.Host.LocalPlayerId);
            Assert.IsNotNull(guestResult, "Guest must receive host's PlayerConfig via broadcast");
            Assert.AreEqual(3, guestResult.SelectedCharacterClass);
        }

        [Test]
        public void SendPlayerConfig_GuestToHost_HostBroadcastsToOtherGuests()
        {
            _harness.CreateHost(3);
            _harness.AddGuest();
            _harness.AddGuest();
            _harness.StartPlaying();

            var guest0 = _harness.Guests[0];
            var guest1 = _harness.Guests[1];

            var config = new BrawlerPlayerConfig { SelectedCharacterClass = 7 };
            guest0.NetworkService.SendPlayerConfig(guest0.LocalPlayerId, config);
            _harness.PumpMessages();

            // Stored on the host
            var hostResult = _harness.Host.Engine.GetPlayerConfig<BrawlerPlayerConfig>(guest0.LocalPlayerId);
            Assert.IsNotNull(hostResult, "Host must store guest PlayerConfig");
            Assert.AreEqual(7, hostResult.SelectedCharacterClass);

            // Also relayed to the other guest
            var guest1Result = guest1.Engine.GetPlayerConfig<BrawlerPlayerConfig>(guest0.LocalPlayerId);
            Assert.IsNotNull(guest1Result, "Other guest must receive relayed PlayerConfig");
            Assert.AreEqual(7, guest1Result.SelectedCharacterClass);
        }

        // ── OnPlayerConfigReceived firstTime flag ──

        [Test]
        public void OnPlayerConfigReceived_FirstTime_TrueOnFirstSend_FalseOnResend()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();

            bool? firstTimeFlag = null;
            _harness.Host.Engine.OnPlayerConfigReceived += (playerId, firstTime) =>
            {
                if (playerId == _harness.Host.LocalPlayerId)
                    firstTimeFlag = firstTime;
            };

            // First send
            var config = new BrawlerPlayerConfig { SelectedCharacterClass = 1 };
            _harness.Host.NetworkService.SendPlayerConfig(_harness.Host.LocalPlayerId, config);
            _harness.PumpMessages();

            Assert.IsTrue(firstTimeFlag.HasValue, "OnPlayerConfigReceived should have been called");
            Assert.IsTrue(firstTimeFlag.Value, "firstTime should be true on first send");

            // Resend
            firstTimeFlag = null;
            var config2 = new BrawlerPlayerConfig { SelectedCharacterClass = 2 };
            _harness.Host.NetworkService.SendPlayerConfig(_harness.Host.LocalPlayerId, config2);
            _harness.PumpMessages();

            Assert.IsTrue(firstTimeFlag.HasValue, "OnPlayerConfigReceived should have been called on resend");
            Assert.IsFalse(firstTimeFlag.Value, "firstTime should be false on resend");
        }

        // ── BrawlerPlayerConfig cross-assembly auto registration + MessageSerializer round-trip ──

        [Test]
        public void BrawlerPlayerConfig_RoundTrip_TypePreserved()
        {
            // BrawlerPlayerConfig is auto-registered in the MessageSerializer constructor via
            // cross-assembly [ModuleInitializer] + MessageRegistry, allowing round-trip without manual Register calls.
            var serializer = new MessageSerializer();
            var original = new BrawlerPlayerConfig { SelectedCharacterClass = 5 };

            var bytes = serializer.Serialize(original);

            // Verify the first byte is MessageTypeId (=200=UserDefined_Start)
            Assert.AreEqual((byte)NetworkMessageType.UserDefined_Start, bytes[0],
                "Serialized first byte must be BrawlerPlayerConfig MessageTypeId");

            var restored = serializer.Deserialize(bytes) as BrawlerPlayerConfig;

            Assert.IsNotNull(restored, "Auto-registration (ModuleInitializer) is not working — BrawlerPlayerConfig not recognized");
            Assert.AreEqual(5, restored.SelectedCharacterClass,
                "SelectedCharacterClass must be preserved after round-trip");
        }

        // ── D-2: cross-assembly auto-registration mechanism regression detection ──

        [Test]
        public void BrawlerPlayerConfig_AutoRegistered_AcrossMultipleSerializerInstances()
        {
            // Once [ModuleInitializer] registers BrawlerPlayerConfig in MessageRegistry,
            // every MessageSerializer instance recognizes the type via the MessageRegistry.ApplyTo call in its constructor.
            var original = new BrawlerPlayerConfig { SelectedCharacterClass = 11 };

            var serializerA = new MessageSerializer();
            var bytes = serializerA.Serialize(original);

            // Deserialize the same bytes with a completely new MessageSerializer instance — no manual Register
            var serializerB = new MessageSerializer();
            var restored = serializerB.Deserialize(bytes) as BrawlerPlayerConfig;

            Assert.IsNotNull(restored,
                "New MessageSerializer instances must also recognize cross-assembly types — MessageRegistry.ApplyTo path regression");
            Assert.AreEqual(11, restored.SelectedCharacterClass);

            // Same behavior for a third instance — verifies the static registry persists for the process lifetime
            var serializerC = new MessageSerializer();
            var restored2 = serializerC.Deserialize(bytes) as BrawlerPlayerConfig;
            Assert.IsNotNull(restored2, "MessageRegistry._registrations must be shared across instances");
            Assert.AreEqual(11, restored2.SelectedCharacterClass);
        }

        // ── LateJoinAcceptMessage PlayerConfigData/PlayerConfigLengths serialization roundtrip ──

        [Test]
        public void LateJoinAcceptMessage_PlayerConfigData_RoundTrip()
        {
            var config = new BrawlerPlayerConfig { SelectedCharacterClass = 9 };
            int size = config.GetSerializedSize();
            byte[] configData = new byte[size];
            var w = new SpanWriter(configData);
            config.Serialize(ref w);
            int configLen = w.Position;

            var original = new LateJoinAcceptMessage
            {
                PlayerId = 2,
                CurrentTick = 50,
                Magic = 0x12345678,
                SharedEpoch = 1000000L,
                ClockOffset = -500L,
                PlayerCount = 2,
                PlayerIds = new List<int> { 0, 1 },
                PlayerConnectionStates = new List<byte> { 0, 0 },
                RandomSeed = 77,
                MaxPlayers = 4,
                MinPlayers = 2,
                AllowLateJoin = true,
                ReconnectTimeoutMs = 30000,
                ReconnectMaxRetries = 3,
                LateJoinDelayTicks = 10,
                ResyncMaxRetries = 3,
                DesyncThresholdForResync = 3,
                CountdownDurationMs = 3000,
                CatchupMaxTicksPerFrame = 200,
                PlayerConfigData = configData,
                PlayerConfigLengths = new List<int> { configLen },
            };

            var buf = new byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);

            var restored = new LateJoinAcceptMessage();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            restored.Deserialize(ref reader);

            Assert.AreEqual(0x12345678, restored.Magic,
                "Magic must be preserved (session magic for Reconnect)");
            Assert.AreEqual(77, restored.RandomSeed, "RandomSeed roundtrip");
            Assert.AreEqual(4, restored.MaxPlayers, "MaxPlayers roundtrip");
            Assert.AreEqual(2, restored.MinPlayers, "MinPlayers roundtrip");
            Assert.IsTrue(restored.AllowLateJoin, "AllowLateJoin roundtrip");
            Assert.AreEqual(30000, restored.ReconnectTimeoutMs, "ReconnectTimeoutMs roundtrip");
            Assert.AreEqual(3, restored.ReconnectMaxRetries, "ReconnectMaxRetries roundtrip");
            Assert.AreEqual(10, restored.LateJoinDelayTicks, "LateJoinDelayTicks roundtrip");
            Assert.AreEqual(3, restored.ResyncMaxRetries, "ResyncMaxRetries roundtrip");
            Assert.AreEqual(3, restored.DesyncThresholdForResync, "DesyncThresholdForResync roundtrip");
            Assert.AreEqual(3000, restored.CountdownDurationMs, "CountdownDurationMs roundtrip");
            Assert.AreEqual(200, restored.CatchupMaxTicksPerFrame, "CatchupMaxTicksPerFrame roundtrip");
            Assert.AreEqual(1, restored.PlayerConfigLengths.Count,
                "PlayerConfigLengths count must be preserved");
            Assert.AreEqual(configLen, restored.PlayerConfigLengths[0],
                "PlayerConfigLengths[0] must match original length");
            Assert.IsNotNull(restored.PlayerConfigData,
                "PlayerConfigData must be preserved");
            Assert.AreEqual(configData.Length, restored.PlayerConfigData.Length,
                "PlayerConfigData length must match");

            // Deserialization: LateJoinAcceptMessage is a structure that concatenates multiple PlayerConfig bytes,
            // so each item's offset/length is separated by PlayerConfigLengths. MessageSerializer would also work but
            // this test directly uses SpanReader since the goal is byte-layout roundtrip validation.
            var restoredConfig = new BrawlerPlayerConfig();
            var configReader = new SpanReader(new ReadOnlySpan<byte>(restored.PlayerConfigData, 0, restored.PlayerConfigLengths[0]));
            restoredConfig.Deserialize(ref configReader);
            Assert.AreEqual(9, restoredConfig.SelectedCharacterClass,
                "PlayerConfigData must deserialize back to correct SelectedCharacterClass");
        }

        // ── MessageSerializer.Deserialize(byte[], int, int) offset overload ──

        [Test]
        public void MessageSerializer_DeserializeWithOffset_RoundTrip()
        {
            // From a buffer concatenating two BrawlerPlayerConfigs, deserialize each by offset
            var serializer = new MessageSerializer();
            var configA = new BrawlerPlayerConfig { SelectedCharacterClass = 3 };
            var configB = new BrawlerPlayerConfig { SelectedCharacterClass = 7 };

            int sizeA = configA.GetSerializedSize();
            int sizeB = configB.GetSerializedSize();
            byte[] concat = new byte[sizeA + sizeB];

            var wA = new SpanWriter(concat.AsSpan(0, sizeA));
            configA.Serialize(ref wA);
            int lenA = wA.Position;

            var wB = new SpanWriter(concat.AsSpan(sizeA, sizeB));
            configB.Serialize(ref wB);
            int lenB = wB.Position;

            // Restore configA from offset=0
            var restoredA = serializer.Deserialize(concat, lenA, 0) as BrawlerPlayerConfig;
            Assert.IsNotNull(restoredA, "configA restored");
            Assert.AreEqual(3, restoredA.SelectedCharacterClass);

            // Restore configB from offset=sizeA
            var restoredB = serializer.Deserialize(concat, lenB, sizeA) as BrawlerPlayerConfig;
            Assert.IsNotNull(restoredB, "configB restored");
            Assert.AreEqual(7, restoredB.SelectedCharacterClass);
        }
    }
}
