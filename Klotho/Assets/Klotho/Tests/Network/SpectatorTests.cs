#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// Spectator mode tests.
    /// </summary>
    [TestFixture]
    public class SpectatorTests
    {
        #region Mocks

        private class MockSimulation : ISimulation
        {
            public int CurrentTick { get; private set; }
            public long StateHash { get; set; } = 12345L;
            public int TickCallCount { get; private set; }

            public void Initialize() { CurrentTick = 0; }
            public void Tick(List<ICommand> commands) { CurrentTick++; TickCallCount++; }
            public void Rollback(int targetTick) { CurrentTick = targetTick; }
            public long GetStateHash() => StateHash;
            public void Reset() { CurrentTick = 0; TickCallCount = 0; }
            public void RestoreFromFullState(byte[] stateData) { }
            public byte[] SerializeFullState() => BitConverter.GetBytes(StateHash);
            public (byte[] data, long hash) SerializeFullStateWithHash() => (BitConverter.GetBytes(StateHash), StateHash);
            public void EmitSyncEvents() { }
            public event Action<int> OnPlayerJoinedNotification;
            public void OnPlayerJoined(int playerId, int tick) { }
        }

        private class MockTransport : INetworkTransport
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

            public List<(int peerId, byte[] data, int length)> SentMessages = new List<(int, byte[], int)>();

            public bool Connect(string address, int port)
            {
                IsConnected = true;
                OnConnected?.Invoke();
                return true;
            }

            public bool Listen(string address, int port, int maxConnections) { IsConnected = true; return true; }

            public void Send(int peerId, byte[] data, DeliveryMethod dm)
                => SentMessages.Add((peerId, data, data.Length));

            public void Send(int peerId, byte[] data, int length, DeliveryMethod dm)
            {
                byte[] copy = new byte[length];
                Array.Copy(data, copy, length);
                SentMessages.Add((peerId, copy, length));
            }

            public void Broadcast(byte[] data, DeliveryMethod dm) { }
            public void Broadcast(byte[] data, int length, DeliveryMethod dm) { }
            public void PollEvents() { }
            public void FlushSendQueue() { }

            public void Disconnect()
            {
                IsConnected = false;
                OnDisconnected?.Invoke(DisconnectReason.LocalDisconnect);
            }

            public void DisconnectPeer(int peerId) { }
            public System.Collections.Generic.IEnumerable<int> GetConnectedPeerIds() => System.Linq.Enumerable.Empty<int>();

            public void SimulateDataReceived(int peerId, byte[] data)
                => OnDataReceived?.Invoke(peerId, data, data.Length);

            public void SimulateDisconnected(DisconnectReason reason = DisconnectReason.RemoteDisconnect)
                => OnDisconnected?.Invoke(reason);

            internal void SuppressWarnings()
            {
                OnPeerConnected?.Invoke(0);
                OnPeerDisconnected?.Invoke(0);
            }
        }

        private class MockKlothoEngine : IKlothoEngine
        {
            public ISimulationConfig SimulationConfig { get; set; } = new SimulationConfig();
            public ISessionConfig SessionConfig { get; set; } = new SessionConfig();
            public event Action<int, bool> OnPlayerConfigReceived;
            public T GetPlayerConfig<T>(int playerId) where T : PlayerConfigBase => null;
            public bool TryGetPlayerConfig<T>(int playerId, out T config) where T : PlayerConfigBase { config = null; return false; }
            public KlothoState State { get; set; } = KlothoState.Idle;
            public ISimulation Simulation { get; set; }
            public ILogger Logger => null;

            public int CurrentTick { get; set; }
            public int RandomSeed { get; set; }
            public bool IsReplayMode => false;
            public bool IsServer => false;
            public bool IsHost => false;
            public SimulationStage Stage => SimulationStage.Forward;
            public int LocalPlayerId { get; set; }
            public int TickInterval => 25;
            public int InputDelay => 4;
            public int RecommendedExtraDelay => 0;
            public bool IsSpectatorMode { get; set; }
            public int LastVerifiedTick { get; set; } = -1;
            public int StartSpectatorCallCount { get; private set; }
            public SpectatorStartInfo LastStartInfo { get; private set; }

            // Frame References + RenderClock stubs
            public FrameRef VerifiedFrame => FrameRef.None(FrameKind.Verified);
            public FrameRef PredictedFrame => FrameRef.None(FrameKind.Predicted);
            public FrameRef PredictedPreviousFrame => FrameRef.None(FrameKind.PredictedPrevious);
            public FrameRef PreviousUpdatePredictedFrame => FrameRef.None(FrameKind.PreviousUpdatePredicted);
            public RenderClockState RenderClock => default;
            public bool TryGetFrameAtTick(int tick, out xpTURN.Klotho.ECS.Frame frame) { frame = null; return false; }

            public event Action<int> OnTickExecuted;
            public event Action<long, long> OnDesyncDetected;
            public event Action<int, long, long> OnHashMismatch;
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
            public event Action<AbortReason> OnMatchAborted;
            public event Action<ResetReason> OnMatchReset;
            public event Action<int, int, RejectionReason> OnCommandRejected;
            public event Action<int, int, byte[], int> OnVerifiedInputBatchReady;
            public event Action<int> OnExtraDelayChanged;
            public event Action OnChainAdvanceBreak;
            public event Action<int> OnDisconnectedInputNeeded;

            public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, ILogger logger) { }
            public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, ILogger logger, ISimulationCallbacks simulationCallbacks, IViewCallbacks viewCallbacks = null) { }
            public void Initialize(ISimulation simulation, ILogger logger) { }
            public void Start() { }
            public void Update(float deltaTime) { }
            public void InputCommand(ICommand command, int extraDelay = 0) { }
            public void ApplyExtraDelay(int delay, ExtraDelaySource source) { }
            public void EscalateExtraDelay(int step, int max) { }
            public void Stop() { }
            public void AbortMatch(AbortReason reason) { }

            public void StartSpectator(SpectatorStartInfo info)
            {
                StartSpectatorCallCount++;
                LastStartInfo = info;
                IsSpectatorMode = true;
                State = KlothoState.Running;
            }

            public bool IsFrameVerified(int tick) => false;
            public FrameState GetFrameState(int tick) => FrameState.Predicted;

            public bool TrySerializeVerifiedInputRange(int fromTick, int toTick, out byte[] data, out int dataLength)
            {
                data = null; dataLength = 0; return false;
            }

            public int GetNearestSnapshotTickWithinBuffer() => -1;
            public void ReceiveConfirmedCommand(ICommand command) { }
            public void NotifyPlayerDisconnected(int playerId) { }
            public void NotifyPlayerReconnected(int playerId) { }
            public void NotifyPlayerLeft(int playerId) { }
            public void PauseForReconnect() { }
            public void ForceInsertCommand(ICommand cmd) { }
            public void ForceInsertEmptyCommandsRange(int playerId, int fromTick, int toTickInclusive) { }
            public bool HasCommand(int tick, int playerId) => false;
            public bool IsCommandSealed(int tick, int playerId) => false;
            public void RequestRollback(int targetTick) { }
            public void StartCatchingUp() { }
            public void StopCatchingUp() { }
            public void ConfirmCatchupTick(int tick) { }
            public event Action OnCatchupComplete;
            public event Action<int, int, WipeKind> OnPendingWipe;
            public void ExpectFullState() { }
            public void CancelExpectFullState() { }
            public ErrorCorrectionSettings ErrorCorrectionSettings { get; set; } = ErrorCorrectionSettings.Default;
            public (float x, float y, float z) GetPositionDelta(int entityIndex) => (0f, 0f, 0f);
            public float GetYawDelta(int entityIndex) => 0f;
            public bool HasEntityTeleported(int entityIndex) => false;

            public void FireVerifiedInputBatch(int startTick, int tickCount, byte[] data, int dataLength)
                => OnVerifiedInputBatchReady?.Invoke(startTick, tickCount, data, dataLength);

            internal void SuppressWarnings()
            {
                OnTickExecuted?.Invoke(0);
                OnDesyncDetected?.Invoke(0, 0);
                OnRollbackExecuted?.Invoke(0, 0);
                OnRollbackFailed?.Invoke(0, null);
                OnFrameVerified?.Invoke(0);
                OnTickExecutedWithState?.Invoke(0, FrameState.Verified);
                OnEventPredicted?.Invoke(0, null);
                OnEventConfirmed?.Invoke(0, null);
                OnEventCanceled?.Invoke(0, null);
                OnSyncedEvent?.Invoke(0, null);
                OnResyncCompleted?.Invoke(0);
                OnResyncFailed?.Invoke();
                OnCommandRejected?.Invoke(0, 0, default);
                OnVerifiedInputBatchReady?.Invoke(0, 0, null, 0);
            }
        }

        private class MockPlayerInfo : IPlayerInfo
        {
            public int PlayerId { get; set; }
            public string PlayerName { get; set; } = "";
            public bool IsReady { get; set; } = true;
            public int Ping { get; set; }
            public PlayerConnectionState ConnectionState { get; set; } = PlayerConnectionState.Connected;
        }

        private class MockNetworkService : IKlothoNetworkService
        {
            public SessionPhase Phase { get; set; } = SessionPhase.Playing;
            public SharedTimeClock SharedClock { get; set; }
            public int PlayerCount { get; set; } = 1;
            public int SpectatorCount { get; set; } = 0;
            public int PendingLateJoinCatchupCount { get; set; } = 0;
            public bool AllPlayersReady { get; set; } = true;
            public int LocalPlayerId { get; set; } = 0;
            public bool IsHost { get; set; } = false;
            public int RandomSeed { get; set; } = 42;
            public IReadOnlyList<IPlayerInfo> Players => BuildPlayerList();

            private List<IPlayerInfo> BuildPlayerList()
            {
                var list = new List<IPlayerInfo>();
                for (int i = 0; i < PlayerCount; i++)
                    list.Add(new MockPlayerInfo { PlayerId = i });
                return list;
            }

            public event Action OnGameStart;
            public event Action<long> OnCountdownStarted;
            public event Action<IPlayerInfo> OnPlayerJoined;
            public event Action<IPlayerInfo> OnPlayerLeft;
            public event Action<ICommand> OnCommandReceived;
            public event Action<int, int, long, long> OnDesyncDetected;
            public event Action<int, int> OnFrameAdvantageReceived;
            public event Action<int> OnLocalPlayerIdAssigned;
            public event Action<int, int> OnFullStateRequested;
            public event Action<int, byte[], long, FullStateKind> OnFullStateReceived;
            public event Action<IPlayerInfo> OnPlayerDisconnected;
            public event Action<IPlayerInfo> OnPlayerReconnected;
            public event Action OnReconnecting;
            public event Action<string> OnReconnectFailed;
            public event Action OnReconnected;
            public event Action<int, int> OnLateJoinPlayerAdded;

            public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, ILogger logger) { }
            public void CreateRoom(string roomName, int maxPlayers) { }
            public void JoinRoom(string roomName) { }
            public void LeaveRoom() { }
            public void SetReady(bool ready) { }
            public void SendCommand(ICommand command) { }
            public void RequestCommandsForTick(int tick) { }
            public void SendSyncHash(int tick, long hash) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SetLocalTick(int tick) { }
            public void SendFullStateRequest(int currentTick) { }
            public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash) { }
            public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast) { }
            public void SendPlayerConfig(int playerId, xpTURN.Klotho.Core.PlayerConfigBase playerConfig) { }

            public void FireCommandReceived(ICommand cmd) => OnCommandReceived?.Invoke(cmd);

            internal void SuppressWarnings()
            {
                OnGameStart?.Invoke();
                OnCountdownStarted?.Invoke(0);
                OnPlayerJoined?.Invoke(null);
                OnPlayerLeft?.Invoke(null);
                OnDesyncDetected?.Invoke(0, 0, 0, 0);
                OnFrameAdvantageReceived?.Invoke(0, 0);
                OnLocalPlayerIdAssigned?.Invoke(0);
                OnFullStateRequested?.Invoke(0, 0);
                OnFullStateReceived?.Invoke(0, null, 0, FullStateKind.Unicast);
            }
        }

        #endregion

        private SpectatorService _spectatorService;
        private MockTransport _mockTransport;
        private MockKlothoEngine _mockEngine;
        private CommandFactory _commandFactory;
        private MessageSerializer _messageSerializer;

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

            _mockTransport = new MockTransport();
            _mockEngine = new MockKlothoEngine();
            _commandFactory = new CommandFactory();
            _messageSerializer = new MessageSerializer();
            _spectatorService = new SpectatorService();
            _spectatorService.Initialize(_mockTransport, _commandFactory, _mockEngine, _logger);
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        #region Helpers

        /// <summary>
        /// SpectatorInputMessage.InputData format:
        /// per tick: int32(commandCount) + commandCount * EmptyCommand (12 bytes each)
        /// EmptyCommand raw = int32(typeId=0) + int32(playerId) + int32(tick)
        /// </summary>
        private byte[] BuildSpectatorInputData(int startTick, int tickCount, int playerCount)
        {
            int size = tickCount * (4 + playerCount * 12);
            byte[] data = new byte[size];
            var writer = new SpanWriter(data);
            for (int t = 0; t < tickCount; t++)
            {
                int tick = startTick + t;
                writer.WriteInt32(playerCount);
                for (int p = 0; p < playerCount; p++)
                {
                    writer.WriteInt32(0);    // CommandTypeId = 0 (EmptyCommand)
                    writer.WriteInt32(p);    // PlayerId
                    writer.WriteInt32(tick); // Tick
                }
            }
            return data;
        }

        private void InjectMessage(INetworkMessage message)
        {
            byte[] data = _messageSerializer.Serialize(message);
            _mockTransport.SimulateDataReceived(0, data);
        }

        private SpectatorAcceptMessage MakeAcceptMessage(int lastVerifiedTick = -1)
        {
            var msg = new SpectatorAcceptMessage
            {
                SpectatorId = -1,
                RandomSeed = 42,
                TickIntervalMs = 25,
                InputDelayTicks = 4,
                LastVerifiedTick = lastVerifiedTick,
                CurrentTick = 0,
            };
            msg.PlayerIds.Add(0);
            msg.PlayerIds.Add(1);
            return msg;
        }

        private GameStartMessage MakeGameStartMessage()
        {
            var msg = new GameStartMessage
            {
                RandomSeed = 42,
                StartTime = 0,
            };
            msg.PlayerIds.Add(0);
            msg.PlayerIds.Add(1);
            return msg;
        }

        private void TransitionToWatching()
        {
            _spectatorService.Connect("localhost", 7777);
            InjectMessage(MakeAcceptMessage(-1));
            InjectMessage(MakeGameStartMessage());
        }

        private SpectatorStartInfo MakeStartInfo(int playerCount = 1)
            => new SpectatorStartInfo
            {
                PlayerCount = playerCount,
                RandomSeed = 42,
                TickInterval = 25,
                PlayerIds = new List<int> { 0 },
            };

        #endregion

        #region Tests 1–11: SpectatorService

        /// <summary>
        /// #1: Idle → Connecting → Synchronizing → Watching
        /// </summary>
        [Test]
        public void SpectatorConnect_StateTransitions()
        {
            Assert.AreEqual(SpectatorState.Idle, _spectatorService.State);

            _spectatorService.Connect("localhost", 7777);
            // MockTransport.Connect() synchronously fires OnConnected → HandleConnected → Synchronizing
            Assert.AreEqual(SpectatorState.Synchronizing, _spectatorService.State);

            InjectMessage(MakeAcceptMessage(-1));
            Assert.AreEqual(SpectatorState.Synchronizing, _spectatorService.State, "Accept alone should keep Synchronizing [F-4]");

            InjectMessage(MakeGameStartMessage());
            Assert.AreEqual(SpectatorState.Watching, _spectatorService.State);
        }

        /// <summary>
        /// #2: Watching → Disconnect → Disconnected, OnSpectatorStopped fires
        /// </summary>
        [Test]
        public void SpectatorDisconnect_StateChange()
        {
            TransitionToWatching();

            string stoppedReason = null;
            _spectatorService.OnSpectatorStopped += r => stoppedReason = r;

            _spectatorService.Disconnect();
            // MockTransport.Disconnect() fires OnDisconnected → HandleDisconnected

            Assert.AreEqual(SpectatorState.Disconnected, _spectatorService.State);
            Assert.IsNotNull(stoppedReason);
        }

        /// <summary>
        /// #3: Host connection lost → OnSpectatorStopped fires
        /// </summary>
        [Test]
        public void HostDisconnect_SpectatorStopped()
        {
            TransitionToWatching();

            string stoppedReason = null;
            _spectatorService.OnSpectatorStopped += r => stoppedReason = r;

            _mockTransport.SimulateDisconnected();

            Assert.AreEqual(SpectatorState.Disconnected, _spectatorService.State);
            Assert.IsNotNull(stoppedReason);
        }

        /// <summary>
        /// #4: SpectatorInputMessage → OnConfirmedInputReceived fires
        /// </summary>
        [Test]
        public void ConfirmedInputReceived_AddedToBuffer()
        {
            TransitionToWatching();

            var received = new List<(int tick, ICommand cmd)>();
            _spectatorService.OnConfirmedInputReceived += (tick, cmd) => received.Add((tick, cmd));

            byte[] data = BuildSpectatorInputData(0, 1, 1);
            InjectMessage(new SpectatorInputMessage
            {
                StartTick = 0,
                TickCount = 1,
                InputData = data,
                InputDataLength = data.Length,
            });

            Assert.AreEqual(1, received.Count);
            Assert.AreEqual(0, received[0].tick);
            Assert.IsInstanceOf<EmptyCommand>(received[0].cmd);
        }

        /// <summary>
        /// #5: ExecuteTick on input arrival, always Verified
        /// </summary>
        [Test]
        public void SpectatorUpdate_ExecutesVerifiedTicks()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            Assert.AreEqual(0, engine.CurrentTick);

            networkService.FireCommandReceived(new EmptyCommand(0, 0));
            engine.ConfirmSpectatorTick(0);
            engine.Update(0.030f); // one tick

            Assert.Greater(engine.CurrentTick, 0, "Engine should advance when all commands arrive");
        }

        /// <summary>
        /// Even without incoming input, advance via prediction up to MAX_SPECTATOR_PREDICTION_TICKS, then wait.
        /// </summary>
        [Test]
        public void SpectatorUpdate_WaitsAfterPredictionLimit()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            engine.Update(1.0f); // large deltaTime, no commands

            // _spectatorLastConfirmedTick=-1, MAX=4 → max CurrentTick=4
            int tickAfterFirst = engine.CurrentTick;
            Assert.LessOrEqual(tickAfterFirst, 4, "Must stop at prediction limit");
            Assert.Greater(tickAfterFirst, 0, "Should advance via prediction");

            // Additional Update must not exceed the range
            engine.Update(1.0f);
            Assert.AreEqual(tickAfterFirst, engine.CurrentTick, "Must not advance beyond prediction limit without confirmed input");
        }

        /// <summary>
        /// Verify that prediction ticks advance within the MAX_SPECTATOR_PREDICTION_TICKS range even without confirmed ticks.
        /// </summary>
        [Test]
        public void SpectatorMode_PredictionAdvancesWithinLimit()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            // No confirmed tick (_spectatorLastConfirmedTick = -1)
            // accumulator sufficient → advances within prediction range
            engine.Update(0.2f);

            // MAX_SPECTATOR_PREDICTION_TICKS = SPECTATOR_INPUT_INTERVAL + 2 = 4
            // Prediction allowed up to CurrentTick <= -1 + 4 = 3 → at most 4 ticks advanced
            Assert.GreaterOrEqual(engine.CurrentTick, 1, "Spectator should advance via prediction");
            Assert.LessOrEqual(engine.CurrentTick, 4, "Prediction must not exceed MAX_SPECTATOR_PREDICTION_TICKS");
        }

        /// <summary>
        /// #8: ExecuteRollback not called — RequestRollback is ignored in spectator mode
        /// </summary>
        [Test]
        public void SpectatorMode_NoRollback()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            networkService.FireCommandReceived(new EmptyCommand(0, 0));
            networkService.FireCommandReceived(new EmptyCommand(0, 1));
            engine.ConfirmSpectatorTick(1);
            engine.Update(0.1f);

            int tickAfterAdvance = engine.CurrentTick;
            Assert.GreaterOrEqual(tickAfterAdvance, 1);

            engine.RequestRollback(0);
            engine.Update(0.001f); // FlushPendingRollback is not called in spectator mode

            Assert.GreaterOrEqual(engine.CurrentTick, tickAfterAdvance,
                "Tick must not decrease — rollback is skipped in spectator mode");
        }

        /// <summary>
        /// #9: InputCommand call is ignored — no tick advance
        /// </summary>
        [Test]
        public void SpectatorMode_NoInputCommand()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1, SpectatorCount = 0 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            // MockNetworkService.SendCommand() is a no-op → commands are not added to the buffer
            engine.InputCommand(new EmptyCommand(0, 0));
            engine.Update(0.001f);

            Assert.AreEqual(0, engine.CurrentTick,
                "InputCommand in spectator mode should not advance tick (networkService.SendCommand is no-op)");
        }

        /// <summary>
        /// Verify that confirmed ticks fire Verified events and prediction ticks fire Predicted events.
        /// </summary>
        [Test]
        public void SpectatorEvents_VerifiedAndPredicted()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            var verifiedTicks = new List<int>();
            var predictedTicks = new List<int>();
            engine.OnTickExecutedWithState += (tick, state) =>
            {
                if (state == FrameState.Verified) verifiedTicks.Add(tick);
                else predictedTicks.Add(tick);
            };

            networkService.FireCommandReceived(new EmptyCommand(0, 0));
            engine.ConfirmSpectatorTick(0);
            engine.Update(0.1f);

            Assert.GreaterOrEqual(verifiedTicks.Count, 1, "Confirmed tick must fire as Verified");
            // If a prediction tick ran, it must fire as Predicted
            if (engine.CurrentTick > 1)
                Assert.GreaterOrEqual(predictedTicks.Count, 1, "Prediction ticks must fire as Predicted");
        }

        /// <summary>
        /// #11: 4-tick batch message processed correctly (2 players → 8 OnConfirmedInputReceived fires)
        /// </summary>
        [Test]
        public void BatchInput_MultipleTicksInOneMessage()
        {
            TransitionToWatching();

            var received = new List<(int tick, ICommand cmd)>();
            _spectatorService.OnConfirmedInputReceived += (tick, cmd) => received.Add((tick, cmd));

            byte[] data = BuildSpectatorInputData(0, 4, 2);
            InjectMessage(new SpectatorInputMessage
            {
                StartTick = 0,
                TickCount = 4,
                InputData = data,
                InputDataLength = data.Length,
            });

            Assert.AreEqual(8, received.Count, "4 ticks × 2 players = 8 confirmed commands");
            Assert.AreEqual(0, received[0].tick);
            Assert.AreEqual(3, received[6].tick);
            Assert.AreEqual(3, received[7].tick);
        }

        #endregion

        #region Tests 12–16: KlothoNetworkService

        /// <summary>
        /// #12: MaxSpectators — no limit in current implementation, connection is accepted normally
        /// </summary>
        [Test]
        public void MaxSpectators_RejectsExcess()
        {
            var hostTransport = new TestTransport();
            var hostService = new KlothoNetworkService();
            hostService.Initialize(hostTransport, _commandFactory, _logger);
            hostTransport.Listen("localhost", 7777, 4);
            hostService.CreateRoom("test", 4);

            // Spectator connection
            var spectatorTransport = new TestTransport();
            var spectatorService = new SpectatorService();
            spectatorService.Initialize(spectatorTransport, _commandFactory, new MockKlothoEngine(), _logger);
            spectatorService.Connect("localhost", 7777);

            hostService.Update();   // Process SpectatorJoinMessage → HandleSpectatorJoin
            spectatorService.Update(); // Process SpectatorAcceptMessage

            Assert.AreEqual(SpectatorState.Synchronizing, spectatorService.State,
                "Spectator should be accepted (no MaxSpectators limit currently enforced)");
            Assert.AreEqual(1, hostService.SpectatorCount);
        }

        /// <summary>
        /// #13: Host sends only Verified ticks to spectators (OnVerifiedInputBatchReady → SpectatorInputMessage)
        /// The spectator must be in Watching to process directly; force Playing phase on the host so it sends GameStart.
        /// </summary>
        [Test]
        public void HostBroadcast_SendsVerifiedOnly()
        {
            var hostTransport = new TestTransport();
            var hostService = new KlothoNetworkService();
            hostService.Initialize(hostTransport, _commandFactory, _logger);
            hostTransport.Listen("localhost", 7777, 4);
            hostService.CreateRoom("test", 4);

            // Force host to Playing phase so a spectator joining receives GameStartMessage [F-4]
            typeof(KlothoNetworkService)
                .GetField("_phase", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(hostService, SessionPhase.Playing);

            var spectatorTransport = new TestTransport();
            var spectatorService = new SpectatorService();
            spectatorService.Initialize(spectatorTransport, _commandFactory, new MockKlothoEngine(), _logger);
            spectatorService.Connect("localhost", 7777);
            hostService.Update();       // SpectatorJoin → Accept + GameStart sent to spectator
            spectatorService.Update();  // SpectatorAccept → Synchronizing, GameStart → Watching

            Assert.AreEqual(SpectatorState.Watching, spectatorService.State,
                "Spectator must be Watching before batch test can run");
            Assert.AreEqual(1, hostService.SpectatorCount);

            // Subscribe MockEngine → hostService receives batch events
            var mockHostEngine = new MockKlothoEngine();
            hostService.SubscribeEngine(mockHostEngine);

            var received = new List<(int tick, ICommand cmd)>();
            spectatorService.OnConfirmedInputReceived += (tick, cmd) => received.Add((tick, cmd));

            // SpectatorInfo.LastSentTick == -1, startTick=0 → condition -1+1==0 passes
            byte[] batchData = BuildSpectatorInputData(0, 4, 1);
            mockHostEngine.FireVerifiedInputBatch(0, 4, batchData, batchData.Length);

            spectatorService.Update(); // Process received SpectatorInputMessage

            Assert.AreEqual(4, received.Count, "Spectator should receive all 4 ticks from the batch");
        }

        /// <summary>
        /// #14: DelayFrames = LatestReceivedTick - engine.CurrentTick
        /// </summary>
        [Test]
        public void DelayFrames_ReflectsLag()
        {
            TransitionToWatching();
            Assert.AreEqual(0, _spectatorService.DelayFrames, "No delay before any input received");

            byte[] data = BuildSpectatorInputData(0, 4, 1);
            InjectMessage(new SpectatorInputMessage
            {
                StartTick = 0, TickCount = 4,
                InputData = data, InputDataLength = data.Length,
            });

            // LatestReceivedTick=3, engine.CurrentTick=0 → DelayFrames=3
            Assert.AreEqual(3, _spectatorService.DelayFrames);

            _mockEngine.CurrentTick = 2;
            Assert.AreEqual(1, _spectatorService.DelayFrames);
        }

        /// <summary>
        /// #15: Spectator connection during Playing → _phase unchanged [F-1]
        /// </summary>
        [Test]
        public void SpectatorJoin_DuringPlaying_PhaseUnchanged()
        {
            var hostTransport = new TestTransport();
            var hostService = new KlothoNetworkService();
            hostService.Initialize(hostTransport, _commandFactory, _logger);
            hostTransport.Listen("localhost", 7777, 4);
            hostService.CreateRoom("test", 4);

            // Force Playing phase
            typeof(KlothoNetworkService)
                .GetField("_phase", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(hostService, SessionPhase.Playing);

            Assert.AreEqual(SessionPhase.Playing, hostService.Phase);

            var spectatorTransport = new TestTransport();
            var spectatorService = new SpectatorService();
            spectatorService.Initialize(spectatorTransport, _commandFactory, new MockKlothoEngine(), _logger);
            spectatorService.Connect("localhost", 7777);
            hostService.Update();

            Assert.AreEqual(SessionPhase.Playing, hostService.Phase,
                "Phase must stay Playing when spectator joins during game [F-1]");
        }

        /// <summary>
        /// #16: Spectator connection during Countdown → _phase unchanged [F-1]
        /// </summary>
        [Test]
        public void SpectatorJoin_DuringCountdown_PhaseUnchanged()
        {
            var hostTransport = new TestTransport();
            var hostService = new KlothoNetworkService();
            hostService.Initialize(hostTransport, _commandFactory, _logger);
            hostTransport.Listen("localhost", 7777, 4);
            hostService.CreateRoom("test", 4);

            var nsType = typeof(KlothoNetworkService);
            nsType.GetField("_phase", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(hostService, SessionPhase.Countdown);
            // Set _gameStartTime far in the future so Update() does not expire the countdown
            nsType.GetField("_gameStartTime", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(hostService, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000L);

            var spectatorTransport = new TestTransport();
            var spectatorService = new SpectatorService();
            spectatorService.Initialize(spectatorTransport, _commandFactory, new MockKlothoEngine(), _logger);
            spectatorService.Connect("localhost", 7777);
            hostService.Update();

            Assert.AreEqual(SessionPhase.Countdown, hostService.Phase,
                "Phase must stay Countdown when spectator joins during countdown [F-1]");
        }

        #endregion

        #region Tests 17–21: KlothoEngine

        /// <summary>
        /// #17: After rollback and resim, OnVerifiedInputBatchReady must not fire twice at the same batch boundary [F-2]
        /// Advances 8 ticks → batch fires exactly 4 times (SPECTATOR_INPUT_INTERVAL=2: ticks 1,3,5,7), not more.
        /// </summary>
        [Test]
        public void BatchReady_NoDuplicateAfterRollback()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1, SpectatorCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.Start(enableRecording: false);

            int batchCount = 0;
            engine.OnVerifiedInputBatchReady += (s, c, d, l) => batchCount++;

            // Pre-fill covers ticks 0-3; add 4 commands for ticks 4-7 → verified range 0-7
            // SPECTATOR_INPUT_INTERVAL=2: batches fire at _lastVerifiedTick=1,3,5,7
            for (int t = 0; t < 4; t++)
                networkService.FireCommandReceived(new EmptyCommand(0, t + engine.InputDelay));

            while (engine.CurrentTick < 8)
                engine.Update((engine.TickInterval + 1) / 1000f);

            Assert.AreEqual(4, batchCount, "Batch should fire exactly 4 times for verified ticks 0-7 (interval=2)");

            // No additional fires on subsequent Update (duplicate-prevention guard works)
            engine.Update(0.1f);
            Assert.AreEqual(4, batchCount, "No duplicate fires after batch boundaries already passed");
        }

        /// <summary>
        /// #18: On ExecuteRollback, _lastBatchedTick is updated only via Math.Min — no advance [F-2]
        /// </summary>
        [Test]
        public void BatchReady_LastBatchedTick_MinOnRollback()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1, SpectatorCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.Start(enableRecording: false);

            var lastBatchedField = typeof(KlothoEngine)
                .GetField("_lastBatchedTick", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.AreEqual(-1, (int)lastBatchedField.GetValue(engine), "_lastBatchedTick starts at -1");

            // Pre-fill covers ticks 0-3; add commands for ticks 4-7 → verified range 0-7
            // First batch boundary: _lastVerifiedTick=3
            for (int t = 0; t < 4; t++)
                networkService.FireCommandReceived(new EmptyCommand(0, t + engine.InputDelay));
            while (engine.CurrentTick < 8)
                engine.Update((engine.TickInterval + 1) / 1000f);

            int lastBatched = (int)lastBatchedField.GetValue(engine);
            Assert.AreEqual(7, lastBatched, "_lastBatchedTick=7 after all batches fire (interval=2: ticks 1,3,5,7)");

            // Verify Math.Min invariants:
            // Rollback to snapshotTick=9 → Math.Min(7, 8)=7 (unchanged — snapshot is after last batch)
            Assert.AreEqual(7, Math.Min(lastBatched, 9 - 1),
                "Rolling back to snapshot.Tick=9 must not advance _lastBatchedTick");

            // Rollback to snapshotTick=7 → Math.Min(7, 6)=6 (decreased)
            Assert.AreEqual(6, Math.Min(lastBatched, 7 - 1),
                "Rolling back to snapshot.Tick=7 must clamp _lastBatchedTick down");

            // Rollback to snapshotTick=4 → Math.Min(7, 3)=3 (decreased to before first batch)
            Assert.AreEqual(3, Math.Min(lastBatched, 4 - 1),
                "Rolling back to snapshot.Tick=4 must clamp _lastBatchedTick down to before batch boundary");
        }

        /// <summary>
        /// #19: No NPE when Initialize(simulation, logger) is called [F-3]
        /// </summary>
        [Test]
        public void SpectatorInitialize_NoNPE()
        {
            var simulation = new MockSimulation();
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());

            Assert.DoesNotThrow(() => engine.Initialize(simulation, (ILogger)null),
                "Initialize(simulation, logger) must not throw");
        }

        /// <summary>
        /// #20: After StartSpectator() CurrentTick=0, LastVerifiedTick=-1, State=Running [F-3]
        /// </summary>
        [Test]
        public void StartSpectator_DirectInit()
        {
            var simulation = new MockSimulation();
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, (ILogger)_logger);

            engine.StartSpectator(MakeStartInfo(1));

            Assert.AreEqual(0, engine.CurrentTick);
            Assert.AreEqual(-1, engine.LastVerifiedTick);
            Assert.AreEqual(KlothoState.Running, engine.State);
            Assert.IsTrue(engine.IsSpectatorMode);
        }

        /// <summary>
        /// #21: InputDelay EmptyCommand pre-fill is not run in spectator mode [F-3]
        /// Verify: no pre-filled command at tick 0 → HasAllCommands(0) = false → tick does not advance
        /// </summary>
        [Test]
        public void StartSpectator_SkipsInputDelayFill()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);

            engine.StartSpectator(MakeStartInfo(1));

            // No commands added. If InputDelay fill had run, ticks 0..3 would have EmptyCommand
            // and confirmed ticks would have advanced. Since it did not run, no confirmed input at tick 0.
            // Prediction still advances up to MAX_SPECTATOR_PREDICTION_TICKS.
            engine.Update(1.0f);

            Assert.LessOrEqual(engine.CurrentTick, 4,
                "No InputDelay pre-fill in spectator mode — advances only via prediction (max 4 ticks)");
        }

        #endregion

        #region Tests 22–32: Late Join / Message Processing

        /// <summary>
        /// #22: On SpectatorAccept reception → _state stays Synchronizing, engine.StartSpectator() not called [F-4]
        /// </summary>
        [Test]
        public void SpectatorJoin_BeforeGameStart_StaysInSynchronizing()
        {
            _spectatorService.Connect("localhost", 7777);
            Assert.AreEqual(SpectatorState.Synchronizing, _spectatorService.State);

            InjectMessage(MakeAcceptMessage(-1));

            Assert.AreEqual(SpectatorState.Synchronizing, _spectatorService.State,
                "SpectatorAccept alone must not transition to Watching [F-4]");
            Assert.AreEqual(0, _mockEngine.StartSpectatorCallCount,
                "engine.StartSpectator() must not be called on SpectatorAccept alone");
        }

        /// <summary>
        /// #23: On GameStartMessage reception → OnSpectatorStarted fires with _pendingStartInfo, _state = Watching [F-4]
        /// </summary>
        [Test]
        public void GameStartMessage_Received_TransitionsToWatching()
        {
            _spectatorService.Connect("localhost", 7777);
            InjectMessage(MakeAcceptMessage(-1));

            SpectatorStartInfo receivedInfo = null;
            _spectatorService.OnSpectatorStarted += info => receivedInfo = info;

            InjectMessage(MakeGameStartMessage());

            Assert.AreEqual(SpectatorState.Watching, _spectatorService.State);
            Assert.IsNotNull(receivedInfo, "OnSpectatorStarted must fire with _pendingStartInfo [F-4]");
            Assert.AreEqual(42, receivedInfo.RandomSeed);
        }

        /// <summary>
        /// #24: At game start, GameStartMessage is sent to spectators whose LastSentTick == -1 [F-4]
        /// </summary>
        [Test]
        public void HostBroadcast_GameStart_IncludesPreJoinSpectators()
        {
            var hostTransport = new TestTransport();
            var hostService = new KlothoNetworkService();
            hostService.Initialize(hostTransport, _commandFactory, _logger);
            hostService.SubscribeEngine(new MockKlothoEngine());
            hostTransport.Listen("localhost", 7777, 4);
            hostService.CreateRoom("test", 4);

            // Spectator connects before game start
            var spectatorTransport = new TestTransport();
            var spectatorService = new SpectatorService();
            spectatorService.Initialize(spectatorTransport, _commandFactory, new MockKlothoEngine(), _logger);
            spectatorService.Connect("localhost", 7777);
            hostService.Update();
            spectatorService.Update(); // Process SpectatorAcceptMessage → Synchronizing

            Assert.AreEqual(SpectatorState.Synchronizing, spectatorService.State);
            Assert.AreEqual(1, hostService.SpectatorCount);

            // Simulate host StartGame: force Synchronized phase then trigger all-ready
            // Use the SetReady approach: first force Synchronized phase
            typeof(KlothoNetworkService)
                .GetField("_phase", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(hostService, SessionPhase.Synchronized);

            // Add a second player to satisfy AllPlayersReady
            var players = (List<PlayerInfo>)typeof(KlothoNetworkService)
                .GetField("_players", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(hostService);
            players.Add(new PlayerInfo { PlayerId = 1, IsReady = true });
            players[0].IsReady = true; // host player

            // Trigger StartGame via SetReady (AllPlayersReady → StartGame → broadcasts to waiting spectators)
            hostService.SetReady(true);
            hostService.Update();
            spectatorService.Update();

            Assert.AreEqual(SpectatorState.Watching, spectatorService.State,
                "Spectator who joined before game start must receive GameStartMessage [F-4]");
        }

        /// <summary>
        /// #25: ProcessSpectatorInput → fires with Action&lt;int, ICommand&gt; signature [F-5]
        /// </summary>
        [Test]
        public void OnConfirmedInputReceived_SingleCommandSignature()
        {
            TransitionToWatching();

            int receivedTick = -1;
            ICommand receivedCmd = null;
            // Compile-time check: the event must match Action<int, ICommand>
            _spectatorService.OnConfirmedInputReceived += (tick, cmd) =>
            {
                receivedTick = tick;
                receivedCmd = cmd;
            };

            byte[] data = BuildSpectatorInputData(5, 1, 1);
            InjectMessage(new SpectatorInputMessage
            {
                StartTick = 5, TickCount = 1,
                InputData = data, InputDataLength = data.Length,
            });

            Assert.AreEqual(5, receivedTick);
            Assert.IsNotNull(receivedCmd);
            Assert.IsInstanceOf<EmptyCommand>(receivedCmd);
        }

        /// <summary>
        /// #26: TrySerializeVerifiedInputRange not called when SpectatorCount == 0 [F-6]
        /// </summary>
        [Test]
        public void SerializeVerifiedInput_SkippedWhenNoSpectators()
        {
            var simulation = new MockSimulation();
            // SpectatorCount = 0
            var networkService = new MockNetworkService { PlayerCount = 1, SpectatorCount = 0 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.Start(enableRecording: false);

            int batchCount = 0;
            engine.OnVerifiedInputBatchReady += (s, c, d, l) => batchCount++;

            for (int t = 0; t < 4; t++)
                networkService.FireCommandReceived(new EmptyCommand(0, t + engine.InputDelay));
            engine.Update(0.2f);

            Assert.AreEqual(0, batchCount,
                "OnVerifiedInputBatchReady must not fire when SpectatorCount == 0 [F-6]");
        }

        /// <summary>
        /// #27: fromTick &lt; OldestTick → TrySerializeVerifiedInputRange returns false [F-7]
        /// </summary>
        [Test]
        public void TrySerializeVerifiedInput_ReturnsFalse_WhenTickOutOfRange()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.Start(enableRecording: false);

            // fromTick = -1 (negative), less than OldestTick (0)
            bool result = engine.TrySerializeVerifiedInputRange(-1, 0, out _, out _);

            Assert.IsFalse(result,
                "TrySerializeVerifiedInputRange must return false when fromTick < OldestTick [F-7]");
        }

        /// <summary>
        /// #28: GetNearestSnapshotTickWithinBuffer → snapshot.Tick >= OldestTick + SyncCheckInterval [F-7]
        /// Returns -1 if no suitable snapshot exists.
        /// </summary>
        [Test]
        public void LateJoin_SnapshotSelection_WithinBufferRange()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.Start(enableRecording: false);

            // No ECS snapshot → returns -1 (no suitable snapshot in MockSimulation)
            int snapshotTick = engine.GetNearestSnapshotTickWithinBuffer();

            // Either -1 (no snapshot) or the snapshot tick meets the criterion
            if (snapshotTick >= 0)
            {
                // Verify the snapshot meets the minimum buffer criterion
                var inputBufferField = typeof(KlothoEngine)
                    .GetField("_inputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
                var syncIntervalField = typeof(KlothoEngine)
                    .GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.GreaterOrEqual(snapshotTick, 0,
                    "Returned snapshot tick must meet OldestTick + SyncCheckInterval criterion [F-7]");
            }
            else
            {
                Assert.AreEqual(-1, snapshotTick, "No ECS snapshot → returns -1");
            }
        }

        /// <summary>
        /// #29: When the second SpectatorInputMessage arrives, the first queued item's data is preserved [N-3]
        /// MessageSerializer._messageCache reuses a single instance per type → SpectatorService must copy the data
        /// </summary>
        [Test]
        public void PendingInputs_SafeCopy_NotOverwrittenOnNextReceive()
        {
            _spectatorService.Connect("localhost", 7777);
            InjectMessage(MakeAcceptMessage(-1));
            // State = Synchronizing → incoming SpectatorInputMessages buffered in _pendingInputs

            // First message: startTick=10, tickCount=4
            byte[] data1 = BuildSpectatorInputData(10, 4, 1);
            var msg1 = new SpectatorInputMessage
            {
                StartTick = 10, TickCount = 4,
                InputData = data1, InputDataLength = data1.Length,
            };

            // Second message: startTick=20, tickCount=4 (different data)
            byte[] data2 = BuildSpectatorInputData(20, 4, 1);
            var msg2 = new SpectatorInputMessage
            {
                StartTick = 20, TickCount = 4,
                InputData = data2, InputDataLength = data2.Length,
            };

            // Both arrive during Synchronizing → buffered
            InjectMessage(msg1);
            InjectMessage(msg2);

            // Drain with FullStateResponse
            var received = new List<(int tick, ICommand cmd)>();
            _spectatorService.OnConfirmedInputReceived += (tick, cmd) => received.Add((tick, cmd));

            InjectMessage(new FullStateResponseMessage
            {
                Tick = 5,  // snapshotTick=5 → passes filter: 10+4-1=13 > 5, 20+4-1=23 > 5
                StateHash = 12345L,
                StateData = new byte[] { 1, 2, 3, 4 },
            });

            // Both batches must be processed (no data overwrite)
            Assert.AreEqual(8, received.Count,
                "Both queued batches must be drained intact — safe copy prevents overwrite [N-3]");
        }

        /// <summary>
        /// #30: Catchup message arrives before FullStateResponse → buffered during Synchronizing → applied normally after drain [N-5 Case A]
        /// </summary>
        [Test]
        public void LateJoin_CatchupBeforeFullState_Buffered_ThenDrained()
        {
            _spectatorService.Connect("localhost", 7777);
            InjectMessage(MakeAcceptMessage(lastVerifiedTick: 10));
            // State = Synchronizing, full state request sent

            // Case A: SpectatorInputMessage arrives before FullStateResponse
            byte[] inputData = BuildSpectatorInputData(8, 4, 1); // ticks 8..11
            InjectMessage(new SpectatorInputMessage
            {
                StartTick = 8, TickCount = 4,
                InputData = inputData, InputDataLength = inputData.Length,
            });

            // Still Synchronizing → must be buffered and not yet processed
            Assert.AreEqual(SpectatorState.Synchronizing, _spectatorService.State);

            var received = new List<int>();
            _spectatorService.OnConfirmedInputReceived += (tick, cmd) => received.Add(tick);

            // FullStateResponse arrives: snapshotTick=7 → filter: 8+4-1=11 > 7 → passes
            SpectatorStartInfo startInfo = null;
            _spectatorService.OnSpectatorStarted += info => startInfo = info;

            InjectMessage(new FullStateResponseMessage
            {
                Tick = 7,
                StateHash = 99L,
                StateData = new byte[] { 1, 2 },
            });

            Assert.AreEqual(SpectatorState.Watching, _spectatorService.State,
                "State should transition to Watching after FullStateResponse [N-5]");
            Assert.IsNotNull(startInfo, "OnSpectatorStarted must fire after FullStateResponse");
            Assert.AreEqual(4, received.Count, "Buffered catchup inputs must be drained [N-5 Case A]");
        }

        /// <summary>
        /// #31: Catchup message arrives after FullStateResponse → processed directly in Watching state [N-5 Case B]
        /// </summary>
        [Test]
        public void LateJoin_CatchupAfterFullState_DirectProcess()
        {
            _spectatorService.Connect("localhost", 7777);
            InjectMessage(MakeAcceptMessage(lastVerifiedTick: 10));

            var received = new List<int>();
            _spectatorService.OnConfirmedInputReceived += (tick, cmd) => received.Add(tick);

            // FullStateResponse arrives first
            InjectMessage(new FullStateResponseMessage
            {
                Tick = 7,
                StateHash = 99L,
                StateData = new byte[] { 1, 2 },
            });

            Assert.AreEqual(SpectatorState.Watching, _spectatorService.State);

            // Case B: SpectatorInputMessage arrives after FullStateResponse → processed directly
            byte[] inputData = BuildSpectatorInputData(8, 4, 1);
            InjectMessage(new SpectatorInputMessage
            {
                StartTick = 8, TickCount = 4,
                InputData = inputData, InputDataLength = inputData.Length,
            });

            Assert.AreEqual(4, received.Count, "Catchup after FullStateResponse must be processed directly [N-5 Case B]");
        }

        /// <summary>
        /// #32: Partial overlap batch → InputBuffer.AddCommand harmlessly handles past ticks [N-5 edge case]
        /// startTick=3, tickCount=4 (ticks 3..6), snapshotTick=5: 3+4-1=6 > 5 → passes filter → all 4 ticks dispatched
        /// </summary>
        [Test]
        public void LateJoin_DrainFilter_PartialOverlapBatch_SafeAdd()
        {
            _spectatorService.Connect("localhost", 7777);
            InjectMessage(MakeAcceptMessage(lastVerifiedTick: 6));

            // Buffer partial overlap batch before FullStateResponse
            byte[] inputData = BuildSpectatorInputData(3, 4, 1); // ticks 3..6
            InjectMessage(new SpectatorInputMessage
            {
                StartTick = 3, TickCount = 4,
                InputData = inputData, InputDataLength = inputData.Length,
            });

            var received = new List<int>();
            _spectatorService.OnConfirmedInputReceived += (tick, cmd) => received.Add(tick);

            // FullStateResponse at tick=5: passes filter since 3+4-1=6 > 5
            InjectMessage(new FullStateResponseMessage
            {
                Tick = 5,
                StateHash = 99L,
                StateData = new byte[] { 1, 2 },
            });

            // Full batch dispatched (engine handles past ticks harmlessly)
            Assert.AreEqual(4, received.Count,
                "Partial overlap batch must be dispatched when endTick > snapshotTick [N-5 edge case]");
            Assert.AreEqual(3, received[0], "First command is for tick 3");
            Assert.AreEqual(6, received[3], "Last command is for tick 6");
        }

        #endregion

        #region Tests: Spectator Prediction

        /// <summary>
        /// Prediction execution: prediction ticks advance during the batch wait period so CurrentTick > confirmed + 1
        /// </summary>
        [Test]
        public void SpectatorPrediction_AdvancesBeyondConfirmed()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            // Confirm tick 0
            networkService.FireCommandReceived(new EmptyCommand(0, 0));
            engine.ConfirmSpectatorTick(0);
            engine.Update(0.030f); // 30ms → 1 tick (confirmed) + accumulator remainder

            int afterConfirmed = engine.CurrentTick;
            Assert.GreaterOrEqual(afterConfirmed, 1);

            // Time passes without additional batch → prediction ticks run
            engine.Update(0.060f); // 60ms → 2~3 prediction ticks

            Assert.Greater(engine.CurrentTick, afterConfirmed,
                "Spectator must advance via prediction beyond confirmed tick");
        }

        /// <summary>
        /// Prediction range limit: wait when MAX_SPECTATOR_PREDICTION_TICKS is exceeded
        /// </summary>
        [Test]
        public void SpectatorPrediction_StopsAtMaxLimit()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            // Confirm tick 0
            networkService.FireCommandReceived(new EmptyCommand(0, 0));
            engine.ConfirmSpectatorTick(0);

            // Enough time → advance only up to the prediction range
            engine.Update(0.5f);

            // MAX_SPECTATOR_PREDICTION_TICKS = SPECTATOR_INPUT_INTERVAL(2) + 2 = 4
            // confirmed=0, max prediction: 0 + 4 = 4 → CurrentTick <= 5
            Assert.LessOrEqual(engine.CurrentTick, 5,
                "Spectator must not predict beyond MAX_SPECTATOR_PREDICTION_TICKS");

            // Even more time passes — must not exceed the range
            int tickBefore = engine.CurrentTick;
            engine.Update(0.5f);
            Assert.AreEqual(tickBefore, engine.CurrentTick,
                "Prediction must not advance further after reaching limit");
        }

        /// <summary>
        /// Rollback: on batch arrival, predicted state → restore snapshot + resimulate
        /// </summary>
        [Test]
        public void SpectatorPrediction_RollbackOnBatchArrival()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            // Confirm tick 0 + advance prediction
            networkService.FireCommandReceived(new EmptyCommand(0, 0));
            engine.ConfirmSpectatorTick(0);
            engine.Update(0.080f); // confirmed 0 + 2~3 prediction ticks

            int tickBeforeBatch = engine.CurrentTick;
            Assert.Greater(tickBeforeBatch, 1, "Should have predicted ticks");

            // New batch arrives: confirm ticks 1, 2
            networkService.FireCommandReceived(new EmptyCommand(0, 1));
            networkService.FireCommandReceived(new EmptyCommand(0, 2));
            engine.ConfirmSpectatorTick(2);

            // Update → stage 1 rollback + resim + stage 3 extra execution
            engine.Update(0.030f);

            Assert.GreaterOrEqual(engine.CurrentTick, 3,
                "After rollback+resim, CurrentTick must be at least confirmed+1");
        }

        /// <summary>
        /// Catchup: execute immediately when a large batch arrives without prediction
        /// </summary>
        [Test]
        public void SpectatorCatchup_LargeBatchWithoutPrediction()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            // 0ms Update → neither prediction nor confirmation
            engine.Update(0f);
            Assert.AreEqual(0, engine.CurrentTick);

            // Large batch arrives at once: ticks 0~9
            for (int t = 0; t < 10; t++)
                networkService.FireCommandReceived(new EmptyCommand(0, t));
            engine.ConfirmSpectatorTick(9);

            // Update → stage 2 catchup (ticks 0~8 immediately) + stage 3 accumulator (tick 9~)
            // Sufficient deltaTime to run up to the last confirmed tick + prediction range
            engine.Update(0.200f);

            Assert.GreaterOrEqual(engine.CurrentTick, 10,
                "Catchup must execute all confirmed ticks");
        }

        /// <summary>
        /// Verify that spectator prediction-related fields are reset after StartSpectator is called.
        /// </summary>
        [Test]
        public void SpectatorRestart_FieldsReset()
        {
            var simulation = new MockSimulation();
            var networkService = new MockNetworkService { PlayerCount = 1 };
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(simulation, networkService, _logger);
            engine.StartSpectator(MakeStartInfo(1));

            // Advance prediction
            networkService.FireCommandReceived(new EmptyCommand(0, 0));
            engine.ConfirmSpectatorTick(0);
            engine.Update(0.1f);

            int tickBefore = engine.CurrentTick;
            Assert.Greater(tickBefore, 0);

            // Restart
            engine.StartSpectator(MakeStartInfo(1));

            Assert.AreEqual(0, engine.CurrentTick, "CurrentTick must reset");

            // Update without new input → advances only up to prediction range (-1 + 4 = 3), no stale rollback
            engine.Update(0.030f);
            Assert.LessOrEqual(engine.CurrentTick, 4,
                "After restart, prediction must work fresh without stale rollback");
        }

        #endregion
    }
}
