#pragma warning disable CS0219
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Tests.Integration
{
    /// <summary>
    /// Verifies on top of the actual ServerNetworkService and TestTransport that the
    /// KlothoConnection Late Join path receives the three messages
    /// SimulationConfig + LateJoinAccept + FullStateResponse, and that the engine can
    /// be initialized into the Running + Catchup state via the Seed* APIs.
    ///
    /// To avoid the EcsSimulation dependency, the same call sequence is reproduced manually instead of using KlothoSession.Create.
    /// </summary>
    [TestFixture]
    public class SDLateJoinConnectionTests
    {
        private ILogger _logger;
        private CommandFactory _commandFactory;

        private SDTestPeer _server;
        private List<SDTestPeer> _clients;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("SDLateJoinConnectionTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _commandFactory = new CommandFactory();
            _server = null;
            _clients = new List<SDTestPeer>();
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        // ── #1 Receive 3 messages + Kind == LateJoin ───────────────────────

        /// <summary>
        /// Verifies that when the server is in the Playing state and a new guest connects via KlothoConnection,
        /// it receives the 3 messages SimulationConfig + LateJoinAccept + FullStateResponse
        /// and completes with ConnectionResult.Kind == JoinKind.LateJoin.
        /// </summary>
        [Test]
        public void KlothoConnection_LateJoin_ReceivesAllMessagesAndCompletes()
        {
            SetupServerAtPlaying(connectedClients: 2);

            // New guest transport — connect via KlothoConnection
            var lateTransport = new TestTransport();
            ConnectionResult result = null;
            string failReason = null;

            var conn = KlothoConnection.Connect(
                lateTransport, "localhost", 7777,
                onCompleted: r => result = r,
                onFailed: reason => failReason = reason,
                logger: _logger);

            // Pump — until the server sends SimConfig/LateJoinAccept/FullState after sync
            PumpAll(lateTransport, conn, rounds: 30);

            Assert.IsNull(failReason, $"Should not fail: {failReason}");
            Assert.IsNotNull(result, "Result received via completion callback");
            Assert.AreEqual(JoinKind.LateJoin, result.Kind, "Kind == LateJoin");
            Assert.IsNotNull(result.LateJoinPayload, "LateJoinPayload non-null");
            Assert.IsNotNull(result.LateJoinPayload.AcceptMessage, "AcceptMessage retained");
            Assert.IsNotNull(result.LateJoinPayload.FullStateData, "FullStateData retained");
            Assert.AreNotEqual(0, result.SessionMagic, "SessionMagic restored (Reconnect magic)");
            Assert.IsNotNull(result.SimulationConfig, "SimulationConfig arrived");
            Assert.AreEqual(2 + 1, result.LateJoinPayload.AcceptMessage.PlayerCount,
                "Existing guests 2 + new guest 1 = 3");
        }

        // ── #2 SessionConfig fields properly delivered in LateJoinAcceptMessage ─

        [Test]
        public void KlothoConnection_LateJoin_SessionConfigFieldsPopulated()
        {
            SetupServerAtPlaying(connectedClients: 1);

            var lateTransport = new TestTransport();
            ConnectionResult result = null;
            var conn = KlothoConnection.Connect(
                lateTransport, "localhost", 7777,
                onCompleted: r => result = r,
                onFailed: reason => Assert.Fail(reason),
                logger: _logger);

            PumpAll(lateTransport, conn, rounds: 30);

            Assert.IsNotNull(result);
            var accept = result.LateJoinPayload.AcceptMessage;
            // Verify it matches server SessionConfig defaults / SetupServerAtPlaying settings
            Assert.Greater(accept.MaxPlayers, 0, "MaxPlayers set (≠0)");
            Assert.Greater(accept.MinPlayers, 0, "MinPlayers set (≠0)");
            Assert.Greater(accept.ReconnectTimeoutMs, 0, "ReconnectTimeoutMs set (≠0)");
            // RandomSeed should be exactly the server's _randomSeed value
            Assert.AreEqual(_server.Engine.RandomSeed, accept.RandomSeed, "RandomSeed matches");
        }

        // ── #3 Engine reaches Running + Catchup via Seed* APIs ────────────

        /// <summary>
        /// After KlothoConnection completes, configure ServerDrivenClientService + KlothoEngine and
        /// invoke SeedLateJoinPlayers + SeedLateJoinFullState in order — verify the engine reaches
        /// State=Running + _isCatchingUp=true + _activePlayerIds contains all players.
        /// </summary>
        [Test]
        public void LateJoinSeed_EngineReachesRunningAndCatchingUp()
        {
            SetupServerAtPlaying(connectedClients: 2);

            // Advance the server a few ticks (so FullState's tick is > 0)
            for (int i = 0; i < 5; i++) TickAllConnected();

            var lateTransport = new TestTransport();
            ConnectionResult result = null;
            var conn = KlothoConnection.Connect(
                lateTransport, "localhost", 7777,
                onCompleted: r => result = r,
                onFailed: reason => Assert.Fail(reason),
                logger: _logger);

            PumpAll(lateTransport, conn, rounds: 30);
            Assert.IsNotNull(result, "Late Join completed");

            // Reproduce KlothoSession.Create order (manually reproduced to avoid EcsSimulation dependency)
            var clientSim = new TestSimulation();
            var clientService = new ServerDrivenClientService();
            clientService.InitializeFromConnection(result, _commandFactory, _logger);

            // 5.5 SeedLateJoinPlayers — before engine.Initialize
            clientService.SeedLateJoinPlayers(result.LateJoinPayload);

            var clientEngine = new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = 50,
                    MaxRollbackTicks = 50,
                    UsePrediction = true,
                    InputDelayTicks = 0,
                    HardToleranceMs = 0,
                },
                new SessionConfig());
            clientEngine.Initialize(clientSim, clientService, _logger);
            clientEngine.SetCommandFactory(_commandFactory);
            clientService.SubscribeEngine(clientEngine);

            // 7.5 SeedLateJoinFullState — afterwards
            clientEngine.SeedLateJoinFullState(result.LateJoinPayload);

            // Verify
            Assert.AreEqual(KlothoState.Running, clientEngine.State,
                "State=Running after SeedLateJoinFullState");
            Assert.AreEqual(result.LateJoinPayload.FullStateTick, clientEngine.CurrentTick,
                "CurrentTick = FullStateTick");

            // _isCatchingUp == true (set by StartCatchingUp)
            var isCatchingUpField = typeof(KlothoEngine).GetField("_isCatchingUp",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(isCatchingUpField);
            bool isCatchingUp = (bool)isCatchingUpField.GetValue(clientEngine);
            Assert.IsTrue(isCatchingUp, "SeedLateJoinFullState → StartCatchingUp → _isCatchingUp=true");

            // _activePlayerIds contains both existing players + the new player
            var activeIdsField = typeof(KlothoEngine).GetField("_activePlayerIds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(activeIdsField);
            var activeIds = activeIdsField.GetValue(clientEngine) as List<int>;
            Assert.IsNotNull(activeIds);
            Assert.AreEqual(result.LateJoinPayload.AcceptMessage.PlayerCount, activeIds.Count,
                "_activePlayerIds contains all players from LateJoinAccept");

            // RandomSeed sync
            Assert.AreEqual(result.LateJoinPayload.AcceptMessage.RandomSeed, clientEngine.RandomSeed,
                "SeedLateJoinFullState syncs _randomSeed from networkService");
        }

        // ── #4 Reconnect magic path compatibility ──────────────────────────

        /// <summary>
        /// Verifies that the SessionMagic of a guest that joined via Late Join matches the server's actual magic.
        /// This is a precondition for the ReconnectRequest.SessionMagic validation path to work correctly on subsequent reconnect attempts.
        /// </summary>
        [Test]
        public void KlothoConnection_LateJoin_MagicMatchesServer()
        {
            SetupServerAtPlaying(connectedClients: 1);

            // Extract the server's _sessionMagic
            var magicField = typeof(ServerNetworkService).GetField("_sessionMagic",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(magicField);
            int serverMagic = (int)magicField.GetValue(_server.NetworkService);

            var lateTransport = new TestTransport();
            ConnectionResult result = null;
            var conn = KlothoConnection.Connect(
                lateTransport, "localhost", 7777,
                onCompleted: r => result = r,
                onFailed: reason => Assert.Fail(reason),
                logger: _logger);

            PumpAll(lateTransport, conn, rounds: 30);

            Assert.IsNotNull(result);
            Assert.AreEqual(serverMagic, result.SessionMagic,
                "Late Join guest's SessionMagic == server _sessionMagic");
            Assert.AreEqual(serverMagic, result.LateJoinPayload.AcceptMessage.Magic,
                "LateJoinAccept.Magic == server _sessionMagic");
        }

        // ── Helpers ───────────────────────────────────────────

        private void SetupServerAtPlaying(int connectedClients)
        {
            _server = CreateServer();
            for (int i = 0; i < connectedClients; i++)
                AddClient();
            StartPlaying();
        }

        private SDTestPeer CreateServer(int maxPlayers = 4)
        {
            var peer = new SDTestPeer
            {
                Transport = new TestTransport(),
                Simulation = new TestSimulation(),
                IsServer = true
            };

            var serverService = new ServerNetworkService();
            serverService.Initialize(peer.Transport, _commandFactory, _logger);
            peer.NetworkService = serverService;

            peer.Engine = new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = 50,
                    MaxRollbackTicks = 50,
                    UsePrediction = false,
                    InputDelayTicks = 0,
                    HardToleranceMs = 200,
                },
                new SessionConfig
                {
                    CountdownDurationMs = 0,
                    MaxPlayers = maxPlayers,
                    MinPlayers = 1,
                    AllowLateJoin = true,
                    ReconnectTimeoutMs = 30000,
                    ReconnectMaxRetries = 3,
                    LateJoinDelayTicks = 10,
                    ResyncMaxRetries = 3,
                    DesyncThresholdForResync = 3,
                    CatchupMaxTicksPerFrame = 200,
                });
            peer.Engine.Initialize(peer.Simulation, serverService, _logger);
            peer.Engine.SetCommandFactory(_commandFactory);
            serverService.SubscribeEngine(peer.Engine);

            serverService.CreateRoom("test", maxPlayers);
            serverService.Listen("localhost", 7777, maxPlayers);
            return peer;
        }

        private SDTestPeer AddClient()
        {
            var peer = new SDTestPeer
            {
                Transport = new TestTransport(),
                Simulation = new TestSimulation(),
                IsServer = false
            };

            var clientService = new ServerDrivenClientService();
            clientService.Initialize(peer.Transport, _commandFactory, _logger);
            peer.NetworkService = clientService;

            peer.Engine = new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = 50,
                    MaxRollbackTicks = 50,
                    UsePrediction = true,
                    InputDelayTicks = 0,
                    HardToleranceMs = 0,
                },
                new SessionConfig { CountdownDurationMs = 0 });
            peer.Engine.Initialize(peer.Simulation, clientService, _logger);
            peer.Engine.SetCommandFactory(_commandFactory);
            clientService.SubscribeEngine(peer.Engine);

            clientService.JoinRoom("test");
            clientService.Connect("localhost", 7777);

            _clients.Add(peer);
            PumpMessages();
            return peer;
        }

        private void PumpMessages(int rounds = 12)
        {
            for (int i = 0; i < rounds; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                foreach (var client in _clients)
                {
                    if (client.Transport.IsConnected)
                        client.NetworkService.Update();
                }
            }
        }

        private void StartPlaying()
        {
            foreach (var client in _clients)
                client.NetworkService.SetReady(true);
            PumpMessages();
            PumpMessages();

            Assert.AreEqual(SessionPhase.Playing, _server.Phase, "Server should be Playing");
        }

        /// <summary>
        /// Advance server + existing clients by 1 frame.
        /// </summary>
        private void TickAllConnected(float deltaTime = 0.05f)
        {
            _server.Transport.PollEvents();
            _server.NetworkService.Update();
            if (_server.Phase == SessionPhase.Playing)
                _server.Engine.Update(deltaTime);
            foreach (var client in _clients)
            {
                if (!client.Transport.IsConnected) continue;
                client.NetworkService.Update();
                if (client.Phase == SessionPhase.Playing)
                    client.Engine.Update(deltaTime);
            }
        }

        /// <summary>
        /// Pump everything including the Late Join transport. Completes server sync → CompleteLateJoinSync →
        /// 3-message send in a few rounds.
        /// </summary>
        private void PumpAll(TestTransport lateTransport, KlothoConnection conn, int rounds)
        {
            for (int i = 0; i < rounds && !conn.IsCompleted; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                foreach (var client in _clients)
                {
                    if (client.Transport.IsConnected)
                        client.NetworkService.Update();
                }
                lateTransport.PollEvents();
                conn.Update();
            }
        }
    }
}
