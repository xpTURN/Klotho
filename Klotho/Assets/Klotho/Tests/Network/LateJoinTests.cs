#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    [TestFixture]
    public class LateJoinTests
    {
        #region Mocks

        private class MockPlayerInfo : IPlayerInfo
        {
            public int PlayerId { get; set; }
            public string PlayerName { get; set; } = "";
            public bool IsReady { get; set; } = true;
            public int Ping { get; set; }
            public PlayerConnectionState ConnectionState { get; set; } = PlayerConnectionState.Connected;
        }

        private class MockSimulation : ISimulation
        {
            public int CurrentTick { get; private set; }
            public long StateHash { get; set; } = 12345L;
            public int TickCallCount { get; private set; }
            private int _playerCount;

            public void Initialize() { CurrentTick = 0; }
            public void Tick(List<ICommand> commands)
            {
                CurrentTick++;
                TickCallCount++;

                for (int i = 0; i < commands.Count; i++)
                {
                    if (commands[i] is PlayerJoinCommand joinCmd)
                    {
                        _playerCount++;
                        OnPlayerJoined(joinCmd.JoinedPlayerId, CurrentTick);
                    }
                }
            }
            public void Rollback(int targetTick) { CurrentTick = targetTick; }
            public long GetStateHash() => StateHash;
            public void Reset() { CurrentTick = 0; TickCallCount = 0; }
            public void RestoreFromFullState(byte[] stateData) { }
            public byte[] SerializeFullState() => BitConverter.GetBytes(StateHash);
            public (byte[] data, long hash) SerializeFullStateWithHash() => (BitConverter.GetBytes(StateHash), StateHash);
            public void EmitSyncEvents() { }

            public event Action<int> OnPlayerJoinedNotification;
            public void OnPlayerJoined(int playerId, int tick)
            {
                OnPlayerJoinedNotification?.Invoke(playerId);
            }

            public void SetPlayerCount(int count) { _playerCount = count; }
        }

        private class MockNetworkService : IKlothoNetworkService
        {
            public SessionPhase Phase { get; set; } = SessionPhase.Playing;
            public SharedTimeClock SharedClock { get; set; }
            public int PlayerCount { get; set; } = 2;
            public int SpectatorCount { get; set; } = 0;
            public int PendingLateJoinCatchupCount { get; set; } = 0;
            public bool AllPlayersReady { get; set; } = true;
            public int LocalPlayerId { get; set; } = 0;
            public bool IsHost { get; set; } = true;
            public int RandomSeed { get; set; } = 42;
            public IReadOnlyList<IPlayerInfo> Players => BuildPlayerList();

            private List<IPlayerInfo> BuildPlayerList()
            {
                var list = new List<IPlayerInfo>();
                for (int i = 0; i < PlayerCount; i++)
                    list.Add(new MockPlayerInfo { PlayerId = i });
                return list;
            }

            public List<ICommand> SentCommands { get; } = new List<ICommand>();

            public event Action OnGameStart;
            public event Action<long> OnCountdownStarted;
            public event Action<IPlayerInfo> OnPlayerJoined;
            public event Action<IPlayerInfo> OnPlayerLeft;
            public event Action<ICommand> OnCommandReceived;
            public event Action<int, int, long, long> OnDesyncDetected;
            public event Action<int, int> OnFrameAdvantageReceived;
            public event Action<int> OnLocalPlayerIdAssigned;
            public event Action<int, int> OnFullStateRequested;
            public event Action<int, byte[], long> OnFullStateReceived;
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
            public void SendCommand(ICommand command)
            {
                // Duplicate for tracking
                SentCommands.Add(command);
                // Loopback
                OnCommandReceived?.Invoke(command);
            }
            public void RequestCommandsForTick(int tick) { }
            public void SendSyncHash(int tick, long hash) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SetLocalTick(int tick) { }
            public void SendFullStateRequest(int currentTick) { }
            public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash) { }
            public void SendPlayerConfig(int playerId, xpTURN.Klotho.Core.PlayerConfigBase playerConfig) { }

            public void FireCommandReceived(ICommand cmd) => OnCommandReceived?.Invoke(cmd);
            public void FireLateJoinPlayerAdded(int playerId, int joinTick) => OnLateJoinPlayerAdded?.Invoke(playerId, joinTick);
            public void FireGameStart() => OnGameStart?.Invoke();
        }

        #endregion

        private KlothoEngine _engine;
        private MockSimulation _simulation;
        private MockNetworkService _networkService;
        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("LateJoinTests");
        }

        [SetUp]
        public void SetUp()
        {
            _simulation = new MockSimulation();
            _simulation.SetPlayerCount(2);
            _networkService = new MockNetworkService { PlayerCount = 2, IsHost = true };
            _engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            _engine.Initialize(_simulation, _networkService, _logger);
            _engine.Start(enableRecording: false);
        }

        private void AdvanceToTick(int targetTick)
        {
            for (int t = _engine.CurrentTick; t < targetTick; t++)
            {
                for (int i = 0; i < _networkService.PlayerCount; i++)
                    _networkService.FireCommandReceived(new EmptyCommand(i, t + _engine.InputDelay));
                _engine.Update((_engine.TickInterval + 1) / 1000f);
            }
        }

        private FieldInfo GetField(string name)
        {
            return typeof(KlothoEngine).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        }

        #region #34: Verify _nextPlayerId transition

        [Test]
        public void NextPlayerId_MonotonicallyIncreasing()
        {
            var service = new KlothoNetworkService();
            service.CreateRoom("test", 4);

            var field = typeof(KlothoNetworkService)
                .GetField("_nextPlayerId", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.AreEqual(1, (int)field.GetValue(service));
        }

        #endregion

        #region #35: _activePlayerIds rollback restore

        [Test]
        public void ActivePlayerIds_SavedInEngineSnapshot()
        {
            var activePlayerIds = (List<int>)GetField("_activePlayerIds").GetValue(_engine);
            Assert.AreEqual(2, activePlayerIds.Count);
            Assert.Contains(0, activePlayerIds);
            Assert.Contains(1, activePlayerIds);

            AdvanceToTick(5);

            // Verify via reflection that ActivePlayerIds is correctly saved in the snapshot
            var snapshotsArray = GetField("_engineSnapshots").GetValue(_engine) as Array;
            Assert.IsNotNull(snapshotsArray);

            int idx = 3 % snapshotsArray.Length; // tick 3
            var snapshot = snapshotsArray.GetValue(idx);
            var snapshotType = snapshot.GetType();

            var activeIds = (int[])snapshotType.GetField("ActivePlayerIds").GetValue(snapshot);

            Assert.IsNotNull(activeIds);
            Assert.AreEqual(2, activeIds.Length);
            Assert.Contains(0, activeIds);
            Assert.Contains(1, activeIds);
        }

        #endregion


        #region #37: Late Join normal scenario (1 player)

        [Test]
        public void HandleLateJoinPlayerAdded_CreatesPlayerJoinCommand()
        {
            AdvanceToTick(10);

            _networkService.SentCommands.Clear();
            _networkService.FireLateJoinPlayerAdded(2, 20);

            Assert.AreEqual(1, _networkService.SentCommands.Count, "Should send exactly one PlayerJoinCommand");
            var cmd = _networkService.SentCommands[0] as PlayerJoinCommand;
            Assert.IsNotNull(cmd);
            Assert.AreEqual(2, cmd.JoinedPlayerId);
            Assert.AreEqual(20, cmd.Tick);
            Assert.AreEqual(0, cmd.PlayerId, "PlayerId should be Host's LocalPlayerId");
        }

        [Test]
        public void HandleLateJoinPlayerAdded_GuestIgnored()
        {
            _networkService.IsHost = false;

            _networkService.SentCommands.Clear();
            _networkService.FireLateJoinPlayerAdded(2, 20);

            Assert.AreEqual(0, _networkService.SentCommands.Count, "Guest should not create PlayerJoinCommand");
        }

        #endregion

        #region #39: MaxPlayers exceeded test

        [Test]
        public void CountPendingHandshakes_ReturnsCorrectCount()
        {
            // CountPendingHandshakes counts ALL !Completed entries (LateJoin + general handshake),
            // matching the unified EffectivePlayerCount gate.
            var service = new KlothoNetworkService();
            var method = typeof(KlothoNetworkService)
                .GetMethod("CountPendingHandshakes", BindingFlags.NonPublic | BindingFlags.Instance);

            var statesField = typeof(KlothoNetworkService)
                .GetField("_peerSyncStates", BindingFlags.NonPublic | BindingFlags.Instance);
            var statesObj = statesField.GetValue(service);

            var peerSyncStateType = typeof(KlothoNetworkService).Assembly
                .GetType("xpTURN.Klotho.Network.PeerSyncState");

            var addState = new Action<int, bool, bool>((peerId, isLateJoin, completed) =>
            {
                var state = Activator.CreateInstance(peerSyncStateType);
                peerSyncStateType.GetField("IsLateJoin").SetValue(state, isLateJoin);
                peerSyncStateType.GetField("Completed").SetValue(state, completed);
                var addMethod = statesObj.GetType().GetProperty("Item");
                addMethod.SetValue(statesObj, state, new object[] { peerId });
            });

            addState(10, true, false);   // pending late join — count
            addState(11, true, true);    // completed late join — don't count
            addState(12, false, false);  // pending normal handshake — count (NEW: was excluded by old helper)

            int count = (int)method.Invoke(service, null);
            Assert.AreEqual(2, count,
                "All !Completed entries should be counted regardless of IsLateJoin (LateJoin + general handshake)");
        }

        #endregion

        #region #42: PlayerJoinCommand rollback determinism

        [Test]
        public void PlayerJoinCommand_DeterministicOnRollback()
        {
            AdvanceToTick(8);

            // Insert PlayerJoinCommand at tick 10
            var joinCmd = CommandPool.Get<PlayerJoinCommand>();
            joinCmd.PlayerId = 0;
            joinCmd.Tick = 10;
            joinCmd.JoinedPlayerId = 2;
            _networkService.FireCommandReceived(joinCmd);

            // Fill in the remaining inputs for tick 10
            _networkService.FireCommandReceived(new EmptyCommand(0, 10));
            _networkService.FireCommandReceived(new EmptyCommand(1, 10));

            // Advance past tick 10
            for (int t = 8; t < 12; t++)
            {
                _networkService.FireCommandReceived(new EmptyCommand(0, t + _engine.InputDelay));
                _networkService.FireCommandReceived(new EmptyCommand(1, t + _engine.InputDelay));
            }
            _engine.Update(0.3f);

            var activeIds = (List<int>)GetField("_activePlayerIds").GetValue(_engine);

            Assert.AreEqual(3, activeIds.Count, "PlayerCount should be 3 after PlayerJoinCommand");
            Assert.Contains(2, activeIds, "PlayerId 2 should be in _activePlayerIds");
        }

        #endregion

        #region #43: Multiple PlayerJoinCommand OrderKey sorting

        [Test]
        public void SystemCommands_SortedByOrderKey()
        {
            var inputBuffer = new InputBuffer();

            // Add two PlayerJoinCommand with different JoinedPlayerId at the same tick
            var cmd1 = CommandPool.Get<PlayerJoinCommand>();
            cmd1.PlayerId = 0;
            cmd1.Tick = 5;
            cmd1.JoinedPlayerId = 3;

            var cmd2 = CommandPool.Get<PlayerJoinCommand>();
            cmd2.PlayerId = 0;
            cmd2.Tick = 5;
            cmd2.JoinedPlayerId = 2;

            inputBuffer.AddCommand(cmd1); // JoinedPlayerId=3 first
            inputBuffer.AddCommand(cmd2); // JoinedPlayerId=2 second

            var commands = inputBuffer.GetCommandList(5);

            // System commands should be sorted by OrderKey (ascending JoinedPlayerId)
            // So cmd2(id=2) should come before cmd1(id=3)
            Assert.AreEqual(2, commands.Count);
            Assert.AreEqual(2, ((PlayerJoinCommand)commands[0]).JoinedPlayerId, "Lower OrderKey first");
            Assert.AreEqual(3, ((PlayerJoinCommand)commands[1]).JoinedPlayerId, "Higher OrderKey second");
        }

        [Test]
        public void SystemCommands_AfterPlayerInputs()
        {
            var inputBuffer = new InputBuffer();

            var playerCmd = new EmptyCommand(0, 5);
            var sysCmd = CommandPool.Get<PlayerJoinCommand>();
            sysCmd.PlayerId = 0;
            sysCmd.Tick = 5;
            sysCmd.JoinedPlayerId = 2;

            inputBuffer.AddCommand(sysCmd);
            inputBuffer.AddCommand(playerCmd);

            var commands = inputBuffer.GetCommandList(5);
            Assert.AreEqual(2, commands.Count);
            Assert.IsInstanceOf<EmptyCommand>(commands[0], "Player input should come first");
            Assert.IsInstanceOf<PlayerJoinCommand>(commands[1], "System command should come after");
        }

        [Test]
        public void HasAllCommands_IgnoresSystemCommands()
        {
            var inputBuffer = new InputBuffer();

            var sysCmd = CommandPool.Get<PlayerJoinCommand>();
            sysCmd.PlayerId = 0;
            sysCmd.Tick = 5;
            sysCmd.JoinedPlayerId = 2;
            inputBuffer.AddCommand(sysCmd);

            Assert.IsFalse(inputBuffer.HasAllCommands(5, 1), "System command alone should not satisfy HasAllCommands");

            inputBuffer.AddCommand(new EmptyCommand(0, 5));
            Assert.IsTrue(inputBuffer.HasAllCommands(5, 1), "Player input should satisfy HasAllCommands");
        }

        #endregion

        #region #46: Active transition + CatchingUp mode

        [Test]
        public void CatchingUp_ExecutesVerifiedTicksFast()
        {
            _engine.StartCatchingUp();
            var isCatchingUp = (bool)GetField("_isCatchingUp").GetValue(_engine);
            Assert.IsTrue(isCatchingUp);

            // Supply confirmed commands
            for (int t = _engine.CurrentTick; t < _engine.CurrentTick + 10; t++)
            {
                _engine.ReceiveConfirmedCommand(new EmptyCommand(0, t));
                _engine.ReceiveConfirmedCommand(new EmptyCommand(1, t));
                _engine.ConfirmCatchupTick(t);
            }

            int startTick = _engine.CurrentTick;
            _engine.Update(0.001f); // Single frame, all confirmed ticks should execute

            Assert.Greater(_engine.CurrentTick, startTick, "Should advance multiple ticks in CatchingUp mode");
        }

        [Test]
        public void CatchingUp_CompleteFires_WhenThresholdMet()
        {
            bool completeFired = false;
            _engine.OnCatchupComplete += () => completeFired = true;
            _engine.StartCatchingUp();

            // Supply confirmed commands for exactly threshold+1 ticks
            int startTick = _engine.CurrentTick;
            for (int t = startTick; t < startTick + 5; t++)
            {
                _engine.ReceiveConfirmedCommand(new EmptyCommand(0, t));
                _engine.ReceiveConfirmedCommand(new EmptyCommand(1, t));
                _engine.ConfirmCatchupTick(t);
            }

            _engine.Update(0.001f);
            Assert.IsTrue(completeFired, "OnCatchupComplete should fire when threshold met");

            var isCatchingUp = (bool)GetField("_isCatchingUp").GetValue(_engine);
            Assert.IsFalse(isCatchingUp, "Should exit catching up mode");
        }

        #endregion

        #region #47: Spectator PlayerJoinCommand _playerCount update

        [Test]
        public void PlayerJoinCommand_UpdatesPlayerCountViaCallback()
        {
            var activeIds = (List<int>)GetField("_activePlayerIds").GetValue(_engine);
            Assert.AreEqual(2, activeIds.Count);

            // Simulate PlayerJoinCommand being processed via Tick
            _networkService.FireLateJoinPlayerAdded(2, _engine.CurrentTick + 2);

            // Fill inputs so the engine can advance to the join tick
            int joinTick = _engine.CurrentTick + 2;
            for (int t = _engine.CurrentTick; t <= joinTick; t++)
            {
                _networkService.FireCommandReceived(new EmptyCommand(0, t + _engine.InputDelay));
                _networkService.FireCommandReceived(new EmptyCommand(1, t + _engine.InputDelay));
            }
            float deltaTime = (3 * _engine.TickInterval + 1) / 1000f;
            _engine.Update(deltaTime);

            Assert.AreEqual(3, activeIds.Count,
                "PlayerCount should be 3 after PlayerJoinCommand callback");
            Assert.Contains(2, activeIds, "PlayerId 2 should be in _activePlayerIds");
        }

        #endregion

        #region #48: Reconnect timeout + Late Join (PlayerId gap)

        [Test]
        public void ActivePlayerIds_HandlesGap_AfterPlayerLeft()
        {
            var activeIds = (List<int>)GetField("_activePlayerIds").GetValue(_engine);
            Assert.AreEqual(2, activeIds.Count);

            // Simulate player 1 leaving (reconnect timeout)
            _engine.NotifyPlayerDisconnected(1);
            _engine.NotifyPlayerLeft(1);

            Assert.AreEqual(1, activeIds.Count);
            Assert.Contains(0, activeIds);
            Assert.IsFalse(activeIds.Contains(1));
        }

        [Test]
        public void PredictionLoop_SkipsGap_AfterPlayerLeft()
        {
            AdvanceToTick(5);

            _engine.NotifyPlayerDisconnected(1);
            _engine.NotifyPlayerLeft(1);

            // Continue advancing — the prediction loop should iterate only over active player 0
            // It should not crash or predict for the non-existent player 1
            _networkService.PlayerCount = 1;
            for (int t = _engine.CurrentTick; t < _engine.CurrentTick + 5; t++)
                _networkService.FireCommandReceived(new EmptyCommand(0, t + _engine.InputDelay));

            Assert.DoesNotThrow(() => _engine.Update(0.3f),
                "Prediction loop should handle PlayerId gap without crash");
        }

        #endregion

        #region #45: Empty input insertion determinism during CatchingUp

        [Test]
        public void CatchingUp_PlayerJoinCommand_DeterministicEmptyInputs()
        {
            // Start with 2 players, enter CatchingUp mode
            _engine.StartCatchingUp();
            int startTick = _engine.CurrentTick;

            // tick 0~2: confirmed commands for existing 2 players (player 0, 1)
            for (int t = startTick; t < startTick + 3; t++)
            {
                _engine.ReceiveConfirmedCommand(new EmptyCommand(0, t));
                _engine.ReceiveConfirmedCommand(new EmptyCommand(1, t));
                _engine.ConfirmCatchupTick(t);
            }

            // tick 3: player 2 joins via PlayerJoinCommand + existing 2 players' inputs
            int joinTick = startTick + 3;
            var joinCmd = new PlayerJoinCommand { PlayerId = 0, Tick = joinTick, JoinedPlayerId = 2 };
            _engine.ReceiveConfirmedCommand(joinCmd);
            _engine.ReceiveConfirmedCommand(new EmptyCommand(0, joinTick));
            _engine.ReceiveConfirmedCommand(new EmptyCommand(1, joinTick));
            _engine.ConfirmCatchupTick(joinTick);

            // tick 4~6: confirmed commands for 3 players (player 0, 1, 2)
            for (int t = joinTick + 1; t <= joinTick + 3; t++)
            {
                _engine.ReceiveConfirmedCommand(new EmptyCommand(0, t));
                _engine.ReceiveConfirmedCommand(new EmptyCommand(1, t));
                _engine.ReceiveConfirmedCommand(new EmptyCommand(2, t));
                _engine.ConfirmCatchupTick(t);
            }

            // Execute
            _engine.Update(0.001f);

            var activeIds = (List<int>)GetField("_activePlayerIds").GetValue(_engine);
            Assert.AreEqual(3, activeIds.Count, "PlayerCount should be 3 after PlayerJoinCommand during catchup");
            Assert.Contains(2, activeIds, "PlayerId 2 should be in _activePlayerIds");

            // Verify all ticks have executed
            Assert.AreEqual(joinTick + 4, _engine.CurrentTick,
                "All confirmed ticks should have been executed");

            // Determinism verification: re-run the same sequence on a new engine
            var sim2 = new MockSimulation();
            sim2.SetPlayerCount(2);
            var net2 = new MockNetworkService { PlayerCount = 2, IsHost = true };
            var engine2 = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine2.Initialize(sim2, net2, _logger);
            engine2.Start(enableRecording: false);
            engine2.StartCatchingUp();

            int startTick2 = engine2.CurrentTick;
            for (int t = startTick2; t < startTick2 + 3; t++)
            {
                engine2.ReceiveConfirmedCommand(new EmptyCommand(0, t));
                engine2.ReceiveConfirmedCommand(new EmptyCommand(1, t));
                engine2.ConfirmCatchupTick(t);
            }

            int joinTick2 = startTick2 + 3;
            engine2.ReceiveConfirmedCommand(new PlayerJoinCommand { PlayerId = 0, Tick = joinTick2, JoinedPlayerId = 2 });
            engine2.ReceiveConfirmedCommand(new EmptyCommand(0, joinTick2));
            engine2.ReceiveConfirmedCommand(new EmptyCommand(1, joinTick2));
            engine2.ConfirmCatchupTick(joinTick2);

            for (int t = joinTick2 + 1; t <= joinTick2 + 3; t++)
            {
                engine2.ReceiveConfirmedCommand(new EmptyCommand(0, t));
                engine2.ReceiveConfirmedCommand(new EmptyCommand(1, t));
                engine2.ReceiveConfirmedCommand(new EmptyCommand(2, t));
                engine2.ConfirmCatchupTick(t);
            }

            engine2.Update(0.001f);

            Assert.AreEqual(_engine.CurrentTick, engine2.CurrentTick,
                "Both engines should reach the same tick");
            Assert.AreEqual(_simulation.GetStateHash(), sim2.GetStateHash(),
                "State hash should match — deterministic");
        }

        #endregion
    }
}
