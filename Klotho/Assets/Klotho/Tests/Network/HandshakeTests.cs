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

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Handshake protocol test.
    /// Verifies the full SyncRequest/Reply/Complete handshake flow between Host and Client.
    /// </summary>
    [TestFixture]
    public class HandshakeTests
    {
        private TestTransport _hostTransport;
        private TestTransport _clientTransport;
        private KlothoNetworkService _hostService;
        private KlothoNetworkService _clientService;
        private CommandFactory _commandFactory;
        private MockKlothoEngine _hostEngine;
        private MockKlothoEngine _clientEngine;

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // LoggerFactory setup (same as ZLogger)
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });

            _logger = loggerFactory.CreateLogger("Tests");
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

        /// <summary>
        /// Calls PollEvents on both sides to process messages.
        /// The handshake takes 5 round trips, so iterate enough times.
        /// </summary>
        private void PumpMessages(int rounds = 12)
        {
            for (int i = 0; i < rounds; i++)
            {
                _hostService.Update();
                _clientService.Update();
            }
        }

        /// <summary>
        /// Runs Host CreateRoom + Client JoinRoom and completes the handshake.
        /// </summary>
        private void CompleteHandshake()
        {
            HostCreateRoom();
            ClientJoinRoom();
            PumpMessages();
        }

        /// <summary>
        /// Since _players is empty on the Client side (not created during handshake),
        /// sends the Client's PlayerReadyMessage directly to the Host.
        /// </summary>
        private void SendClientReadyToHost(int clientPlayerId, bool isReady)
        {
            var serializer = new MessageSerializer();
            var msg = new PlayerReadyMessage
            {
                PlayerId = clientPlayerId,
                IsReady = isReady
            };
            // Send via Client transport → Host
            _clientTransport.Send(0, serializer.Serialize(msg), DeliveryMethod.Reliable);
        }

        #endregion

        #region Normal handshake flow

        [Test]
        public void Handshake_HostPhase_StartsAsSynchronized()
        {
            HostCreateRoom();
            Assert.AreEqual(SessionPhase.Synchronized, _hostService.Phase);
        }

        [Test]
        public void Handshake_ClientPhase_StartsAsLobby()
        {
            HostCreateRoom();
            _clientService.JoinRoom("test");
            Assert.AreEqual(SessionPhase.Lobby, _clientService.Phase);
        }

        [Test]
        public void Handshake_HostPhase_SyncingOnPeerConnect()
        {
            HostCreateRoom();
            ClientJoinRoom();
            // PeerConnected fires → Host Syncing
            _hostTransport.PollEvents();
            Assert.AreEqual(SessionPhase.Syncing, _hostService.Phase);
        }

        [Test]
        public void Handshake_CompletesSuccessfully()
        {
            CompleteHandshake();

            Assert.AreEqual(SessionPhase.Synchronized, _hostService.Phase);
            Assert.AreEqual(SessionPhase.Synchronized, _clientService.Phase);
        }

        [Test]
        public void Handshake_HostHasTwoPlayers()
        {
            CompleteHandshake();

            Assert.AreEqual(2, _hostService.PlayerCount);
            Assert.AreEqual(0, _hostService.LocalPlayerId);
        }

        [Test]
        public void Handshake_ClientGetsPlayerId()
        {
            CompleteHandshake();

            Assert.AreEqual(1, _clientService.LocalPlayerId);
            Assert.IsFalse(_clientService.IsHost);
        }

        [Test]
        public void Handshake_HostIsHost()
        {
            HostCreateRoom();
            Assert.IsTrue(_hostService.IsHost);
        }

        [Test]
        public void Handshake_OnPlayerJoined_Fires()
        {
            HostCreateRoom();

            IPlayerInfo joinedPlayer = null;
            _hostService.OnPlayerJoined += p => joinedPlayer = p;

            ClientJoinRoom();
            PumpMessages();

            Assert.IsNotNull(joinedPlayer);
            Assert.AreEqual(1, joinedPlayer.PlayerId);
        }

        #endregion

        #region Magic mismatch

        [Test]
        public void Handshake_WrongMagic_SyncRequestIgnored()
        {
            HostCreateRoom();

            // Scenario where the Client sends a manual SyncReply with a Magic from another session.
            // If the Client only does JoinRoom and directly receives a SyncRequest with a wrong Magic, it ignores it.
            _clientService.JoinRoom("test");
            _clientTransport.Connect("localhost", 7777);

            // Run the normal handshake
            PumpMessages();

            // Verify Synchronized is reached
            Assert.AreEqual(SessionPhase.Synchronized, _hostService.Phase);
            Assert.AreEqual(SessionPhase.Synchronized, _clientService.Phase);

            // Now send a SyncComplete with wrong Magic directly to the Client
            var serializer = new MessageSerializer();
            var badMsg = new SyncCompleteMessage
            {
                Magic = 99999,  // wrong Magic
                PlayerId = 5,
                SharedEpoch = 100,
                ClockOffset = 0
            };
            // Manually inject into the Client side
            var data = serializer.Serialize(badMsg);
            // Send from Host to Client (peerId=1)
            _hostTransport.Send(1, data, DeliveryMethod.ReliableOrdered);
            _clientService.Update();

            // PlayerId must not change (wrong Magic is ignored)
            Assert.AreEqual(1, _clientService.LocalPlayerId);
        }

        #endregion

        #region Countdown → Game start

        [Test]
        public void Countdown_TriggersOnBothReady()
        {
            CompleteHandshake();

            long countdownStartTime = 0;
            _hostService.OnCountdownStarted += t => countdownStartTime = t;

            // Send Client Ready directly to Host (avoids the issue of _players not being created on the Client)
            SendClientReadyToHost(1, true);
            PumpMessages();

            // Before Host alone is Ready → not started yet (Client Ready received but Host not Ready)
            Assert.AreEqual(SessionPhase.Synchronized, _hostService.Phase);

            // Host Ready → AllPlayersReady → StartGame → Countdown
            _hostService.SetReady(true);
            PumpMessages();

            Assert.AreEqual(SessionPhase.Countdown, _hostService.Phase);
            Assert.Greater(countdownStartTime, 0);
        }

        [Test]
        public void Countdown_ClientReceivesGameStartMessage()
        {
            CompleteHandshake();

            long clientCountdownTime = 0;
            _clientService.OnCountdownStarted += t => clientCountdownTime = t;

            // Send Client Ready directly to Host
            SendClientReadyToHost(1, true);
            PumpMessages();

            // Host Ready → StartGame → broadcast GameStartMessage
            _hostService.SetReady(true);
            PumpMessages(); // PlayerReadyMessage

            PumpMessages(); // GameStartMessage

            Assert.AreEqual(SessionPhase.Countdown, _clientService.Phase);
            Assert.Greater(clientCountdownTime, 0);
        }

        #endregion

        #region Phase guard

        [Test]
        public void SyncComplete_IgnoredAfterCountdown()
        {
            CompleteHandshake();

            // Send Client Ready directly to Host → Host Ready → enter Countdown
            SendClientReadyToHost(1, true);
            PumpMessages();
            _hostService.SetReady(true);
            PumpMessages();

            Assert.AreEqual(SessionPhase.Countdown, _clientService.Phase);
            int originalPlayerId = _clientService.LocalPlayerId;

            // After Countdown, sending SyncComplete again must be ignored
            var serializer = new MessageSerializer();
            var lateMsg = new SyncCompleteMessage
            {
                Magic = GetSessionMagic(),
                PlayerId = 99,
                SharedEpoch = 999,
                ClockOffset = 0
            };
            _hostTransport.Send(1, serializer.Serialize(lateMsg), DeliveryMethod.ReliableOrdered);
            _clientService.Update();

            Assert.AreEqual(originalPlayerId, _clientService.LocalPlayerId,
                "SyncComplete must be ignored after Countdown");
        }

        #endregion

        #region Disconnect / LeaveRoom

        [Test]
        public void PeerDisconnect_HostReturnsToLobby()
        {
            CompleteHandshake();

            // Client disconnect
            _clientTransport.Disconnect();
            _hostService.Update();

            Assert.AreEqual(SessionPhase.Lobby, _hostService.Phase);
            Assert.AreEqual(1, _hostService.PlayerCount, "Only the Host itself should remain");
        }

        [Test]
        public void PeerDisconnect_OnPlayerLeft_Fires()
        {
            CompleteHandshake();

            IPlayerInfo leftPlayer = null;
            _hostService.OnPlayerLeft += p => leftPlayer = p;

            _clientTransport.Disconnect();
            _hostService.Update();

            Assert.IsNotNull(leftPlayer);
            Assert.AreEqual(1, leftPlayer.PlayerId);
        }

        [Test]
        public void LeaveRoom_ClearsAllState()
        {
            CompleteHandshake();

            _hostService.LeaveRoom();

            Assert.AreEqual(SessionPhase.Disconnected, _hostService.Phase);
            Assert.AreEqual(0, _hostService.PlayerCount);
        }

        [Test]
        public void LeaveRoom_UnsubscribesTransportEvents()
        {
            CompleteHandshake();
            _hostService.LeaveRoom();

            // After LeaveRoom, transport events must be ignored without exceptions
            Assert.DoesNotThrow(() =>
            {
                _clientTransport.Disconnect();
                _hostTransport.PollEvents();
            });
        }

        #endregion

        #region Message serialization roundtrip

        [Test]
        public void SyncRequestMessage_SerializeRoundtrip()
        {
            var original = new SyncRequestMessage
            {
                Magic = 12345,
                Sequence = 3,
                Attempt = 2,
                HostTime = 1700000000000L
            };

            var buf = new byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var deserialized = new SyncRequestMessage();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            deserialized.Deserialize(ref reader);

            Assert.AreEqual(original.Magic, deserialized.Magic);
            Assert.AreEqual(original.Sequence, deserialized.Sequence);
            Assert.AreEqual(original.Attempt, deserialized.Attempt);
            Assert.AreEqual(original.HostTime, deserialized.HostTime);
        }

        [Test]
        public void SyncReplyMessage_SerializeRoundtrip()
        {
            var original = new SyncReplyMessage
            {
                Magic = 54321,
                Sequence = 4,
                Attempt = 1,
                ClientTime = 1700000001000L
            };

            var buf = new byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var deserialized = new SyncReplyMessage();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            deserialized.Deserialize(ref reader);

            Assert.AreEqual(original.Magic, deserialized.Magic);
            Assert.AreEqual(original.Sequence, deserialized.Sequence);
            Assert.AreEqual(original.Attempt, deserialized.Attempt);
            Assert.AreEqual(original.ClientTime, deserialized.ClientTime);
        }

        [Test]
        public void SyncCompleteMessage_SerializeRoundtrip()
        {
            var original = new SyncCompleteMessage
            {
                Magic = 99999,
                PlayerId = 7,
                SharedEpoch = 1700000000000L,
                ClockOffset = -150
            };

            var buf = new byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var deserialized = new SyncCompleteMessage();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            deserialized.Deserialize(ref reader);

            Assert.AreEqual(original.Magic, deserialized.Magic);
            Assert.AreEqual(original.PlayerId, deserialized.PlayerId);
            Assert.AreEqual(original.SharedEpoch, deserialized.SharedEpoch);
            Assert.AreEqual(original.ClockOffset, deserialized.ClockOffset);
        }

        [Test]
        public void GameStartMessage_SerializeRoundtrip()
        {
            var original = new GameStartMessage
            {
                RandomSeed = 42,
                StartTime = 1700000003000L,
                PlayerIds = new List<int> { 0, 1, 2 }
            };

            var buf = new byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buf);
            original.Serialize(ref writer);
            var deserialized = new GameStartMessage();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            deserialized.Deserialize(ref reader);

            Assert.AreEqual(original.RandomSeed, deserialized.RandomSeed);
            Assert.AreEqual(original.StartTime, deserialized.StartTime);
            Assert.AreEqual(original.PlayerIds.Count, deserialized.PlayerIds.Count);
            for (int i = 0; i < original.PlayerIds.Count; i++)
                Assert.AreEqual(original.PlayerIds[i], deserialized.PlayerIds[i]);
        }

        [Test]
        public void MessageSerializer_DeserializesCorrectType()
        {
            var serializer = new MessageSerializer();

            var syncReq = new SyncRequestMessage { Magic = 1, Sequence = 0, Attempt = 0, HostTime = 100 };
            byte[] data = serializer.Serialize(syncReq);
            var result = serializer.Deserialize(data);

            Assert.IsInstanceOf<SyncRequestMessage>(result);
            Assert.AreEqual(1, ((SyncRequestMessage)result).Magic);
        }

        [Test]
        public void MessageSerializer_NullOrEmpty_ReturnsNull()
        {
            var serializer = new MessageSerializer();

            Assert.IsNull(serializer.Deserialize(null));
            Assert.IsNull(serializer.Deserialize(new byte[0]));
        }

        [Test]
        public void MessageSerializer_UnknownType_ReturnsNull()
        {
            var serializer = new MessageSerializer();

            // unregistered type byte
            Assert.IsNull(serializer.Deserialize(new byte[] { 255 }));
        }

        #endregion

        #region Helpers (private)

        /// <summary>
        /// Indirectly extracts the Host's _sessionMagic after handshake completion.
        /// Since the Client echoes the Magic it received in SyncRequest back via SyncReply,
        /// the Magic in the SyncComplete delivered to the Client is the session magic.
        /// This avoids using an arbitrary value and extracts the value actually used in the handshake.
        /// </summary>
        private long GetSessionMagic()
        {
            // Instead of creating a separate transport to capture the SyncComplete sent by the Host,
            // or sending a new SyncRequest after handshake completion to discover the Magic,
            // and instead of simply connecting a new Client to re-handshake with the Host to extract the Magic,
            // → here we extract via reflection because the test needs the actual Magic value
            var field = typeof(KlothoNetworkService).GetField("_sessionMagic",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (long)field.GetValue(_hostService);
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
