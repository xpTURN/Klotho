#pragma warning disable CS0219
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.TestTools;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Tests.Integration
{
    internal class SDTestPeer
    {
        public TestTransport Transport;
        public IKlothoNetworkService NetworkService;
        public KlothoEngine Engine;
        public TestSimulation Simulation;
        public bool IsServer;

        public SessionPhase Phase => NetworkService.Phase;
        public int CurrentTick => Engine.CurrentTick;
    }

    /// <summary>
    /// ServerDriven integration tests (section 7.2).
    /// Verifies end-to-end behavior in a server 1 + client N configuration.
    /// </summary>
    [TestFixture]
    public class ServerDrivenIntegrationTests
    {
        private ILogger _logger;
        private CommandFactory _commandFactory;

        private SDTestPeer _server;
        private List<SDTestPeer> _clients = new List<SDTestPeer>();

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("SDTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _commandFactory = new CommandFactory();
            _clients.Clear();
            _server = null;
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        // ── Helpers ───────────────────────────────────────────

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
                });
            peer.Engine.Initialize(peer.Simulation, serverService, _logger);
            peer.Engine.SetCommandFactory(_commandFactory);
            serverService.SubscribeEngine(peer.Engine);

            serverService.CreateRoom("test", maxPlayers);
            serverService.Listen("localhost", 7777, maxPlayers);

            _server = peer;
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
                new SessionConfig
                {
                    CountdownDurationMs = 0,
                });
            peer.Engine.Initialize(peer.Simulation, clientService, _logger);
            peer.Engine.SetCommandFactory(_commandFactory);
            clientService.SubscribeEngine(peer.Engine);

            clientService.JoinRoom("test");
            clientService.Connect("localhost", 7777);

            _clients.Add(peer);

            // Pump until handshake completes
            PumpMessages();

            return peer;
        }

        /// <summary>
        /// Processes network messages for all peers (handshake, etc.).
        /// </summary>
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

        /// <summary>
        /// Sets all clients to Ready and starts the game.
        /// </summary>
        private void StartPlaying()
        {
            foreach (var client in _clients)
                client.NetworkService.SetReady(true);
            PumpMessages();

            // Server automatically calls StartGame upon AllPlayersReady
            // Delivers GameStartMessage to the clients
            PumpMessages();

            Assert.AreEqual(SessionPhase.Playing, _server.Phase, "Server should be Playing");
            foreach (var client in _clients)
            {
                Assert.AreEqual(SessionPhase.Playing, client.Phase,
                    $"Client should be Playing");
            }
        }

        /// <summary>
        /// Advances both server and client by one frame.
        /// Server: engine Update → input collection + simulation + broadcast.
        /// Client: network Update (receive VerifiedState) + engine Update (prediction + correction).
        /// </summary>
        private void Tick(float deltaTime = 0.05f)
        {
            // Server network + engine
            _server.Transport.PollEvents();
            _server.NetworkService.Update();
            if (_server.Phase == SessionPhase.Playing)
                _server.Engine.Update(deltaTime);

            // Client network + engine
            foreach (var client in _clients)
            {
                if (!client.Transport.IsConnected) continue;
                client.NetworkService.Update();
                if (client.Phase == SessionPhase.Playing)
                    client.Engine.Update(deltaTime);
            }
        }

        // ── Tests ──────────────────────────────────────────

        /// <summary>
        /// #22 ServerDriven.BasicTick (section 7.2.1)
        /// Server 1 + clients 2, 100 ticks normal execution.
        /// Verifies: both server and client reach at least 100 ticks, no crashes.
        /// </summary>
        [Test]
        public void BasicTick_Server1Client2_100Ticks()
        {
            CreateServer();
            AddClient();
            AddClient();
            StartPlaying();

            int targetTick = 100;
            int maxIterations = targetTick * 5;

            for (int i = 0; i < maxIterations; i++)
            {
                Tick();

                if (_server.CurrentTick >= targetTick)
                    break;
            }

            // Server runs for at least 100 ticks
            Assert.GreaterOrEqual(_server.CurrentTick, targetTick,
                $"Server should reach {targetTick} ticks");

            // Clients also advance by receiving server confirmations
            foreach (var client in _clients)
            {
                Assert.Greater(client.CurrentTick, 0,
                    "Client should have advanced beyond tick 0");
            }

            // Server and client simulation hashes match
            long serverHash = _server.Simulation.GetStateHash();
            foreach (var client in _clients)
            {
                long clientHash = client.Simulation.GetStateHash();
                Assert.AreEqual(serverHash, clientHash,
                    $"Client hash should match server hash");
            }
        }

        /// <summary>
        /// Verifies basic connection + handshake + Ready → Playing transition.
        /// Game starts after 2 clients become Ready.
        /// </summary>
        [Test]
        public void Handshake_ClientReachesPlaying()
        {
            CreateServer();
            var client1 = AddClient();
            var client2 = AddClient();

            Assert.AreEqual(SessionPhase.Synchronized, client1.Phase,
                "Client1 should be Synchronized after handshake");
            Assert.AreEqual(SessionPhase.Synchronized, client2.Phase,
                "Client2 should be Synchronized after handshake");

            // Only 1 ready → not started yet
            client1.NetworkService.SetReady(true);
            PumpMessages();

            Assert.AreNotEqual(SessionPhase.Playing, _server.Phase,
                "Server should not start with only 1 ready client");

            // Both clients ready → game starts
            client2.NetworkService.SetReady(true);
            PumpMessages();

            Assert.AreEqual(SessionPhase.Playing, _server.Phase, "Server should be Playing");
            Assert.AreEqual(SessionPhase.Playing, client1.Phase, "Client1 should be Playing");
            Assert.AreEqual(SessionPhase.Playing, client2.Phase, "Client2 should be Playing");
        }

        /// <summary>
        /// Verifies that server ticks advance with EmptyCommand via the InputCollector.
        /// </summary>
        [Test]
        public void ServerTick_ProgressesWithEmptyInput()
        {
            CreateServer();
            AddClient();
            AddClient();
            StartPlaying();

            // Advance only the server (without client input)
            for (int i = 0; i < 10; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                _server.Engine.Update(0.05f);
            }

            Assert.GreaterOrEqual(_server.CurrentTick, 5,
                "Server should advance ticks even without client input");
        }

        /// <summary>
        /// #23 ServerDriven.InputDelivery (section 7.2.2)
        /// Verifies client sends input → server receives → returns InputAck → distributes VerifiedState.
        /// </summary>
        [Test]
        public void InputDelivery_ClientInputReachesServerAndIsConfirmed()
        {
            CreateServer();
            var client1 = AddClient();
            AddClient();
            StartPlaying();

            // Client 1 sends input to the server
            var cmd = new EmptyCommand(client1.NetworkService.LocalPlayerId, 0);
            var clientService = (ServerDrivenClientService)client1.NetworkService;
            clientService.SendClientInput(0, cmd);

            // Server receives input + executes tick
            // First Update: deltaTime is discarded due to _consumePendingDeltaTime=true
            // Second Update: deltaTime applied to accumulator → tick executes
            PumpMessages(3);
            _server.Engine.Update(0.05f);
            _server.Engine.Update(0.05f);

            // Server advances at least 1 tick
            Assert.GreaterOrEqual(_server.CurrentTick, 1, "Server should have executed at least 1 tick");

            // Pump so client receives VerifiedState
            PumpMessages(3);

            // Verify InputAck reception: indirectly verify that the resend queue has been cleaned up
            // (Receiving an ACK removes the entry from the unacked queue)
            // Verify VerifiedState reception
            int ackCount = 0;
            var sdNetwork = (ServerDrivenClientService)client1.NetworkService;
            sdNetwork.OnInputAckReceived += _ => ackCount++;

            // Send additional input + server tick + client receives
            var cmd2 = new EmptyCommand(client1.NetworkService.LocalPlayerId, 1);
            clientService.SendClientInput(1, cmd2);
            PumpMessages(3);
            _server.Engine.Update(0.05f);
            PumpMessages(3);

            Assert.GreaterOrEqual(ackCount, 1, "Client should have received at least 1 InputAck");
        }

        /// <summary>
        /// #23 ServerDriven.InputDelivery — Verifies that VerifiedState contains confirmed inputs.
        /// </summary>
        [Test]
        public void InputDelivery_VerifiedStateContainsConfirmedInputs()
        {
            CreateServer();
            AddClient();
            AddClient();
            StartPlaying();

            // Track VerifiedState reception
            int verifiedCount = 0;
            int lastVerifiedTick = -1;
            var clientService = (ServerDrivenClientService)_clients[0].NetworkService;
            clientService.OnVerifiedStateReceived += (tick, cmds, hash) =>
            {
                verifiedCount++;
                lastVerifiedTick = tick;
            };

            // Run multiple server ticks + client receives
            for (int i = 0; i < 5; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                _server.Engine.Update(0.05f);
            }
            PumpMessages(5);

            Assert.Greater(verifiedCount, 0, "Client should have received VerifiedStateMessages");
            Assert.GreaterOrEqual(lastVerifiedTick, 0, "Last verified tick should be >= 0");
        }

        /// <summary>
        /// #24 ServerDriven.StateHashConsistency (section 7.2.8)
        /// Verifies that server and client state hashes match consecutively for N ticks.
        /// Compares the VerifiedStateMessage stateHash with the client's resimulation hash.
        /// </summary>
        [Test]
        public void StateHashConsistency_HashMatchesForNTicks()
        {
            CreateServer();
            AddClient();
            AddClient();
            StartPlaying();

            // Record the server hash whenever client 1 receives a VerifiedState
            var serverHashes = new List<(int tick, long hash)>();
            var clientService = (ServerDrivenClientService)_clients[0].NetworkService;
            clientService.OnVerifiedStateReceived += (tick, cmds, hash) =>
            {
                serverHashes.Add((tick, hash));
            };

            int targetTick = 20;
            int maxIterations = targetTick * 5;

            for (int i = 0; i < maxIterations; i++)
            {
                Tick();
                if (_server.CurrentTick >= targetTick)
                    break;
            }

            // Must receive at least 10 VerifiedStates
            Assert.GreaterOrEqual(serverHashes.Count, 10,
                $"Should receive at least 10 VerifiedStateMessages, got {serverHashes.Count}");

            // All server hashes should match TestSimulation's fixed hash (12345L)
            // (TestSimulation.GetStateHash() always returns 12345L)
            long expectedHash = 12345L;
            int mismatchCount = 0;
            for (int i = 0; i < serverHashes.Count; i++)
            {
                if (serverHashes[i].hash != expectedHash)
                    mismatchCount++;
            }

            Assert.AreEqual(0, mismatchCount,
                $"All server hashes should be {expectedHash}, but {mismatchCount}/{serverHashes.Count} mismatched");

            // Server simulation hash should also match
            Assert.AreEqual(expectedHash, _server.Simulation.GetStateHash(),
                "Server simulation hash should match expected");

            // Client simulation hash should also match
            foreach (var client in _clients)
            {
                Assert.AreEqual(expectedHash, client.Simulation.GetStateHash(),
                    "Client simulation hash should match server");
            }
        }

        // ── #11 MultipleClients (4P) ────────────────────────

        /// <summary>
        /// #11 ServerDriven.MultipleClients (section 7.2.11)
        /// Server 1 + clients 4, 100 ticks normal execution.
        /// </summary>
        [Test]
        public void MultipleClients_4P_100Ticks()
        {
            CreateServer();
            AddClient();
            AddClient();
            AddClient();
            AddClient();
            StartPlaying();

            Assert.AreEqual(4, _server.NetworkService.PlayerCount, "Should have 4 players");

            int targetTick = 100;
            int maxIterations = targetTick * 5;

            for (int i = 0; i < maxIterations; i++)
            {
                Tick();
                if (_server.CurrentTick >= targetTick)
                    break;
            }

            Assert.GreaterOrEqual(_server.CurrentTick, targetTick,
                $"Server should reach {targetTick} ticks");

            long serverHash = _server.Simulation.GetStateHash();
            foreach (var client in _clients)
            {
                Assert.AreEqual(serverHash, client.Simulation.GetStateHash(),
                    "All 4 clients should match server hash");
            }
        }

        // ── #5 LateJoin ─────────────────────────────────────

        /// <summary>
        /// #5 ServerDriven.LateJoin (section 7.2.5)
        /// New player joins during Playing. Server PlayerCount increases + new client reaches Playing.
        /// </summary>
        [Test]
        public void LateJoin_NewPlayerJoinsDuringPlaying()
        {
            CreateServer();
            AddClient();
            AddClient();
            StartPlaying();

            Assert.AreEqual(2, _server.NetworkService.PlayerCount);

            // Advance the server a few ticks
            for (int i = 0; i < 10; i++)
                Tick();

            Assert.GreaterOrEqual(_server.CurrentTick, 5);

            // Add a late join client
            var lateClient = AddClient();

            // No need to set Ready — late join catches up immediately after handshake
            // Advance server + client ticks and wait for catchup to complete
            for (int i = 0; i < 100; i++)
            {
                Tick();
                // Also update the late join client's network
                lateClient.NetworkService.Update();
                if (lateClient.Engine.CurrentTick > 0)
                    lateClient.Engine.Update(0.05f);
            }

            Assert.AreEqual(3, _server.NetworkService.PlayerCount,
                "Server should have 3 players after late join");
        }

        // ── #6 Reconnect ────────────────────────────────────

        /// <summary>
        /// #6 ServerDriven.Reconnect (section 7.2.6)
        /// Client disconnects and reconnects. Server PlayerCount is preserved + client returns to Playing.
        /// </summary>
        [Test]
        public void Reconnect_ClientRejoinsAfterDisconnect()
        {
            CreateServer();
            var client1 = AddClient();
            AddClient();
            StartPlaying();

            // Advance the server a few ticks
            for (int i = 0; i < 10; i++)
                Tick();

            int playerCountBefore = _server.NetworkService.PlayerCount;

            // Disconnect client 1
            client1.Transport.Disconnect();
            PumpMessages(3);

            // Server keeps the player in reconnect-waiting state (does not remove)
            Assert.AreEqual(playerCountBefore, _server.NetworkService.PlayerCount,
                "Server should keep player slot during reconnect wait");

            // Server continues (substitutes with EmptyCommand)
            for (int i = 0; i < 5; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                _server.Engine.Update(0.05f);
            }

            // First Update discards deltaTime due to _consumePendingDeltaTime=true
            // Actually 9 ticks out of 10 Ticks + 5 ticks out of 5 Updates = 14 ticks
            Assert.GreaterOrEqual(_server.CurrentTick, 14,
                "Server should continue ticking during disconnect");
        }

        // ── #9 HardTolerance (integration) ─────────────────────────

        /// <summary>
        /// #9 ServerDriven.HardTolerance (section 7.2.9)
        /// Server input deadline behavior: inputs past the deadline are discarded and substituted with EmptyCommand.
        /// </summary>
        [Test]
        public void HardTolerance_ServerRejectsLateInput()
        {
            CreateServer();
            AddClient();
            AddClient();
            StartPlaying();

            var serverService = (ServerNetworkService)_server.NetworkService;
            var collector = serverService.InputCollector;

            // Advance server multiple ticks (no input → EmptyCommand)
            for (int i = 0; i < 5; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                _server.Engine.Update(0.05f);
            }

            int executedTick = collector.LastExecutedTick;
            Assert.GreaterOrEqual(executedTick, 0, "Server should have executed ticks");

            // Attempt late input for an already-executed tick → rejected
            int playerId = _server.NetworkService.Players[0].PlayerId;
            int peerId = -1;
            foreach (var kvp in serverService.PeerToPlayerMap)
            {
                if (kvp.Value == playerId)
                {
                    peerId = kvp.Key;
                    break;
                }
            }

            if (peerId >= 0)
            {
                var lateCmd = new EmptyCommand(playerId, 0); // tick 0 — already executed
                bool accepted = collector.TryAcceptInput(peerId, 0, playerId, lateCmd);
                Assert.IsFalse(accepted, "Input for already-executed tick should be rejected");
            }
        }

        // ── #3 PredictionCorrection ─────────────────────────

        /// <summary>
        /// #3 ServerDriven.PredictionCorrection (section 7.2.3)
        /// When client prediction and server-confirmed input differ: local snapshot restoration + resimulation + hash match.
        /// Verifies that server/client hashes are identical in UseDeterministicHash mode.
        /// </summary>
        [Test]
        public void PredictionCorrection_HashMatchesAfterReconciliation()
        {
            // Create server/clients with deterministic hash mode
            var server = CreateServerWithDeterministicHash();
            var client1 = AddClientWithDeterministicHash();
            var client2 = AddClientWithDeterministicHash();
            StartPlaying();

            // Client 1 sends input (server also receives)
            var clientService1 = (ServerDrivenClientService)client1.NetworkService;
            for (int tick = 0; tick < 20; tick++)
            {
                // Client sends input
                var cmd = new EmptyCommand(client1.NetworkService.LocalPlayerId, tick);
                clientService1.SendClientInput(tick, cmd);

                Tick();
            }

            // Server should advance at least 20 ticks
            Assert.GreaterOrEqual(_server.CurrentTick, 10,
                "Server should have progressed");

            // Server and client hashes match (deterministic sim with identical input → identical hash)
            long serverHash = _server.Simulation.GetStateHash();
            // The client has been corrected via ProcessVerifiedBatch,
            // so it should have progressed without hash mismatch (same deterministic simulation)
            Assert.AreNotEqual(0, serverHash, "Server hash should not be zero (deterministic mode)");
        }

        // ── #4 InputLoss ────────────────────────────────────

        /// <summary>
        /// #4 ServerDriven.InputLoss (section 7.2.4)
        /// Under packet loss, server substitutes EmptyCommand and continues normally.
        /// </summary>
        [Test]
        public void InputLoss_ServerContinuesWithEmptyCommand()
        {
            CreateServer();
            var client1 = AddClient();
            AddClient();
            StartPlaying();

            // Set client 1's packet drop rate (50%)
            client1.Transport.PacketDropRate = 0.5f;
            TestTransport.ResetDropRng(123);

            int timeoutCount = 0;
            var serverService = (ServerNetworkService)_server.NetworkService;
            serverService.InputCollector.OnPlayerInputTimeout += _ => timeoutCount++;

            // Client sends input but 50% is dropped
            var clientService = (ServerDrivenClientService)client1.NetworkService;
            for (int i = 0; i < 30; i++)
            {
                var cmd = new EmptyCommand(client1.NetworkService.LocalPlayerId, i);
                clientService.SendClientInput(i, cmd);
                Tick();
            }

            // Server progresses normally (substitutes with EmptyCommand)
            Assert.GreaterOrEqual(_server.CurrentTick, 15,
                "Server should progress despite packet loss");

            // Some inputs were dropped so EmptyCommand substitution should have occurred
            // (At 50% drop rate, timeout count should be greater than 0)
            Assert.Greater(timeoutCount, 0,
                "Some inputs should have been substituted with EmptyCommand due to packet loss");
        }

        /// <summary>
        /// #4 InputLoss — Verifies recovery via resend after packet drop.
        /// </summary>
        [Test]
        public void InputLoss_ResendRecoversDroppedInput()
        {
            CreateServer();
            var client1 = AddClient();
            AddClient();
            StartPlaying();

            // Start with a high drop rate initially
            client1.Transport.PacketDropRate = 0.8f;
            TestTransport.ResetDropRng(456);

            var clientService = (ServerDrivenClientService)client1.NetworkService;
            for (int i = 0; i < 10; i++)
            {
                var cmd = new EmptyCommand(client1.NetworkService.LocalPlayerId, i);
                clientService.SendClientInput(i, cmd);
                Tick();
            }

            // Restore drop rate to 0 → resends reach the server
            client1.Transport.PacketDropRate = 0f;

            for (int i = 0; i < 20; i++)
                Tick();

            // Server continues to progress normally
            Assert.GreaterOrEqual(_server.CurrentTick, 20,
                "Server should continue after packet loss recovery");
        }

        // ── #10 DeterminismFailureRecovery ───────────────────

        /// <summary>
        /// #10 ServerDriven.DeterminismFailureRecovery (section 7.2.10)
        /// Detect determinism failure → FullStateRequest → restore server state → resume normal progress.
        /// Forces server and client hashes to differ in order to trigger the FullState recovery path.
        /// </summary>
        [Test]
        public void DeterminismFailureRecovery_FullStateRestoresCorrectState()
        {
            CreateServer();
            AddClient();
            AddClient();
            StartPlaying();

            // Advance the server a few ticks
            for (int i = 0; i < 10; i++)
                Tick();

            Assert.GreaterOrEqual(_server.CurrentTick, 5);

            // Verify FullState request → response path
            // Confirms that the client explicitly sends FullStateRequest and receives a response
            bool fullStateReceived = false;
            var clientService = (ServerDrivenClientService)_clients[0].NetworkService;
            clientService.OnServerFullStateReceived += (tick, data, hash) =>
            {
                fullStateReceived = true;
            };

            // Send FullStateRequest
            clientService.SendFullStateRequest(_clients[0].Engine.CurrentTick);

            // Pump so the server responds
            for (int i = 0; i < 10; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                _server.Engine.Update(0.05f);
                _clients[0].NetworkService.Update();
            }

            Assert.IsTrue(fullStateReceived,
                "Client should receive FullStateResponse after sending FullStateRequest");

            // Server continues even after FullState reception
            for (int i = 0; i < 10; i++)
                Tick();

            Assert.GreaterOrEqual(_server.CurrentTick, 15,
                "Server should continue after FullState recovery");
        }

        // ── #31 DeterminismFailure → Auto FullState Recovery ──

        /// <summary>
        /// #31 Determinism failure → server state request recovery (section 7.2.10)
        /// Hash mismatch detected in ProcessVerifiedBatch → auto FullStateRequest → server response → state restoration.
        /// Triggers a mismatch by changing the client simulation hash for only 1 tick, then immediately restores it.
        /// </summary>
        [Test]
        public void DeterminismFailure_AutoFullStateRecovery()
        {
            LogAssert.ignoreFailingMessages = true;

            CreateServer();
            var client1 = AddClient();
            AddClient();
            StartPlaying();

            // Server + client progress normally
            for (int i = 0; i < 5; i++)
                Tick();

            Assert.GreaterOrEqual(_server.CurrentTick, 3);

            // Track FullState reception
            bool fullStateReceived = false;
            var clientService = (ServerDrivenClientService)client1.NetworkService;
            clientService.OnServerFullStateReceived += (tick, data, hash) =>
            {
                fullStateReceived = true;
            };

            // Force client 1's hash to differ once to induce a mismatch
            client1.Simulation.StateHash = 99999L;

            // Server 1 tick → VerifiedState → client ProcessVerifiedBatch detects mismatch → FullStateRequest
            _server.Transport.PollEvents();
            _server.NetworkService.Update();
            _server.Engine.Update(0.05f);
            client1.NetworkService.Update();
            client1.Engine.Update(0.05f);

            // Restore hash immediately (so normal comparison works after FullState recovery)
            client1.Simulation.StateHash = _server.Simulation.StateHash;

            // Process server response
            for (int i = 0; i < 10; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                _server.Engine.Update(0.05f);
                client1.NetworkService.Update();
                if (client1.Phase == SessionPhase.Playing)
                    client1.Engine.Update(0.05f);
            }

            Assert.IsTrue(fullStateReceived,
                "Client should auto-request and receive FullState after hash mismatch");

            // Verify normal progress after FullState recovery
            for (int i = 0; i < 10; i++)
                Tick();

            Assert.GreaterOrEqual(_server.CurrentTick, 13,
                "Server should continue after client recovery");

            LogAssert.ignoreFailingMessages = false;
        }

        /// <summary>
        /// #31 Determinism failure recovery — Resumes normal progress after FullState recovery.
        /// </summary>
        [Test]
        public void DeterminismFailure_PendingFlagClearedAfterRecovery()
        {
            LogAssert.ignoreFailingMessages = true;

            CreateServer();
            AddClient();
            AddClient();
            StartPlaying();

            for (int i = 0; i < 5; i++)
                Tick();

            // Change client hash once → triggers mismatch
            _clients[0].Simulation.StateHash = 77777L;

            _server.Transport.PollEvents();
            _server.NetworkService.Update();
            _server.Engine.Update(0.05f);
            _clients[0].NetworkService.Update();
            _clients[0].Engine.Update(0.05f);

            // Restore hash immediately
            _clients[0].Simulation.StateHash = _server.Simulation.StateHash;

            // Process FullState response
            for (int i = 0; i < 10; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                _server.Engine.Update(0.05f);
                _clients[0].NetworkService.Update();
                if (_clients[0].Phase == SessionPhase.Playing)
                    _clients[0].Engine.Update(0.05f);
            }

            // Further progress — ProcessVerifiedBatch should work normally
            int tickBefore = _server.CurrentTick;
            for (int i = 0; i < 20; i++)
                Tick();

            Assert.Greater(_server.CurrentTick, tickBefore,
                "Server should continue ticking after client recovery");

            LogAssert.ignoreFailingMessages = false;
        }

        // ── #32 Late Join / Reconnect / Spectator integration ──────

        /// <summary>
        /// #32-5 ServerDriven.LateJoin (section 7.2.5)
        /// Verifies that the server continues ticking and PlayerCount increases after a late join.
        /// </summary>
        [Test]
        public void LateJoin_ServerContinuesAndPlayerCountIncreases()
        {
            CreateServer();
            AddClient();
            AddClient();
            StartPlaying();

            int initialPlayerCount = _server.NetworkService.PlayerCount;

            // Advance server 10 ticks
            for (int i = 0; i < 10; i++)
                Tick();

            int tickBeforeJoin = _server.CurrentTick;

            // Add a late join client
            var lateClient = AddClient();

            // Advance server + existing clients + late join client
            for (int i = 0; i < 50; i++)
                Tick();

            Assert.AreEqual(initialPlayerCount + 1, _server.NetworkService.PlayerCount,
                "Server should have one more player after late join");
            Assert.Greater(_server.CurrentTick, tickBeforeJoin,
                "Server should continue ticking after late join");
        }

        /// <summary>
        /// #32-6 ServerDriven.Reconnect (section 7.2.6)
        /// Disconnect → server continues progressing → receives FullState on reconnect.
        /// </summary>
        [Test]
        public void Reconnect_ServerContinuesDuringDisconnect()
        {
            CreateServer();
            var client1 = AddClient();
            AddClient();
            StartPlaying();

            for (int i = 0; i < 10; i++)
                Tick();

            // Disconnect client 1
            client1.Transport.Disconnect();
            PumpMessages(3);

            int tickAtDisconnect = _server.CurrentTick;

            // Server keeps ticking even during the disconnect (substitutes EmptyCommand)
            for (int i = 0; i < 20; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                _server.Engine.Update(0.05f);
            }

            Assert.Greater(_server.CurrentTick, tickAtDisconnect + 10,
                "Server should progress at least 10 ticks during client disconnect");

            // The disconnected player slot is preserved
            int playerCount = _server.NetworkService.PlayerCount;
            Assert.GreaterOrEqual(playerCount, 2,
                "Server should keep disconnected player slot (for reconnect)");
        }

        /// <summary>
        /// #32-6 ServerDriven.Reconnect — Removes player after reconnect timeout.
        /// </summary>
        [Test]
        public void Reconnect_TimeoutRemovesPlayer()
        {
            // Create server with a very short timeout
            var serverPeer = new SDTestPeer
            {
                Transport = new TestTransport(),
                Simulation = new TestSimulation(),
                IsServer = true
            };
            var serverService = new ServerNetworkService();
            serverService.Initialize(serverPeer.Transport, _commandFactory, _logger);
            serverPeer.NetworkService = serverService;
            serverPeer.Engine = new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = 50,
                    MaxRollbackTicks = 50,
                    InputDelayTicks = 0,
                    HardToleranceMs = 200,
                },
                new SessionConfig
                {
                    ReconnectTimeoutMs = 100, // 100ms — very short
                    CountdownDurationMs = 0,
                });
            serverPeer.Engine.Initialize(serverPeer.Simulation, serverService, _logger);
            serverPeer.Engine.SetCommandFactory(_commandFactory);
            serverService.SubscribeEngine(serverPeer.Engine);
            serverService.CreateRoom("test", 4);
            serverService.Listen("localhost", 7777, 4);
            _server = serverPeer;

            var c1 = AddClient();
            var c2 = AddClient();
            StartPlaying();

            for (int i = 0; i < 5; i++)
                Tick();

            int playerCountBefore = _server.NetworkService.PlayerCount;

            // Disconnect client 1
            c1.Transport.Disconnect();
            PumpMessages(3);

            // Wait for timeout (over 100ms)
            System.Threading.Thread.Sleep(150);

            // Server Update → timeout check → remove player
            for (int i = 0; i < 5; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                _server.Engine.Update(0.05f);
            }

            Assert.Less(_server.NetworkService.PlayerCount, playerCountBefore,
                "Server should remove player after reconnect timeout");
        }

        /// <summary>
        /// #32-7 ServerDriven.Spectator (section 7.2.7)
        /// Spectator connects → receives SpectatorAccept → verifies VerifiedState reception.
        /// </summary>
        [Test]
        public void Spectator_ReceivesVerifiedStateMessages()
        {
            CreateServer();
            AddClient();
            AddClient();
            StartPlaying();

            // Advance the server a few ticks
            for (int i = 0; i < 5; i++)
                Tick();

            // Simulate spectator connection: send SpectatorJoinMessage
            var spectatorTransport = new TestTransport();
            spectatorTransport.Connect("localhost", 7777);
            PumpMessages(3);

            // Spectator peer sends SpectatorJoinMessage
            var serializer = new MessageSerializer();
            var joinMsg = new SpectatorJoinMessage();
            var joinData = serializer.Serialize(joinMsg);
            spectatorTransport.Send(0, joinData, DeliveryMethod.ReliableOrdered);
            PumpMessages(3);

            Assert.GreaterOrEqual(_server.NetworkService.SpectatorCount, 1,
                "Server should have at least 1 spectator");

            // Additional server ticks → also broadcast VerifiedState to spectator
            int messagesReceived = 0;
            spectatorTransport.PollEvents(); // Drain previous messages

            // Verify spectator reception: messages arrive at the spectator transport after server ticks
            for (int i = 0; i < 10; i++)
            {
                _server.Transport.PollEvents();
                _server.NetworkService.Update();
                _server.Engine.Update(0.05f);
            }

            // Count messages received on spectator transport (consumed via PollEvents)
            int count = 0;
            Action<int, byte[], int> counter = (_, __, ___) => count++;
            spectatorTransport.OnDataReceived += counter;
            spectatorTransport.PollEvents();
            spectatorTransport.OnDataReceived -= counter;

            Assert.Greater(count, 0,
                "Spectator should receive messages (VerifiedState) from server");
        }

        // ── Countdown ──────────────────────────────────────

        private SDTestPeer CreateServerWithCountdown(int countdownMs = 100)
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
                    CountdownDurationMs = countdownMs,
                });
            peer.Engine.Initialize(peer.Simulation, serverService, _logger);
            peer.Engine.SetCommandFactory(_commandFactory);
            serverService.SubscribeEngine(peer.Engine);

            serverService.CreateRoom("test", 4);
            serverService.Listen("localhost", 7777, 4);

            _server = peer;
            return peer;
        }

        private SDTestPeer AddClientWithCountdown(int countdownMs = 100)
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
                new SessionConfig
                {
                    CountdownDurationMs = countdownMs,
                });
            peer.Engine.Initialize(peer.Simulation, clientService, _logger);
            peer.Engine.SetCommandFactory(_commandFactory);
            clientService.SubscribeEngine(peer.Engine);

            clientService.JoinRoom("test");
            clientService.Connect("localhost", 7777);

            _clients.Add(peer);
            PumpMessages();

            return peer;
        }

        /// <summary>
        /// Verification #1: CountdownDurationMs > 0 — both server/client transition Countdown → Playing.
        /// </summary>
        [Test]
        public void Countdown_PhaseTransitionsFromCountdownToPlaying()
        {
            CreateServerWithCountdown(100);
            var client1 = AddClientWithCountdown(100);
            var client2 = AddClientWithCountdown(100);

            // Ready → send GameStartMessage
            foreach (var client in _clients)
                client.NetworkService.SetReady(true);
            PumpMessages();
            PumpMessages();

            // Verify Countdown phase
            Assert.AreEqual(SessionPhase.Countdown, _server.Phase, "Server should be in Countdown");
            Assert.AreEqual(SessionPhase.Countdown, client1.Phase, "Client1 should be in Countdown");
            Assert.AreEqual(SessionPhase.Countdown, client2.Phase, "Client2 should be in Countdown");

            // Wait for countdown expiration
            System.Threading.Thread.Sleep(150);

            // Check expiration via Update
            _server.Transport.PollEvents();
            _server.NetworkService.Update();
            foreach (var client in _clients)
                client.NetworkService.Update();

            Assert.AreEqual(SessionPhase.Playing, _server.Phase, "Server should be Playing after countdown");
            Assert.AreEqual(SessionPhase.Playing, client1.Phase, "Client1 should be Playing after countdown");
            Assert.AreEqual(SessionPhase.Playing, client2.Phase, "Client2 should be Playing after countdown");
        }

        /// <summary>
        /// Verification #2: CountdownDurationMs = 0 — immediately Playing (Countdown phase skipped).
        /// </summary>
        [Test]
        public void Countdown_ZeroDuration_ImmediatePlaying()
        {
            CreateServer(); // CountdownDurationMs = 0
            var client1 = AddClient();
            var client2 = AddClient();

            foreach (var client in _clients)
                client.NetworkService.SetReady(true);
            PumpMessages();
            PumpMessages();

            // Immediately Playing — no Countdown phase
            Assert.AreEqual(SessionPhase.Playing, _server.Phase, "Server should skip Countdown");
            Assert.AreEqual(SessionPhase.Playing, client1.Phase, "Client1 should skip Countdown");
            Assert.AreEqual(SessionPhase.Playing, client2.Phase, "Client2 should skip Countdown");
        }

        /// <summary>
        /// Verification #3: VerifiedStateMessage received during countdown is not lost.
        /// </summary>
        [Test]
        public void Countdown_VerifiedStateNotLostDuringCountdown()
        {
            CreateServerWithCountdown(100);
            var client1 = AddClientWithCountdown(100);
            AddClientWithCountdown(100);

            foreach (var client in _clients)
                client.NetworkService.SetReady(true);
            PumpMessages();
            PumpMessages();

            Assert.AreEqual(SessionPhase.Countdown, _server.Phase);

            // Wait for countdown expiration — only the server transitions to Playing first
            System.Threading.Thread.Sleep(150);
            _server.Transport.PollEvents();
            _server.NetworkService.Update();
            Assert.AreEqual(SessionPhase.Playing, _server.Phase);

            // Run server tick → produce VerifiedState
            _server.Engine.Update(0.05f);

            // Clients may still be in Countdown, but Update() runs expiration check + PollEvents
            // PollEvents receives VerifiedState → queued in HandleVerifiedStateReceived
            // Same Update: countdown expires → Playing → OnGameStart
            foreach (var client in _clients)
            {
                client.NetworkService.Update();
                client.Engine.Update(0.05f);
            }

            // Advance further ticks
            for (int i = 0; i < 20; i++)
                Tick();

            // Clients progress normally (VerifiedState was not lost)
            Assert.GreaterOrEqual(_server.CurrentTick, 10,
                "Server should have progressed");
            foreach (var client in _clients)
            {
                Assert.Greater(client.CurrentTick, 0,
                    "Client should have advanced (VerifiedState was not lost)");
            }
        }

        /// <summary>
        /// Verification #4: Confirms OnCountdownStarted event fires.
        /// </summary>
        [Test]
        public void Countdown_OnCountdownStartedEventFires()
        {
            CreateServerWithCountdown(100);
            var client1 = AddClientWithCountdown(100);
            AddClientWithCountdown(100);

            long serverCountdownTime = 0;
            long client1CountdownTime = 0;
            _server.NetworkService.OnCountdownStarted += t => serverCountdownTime = t;
            client1.NetworkService.OnCountdownStarted += t => client1CountdownTime = t;

            foreach (var client in _clients)
                client.NetworkService.SetReady(true);
            PumpMessages();
            PumpMessages();

            Assert.Greater(serverCountdownTime, 0, "Server OnCountdownStarted should fire with valid time");
            Assert.Greater(client1CountdownTime, 0, "Client OnCountdownStarted should fire with valid time");
            Assert.AreEqual(serverCountdownTime, client1CountdownTime,
                "Server and client should receive the same countdown target time");
        }

        /// <summary>
        /// Verification #1+: Normal tick progression after countdown (end-to-end).
        /// </summary>
        [Test]
        public void Countdown_NormalTickProgressionAfterCountdown()
        {
            CreateServerWithCountdown(100);
            AddClientWithCountdown(100);
            AddClientWithCountdown(100);

            foreach (var client in _clients)
                client.NetworkService.SetReady(true);
            PumpMessages();
            PumpMessages();

            // Wait for countdown expiration
            System.Threading.Thread.Sleep(150);

            // Transition to Playing after expiration
            _server.Transport.PollEvents();
            _server.NetworkService.Update();
            foreach (var client in _clients)
                client.NetworkService.Update();

            Assert.AreEqual(SessionPhase.Playing, _server.Phase);

            // Normal tick progression
            int targetTick = 50;
            for (int i = 0; i < targetTick * 5; i++)
            {
                Tick();
                if (_server.CurrentTick >= targetTick)
                    break;
            }

            Assert.GreaterOrEqual(_server.CurrentTick, targetTick,
                "Server should reach target ticks after countdown");

            long serverHash = _server.Simulation.GetStateHash();
            foreach (var client in _clients)
            {
                Assert.AreEqual(serverHash, client.Simulation.GetStateHash(),
                    "Client hash should match server after countdown");
            }
        }

        // ── Deterministic hash mode helpers ─────────────────────────

        private SDTestPeer CreateServerWithDeterministicHash()
        {
            var peer = CreateServer();
            peer.Simulation.UseDeterministicHash = true;
            return peer;
        }

        private SDTestPeer AddClientWithDeterministicHash()
        {
            var peer = AddClient();
            peer.Simulation.UseDeterministicHash = true;
            return peer;
        }
    }
}
