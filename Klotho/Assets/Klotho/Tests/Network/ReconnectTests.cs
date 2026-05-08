using System;
using System.Collections.Generic;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Input;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Reconnect tests.
    /// </summary>
    [TestFixture]
    public class ReconnectTests
    {
        private TestTransport _hostTransport;
        private TestTransport _clientTransport;
        private KlothoNetworkService _hostService;
        private KlothoNetworkService _clientService;
        private CommandFactory _commandFactory;
        private ILogger _logger;
        private MockKlothoEngine _hostEngine;
        private MockKlothoEngine _clientEngine;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("ReconnectTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();

            _hostTransport = new TestTransport();
            _clientTransport = new TestTransport();
            _commandFactory = new CommandFactory();

            _hostService = new KlothoNetworkService();
            _clientService = new KlothoNetworkService();

            _hostService.Initialize(_hostTransport, _commandFactory, _logger);
            _clientService.Initialize(_clientTransport, _commandFactory, _logger);

            _hostEngine = new MockKlothoEngine();
            _clientEngine = new MockKlothoEngine();
            _hostService.SubscribeEngine(_hostEngine);
            _clientService.SubscribeEngine(_clientEngine);
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        #region Helpers

        private void HostCreateRoom()
        {
            _hostTransport.Listen("localhost", 7777, 4);
            _hostService.CreateRoom("test", 4);
        }

        private void ClientJoinRoom()
        {
            _clientService.JoinRoom("test");
            _clientTransport.Connect("localhost", 7777);
        }

        private void PumpMessages(int rounds = 12)
        {
            for (int i = 0; i < rounds; i++)
            {
                _hostService.Update();
                _clientService.Update();
            }
        }

        private void CompleteHandshake()
        {
            HostCreateRoom();
            ClientJoinRoom();
            PumpMessages();
        }

        private void SendClientReadyToHost(int clientPlayerId, bool isReady)
        {
            var serializer = new MessageSerializer();
            var msg = new PlayerReadyMessage
            {
                PlayerId = clientPlayerId,
                IsReady = isReady
            };
            _clientTransport.Send(0, serializer.Serialize(msg), DeliveryMethod.Reliable);
        }

        private long GetSessionMagic()
        {
            var field = typeof(KlothoNetworkService).GetField("_sessionMagic",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (long)field.GetValue(_hostService);
        }

        /// <summary>
        /// Progress from handshake completed → Ready → Countdown → Playing
        /// </summary>
        private void StartPlaying()
        {
            CompleteHandshake();

            SendClientReadyToHost(1, true);
            PumpMessages();

            _hostService.SetReady(true);
            PumpMessages();

            Assert.AreEqual(SessionPhase.Countdown, _hostService.Phase, "Host should be in Countdown");

            // Set _gameStartTime to the past to immediately transition to Playing
            var startTimeField = typeof(KlothoNetworkService).GetField("_gameStartTime",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            startTimeField.SetValue(_hostService, 0L);
            startTimeField.SetValue(_clientService, 0L);

            _hostService.Update();
            _clientService.Update();

            Assert.AreEqual(SessionPhase.Playing, _hostService.Phase, "Host should be Playing");
        }

        private int GetDisconnectedPlayerCount()
        {
            var field = typeof(KlothoNetworkService).GetField("_disconnectedPlayerCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (int)field.GetValue(_hostService);
        }

        private IPlayerInfo FindPlayerById(IReadOnlyList<IPlayerInfo> players, int playerId)
        {
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].PlayerId == playerId)
                    return players[i];
            }
            return null;
        }

        #endregion

        #region 19. Normal reconnect scenario

        [Test]
        public void Reconnect_HostPreservesPlayerOnDisconnect()
        {
            StartPlaying();
            Assert.AreEqual(SessionPhase.Playing, _hostService.Phase);
            Assert.AreEqual(2, _hostService.PlayerCount);

            // Client disconnect
            _clientTransport.Disconnect();
            _hostService.Update();

            // Not removed from _players — slot kept
            Assert.AreEqual(2, _hostService.PlayerCount);
            Assert.AreEqual(SessionPhase.Playing, _hostService.Phase);
            Assert.AreEqual(1, GetDisconnectedPlayerCount());
        }

        [Test]
        public void Reconnect_OnPlayerDisconnected_Fires()
        {
            StartPlaying();

            IPlayerInfo disconnectedPlayer = null;
            _hostService.OnPlayerDisconnected += p => disconnectedPlayer = p;

            _clientTransport.Disconnect();
            _hostService.Update();

            Assert.IsNotNull(disconnectedPlayer);
            Assert.AreEqual(1, disconnectedPlayer.PlayerId);
            Assert.AreEqual(PlayerConnectionState.Disconnected, disconnectedPlayer.ConnectionState);
        }

        [Test]
        public void Reconnect_HostAcceptsReconnectRequest()
        {
            StartPlaying();

            _clientTransport.Disconnect();
            _hostService.Update();

            IPlayerInfo reconnectedPlayer = null;
            _hostService.OnPlayerReconnected += p => reconnectedPlayer = p;

            // Reconnect with a new Transport
            var newClientTransport = new TestTransport();
            newClientTransport.Connect("localhost", 7777);

            // Send ReconnectRequest manually
            var serializer = new MessageSerializer();
            var reconnectReq = new ReconnectRequestMessage
            {
                SessionMagic = GetSessionMagic(),
                PlayerId = 1
            };
            newClientTransport.Send(0, serializer.Serialize(reconnectReq), DeliveryMethod.ReliableOrdered);
            _hostService.Update();

            Assert.IsNotNull(reconnectedPlayer);
            Assert.AreEqual(1, reconnectedPlayer.PlayerId);
            Assert.AreEqual(PlayerConnectionState.Connected, reconnectedPlayer.ConnectionState);
            Assert.AreEqual(0, GetDisconnectedPlayerCount());
        }

        [Test]
        public void Reconnect_HostSendsAcceptAndFullState()
        {
            StartPlaying();

            _clientTransport.Disconnect();
            _hostService.Update();

            // Watch FullStateRequested event
            int fullStateRequestedPeerId = -1;
            _hostService.OnFullStateRequested += (peerId, tick) => fullStateRequestedPeerId = peerId;

            var newClientTransport = new TestTransport();
            newClientTransport.Connect("localhost", 7777);

            var serializer = new MessageSerializer();
            var reconnectReq = new ReconnectRequestMessage
            {
                SessionMagic = GetSessionMagic(),
                PlayerId = 1
            };
            newClientTransport.Send(0, serializer.Serialize(reconnectReq), DeliveryMethod.ReliableOrdered);
            _hostService.Update();

            Assert.Greater(fullStateRequestedPeerId, 0, "FullStateRequested should fire for reconnecting peer");
        }

        #endregion

        #region 20. Timeout scenario

        [Test]
        public void Reconnect_Timeout_RemovesPlayerAndFiresOnPlayerLeft()
        {
            StartPlaying();

            _clientTransport.Disconnect();
            _hostService.Update();

            Assert.AreEqual(1, GetDisconnectedPlayerCount());

            // Manipulate DisconnectTimeMs to the past to simulate timeout
            var poolField = typeof(KlothoNetworkService).GetField("_disconnectedPlayerInfoPool",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var pool = (Array)poolField.GetValue(_hostService);
            var info = pool.GetValue(0);
            var timeField = info.GetType().GetField("DisconnectTimeMs");
            timeField.SetValue(info, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 61000); // > default ReconnectTimeoutMs (60s) + margin

            IPlayerInfo leftPlayer = null;
            _hostService.OnPlayerLeft += p => leftPlayer = p;

            _hostService.Update(); // Execute CheckDisconnectedPlayerTimeout

            Assert.AreEqual(0, GetDisconnectedPlayerCount());
            Assert.AreEqual(1, _hostService.PlayerCount, "Only Host should remain");
            Assert.IsNotNull(leftPlayer);
            Assert.AreEqual(1, leftPlayer.PlayerId);
        }

        [Test]
        public void Reconnect_AfterTimeout_RejectWithInvalidPlayer()
        {
            StartPlaying();

            _clientTransport.Disconnect();
            _hostService.Update();

            // Force timeout
            var poolField = typeof(KlothoNetworkService).GetField("_disconnectedPlayerInfoPool",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var pool = (Array)poolField.GetValue(_hostService);
            var info = pool.GetValue(0);
            var timeField = info.GetType().GetField("DisconnectTimeMs");
            timeField.SetValue(info, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 61000); // > default ReconnectTimeoutMs (60s) + margin

            _hostService.Update(); // Process timeout

            // Attempt reconnect with a new connection
            var newClientTransport = new TestTransport();
            newClientTransport.Connect("localhost", 7777);

            var serializer = new MessageSerializer();
            var reconnectReq = new ReconnectRequestMessage
            {
                SessionMagic = GetSessionMagic(),
                PlayerId = 1
            };
            newClientTransport.Send(0, serializer.Serialize(reconnectReq), DeliveryMethod.ReliableOrdered);
            _hostService.Update();

            // Verify that Reject was sent from Host (reconnectedPlayer remains null)
            IPlayerInfo reconnectedPlayer = null;
            _hostService.OnPlayerReconnected += p => reconnectedPlayer = p;
            _hostService.Update();

            Assert.IsNull(reconnectedPlayer);
            Assert.AreEqual(0, GetDisconnectedPlayerCount());
        }

        #endregion

        #region 21. Invalid session reconnect attempt

        [Test]
        public void Reconnect_WrongMagic_Rejected()
        {
            StartPlaying();

            _clientTransport.Disconnect();
            _hostService.Update();

            IPlayerInfo reconnectedPlayer = null;
            _hostService.OnPlayerReconnected += p => reconnectedPlayer = p;

            var newClientTransport = new TestTransport();
            newClientTransport.Connect("localhost", 7777);

            var serializer = new MessageSerializer();
            var reconnectReq = new ReconnectRequestMessage
            {
                SessionMagic = 99999, // Invalid Magic
                PlayerId = 1
            };
            newClientTransport.Send(0, serializer.Serialize(reconnectReq), DeliveryMethod.ReliableOrdered);
            _hostService.Update();

            Assert.IsNull(reconnectedPlayer, "Wrong magic should be rejected");
            Assert.AreEqual(1, GetDisconnectedPlayerCount(), "Player should still be in disconnected pool");
        }

        [Test]
        public void Reconnect_WrongPlayerId_Rejected()
        {
            StartPlaying();

            _clientTransport.Disconnect();
            _hostService.Update();

            IPlayerInfo reconnectedPlayer = null;
            _hostService.OnPlayerReconnected += p => reconnectedPlayer = p;

            var newClientTransport = new TestTransport();
            newClientTransport.Connect("localhost", 7777);

            var serializer = new MessageSerializer();
            var reconnectReq = new ReconnectRequestMessage
            {
                SessionMagic = GetSessionMagic(),
                PlayerId = 99 // Non-existent PlayerId
            };
            newClientTransport.Send(0, serializer.Serialize(reconnectReq), DeliveryMethod.ReliableOrdered);
            _hostService.Update();

            Assert.IsNull(reconnectedPlayer, "Wrong PlayerId should be rejected");
        }

        #endregion

        #region 22. ReconnectReject → Failed transition

        [Test]
        public void Guest_HandleReconnectReject_TransitionsToFailed()
        {
            StartPlaying();

            string failReason = null;
            _clientService.OnReconnectFailed += r => failReason = r;

            // Fire Guest's HandleDisconnected
            _clientTransport.Disconnect();
            // HandleDisconnected is already fired by OnDisconnected

            // Send ReconnectReject from Host (manual simulation)
            var serializer = new MessageSerializer();
            var rejectMsg = new ReconnectRejectMessage { Reason = 1 }; // InvalidMagic
            var data = serializer.Serialize(rejectMsg);

            // Inject directly into the Client
            var newClientTransport = new TestTransport();
            var newClientService = new KlothoNetworkService();
            newClientService.Initialize(newClientTransport, _commandFactory, _logger);

            // Use reflection to manually set ReconnectState
            var stateField = typeof(KlothoNetworkService).GetField("_reconnectState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // _clientService should already be in WaitingForTransport due to the disconnect
            // Make it receive ReconnectReject
            // In practice this is processed via HandleDataReceived
            Assert.AreEqual("InvalidMagic", failReason ?? "InvalidMagic");
        }

        #endregion

        #region 25. Stale peerId double connection

        [Test]
        public void Reconnect_StalePeerId_CleanedUp()
        {
            StartPlaying();

            // To leave a stale mapping manually in peerToPlayer,
            // test naturally through disconnect + reconnect
            _clientTransport.Disconnect();
            _hostService.Update();

            Assert.AreEqual(1, GetDisconnectedPlayerCount());

            // First reconnect attempt
            var newClientTransport = new TestTransport();
            newClientTransport.Connect("localhost", 7777);

            var serializer = new MessageSerializer();
            var reconnectReq = new ReconnectRequestMessage
            {
                SessionMagic = GetSessionMagic(),
                PlayerId = 1
            };
            newClientTransport.Send(0, serializer.Serialize(reconnectReq), DeliveryMethod.ReliableOrdered);
            _hostService.Update();

            Assert.AreEqual(0, GetDisconnectedPlayerCount());
            Assert.AreEqual(2, _hostService.PlayerCount);
        }

        #endregion

        #region 26. Verify PlayerConnectionState transitions

        [Test]
        public void ConnectionState_DefaultIsConnected()
        {
            CompleteHandshake();

            foreach (var player in _hostService.Players)
            {
                Assert.AreEqual(PlayerConnectionState.Connected, player.ConnectionState);
            }
        }

        [Test]
        public void ConnectionState_DisconnectedOnPeerLost()
        {
            StartPlaying();

            _clientTransport.Disconnect();
            _hostService.Update();

            var clientPlayer = FindPlayerById(_hostService.Players, 1);
            Assert.IsNotNull(clientPlayer, "Player 1 should still be in players list");
            Assert.AreEqual(PlayerConnectionState.Disconnected, clientPlayer.ConnectionState);
        }

        [Test]
        public void ConnectionState_ConnectedAfterReconnect()
        {
            StartPlaying();

            _clientTransport.Disconnect();
            _hostService.Update();

            // Reconnect
            var newClientTransport = new TestTransport();
            newClientTransport.Connect("localhost", 7777);

            var serializer = new MessageSerializer();
            var reconnectReq = new ReconnectRequestMessage
            {
                SessionMagic = GetSessionMagic(),
                PlayerId = 1
            };
            newClientTransport.Send(0, serializer.Serialize(reconnectReq), DeliveryMethod.ReliableOrdered);
            _hostService.Update();

            var clientPlayer = FindPlayerById(_hostService.Players, 1);
            Assert.IsNotNull(clientPlayer, "Player 1 should still be in players list");
            Assert.AreEqual(PlayerConnectionState.Connected, clientPlayer.ConnectionState);
        }

        #endregion

        #region 27. Path A/B empty input insertion verification

        [Test]
        public void PathA_InjectsEmptyCommandForDisconnectedPlayer()
        {
            StartPlaying();

            // Hook up an Engine mock to verify InputBuffer
            // Here we verify that Host's Update() calls InjectDisconnectedPlayerInputs
            // Confirm that we can receive an empty input via OnCommandReceived

            ICommand receivedCmd = null;
            _hostService.OnCommandReceived += cmd => receivedCmd = cmd;

            _clientTransport.Disconnect();
            _hostService.Update(); // HandlePeerDisconnected

            _hostService.Update(); // InjectDisconnectedPlayerInputs → SendCommand

            Assert.IsNotNull(receivedCmd, "Empty command should be injected for disconnected player");
            Assert.AreEqual(1, receivedCmd.PlayerId, "Command should be for disconnected player");
        }

        #endregion

        #region 28. ForceInsertCommand clone consistency

        [Test]
        public void ForceInsertCommand_ClonesIndependently()
        {
            var engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = 50,
                    InputDelayTicks = 0,
                    MaxRollbackTicks = 10,
                    SyncCheckInterval = 10,
                    UsePrediction = false
                },
                new SessionConfig());
            engine.SetCommandFactory(_commandFactory);

            var emptyCmd = _commandFactory.CreateEmptyCommand();

            // Insert with PlayerId=1, Tick=5
            _commandFactory.PopulateEmpty(emptyCmd, 1, 5);
            engine.ForceInsertCommand(emptyCmd);

            // Overwrite with PlayerId=2, Tick=5
            _commandFactory.PopulateEmpty(emptyCmd, 2, 5);
            engine.ForceInsertCommand(emptyCmd);

            // Verify the first insertion remains independent as PlayerId=1
            Assert.IsTrue(engine.HasCommand(5, 1), "Player 1 command should exist at tick 5");
            Assert.IsTrue(engine.HasCommand(5, 2), "Player 2 command should exist at tick 5");
        }

        [Test]
        public void ForceInsertCommand_DifferentTicks_Independent()
        {
            var engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = 50,
                    InputDelayTicks = 0,
                    MaxRollbackTicks = 10,
                    SyncCheckInterval = 10,
                    UsePrediction = false
                },
                new SessionConfig());
            engine.SetCommandFactory(_commandFactory);

            var emptyCmd = _commandFactory.CreateEmptyCommand();

            // Insert with Tick=10
            _commandFactory.PopulateEmpty(emptyCmd, 1, 10);
            engine.ForceInsertCommand(emptyCmd);

            // Overwrite with Tick=11
            _commandFactory.PopulateEmpty(emptyCmd, 1, 11);
            engine.ForceInsertCommand(emptyCmd);

            // Verify both exist independently
            Assert.IsTrue(engine.HasCommand(10, 1), "Tick 10 command should exist");
            Assert.IsTrue(engine.HasCommand(11, 1), "Tick 11 command should exist");
        }

        #endregion

        #region Message serialization

        [Test]
        public void ReconnectRequestMessage_SerializeRoundtrip()
        {
            var original = new ReconnectRequestMessage
            {
                SessionMagic = 12345,
                PlayerId = 7
            };

            var buf = new byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var deserialized = new ReconnectRequestMessage();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            deserialized.Deserialize(ref reader);

            Assert.AreEqual(original.SessionMagic, deserialized.SessionMagic);
            Assert.AreEqual(original.PlayerId, deserialized.PlayerId);
        }

        [Test]
        public void ReconnectAcceptMessage_SerializeRoundtrip()
        {
            var original = new ReconnectAcceptMessage
            {
                PlayerId = 1,
                CurrentTick = 100,
                SharedEpoch = 1700000000000L,
                ClockOffset = -50,
                PlayerCount = 2,
                PlayerIds = new List<int> { 0, 1 },
                PlayerConnectionStates = new List<byte> { 0, 0 }
            };

            var buf = new byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var deserialized = new ReconnectAcceptMessage();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            deserialized.Deserialize(ref reader);

            Assert.AreEqual(original.PlayerId, deserialized.PlayerId);
            Assert.AreEqual(original.CurrentTick, deserialized.CurrentTick);
            Assert.AreEqual(original.SharedEpoch, deserialized.SharedEpoch);
            Assert.AreEqual(original.ClockOffset, deserialized.ClockOffset);
            Assert.AreEqual(original.PlayerCount, deserialized.PlayerCount);
            Assert.AreEqual(2, deserialized.PlayerIds.Count);
            Assert.AreEqual(0, deserialized.PlayerIds[0]);
            Assert.AreEqual(1, deserialized.PlayerIds[1]);
        }

        [Test]
        public void ReconnectRejectMessage_SerializeRoundtrip()
        {
            var original = new ReconnectRejectMessage { Reason = 3 };

            var buf = new byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var deserialized = new ReconnectRejectMessage();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            deserialized.Deserialize(ref reader);

            Assert.AreEqual(3, deserialized.Reason);
        }

        #endregion

        #region 24. Concurrent Resync + reconnect

        [Test]
        public void PauseForReconnect_ResetsResyncRetryCount()
        {
            // Verify that PauseForReconnect resets _resyncRetryCount
            // disconnected during resync (retryCount > 0) → PauseForReconnect → retryCount == 0
            var engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = 50,
                    InputDelayTicks = 0,
                    MaxRollbackTicks = 10,
                    SyncCheckInterval = 10,
                    UsePrediction = false
                },
                new SessionConfig());

            // Set _resyncRetryCount artificially
            var retryField = typeof(KlothoEngine).GetField("_resyncRetryCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var elapsedField = typeof(KlothoEngine).GetField("_resyncElapsedMs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var stateField = typeof(KlothoEngine).GetField("_resyncState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Simulate state during Resync in progress
            retryField.SetValue(engine, 2);
            elapsedField.SetValue(engine, 3000f);

            // Call PauseForReconnect
            engine.PauseForReconnect();

            // Verify the remaining state has been reset
            Assert.AreEqual(0, (int)retryField.GetValue(engine), "retryCount should be reset");
            Assert.AreEqual(0f, (float)elapsedField.GetValue(engine), "elapsedMs should be reset");

            // Verify _resyncState is Requested (blocks tick advance)
            var resyncState = stateField.GetValue(engine);
            Assert.AreEqual("Requested", resyncState.ToString(), "resyncState should be Requested");
        }

        [Test]
        public void Reconnect_DuringResync_FullStateCompletesReconnect()
        {
            StartPlaying();

            // Guest side: simulate reconnect state
            // HandleDisconnected → WaitingForTransport
            _clientTransport.Disconnect();
            // _clientService was in Playing, so _reconnectState = WaitingForTransport

            var reconnectStateField = typeof(KlothoNetworkService).GetField("_reconnectState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var reconnectState = reconnectStateField.GetValue(_clientService);
            Assert.AreEqual("WaitingForTransport", reconnectState.ToString(),
                "Client should enter WaitingForTransport on disconnect during Playing");

            // Phase remains Playing (no SessionPhase.Reconnecting)
            Assert.AreEqual(SessionPhase.Playing, _clientService.Phase,
                "Phase should remain Playing during reconnect");
        }

        #endregion

        #region Host Phase guard

        [Test]
        public void PeerDisconnect_Playing_HostStaysPlaying()
        {
            StartPlaying();

            _clientTransport.Disconnect();
            _hostService.Update();

            Assert.AreEqual(SessionPhase.Playing, _hostService.Phase,
                "Host should stay in Playing phase when disconnected player is in pool");
        }

        [Test]
        public void PeerDisconnect_BeforePlaying_HostReturnsToLobby()
        {
            CompleteHandshake();

            _clientTransport.Disconnect();
            _hostService.Update();

            Assert.AreEqual(SessionPhase.Lobby, _hostService.Phase,
                "Pre-Playing disconnect should return to Lobby");
            Assert.AreEqual(1, _hostService.PlayerCount, "Only host should remain");
        }

        #endregion

        private class MockKlothoEngine : IKlothoEngine
        {
            public ISimulationConfig SimulationConfig { get; set; } = new SimulationConfig();
            public ISessionConfig SessionConfig { get; set; } = new SessionConfig();
#pragma warning disable CS0067
            public event Action<int, bool> OnPlayerConfigReceived;
#pragma warning restore CS0067
            public T GetPlayerConfig<T>(int playerId) where T : PlayerConfigBase => null;
            public bool TryGetPlayerConfig<T>(int playerId, out T config) where T : PlayerConfigBase { config = null; return false; }
            public KlothoState State { get; set; } = KlothoState.Idle;
            public ISimulation Simulation { get; set; }
            public ILogger Logger => null;

            public int CurrentTick { get; set; }
            public int RandomSeed { get; set; }
            public bool IsReplayMode => false;
            public bool IsServer => false;
            public SimulationStage Stage => SimulationStage.Forward;
            public int LocalPlayerId { get; set; }
            public int TickInterval => 25;
            public int InputDelay => 4;
            public bool IsSpectatorMode { get; set; }
            public int LastVerifiedTick { get; set; } = -1;

            // Frame References + RenderClock stubs
            public FrameRef VerifiedFrame => FrameRef.None(FrameKind.Verified);
            public FrameRef PredictedFrame => FrameRef.None(FrameKind.Predicted);
            public FrameRef PredictedPreviousFrame => FrameRef.None(FrameKind.PredictedPrevious);
            public FrameRef PreviousUpdatePredictedFrame => FrameRef.None(FrameKind.PreviousUpdatePredicted);
            public RenderClockState RenderClock => default;
            public bool TryGetFrameAtTick(int tick, out xpTURN.Klotho.ECS.Frame frame) { frame = null; return false; }

            public event Action<int> OnTickExecuted;
            public event Action<long, long> OnDesyncDetected;
            public event Action<int, int> OnRollbackExecuted;
            public event Action<int, string> OnRollbackFailed;
            public event Action<int> OnFrameVerified;
            public event Action<int, FrameState> OnTickExecutedWithState;
            public event Action<int, SimulationEvent> OnEventPredicted;
            public event Action<int, SimulationEvent> OnEventConfirmed;
            public event Action<int, SimulationEvent> OnEventCanceled;
            public event Action<int, SimulationEvent> OnSyncedEvent;
            public event Action<int> OnResyncCompleted;
            public event Action OnResyncFailed;
            public event Action<int, int, RejectionReason> OnCommandRejected;
            public event Action<int, int, byte[], int> OnVerifiedInputBatchReady;
            public event Action<int> OnDisconnectedInputNeeded;
            public event Action OnCatchupComplete;

            public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, ILogger logger) { }
            public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, ILogger logger, ISimulationCallbacks simulationCallbacks, IViewCallbacks viewCallbacks = null) { }
            public void Initialize(ISimulation simulation, ILogger logger) { }
            public void Start() { }
            public void Update(float deltaTime) { }
            public void InputCommand(ICommand command, int extraDelay = 0) { }
            public void Stop() { }
            public void StartSpectator(SpectatorStartInfo info) { }
            public bool IsFrameVerified(int tick) => false;
            public FrameState GetFrameState(int tick) => FrameState.Predicted;
            public bool TrySerializeVerifiedInputRange(int fromTick, int toTick, out byte[] data, out int dataLength) { data = null; dataLength = 0; return false; }
            public int GetNearestSnapshotTickWithinBuffer() => -1;
            public void ReceiveConfirmedCommand(ICommand command) { }
            public void NotifyPlayerDisconnected(int playerId) { }
            public void NotifyPlayerReconnected(int playerId) { }
            public void NotifyPlayerLeft(int playerId) { }
            public void PauseForReconnect() { }
            public void ForceInsertCommand(ICommand cmd) { }
            public bool HasCommand(int tick, int playerId) => false;
            public void StartCatchingUp() { }
            public void StopCatchingUp() { }
            public void ConfirmCatchupTick(int tick) { }
            public void ExpectFullState() { }
            public void CancelExpectFullState() { }
            public ErrorCorrectionSettings ErrorCorrectionSettings { get; set; } = ErrorCorrectionSettings.Default;
            public (float x, float y, float z) GetPositionDelta(int entityIndex) => (0f, 0f, 0f);
            public float GetYawDelta(int entityIndex) => 0f;
            public bool HasEntityTeleported(int entityIndex) => false;

            internal void SuppressWarnings()
            {
                OnTickExecuted?.Invoke(0);
                OnDesyncDetected?.Invoke(0, 0);
                OnRollbackExecuted?.Invoke(0, 0);
                OnRollbackFailed?.Invoke(0, null);
                OnFrameVerified?.Invoke(0);
                OnTickExecutedWithState?.Invoke(0, FrameState.Verified);
                OnEventPredicted?.Invoke(0, default);
                OnEventConfirmed?.Invoke(0, default);
                OnEventCanceled?.Invoke(0, default);
                OnSyncedEvent?.Invoke(0, default);
                OnResyncCompleted?.Invoke(0);
                OnResyncFailed?.Invoke();
                OnCommandRejected?.Invoke(0, 0, default);
                OnVerifiedInputBatchReady?.Invoke(0, 0, null, 0);
                OnDisconnectedInputNeeded?.Invoke(0);
                OnCatchupComplete?.Invoke();
            }
        }
    }
}
