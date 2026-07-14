#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// P2P desync detection receive side:
    ///   ① anchor veto order-independence: a mismatch arriving after a same-tick match demotes
    ///      the rollback anchor to the prior matched tick (1-step history) BEFORE rung-1 reads it,
    ///      so rung-1 never rolls back to a diverged tick regardless of arrival order. When the
    ///      1-step history is exhausted (no known-good tick &lt; the divergence) rung-1 is skipped
    ///      and the per-peer counter is left to escalate.
    ///   ② counter masking: the consecutive-desync counter is per-peer, so a match with one peer
    ///      no longer wipes another peer's accumulation; a persistently-diverging peer reaches the
    ///      resync threshold even while another peer keeps matching. Per-peer dedup preserves the
    ///      dedup goal (one tick from one peer counts once). The recovery-match reset gate is the
    ///      peer's own MAX mismatched tick, not the global veto.
    /// Events are fired directly on a mock network service; internal state is read via reflection.
    /// </summary>
    [TestFixture]
    public class DesyncAnchorVetoUnitTests
    {
        #region Mock simulation (no restorable history — rollback resolve fails, escalation under test)

        private class MockSimulation : ISimulation
        {
            public int CurrentTick { get; private set; }
            public long StateHash { get; set; } = 12345L;

            public void Initialize() { CurrentTick = 0; }
            public void Tick(List<ICommand> commands) { CurrentTick++; }
            public void SaveSnapshot() { }
            public int GetNearestRollbackTick(int targetTick) => -1;
            public void Rollback(int targetTick) { CurrentTick = targetTick; }
            public long GetStateHash() => StateHash;
            public void Reset() { CurrentTick = 0; }
            public void RestoreFromFullState(byte[] stateData) { }
            public byte[] SerializeFullState() => BitConverter.GetBytes(StateHash);
            public void EmitSyncEvents() { }
            public event System.Action<int> OnPlayerJoinedNotification;
            public void OnPlayerJoined(int playerId, int tick) { }
            public (byte[] data, long hash) SerializeFullStateWithHash() => (BitConverter.GetBytes(StateHash), StateHash);
        }

        #endregion

        #region Mock network service (P2P surface)

        private class MockPlayerInfo : IPlayerInfo
        {
            public int PlayerId { get; set; }
            public string DisplayName => "";
            public string Account => "";
            public bool IsReady => true;
            public int Ping => 0;
            public PlayerConnectionState ConnectionState => PlayerConnectionState.Connected;
        }

        private class MockNetworkService : IKlothoNetworkService
        {
            public SessionPhase Phase => SessionPhase.Playing;
            public SharedTimeClock SharedClock => default;
            public int PlayerCount => 3;
            public int SpectatorCount => 0;
            public int PendingLateJoinCatchupCount => 0;
            public bool AllPlayersReady => true;
            public int LocalPlayerId { get; set; } = 0;
            public bool IsHost { get; set; }
            public int RandomSeed => 42;
            public IReadOnlyList<IPlayerInfo> Players { get; } = new List<IPlayerInfo>
            {
                new MockPlayerInfo { PlayerId = 0 },
                new MockPlayerInfo { PlayerId = 1 },
                new MockPlayerInfo { PlayerId = 2 },
            };

            public int SendFullStateRequestCount { get; private set; }

#pragma warning disable CS0067
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
#pragma warning restore CS0067

            public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger) { }
            public void CreateRoom(string roomName, int maxPlayers) { }
            public void JoinRoom(string roomName) { }
            public void LeaveRoom(bool keepReconnectCredentials = false) { }
            public void SetReady(bool ready) { }
            public void SendCommand(ICommand command) { }
            public void RequestCommandsForTick(int tick) { }
            public void SendSyncHash(int tick, long hash, long cmdHash) { }
            public void InvalidateLocalSyncHashes(int fromTick) { }
            public void InvalidateSyncHashes(int fromTick) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SetLocalTick(int tick) { }
            public void SetLocalAdvantage(int advantage) { }
            public void SendFullStateRequest(int currentTick) { SendFullStateRequestCount++; }
            public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash) { }
            public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast) { }
            public void SendPlayerConfig(int playerId, PlayerConfigBase playerConfig) { }
            public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash) { }
            public void BroadcastMatchAbort(byte reason) { }

            public void FireSyncHashCompared(int tick, int remotePlayerId, bool matched)
                => OnSyncHashCompared?.Invoke(tick, remotePlayerId, matched);

            // Mirrors CompareAndReportSyncHash on a mismatch: OnDesyncDetected (→ HandleNetworkDesync,
            // demotion + rollback + per-peer count) THEN OnSyncHashCompared(false) (→ global veto),
            // in that order.
            public void FireDesyncDetected(int remotePlayerId, int tick)
            {
                OnDesyncDetected?.Invoke(remotePlayerId, tick, 111L, 222L);
                OnSyncHashCompared?.Invoke(tick, remotePlayerId, false);
            }
        }

        #endregion

        #region Reflection helpers

        private static readonly FieldInfo LastMatchedField = typeof(KlothoEngine)
            .GetField("_lastMatchedSyncTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo PrevMatchedField = typeof(KlothoEngine)
            .GetField("_prevMatchedSyncTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo HasPendingRollbackField = typeof(KlothoEngine)
            .GetField("_hasPendingRollback", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo PendingRollbackTickField = typeof(KlothoEngine)
            .GetField("_pendingRollbackTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo DesyncCountByPeerField = typeof(KlothoEngine)
            .GetField("_desyncCountByPeer", BindingFlags.NonPublic | BindingFlags.Instance);

        private static int LastMatched(KlothoEngine e) => (int)LastMatchedField.GetValue(e);
        private static int PrevMatched(KlothoEngine e) => (int)PrevMatchedField.GetValue(e);
        private static bool HasPendingRollback(KlothoEngine e) => (bool)HasPendingRollbackField.GetValue(e);
        private static int PendingRollbackTick(KlothoEngine e) => (int)PendingRollbackTickField.GetValue(e);
        private static void ClearPendingRollback(KlothoEngine e)
        {
            HasPendingRollbackField.SetValue(e, false);
            PendingRollbackTickField.SetValue(e, -1);
        }

        private static int PeerCount(KlothoEngine e, int peerId)
        {
            var dict = (Dictionary<int, int>)DesyncCountByPeerField.GetValue(e);
            return dict.TryGetValue(peerId, out int v) ? v : 0;
        }

        #endregion

        private KlothoEngine _engine;
        private MockSimulation _simulation;
        private MockNetworkService _networkService;
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Error));
            _logger = factory.CreateLogger("DesyncAnchorVetoUnitTests");
        }

        [SetUp]
        public void SetUp()
        {
            _simulation = new MockSimulation();
            _networkService = new MockNetworkService();
            _engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            _engine.Initialize(_simulation, _networkService, _logger);
            _engine.Start(enableRecording: false);
        }

        private void AdvanceToTick(int targetTick)
        {
            while (_engine.CurrentTick < targetTick)
                _engine.Update((_engine.TickInterval + 1) / 1000f);
        }

        // ── ① anchor veto order-independence ──────────────────────────────────────

        [Test]
        public void AnchorDemotedWhenMismatchFollowsMatchSameTick()
        {
            const int peer = 1, P = 10, T = 20;
            AdvanceToTick(30); // so RequestRollback(target < CurrentTick) records a pending tick

            _networkService.FireSyncHashCompared(P, peer, matched: true);  // anchor = 10, prev = 0
            _networkService.FireSyncHashCompared(T, peer, matched: true);  // anchor = 20, prev = 10

            // Mismatch for the just-promoted tick — match arrived first (order dependency).
            _networkService.FireDesyncDetected(peer, T);

            Assert.AreEqual(P, LastMatched(_engine),
                "Anchor must demote to the prior matched tick (P), not stay on the diverged tick T");
            Assert.IsTrue(HasPendingRollback(_engine), "rung-1 rollback must be requested");
            Assert.AreEqual(P, PendingRollbackTick(_engine),
                "Rollback target must be the demoted anchor P, not the poison tick T");
        }

        [Test]
        public void MismatchBeforeMatch_SameTick_VetoesPromotion()
        {
            const int peer = 1, T = 20;
            AdvanceToTick(30);

            _networkService.FireDesyncDetected(peer, T);                    // veto records T
            _networkService.FireSyncHashCompared(T, peer, matched: true);   // must NOT promote (tick <= veto)

            Assert.AreNotEqual(T, LastMatched(_engine),
                "A tick that already mismatched must not be promoted to the anchor (veto)");
        }

        [Test]
        public void MonotonicMatches_AdvanceAnchorAndKeepPrevHistory()
        {
            const int peer = 1, T = 20;

            _networkService.FireSyncHashCompared(T, peer, matched: true);       // anchor=20, prev=0
            _networkService.FireSyncHashCompared(T + 1, peer, matched: true);   // anchor=21, prev=20

            Assert.AreEqual(T + 1, LastMatched(_engine), "Anchor advances monotonically on matches");
            Assert.AreEqual(T, PrevMatched(_engine), "Prior anchor kept as 1-step history");
        }

        [Test]
        public void ExhaustedHistoryDesyncSkipsRollbackAndDefersToCounter()
        {
            const int peer = 1, P = 10, T = 20;
            AdvanceToTick(30);

            _networkService.FireSyncHashCompared(P, peer, matched: true);  // anchor=10, prev=0
            _networkService.FireSyncHashCompared(T, peer, matched: true);  // anchor=20, prev=10

            _networkService.FireDesyncDetected(peer, T);                   // demote anchor->10, rollback 10
            Assert.AreEqual(P, PendingRollbackTick(_engine), "First mismatch rolls back to prev (10)");

            ClearPendingRollback(_engine); // isolate the next request

            // Out-of-order mismatch for the now-anchor tick: rollbackTarget(10) >= tick(10) → skip.
            _networkService.FireDesyncDetected(peer, P);

            Assert.IsFalse(HasPendingRollback(_engine),
                "No known-good tick < the divergence — rung-1 must be skipped (futile rollback avoided)");
            Assert.AreEqual(2, PeerCount(_engine, peer),
                "The divergence is still counted toward per-peer escalation even though rung-1 was skipped");
        }

        // ── ② per-peer counter (masking removed) ──────────────────────────────────

        [Test]
        public void PersistentPeerDesyncEscalatesDespiteOtherPeerMatches()
        {
            const int peerB = 1, peerC = 2;

            // peerC diverges at three distinct ticks, with a peerB match interleaved each time.
            _networkService.FireDesyncDetected(peerC, 5);
            _networkService.FireSyncHashCompared(6, peerB, matched: true);
            _networkService.FireDesyncDetected(peerC, 7);
            _networkService.FireSyncHashCompared(8, peerB, matched: true);
            _networkService.FireDesyncDetected(peerC, 9); // peerC count reaches threshold (3) → resync

            Assert.AreEqual(1, _networkService.SendFullStateRequestCount,
                "A persistently-diverging peer must reach the resync threshold despite another peer's matches (masking removed)");
        }

        [Test]
        public void PeerMatchDoesNotClearOtherPeersCounter()
        {
            const int peerB = 1, peerC = 2;

            _networkService.FireDesyncDetected(peerC, 5);   // C count = 1
            _networkService.FireDesyncDetected(peerC, 7);   // C count = 2
            _networkService.FireSyncHashCompared(8, peerB, matched: true); // B forward match — must not touch C

            Assert.AreEqual(2, PeerCount(_engine, peerC),
                "A match with peer B must not clear peer C's accumulation");

            _networkService.FireSyncHashCompared(9, peerC, matched: true); // C forward match clears C
            Assert.AreEqual(0, PeerCount(_engine, peerC),
                "A forward match with peer C clears its own counter");
        }

        [Test]
        public void SamePeerSameTick_DuplicateReports_CountOnce()
        {
            const int peerC = 2;

            _networkService.FireDesyncDetected(peerC, 5);
            _networkService.FireDesyncDetected(peerC, 5);
            _networkService.FireDesyncDetected(peerC, 5);

            Assert.AreEqual(1, PeerCount(_engine, peerC),
                "Duplicate reports for one (peer, tick) count once (per-peer V1-E3 dedup)");
        }

        [Test]
        public void PeerCounterResetUsesPerPeerNotGlobalVeto()
        {
            const int peerP = 1, peerQ = 2;

            _networkService.FireDesyncDetected(peerP, 100);  // P count = 1, P last-mismatch = 100
            _networkService.FireDesyncDetected(peerQ, 105);  // Q diverges later — raises the GLOBAL veto to 105

            // peerP recovers at 101: forward of P's own last-mismatch (100), so P's counter clears
            // even though the global veto (105) is higher.
            _networkService.FireSyncHashCompared(101, peerP, matched: true);

            Assert.AreEqual(0, PeerCount(_engine, peerP),
                "Peer recovery match must clear that peer's counter via its own last-mismatch gate, not the global veto (C3)");
            Assert.AreEqual(1, PeerCount(_engine, peerQ),
                "Peer Q's accumulation is untouched by peer P's recovery");
        }
    }
}
