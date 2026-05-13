#pragma warning disable CS0067
using System;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Verifies at the unit level that KlothoConnection correctly handles the three
    /// Late Join messages (SimulationConfig + LateJoinAccept + FullStateResponse).
    /// The Normal Join path is covered by HandshakeTests, so this file is dedicated to Late Join.
    /// </summary>
    [TestFixture]
    public class KlothoConnectionLateJoinTests
    {
        private ILogger _logger;
        private StubTransport _transport;
        private MessageSerializer _serializer;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("KlothoConnectionLateJoinTests");
        }

        [SetUp]
        public void SetUp()
        {
            _transport = new StubTransport();
            _serializer = new MessageSerializer();
        }

        // ── #1 Normal scenario — completes when all 3 messages received ──────────────────

        [Test]
        public void LateJoin_CompletesWithAllMessages()
        {
            ConnectionResult result = null;
            string failReason = null;
            var conn = KlothoConnection.Connect(
                _transport, "localhost", 0,
                onCompleted: r => result = r,
                onFailed: reason => failReason = reason,
                logger: _logger);

            // Fire connect event (when the transport's Connect fires OnConnected, KlothoConnection sends PlayerJoinMessage)
            _transport.FireConnected();

            // Simulate the server Late Join flow: inject SimulationConfig → LateJoinAccept → FullStateResponse in sequence
            InjectMessage(BuildSimulationConfigMessage());
            Assert.IsFalse(conn.IsCompleted, "Should not complete with SimConfig alone");

            var accept = BuildLateJoinAccept();
            InjectMessage(accept);
            Assert.IsFalse(conn.IsCompleted, "Should not complete with LateJoinAccept alone without FullState");

            var fullState = BuildFullStateResponse();
            InjectMessage(fullState);

            Assert.IsTrue(conn.IsCompleted, "Completes when all 3 messages received");
            Assert.IsNull(failReason, "Fail callback not fired");
            Assert.IsNotNull(result, "Result delivered via complete callback");
            Assert.AreEqual(JoinKind.LateJoin, result.Kind, "Kind == LateJoin");
            Assert.IsNotNull(result.LateJoinPayload, "LateJoinPayload non-null");
            Assert.AreEqual(accept.PlayerId, result.LocalPlayerId, "LocalPlayerId = LateJoinAccept.PlayerId");
            Assert.AreEqual(accept.Magic, result.SessionMagic, "SessionMagic = LateJoinAccept.Magic");
            Assert.AreEqual(accept.SharedEpoch, result.SharedEpoch);
            Assert.AreEqual(accept.ClockOffset, result.ClockOffset);
            Assert.IsNotNull(result.SimulationConfig, "SimulationConfig applied");
            // Verifies AcceptMessage reference retention — since MessageSerializer._messageCache reuses
            // a per-type singleton, this is a different instance than the test source `accept` but the field values must match.
            Assert.IsNotNull(result.LateJoinPayload.AcceptMessage);
            Assert.AreEqual(accept.PlayerId, result.LateJoinPayload.AcceptMessage.PlayerId);
            Assert.AreEqual(accept.RandomSeed, result.LateJoinPayload.AcceptMessage.RandomSeed);
            Assert.AreEqual(accept.PlayerCount, result.LateJoinPayload.AcceptMessage.PlayerCount);
            Assert.AreEqual(fullState.Tick, result.LateJoinPayload.FullStateTick);
            Assert.AreEqual(fullState.StateHash, result.LateJoinPayload.FullStateHash);
            Assert.IsNotNull(result.LateJoinPayload.FullStateData);
        }

        // ── #2 Timeout — onFailed after 15s ───────────────────────

        [Test]
        public void LateJoin_Timeout_FiresOnFailed()
        {
            string failReason = null;
            ConnectionResult result = null;
            var conn = KlothoConnection.Connect(
                _transport, "localhost", 0,
                onCompleted: r => result = r,
                onFailed: reason => failReason = reason,
                logger: _logger);

            _transport.FireConnected();

            // Move _connectStartMs into the past via reflection → trigger timeout immediately
            var field = typeof(KlothoConnection).GetField("_connectStartMs",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "_connectStartMs reflection");
            long pastMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 20000; // 20s ago
            field.SetValue(conn, pastMs);

            conn.Update();

            Assert.IsTrue(conn.IsCompleted, "IsCompleted=true after timeout");
            Assert.IsNull(result, "Complete callback not fired");
            Assert.IsNotNull(failReason, "Fail callback fired");
            StringAssert.Contains("timeout", failReason.ToLowerInvariant());
        }

        // ── #3 Disconnect midway — onFailed ────────────────────────────

        [Test]
        public void LateJoin_DisconnectMidway_FiresOnFailed()
        {
            string failReason = null;
            ConnectionResult result = null;
            KlothoConnection.Connect(
                _transport, "localhost", 0,
                onCompleted: r => result = r,
                onFailed: reason => failReason = reason,
                logger: _logger);

            _transport.FireConnected();

            // Disconnect after only receiving LateJoinAccept
            InjectMessage(BuildLateJoinAccept());
            _transport.FireDisconnected();

            Assert.IsNull(result, "Completion callback not fired");
            Assert.IsNotNull(failReason, "Fail callback fired");
            StringAssert.Contains("Connection lost", failReason);
        }

        // ── Helpers ─────────────────────────────────────────────

        private void InjectMessage(NetworkMessageBase msg)
        {
            byte[] buf;
            using (var serialized = _serializer.SerializePooled(msg))
            {
                buf = new byte[serialized.Length];
                Array.Copy(serialized.Data, 0, buf, 0, serialized.Length);
            }
            _transport.FireDataReceived(0, buf, buf.Length);
        }

        private SimulationConfigMessage BuildSimulationConfigMessage()
        {
            var cfg = new SimulationConfig
            {
                TickIntervalMs = 25,
                InputDelayTicks = 0,
                MaxRollbackTicks = 20,
                MaxEntities = 64,
            };
            var msg = new SimulationConfigMessage();
            msg.CopyFrom(cfg);
            return msg;
        }

        private LateJoinAcceptMessage BuildLateJoinAccept()
        {
            return new LateJoinAcceptMessage
            {
                PlayerId = 3,
                CurrentTick = 120,
                Magic = 0xABCDEF,
                SharedEpoch = 1_000_000L,
                ClockOffset = -250L,
                PlayerCount = 3,
                PlayerIds = new System.Collections.Generic.List<int> { 1, 2, 3 },
                PlayerConnectionStates = new System.Collections.Generic.List<byte> { 0, 0, 0 },
                RandomSeed = 77,
                MaxPlayers = 4,
                AllowLateJoin = true,
                ReconnectTimeoutMs = 30000,
                ReconnectMaxRetries = 3,
                LateJoinDelayTicks = 10,
                ResyncMaxRetries = 3,
                DesyncThresholdForResync = 3,
                CountdownDurationMs = 3000,
                CatchupMaxTicksPerFrame = 200,
                PlayerConfigData = Array.Empty<byte>(),
                PlayerConfigLengths = new System.Collections.Generic.List<int>(),
            };
        }

        private FullStateResponseMessage BuildFullStateResponse()
        {
            return new FullStateResponseMessage
            {
                Tick = 120,
                StateHash = 0x1234567890ABCDEFL,
                StateData = new byte[] { 1, 2, 3, 4 },
            };
        }

        /// <summary>
        /// Minimal stub for manually firing only the transport events of KlothoConnection.
        /// Send is a no-op — this test only verifies the receive flow.
        /// </summary>
        private class StubTransport : INetworkTransport
        {
            public bool IsConnected { get; private set; }
            public int LocalPeerId => 0;
            public bool IsHost => false;
            public string RemoteAddress { get; private set; }
            public int RemotePort { get; private set; }

            public event Action OnConnected;
            public event Action<DisconnectReason> OnDisconnected;
            public event Action<int, byte[], int> OnDataReceived;
            public event Action<int> OnPeerConnected;
            public event Action<int> OnPeerDisconnected;

            public bool Connect(string address, int port)
            {
                RemoteAddress = address;
                RemotePort = port;
                IsConnected = true;
                // OnConnected is manually triggered by the test via FireConnected (after Connect returns and event subscription completes)
                return true;
            }
            public bool Listen(string address, int port, int maxConnections) { return true; }
            public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod) { }
            public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod) { }
            public void Broadcast(byte[] data, DeliveryMethod deliveryMethod) { }
            public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod) { }
            public void PollEvents() { }
            public void FlushSendQueue() { }
            public void Disconnect() { IsConnected = false; }
            public void DisconnectPeer(int peerId) { }
            public System.Collections.Generic.IEnumerable<int> GetConnectedPeerIds() => System.Linq.Enumerable.Empty<int>();

            public void FireConnected() => OnConnected?.Invoke();
            // Default reason simulates a non-local disconnect during handshake (typical test scenario).
            // For tests that need to verify the LocalDisconnect bypass path, pass DisconnectReason.LocalDisconnect explicitly.
            public void FireDisconnected(DisconnectReason reason = DisconnectReason.NetworkFailure) => OnDisconnected?.Invoke(reason);
            public void FireDataReceived(int peerId, byte[] data, int length)
                => OnDataReceived?.Invoke(peerId, data, length);
        }
    }
}
