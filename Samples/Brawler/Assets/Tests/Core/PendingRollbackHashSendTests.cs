#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Pending-rollback / chain-gap sync-hash mis-send guards.
    ///   - ExecuteTick sends a check-tick hash only when verified-at-execution
    ///     (chain-continuous AND no pending rollback); otherwise it stashes + defers,
    ///     and the post-flush ExecuteRollback re-advance sends it exactly once
    ///     (no immediate-send + re-send double).
    ///   - The normal verified-execution path still sends immediately, exactly once.
    ///   - ForceInsertEmptyCommandsRange does not advance the verified chain while a
    ///     rollback is pending (same guard as the HandleCommandReceived advance path).
    /// </summary>
    [TestFixture]
    public class PendingRollbackHashSendTests
    {
        #region Mock network service (records SendSyncHash ticks)

        private class MockPlayerInfo : IPlayerInfo
        {
            public int PlayerId { get; set; }
            public string DisplayName { get; set; } = "";
            public string Account { get; set; } = "";
            public bool IsReady { get; set; } = true;
            public int Ping { get; set; }
            public PlayerConnectionState ConnectionState { get; set; } = PlayerConnectionState.Connected;
        }

        private class MockNetworkService : IKlothoNetworkService
        {
            public SessionPhase Phase { get; set; } = SessionPhase.Playing;
            public SharedTimeClock SharedClock { get; set; }
            public int PlayerCount { get; set; } = 1;
            public int SpectatorCount => 0;
            public int PendingLateJoinCatchupCount => 0;
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

            // Records every SendSyncHash(tick, _) — tests assert on call counts per tick.
            public List<int> SyncHashSentTicks { get; } = new List<int>();

            public event Action OnGameStart;
            public event Action<long> OnCountdownStarted;
            public event Action<IPlayerInfo> OnPlayerJoined;
            public event Action<IPlayerInfo> OnPlayerLeft;
            public event Action<ICommand> OnCommandReceived;
            public event Action<int, int, long, long> OnDesyncDetected;
            public event Action<int, int, bool> OnSyncHashCompared;
            public event Action<int, int> OnResyncFailureReported;
            public event Action<int> OnMatchAbortReceived;
            public event Action<int, int, int> OnFrameAdvantageReceived;
            public event Action<int> OnLocalPlayerIdAssigned;
            public event Action<int, int> OnFullStateRequested;
            public event Action<int, byte[], long, FullStateKind> OnFullStateReceived;
            public event Action<IPlayerInfo> OnPlayerDisconnected;
            public event Action<IPlayerInfo> OnPlayerReconnected;
            public event Action OnReconnecting;
            public event Action<ReconnectRejectReason> OnReconnectFailed;
            public event Action OnReconnected;
            public event Action<int, int> OnLateJoinPlayerAdded;
            public event Action<SessionPhase> OnPhaseChanged;
            public event Action<int> OnPlayerCountChanged;
            public event Action<bool> OnAllPlayersReadyChanged;

            public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger) { }
            public void CreateRoom(string roomName, int maxPlayers) { }
            public void JoinRoom(string roomName) { }
            public void LeaveRoom(bool keepReconnectCredentials = false) { }
            public void SetReady(bool ready) { }
            public void SendCommand(ICommand command) { }
            public void RequestCommandsForTick(int tick) { }
            public void SendSyncHash(int tick, long hash, long cmdHash) { SyncHashSentTicks.Add(tick); }
            public void InvalidateLocalSyncHashes(int fromTick) { }
            public void InvalidateSyncHashes(int fromTick) { }
            public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash) { }
            public void BroadcastMatchAbort(byte reason) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SetLocalTick(int tick) { }
            public void SetLocalAdvantage(int advantage) { }
            public void SendFullStateRequest(int currentTick) { }
            public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash) { }
            public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast) { }
            public void SendPlayerConfig(int playerId, xpTURN.Klotho.Core.PlayerConfigBase playerConfig) { }

            internal void SuppressWarnings()
            {
                OnGameStart?.Invoke();
                OnCountdownStarted?.Invoke(0);
                OnPlayerJoined?.Invoke(null);
                OnPlayerLeft?.Invoke(null);
                OnCommandReceived?.Invoke(null);
                OnDesyncDetected?.Invoke(0, 0, 0, 0);
                OnSyncHashCompared?.Invoke(0, 0, false);
                OnResyncFailureReported?.Invoke(0, 0);
                OnMatchAbortReceived?.Invoke(0);
                OnFrameAdvantageReceived?.Invoke(0, 0, 0);
                OnLocalPlayerIdAssigned?.Invoke(0);
                OnFullStateRequested?.Invoke(0, 0);
                OnFullStateReceived?.Invoke(0, null, 0, FullStateKind.Unicast);
                OnPlayerDisconnected?.Invoke(null);
                OnPlayerReconnected?.Invoke(null);
                OnReconnecting?.Invoke();
                OnReconnectFailed?.Invoke(default);
                OnReconnected?.Invoke();
                OnLateJoinPlayerAdded?.Invoke(0, 0);
                OnPhaseChanged?.Invoke(default);
                OnPlayerCountChanged?.Invoke(0);
                OnAllPlayersReadyChanged?.Invoke(false);
            }
        }

        #endregion

        #region Reflection helpers

        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo HasPendingRollbackField = typeof(KlothoEngine)
            .GetField("_hasPendingRollback", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void SetLastVerifiedTick(KlothoEngine e, int v) => LastVerifiedTickField.SetValue(e, v);
        private static void SetHasPendingRollback(KlothoEngine e, bool v) => HasPendingRollbackField.SetValue(e, v);

        #endregion

        private KlothoEngine _engine;
        private TestSimulation _simulation;
        private MockNetworkService _networkService;
        private IKLogger _logger;

        private const int SyncInterval = 10;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("PendingRollbackHashSendTests");
        }

        [SetUp]
        public void SetUp()
        {
            // UseDeterministicHash so ExecuteRollback can resolve a snapshot (per-tick state saved);
            // InputDelayTicks pre-fills ticks 0..39 with EmptyCommand so check ticks run the
            // ExecuteTick (all-real-inputs) path, not the speculative path.
            _simulation = new TestSimulation { UseDeterministicHash = true };
            _networkService = new MockNetworkService();
            _engine = new KlothoEngine(
                new SimulationConfig
                {
                    InputDelayTicks = 40,
                    SyncCheckInterval = SyncInterval,
                    MaxRollbackTicks = 50
                },
                new SessionConfig());
            _engine.Initialize(_simulation, _networkService, _logger);
            _engine.SetCommandFactory(new CommandFactory());
            _engine.Start(enableRecording: false);
        }

        private void AdvanceToTick(int targetTick)
        {
            while (_engine.CurrentTick < targetTick)
                _engine.Update((_engine.TickInterval + 1) / 1000f);
        }

        private int SendCount(int tick) => _networkService.SyncHashSentTicks.Count(t => t == tick);

        /// <summary>
        /// Normal verified-execution path is unchanged: a check tick with no pending rollback
        /// sends its hash immediately, exactly once.
        /// </summary>
        [Test]
        public void NormalCheckTick_NoPendingRollback_SendsImmediatelyOnce()
        {
            AdvanceToTick(11); // executes ticks 0..10; tick 10 is a check tick

            Assert.AreEqual(1, SendCount(10), "Check tick 10 should be sent exactly once on the normal path");
        }

        /// <summary>
        /// Verified-at-execution check tick reached while a rollback is pending: the immediate
        /// send is suppressed (deferred), and the post-flush ExecuteRollback re-advance sends it
        /// exactly once — NOT twice (immediate + re-send). This is the second-term
        /// (&amp;&amp; !_hasPendingRollback) guard against the same-value double send.
        /// </summary>
        [Test]
        public void PendingRollback_DuringCheckTick_SendsHashExactlyOnce()
        {
            AdvanceToTick(20); // executes ticks 0..19; tick 20 not yet executed
            Assert.AreEqual(1, SendCount(10), "precondition: tick 10 already sent once");
            Assert.AreEqual(0, SendCount(20), "precondition: check tick 20 not yet executed/sent");

            // Pending rollback to an earlier verified tick (desync-style; chain stays continuous).
            _engine.RequestRollback(15);

            // One frame: the tick loop executes check tick 20 (defers the send because a rollback
            // is pending), then FlushPendingRollback rolls back -> re-sim -> chain re-advance sends.
            _engine.Update((_engine.TickInterval + 1) / 1000f);

            Assert.AreEqual(1, SendCount(20),
                "Check tick 20 must be sent exactly once (deferred then re-advance) — no immediate+re-send double");
            Assert.AreEqual(1, SendCount(10), "tick 10 (below rollback range) must not be re-sent");
        }

        /// <summary>
        /// ForceInsertEmptyCommandsRange must not advance the verified chain while a rollback is
        /// pending (the ExecuteRollback flush re-advances afterwards). Verified by rewinding the
        /// verified watermark behind CurrentTick to create an advanceable gap whose inputs are
        /// already present, then driving the guarded advance with/without a pending rollback.
        /// </summary>
        [Test]
        public void ForceInsert_DuringPendingRollback_DoesNotAdvanceChain()
        {
            AdvanceToTick(10); // _lastVerifiedTick == 9, CurrentTick == 10, ticks 0..9 present

            // Create a gap: ticks 6..9 have all commands present, so the chain CAN advance to 9.
            SetLastVerifiedTick(_engine, 5);
            SetHasPendingRollback(_engine, true);

            _engine.ForceInsertEmptyCommandsRange(0, 6, 9);
            Assert.AreEqual(5, _engine.LastVerifiedTick,
                "ForceInsert must not advance the chain while a rollback is pending");

            SetHasPendingRollback(_engine, false);
            _engine.ForceInsertEmptyCommandsRange(0, 6, 9);
            Assert.AreEqual(9, _engine.LastVerifiedTick,
                "ForceInsert advances the chain once the pending rollback clears");
        }
    }
}
