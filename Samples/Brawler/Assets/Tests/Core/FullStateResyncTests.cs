#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.TestTools;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Full state resync test.
    /// Validates resync trigger conditions and state machine behavior.
    /// </summary>
    [TestFixture]
    public class FullStateResyncTests
    {
        #region Mocks

        private class MockSimulation : ISimulation
        {
            public int CurrentTick { get; private set; }
            public long StateHash { get; set; } = 12345L;
            public byte[] FullStateData { get; set; } = new byte[] { 1, 2, 3, 4 };
            public int RestoreCallCount { get; private set; }
            public int SerializeCallCount { get; private set; }

            public void Initialize() { CurrentTick = 0; }
            public void Tick(List<ICommand> commands) { CurrentTick++; }
            // Engine per-tick save trigger — no-op: this mock keeps no restorable history.
            public void SaveSnapshot() { }

            // No internal snapshot history — engine rollback resolve always fails (escalation paths under test).
            public int GetNearestRollbackTick(int targetTick) => -1;

            public void Rollback(int targetTick) { CurrentTick = targetTick; }
            public long GetStateHash() => StateHash;
            public void Reset() { CurrentTick = 0; }

            public void RestoreFromFullState(byte[] stateData)
            {
                RestoreCallCount++;
                if (stateData != null && stateData.Length >= 8)
                    StateHash = BitConverter.ToInt64(stateData, 0);
            }

            public byte[] SerializeFullState()
            {
                SerializeCallCount++;
                return BitConverter.GetBytes(StateHash);
            }

            public void EmitSyncEvents() { }

            public event System.Action<int> OnPlayerJoinedNotification;
            public void OnPlayerJoined(int playerId, int tick) { }

            public (byte[] data, long hash) SerializeFullStateWithHash()
            {
                SerializeCallCount++;
                return (BitConverter.GetBytes(StateHash), StateHash);
            }
        }

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

            public int SendFullStateRequestCallCount { get; private set; }
            public int LastRequestTick { get; private set; }
            public int SendFullStateResponseCallCount { get; private set; }
            public List<(int peerId, int tick, byte[] stateData, long stateHash)> FullStateResponses { get; }
                = new List<(int, int, byte[], long)>();

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
            public void SendSyncHash(int tick, long hash, long cmdHash) { }
            public void InvalidateLocalSyncHashes(int fromTick) { }

            public int InvalidateSyncHashesCallCount { get; private set; }
            public int LastInvalidateSyncHashesTick { get; private set; } = int.MinValue;
            public void InvalidateSyncHashes(int fromTick)
            {
                InvalidateSyncHashesCallCount++;
                LastInvalidateSyncHashesTick = fromTick;
            }
            public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash) { }
            public void BroadcastMatchAbort(byte reason) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SetLocalTick(int tick) { }
            public void SetLocalAdvantage(int advantage) { }

            public void SendFullStateRequest(int currentTick)
            {
                SendFullStateRequestCallCount++;
                LastRequestTick = currentTick;
            }

            public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash)
            {
                SendFullStateResponseCallCount++;
                FullStateResponses.Add((peerId, tick, stateData, stateHash));
            }

            public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast) { }

            public void FireSyncHashCompared(int tick, int peerId, bool matched)
            {
                OnSyncHashCompared?.Invoke(tick, peerId, matched);
            }

            public void FireDesyncDetected(int playerId, int tick, long localHash, long remoteHash)
            {
                OnDesyncDetected?.Invoke(playerId, tick, localHash, remoteHash);
            }

            public void FireFullStateReceived(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast)
            {
                OnFullStateReceived?.Invoke(tick, stateData, stateHash, kind);
            }

            public void FireFullStateRequested(int peerId, int requestTick)
            {
                OnFullStateRequested?.Invoke(peerId, requestTick);
            }

            public void SendPlayerConfig(int playerId, xpTURN.Klotho.Core.PlayerConfigBase playerConfig) { }

            // Suppress unused event warnings
            internal void SuppressWarnings()
            {
                OnGameStart?.Invoke();
                OnCountdownStarted?.Invoke(0);
                OnPlayerJoined?.Invoke(null);
                OnPlayerLeft?.Invoke(null);
                OnCommandReceived?.Invoke(null);
                OnFrameAdvantageReceived?.Invoke(0, 0, 0);
                OnSyncHashCompared?.Invoke(0, 0, false);
                OnResyncFailureReported?.Invoke(0, 0);
                OnMatchAbortReceived?.Invoke(0);
                OnLocalPlayerIdAssigned?.Invoke(0);
            }
        }

        #endregion

        private KlothoEngine _engine;
        private MockSimulation _simulation;
        private MockNetworkService _networkService;

        IKLogger _logger = null;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Configure LoggerFactory (same as ZLogger)
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
            });

            _logger = loggerFactory.CreateLogger("Tests");
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

        /// <summary>
        /// Calls Update with sufficient deltaTime to advance the engine past the specified tick.
        /// </summary>
        private void AdvanceToTick(int targetTick)
        {
            while (_engine.CurrentTick < targetTick)
                _engine.Update((_engine.TickInterval + 1) / 1000f);
        }

        private static void AdvanceEngine(KlothoEngine engine, int targetTick)
        {
            while (engine.CurrentTick < targetTick)
                engine.Update((engine.TickInterval + 1) / 1000f);
        }

        #region Tests #10~#13: Resync triggers

        /// <summary>
        /// #10: When guard 2 rollback fails (too far, no snapshot), RequestFullStateResync is fired
        /// </summary>
        [Test]
        public void RollbackTooFar_TriggersResync()
        {
            // Advance past MaxRollbackTicks(50)
            AdvanceToTick(60);
            Assert.GreaterOrEqual(_engine.CurrentTick, 60);

            // Request rollback to tick 5 — too far (CurrentTick - MaxRollbackTicks = 10)
            // No snapshot (mock sim) -> guard 2 fails -> RequestFullStateResync
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            _engine.RequestRollback(5);
            _engine.Update(0.001f); // process pending rollback

            Assert.AreEqual(1, _networkService.SendFullStateRequestCallCount);
        }

        /// <summary>
        /// #11: After 3 consecutive desyncs, RequestFullStateResync is fired.
        /// Reports target DISTINCT sync ticks — duplicate reports for the same tick count once
        /// (N peers reporting one diverged tick must not skip rung 1).
        /// </summary>
        [Test]
        public void ConsecutiveDesync_TriggersResync()
        {
            AdvanceToTick(10);

            for (int i = 0; i < 3; i++)
                LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("Desync detected"));

            for (int i = 0; i < 3; i++)
                _networkService.FireDesyncDetected(1, 5 + i, 111, 222);

            Assert.AreEqual(1, _networkService.SendFullStateRequestCallCount);
        }

        /// <summary>
        /// #11b: Duplicate desync reports for the SAME sync tick count once — they must not
        /// multiply the consecutive counter and jump straight to resync.
        /// </summary>
        [Test]
        public void DuplicateDesyncReports_SameTick_CountOnce()
        {
            AdvanceToTick(10);

            for (int i = 0; i < 3; i++)
                LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("Desync detected"));

            for (int i = 0; i < 3; i++)
                _networkService.FireDesyncDetected(1, 5, 111, 222);

            Assert.AreEqual(0, _networkService.SendFullStateRequestCallCount,
                "Three reports for one tick are a single desync occurrence — no escalation");
        }

        /// <summary>
        /// #12: A single desync triggers only normal rollback and does not fire resync
        /// </summary>
        [Test]
        public void SingleDesync_NoResync()
        {
            AdvanceToTick(10);

            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("Desync detected"));
            _networkService.FireDesyncDetected(1, 5, 111, 222);

            Assert.AreEqual(0, _networkService.SendFullStateRequestCallCount);
        }

        /// <summary>
        /// #13: When SyncHash matches, the consecutive desync counter is reset.
        /// Without reset, 2+2=4 desyncs would trigger resync, but after reset each batch stays at 2.
        /// A larger InputDelay is used so the sync check tick runs through the ExecuteTick path.
        /// </summary>
        [Test]
        public void DesyncReset_OnSyncMatch()
        {
            // Recreate engine with custom config for this test
            _simulation = new MockSimulation();
            _networkService = new MockNetworkService();
            _engine = new KlothoEngine(
                new SimulationConfig
                {
                    InputDelayTicks = 40,  // Pre-fill ticks 0~39 with EmptyCommand
                    SyncCheckInterval = 10,
                    MaxRollbackTicks = 50
                },
                new SessionConfig());
            _engine.Initialize(_simulation, _networkService, _logger);
            _engine.Start(enableRecording: false);

            // Advance past the first sync check (tick 10)
            AdvanceToTick(12);

            // Fire 2 reports for the same tick — dedup counts once (count=1, below threshold of 3)
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("Desync detected"));
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("Desync detected"));
            _networkService.FireDesyncDetected(1, 5, 111, 222);
            _networkService.FireDesyncDetected(1, 5, 111, 222);

            // With no matched anchor yet (anchor=0), the rung-1 target would be the
            // desync tick itself — a futile rollback-to-self — so it is skipped (rollbackTarget >= tick),
            // not attempted. No "[Rollback] failed" here (previously it rolled back to tick 5 and failed).
            AdvanceToTick(22);

            // Event-based promotion: a matched hash comparison advances the anchor
            // and resets _consecutiveDesyncCount. Absence of comparisons no longer promotes.
            _networkService.FireSyncHashCompared(20, 1, true); // peer 1 (the desyncing peer) — clears its per-peer counter
            AdvanceToTick(32);

            // Fire 2 additional reports for one tick (count 0->1 after dedup, below threshold)
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("Desync detected"));
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("Desync detected"));
            _networkService.FireDesyncDetected(1, 25, 111, 222);
            _networkService.FireDesyncDetected(1, 25, 111, 222);

            // Without the matched-compare reset, the accumulated count could keep growing across
            // distinct ticks and eventually escalate; after the reset it restarts from 0.
            // After the reset, count is 2 so no resync.
            Assert.AreEqual(0, _networkService.SendFullStateRequestCallCount);
        }

        #endregion

        #region Rollback cache invalidation + SyncHash invalidation wiring

        // Mirrors the proven successful-rollback setup (PendingRollbackHashSendTests): a
        // deterministic-hash sim with per-tick snapshots (so ExecuteRollback resolves) and the
        // input-delay pre-fill so the rolled-back ticks have all commands.
        private KlothoEngine BuildHostEngineWithRollback(out TestSimulation sim, out MockNetworkService net)
        {
            sim = new TestSimulation { UseDeterministicHash = true };
            net = new MockNetworkService { IsHost = true };
            var engine = new KlothoEngine(
                new SimulationConfig { InputDelayTicks = 40, SyncCheckInterval = 10, MaxRollbackTicks = 50 },
                new SessionConfig());
            engine.Initialize(sim, net, _logger);
            engine.SetCommandFactory(new CommandFactory());
            engine.Start(enableRecording: false);
            return engine;
        }

        /// <summary>
        /// A successful rollback must invalidate the host's serialized FullState cache.
        /// Rollback+resim leaves CurrentTick unchanged while the same-tick state bytes may differ,
        /// so the next unicast serve must RE-SERIALIZE the current (rolled-back) state rather than
        /// returning the stale cached bytes (detected via the sim's serialize-call count).
        /// </summary>
        [Test]
        public void Rollback_InvalidatesHostFullStateCache_R3()
        {
            var engine = BuildHostEngineWithRollback(out var sim, out var net);

            AdvanceEngine(engine, 30);

            // Serve #1 — caches the serialized state at CurrentTick = 30.
            net.FireFullStateRequested(peerId: 1, requestTick: 30);
            Assert.AreEqual(1, net.FullStateResponses.Count);
            int serializeAfterServe1 = sim.SerializeCallCount;

            // A successful rollback invalidates the cache; the resim leaves CurrentTick unchanged.
            engine.RequestRollback(25);
            engine.Update(0.001f); // flush pending rollback (no full tick advance at 1ms)
            Assert.AreEqual(30, engine.CurrentTick, "rollback+resim must leave CurrentTick unchanged");

            // Serve #2 — cache was invalidated → re-serialize.
            net.FireFullStateRequested(peerId: 1, requestTick: 30);
            Assert.AreEqual(2, net.FullStateResponses.Count);
            Assert.Greater(sim.SerializeCallCount, serializeAfterServe1,
                "rollback must invalidate the host FullState cache so the next serve re-serializes");
        }

        /// <summary>
        /// Regression: with no rollback between two serves at the same CurrentTick, the cache
        /// stays valid — the second serve hits the cache (no re-serialize), proving the invalidation
        /// is rollback-driven and the normal cache path is unchanged.
        /// </summary>
        [Test]
        public void Serve_WithoutRollback_HitsCache_R3Regression()
        {
            var engine = BuildHostEngineWithRollback(out var sim, out var net);

            AdvanceEngine(engine, 30);

            net.FireFullStateRequested(peerId: 1, requestTick: 30); // serve #1 — cache fill at tick 30
            int serializeAfterServe1 = sim.SerializeCallCount;

            net.FireFullStateRequested(peerId: 1, requestTick: 30); // serve #2 — same CurrentTick, no rollback

            Assert.AreEqual(2, net.FullStateResponses.Count);
            Assert.AreEqual(serializeAfterServe1, sim.SerializeCallCount,
                "without a rollback the cache is valid — the second serve does not re-serialize");
        }

        /// <summary>
        /// SyncHash invalidation wiring: applying a FullState (resync) must invalidate the network layer's sync
        /// hashes for ticks >= the apply tick, so the post-apply recompute is not compared against
        /// a pre-reset remote hash across the reset boundary (false mismatch). This pins the engine
        /// → network-service call; the dict-clearing behavior is covered at the network-service level.
        /// </summary>
        [Test]
        public void ResyncApply_InvalidatesNetworkSyncHashes_DF7()
        {
            AdvanceToTick(60);

            // Trigger resync via guard-2 rollback failure (mock sim has no snapshot history).
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            _engine.RequestRollback(5);
            _engine.Update(0.001f); // -> resync requested

            Assert.AreEqual(0, _networkService.InvalidateSyncHashesCallCount,
                "no sync-hash invalidation before the FullState is applied");

            // Apply the resync FullState at tick 100.
            _networkService.FireFullStateReceived(100, new byte[] { 1, 2, 3, 4 }, 12345L);

            Assert.AreEqual(1, _networkService.InvalidateSyncHashesCallCount,
                "FullState apply must invalidate the network sync hashes (D-F7)");
            Assert.AreEqual(100, _networkService.LastInvalidateSyncHashesTick,
                "invalidation must use the apply tick as the lower bound");
        }

        #endregion

        #region Tests #14~#19: Resync state machine

        /// <summary>
        /// #14: While resync is in the Requested state, tick advancement is paused
        /// </summary>
        [Test]
        public void Resync_PausesTicks()
        {
            AdvanceToTick(60);

            // Trigger resync via guard 2 failure
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            _engine.RequestRollback(5);
            _engine.Update(0.001f); // process -> resync request

            int tickAfterResync = _engine.CurrentTick;

            // Update with enough time for 4 ticks — must not advance
            _engine.Update(0.1f);

            Assert.AreEqual(tickAfterResync, _engine.CurrentTick);
        }

        /// <summary>
        /// #15: Tick advancement resumes after FullStateResponse is received
        /// </summary>
        [Test]
        public void Resync_ResumesAfterResponse()
        {
            AdvanceToTick(60);

            // Trigger resync
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            _engine.RequestRollback(5);
            _engine.Update(0.001f);

            // Send full state response — restore to tick 100
            _networkService.FireFullStateReceived(100, new byte[] { 1, 2, 3, 4 }, 12345L);

            Assert.AreEqual(100, _engine.CurrentTick);

            // Ticks should now advance
            _engine.Update(0.1f);
            Assert.Greater(_engine.CurrentTick, 100);
        }

        /// <summary>
        /// #16: After timeout (5 seconds), resync is automatically retried
        /// </summary>
        [Test]
        public void Resync_Timeout_Retries()
        {
            AdvanceToTick(60);

            // Trigger initial resync
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            _engine.RequestRollback(5);
            _engine.Update(0.001f);
            Assert.AreEqual(1, _networkService.SendFullStateRequestCallCount);

            // Timeout after 5 seconds -> retry
            _engine.Update(5.1f);
            Assert.AreEqual(2, _networkService.SendFullStateRequestCallCount);
        }

        /// <summary>
        /// #17: When max retry count (3) is exceeded, OnResyncFailed fires
        /// </summary>
        [Test]
        public void Resync_MaxRetries_Fails()
        {
            AdvanceToTick(60);

            bool resyncFailed = false;
            _engine.OnResyncFailed += () => resyncFailed = true;

            // Trigger initial resync (attempt 1/3)
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            _engine.RequestRollback(5);
            _engine.Update(0.001f);

            // 3 timeouts -> attempts 2/3, 3/3, then the 4th call exceeds the max count
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("max retry count exceeded"));
            for (int i = 0; i < 3; i++)
                _engine.Update(5.1f);

            Assert.IsTrue(resyncFailed);
            Assert.AreEqual(3, _networkService.SendFullStateRequestCallCount,
                "Must send 3 requests (attempts 1, 2, 3) before failing on the 4th");
        }

        /// <summary>
        /// #18: While already in the Requested state, duplicate RequestFullStateResync calls are ignored
        /// </summary>
        [Test]
        public void Resync_DuplicateRequest_Ignored()
        {
            AdvanceToTick(60);

            // Trigger resync
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            _engine.RequestRollback(5);
            _engine.Update(0.001f);
            Assert.AreEqual(1, _networkService.SendFullStateRequestCallCount);

            // Fire 3 desyncs while in Requested state — all ignored
            for (int i = 0; i < 3; i++)
                LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("Desync detected"));
            for (int i = 0; i < 3; i++)
                _networkService.FireDesyncDetected(1, 55, 111, 222);

            Assert.AreEqual(1, _networkService.SendFullStateRequestCallCount,
                "No additional requests while resync is in progress");
        }

        /// <summary>
        /// #19: Host never triggers resync (host holds authoritative state)
        /// </summary>
        [Test]
        public void Resync_HostIgnores()
        {
            _networkService.IsHost = true;
            AdvanceToTick(60);

            // Fire 3 consecutive desyncs — on a client this would trigger resync
            for (int i = 0; i < 3; i++)
                LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("Desync detected"));
            for (int i = 0; i < 3; i++)
                _networkService.FireDesyncDetected(1, 55, 111, 222);

            Assert.AreEqual(0, _networkService.SendFullStateRequestCallCount,
                "Host must not request full state from itself");
        }

        #endregion

        #region Tests #20~#24: 3+ player scenarios

        /// <summary>
        /// #20: Two clients request simultaneously -> each receives an independent FullStateResponse
        /// </summary>
        [Test]
        public void Resync_3P_SimultaneousRequests()
        {
            // Configure host engine
            _networkService.IsHost = true;
            AdvanceToTick(10);

            // Two different peers request full state simultaneously
            _networkService.FireFullStateRequested(1, 8);
            _networkService.FireFullStateRequested(2, 9);

            Assert.AreEqual(2, _networkService.SendFullStateResponseCallCount);
            Assert.AreEqual(1, _networkService.FullStateResponses[0].peerId);
            Assert.AreEqual(2, _networkService.FullStateResponses[1].peerId);
        }

        /// <summary>
        /// #21: Multiple requests within the same tick -> host serializes only once (cache hit)
        /// </summary>
        [Test]
        public void Resync_3P_CacheHit()
        {
            // Configure host engine
            _networkService.IsHost = true;
            AdvanceToTick(10);

            // Two peers request on the same tick
            _networkService.FireFullStateRequested(1, 8);
            _networkService.FireFullStateRequested(2, 9);

            // Serialization must run only once (the second call is a cache hit)
            Assert.AreEqual(1, _simulation.SerializeCallCount,
                "Host must serialize only once per tick (cache hit)");
            Assert.AreEqual(2, _networkService.SendFullStateResponseCallCount);

            // Both responses must hold the same state data reference
            Assert.AreSame(
                _networkService.FullStateResponses[0].stateData,
                _networkService.FullStateResponses[1].stateData,
                "Cached state data must be reused");
        }

        /// <summary>
        /// #22: Consecutive requests from the same peer -> cooldown throttling (KlothoNetworkService level)
        /// </summary>
        [Test]
        public void Resync_3P_RateLimiting()
        {
            TestTransport.Reset();
            StreamPool.Clear();

            var hostTransport = new TestTransport();
            var clientTransport = new TestTransport();
            var commandFactory = new CommandFactory();

            var hostService = new KlothoNetworkService();
            var clientService = new KlothoNetworkService();

            hostService.Initialize(hostTransport, commandFactory, _logger);
            clientService.Initialize(clientTransport, commandFactory, _logger);

            // Setup: host creates room, client joins, complete handshake
            hostTransport.Listen("localhost", 7777, 4);
            hostService.CreateRoom("test", 4);
            clientService.JoinRoom("test");
            clientTransport.Connect("localhost", 7777);

            for (int i = 0; i < 12; i++)
            {
                hostService.Update();
                clientService.Update();
            }

            // Track host's OnFullStateRequested call count
            int requestedCount = 0;
            hostService.OnFullStateRequested += (peerId, tick) => requestedCount++;

            // Client first request -> must pass through
            clientService.SendFullStateRequest(10);
            hostService.Update();
            clientService.Update();

            Assert.AreEqual(1, requestedCount, "First request must pass the rate limiter");

            // Client second request sent immediately -> must be throttled (within 2s cooldown)
            clientService.SendFullStateRequest(11);
            hostService.Update();
            clientService.Update();

            Assert.AreEqual(1, requestedCount,
                "Second request within cooldown must be throttled");

            TestTransport.Reset();
        }

        /// <summary>
        /// #23: Client A resyncs while client B is normal -> B is unaffected (ticks keep advancing)
        /// </summary>
        [Test]
        public void Resync_3P_PartialResync()
        {
            // Client A: enters resync
            var simA = new MockSimulation();
            var netA = new MockNetworkService();
            var engineA = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engineA.Initialize(simA, netA, _logger);
            engineA.Start(enableRecording: false);

            // Client B: normal operation
            var simB = new MockSimulation();
            var netB = new MockNetworkService();
            var engineB = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engineB.Initialize(simB, netB, _logger);
            engineB.Start(enableRecording: false);

            // Advance both to tick 60
            AdvanceEngine(engineA, 60);
            AdvanceEngine(engineB, 60);

            Assert.GreaterOrEqual(engineA.CurrentTick, 60);
            Assert.GreaterOrEqual(engineB.CurrentTick, 60);

            // Trigger resync on A via guard 2 failure
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            engineA.RequestRollback(5);
            engineA.Update(0.001f);
            Assert.AreEqual(1, netA.SendFullStateRequestCallCount, "A must request resync");

            int tickA_before = engineA.CurrentTick;
            int tickB_before = engineB.CurrentTick;

            // Update both with enough time for several ticks
            engineA.Update(0.1f);
            engineB.Update(0.1f);

            // A must be paused (resync in progress)
            Assert.AreEqual(tickA_before, engineA.CurrentTick,
                "Client A must be paused during resync");

            // B must keep advancing normally
            Assert.Greater(engineB.CurrentTick, tickB_before,
                "Client B must continue advancing ticks normally");
        }

        /// <summary>
        /// #24: While A is resyncing, the B-host SyncHash match is maintained
        /// </summary>
        [Test]
        public void Resync_3P_UnaffectedClientNoDesync()
        {
            // Client A: enters resync
            var simA = new MockSimulation();
            var netA = new MockNetworkService();
            var engineA = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engineA.Initialize(simA, netA, _logger);
            engineA.Start(enableRecording: false);

            // Client B: normal, hash matches host
            var simB = new MockSimulation();
            simB.StateHash = 99999L;
            var netB = new MockNetworkService();
            var engineB = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engineB.Initialize(simB, netB, _logger);
            engineB.Start(enableRecording: false);

            // Advance both
            AdvanceEngine(engineA, 60);
            AdvanceEngine(engineB, 60);

            // Trigger resync on A
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            engineA.RequestRollback(5);
            engineA.Update(0.001f);
            Assert.AreEqual(1, netA.SendFullStateRequestCallCount);

            // B receives matching sync hash from host -> no desync
            bool bDesyncDetected = false;
            engineB.OnDesyncDetected += (localHash, remoteHash) => bDesyncDetected = true;

            // Advance B further — must not produce a desync
            engineB.Update(0.2f);

            Assert.IsFalse(bDesyncDetected,
                "Client B must not detect a desync while A is resyncing");
            Assert.AreEqual(0, netB.SendFullStateRequestCallCount,
                "Client B must not request a resync");
        }

        #endregion

        #region Tests #25~#27: End-to-end

        /// <summary>
        /// Helper: bridges the host<->client resync flow using mock network services.
        /// Client triggers resync -> host serializes state -> client receives and restores.
        /// Returns the restored tick.
        /// </summary>
        private int BridgeResync(
            KlothoEngine hostEngine, MockSimulation hostSim, MockNetworkService hostNet,
            KlothoEngine clientEngine, MockSimulation clientSim, MockNetworkService clientNet,
            int clientPeerId = 1)
        {
            // 1. Host processes the request
            hostNet.FireFullStateRequested(clientPeerId, clientEngine.CurrentTick);

            // 2. Capture host's response
            Assert.GreaterOrEqual(hostNet.FullStateResponses.Count, 1, "Host must have sent a response");
            var response = hostNet.FullStateResponses[hostNet.FullStateResponses.Count - 1];

            // 3. Deliver to client
            clientNet.FireFullStateReceived(response.tick, response.stateData, response.stateHash);

            return response.tick;
        }

        /// <summary>
        /// #25: After full state restore, client's StateHash matches host's StateHash
        /// </summary>
        [Test]
        public void Resync_StateMatch_AfterRestore()
        {
            // Host engine
            var hostSim = new MockSimulation { StateHash = 77777L };
            var hostNet = new MockNetworkService { IsHost = true };
            var hostEngine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            hostEngine.Initialize(hostSim, hostNet, _logger);
            hostEngine.Start(enableRecording: false);

            // Client engine with a different hash
            var clientSim = new MockSimulation { StateHash = 12345L };
            var clientNet = new MockNetworkService { IsHost = false };
            var clientEngine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            clientEngine.Initialize(clientSim, clientNet, _logger);
            clientEngine.Start(enableRecording: false);

            // Advance both to tick 60
            AdvanceEngine(hostEngine, 60);
            AdvanceEngine(clientEngine, 60);

            // Verify hashes differ before resync
            Assert.AreNotEqual(hostSim.GetStateHash(), clientSim.GetStateHash());

            // Client triggers resync via guard 2 failure
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            clientEngine.RequestRollback(5);
            clientEngine.Update(0.001f);
            Assert.AreEqual(1, clientNet.SendFullStateRequestCallCount);

            // Bridge resync: host serializes -> client restores
            int restoredTick = BridgeResync(hostEngine, hostSim, hostNet, clientEngine, clientSim, clientNet);

            // After restore, hashes must match
            Assert.AreEqual(hostSim.GetStateHash(), clientSim.GetStateHash(),
                "After full state restore, client hash must match host hash");
            Assert.AreEqual(restoredTick, clientEngine.CurrentTick,
                "Client tick must match the restored tick");
        }

        /// <summary>
        /// #26: After restore, normal tick advancement continues and SyncHash stays consistent
        /// </summary>
        [Test]
        public void Resync_ContinuePlaying()
        {
            // Host engine
            var hostSim = new MockSimulation { StateHash = 77777L };
            var hostNet = new MockNetworkService { IsHost = true };
            var hostEngine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            hostEngine.Initialize(hostSim, hostNet, _logger);
            hostEngine.Start(enableRecording: false);

            // Client engine
            var clientSim = new MockSimulation { StateHash = 12345L };
            var clientNet = new MockNetworkService { IsHost = false };
            var clientEngine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            clientEngine.Initialize(clientSim, clientNet, _logger);
            clientEngine.Start(enableRecording: false);

            // Advance both
            AdvanceEngine(hostEngine, 60);
            AdvanceEngine(clientEngine, 60);

            // Trigger client resync
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            clientEngine.RequestRollback(5);
            clientEngine.Update(0.001f);

            // Bridge resync
            int restoredTick = BridgeResync(hostEngine, hostSim, hostNet, clientEngine, clientSim, clientNet);

            // Verify tick advancement resumes
            int tickAfterRestore = clientEngine.CurrentTick;
            clientEngine.Update(0.1f);
            Assert.Greater(clientEngine.CurrentTick, tickAfterRestore,
                "Client must resume tick advancement after resync");

            // Verify no desync occurs during continued play
            bool desyncFired = false;
            clientEngine.OnDesyncDetected += (localHash, remoteHash) => desyncFired = true;
            clientEngine.Update(0.2f);

            Assert.IsFalse(desyncFired,
                "No desync must occur during normal play after a successful resync");

            // Hash still matches host
            Assert.AreEqual(hostSim.GetStateHash(), clientSim.GetStateHash(),
                "After continued play, client hash must stay consistent with host");
        }

        /// <summary>
        /// #27: After restore, old snapshots are cleared — rollback to a tick before resync fails
        /// </summary>
        [Test]
        public void Resync_SnapshotManagerCleared()
        {
            // Client engine
            var clientSim = new MockSimulation { StateHash = 77777L };
            var clientNet = new MockNetworkService { IsHost = false };
            var clientEngine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            clientEngine.Initialize(clientSim, clientNet, _logger);
            clientEngine.Start(enableRecording: false);

            // Host engine (for bridging)
            var hostSim = new MockSimulation { StateHash = 77777L };
            var hostNet = new MockNetworkService { IsHost = true };
            var hostEngine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            hostEngine.Initialize(hostSim, hostNet, _logger);
            hostEngine.Start(enableRecording: false);

            // Subscribe to OnResyncCompleted before triggering resync
            int resyncCompletedTick = -1;
            clientEngine.OnResyncCompleted += tick => resyncCompletedTick = tick;

            // Track rollback failures
            int rollbackFailCount = 0;
            clientEngine.OnRollbackFailed += (tick, reason) => rollbackFailCount++;

            // Advance both to tick 60
            AdvanceEngine(clientEngine, 60);
            AdvanceEngine(hostEngine, 60);

            // Trigger client resync
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            clientEngine.RequestRollback(5);
            clientEngine.Update(0.001f);

            // Bridge resync — restore to host's current tick
            int restoredTick = BridgeResync(hostEngine, hostSim, hostNet, clientEngine, clientSim, clientNet);

            // Verify OnResyncCompleted fired with the correct tick
            Assert.AreEqual(restoredTick, resyncCompletedTick,
                "OnResyncCompleted must fire with the restored tick");

            // Advance the client a few ticks past the restored tick
            clientEngine.Update(0.05f);
            int postResyncTick = clientEngine.CurrentTick;
            Assert.Greater(postResyncTick, restoredTick);

            // Attempt rollback to a tick before restore
            // ClearAll() was called during resync so old snapshots are gone
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"\[Rollback\] failed"));
            clientEngine.RequestRollback(restoredTick - 5);
            clientEngine.Update(0.001f);

            Assert.GreaterOrEqual(rollbackFailCount, 1,
                "Rollback to a tick before resync must fail (snapshots cleared)");
        }

        #endregion
    }
}
