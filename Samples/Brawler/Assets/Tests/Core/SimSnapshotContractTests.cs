#pragma warning disable CS0067 // Events on the mock service are required by the interface but never raised in tests.
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// ISimulation snapshot-contract unification (per-tick save trigger).
    ///
    ///   (1) Non-ECS engine-driven rollback succeeds end-to-end: the engine's per-tick
    ///       SaveSnapshot trigger populates the simulation's own history (counter-verified),
    ///       RequestRollback resolves and executes against it, and the post-resim state matches
    ///       the untouched straight-line peer. Impossible to construct before this unification — the
    ///       engine never called save on non-ECS simulations.
    ///   (2) NoSnapshot failure behavior, fixed per mode (current behavior records the
    ///       adoption-vs-implementation deviation): P2P main path = OnRollbackFailed
    ///       (NoSnapshot) only, no rollback executed, no resync requested; SD-client verified
    ///       batch restore = SendFullStateRequest + batch discard.
    ///   (3) Timeline invariant: RestoreFromFullState drops pre-restore history —
    ///       stale ticks must not be exposed via GetNearestRollbackTick.
    /// </summary>
    [TestFixture]
    public class SimSnapshotContractTests
    {
        // ── Reflection handles ──────────────────────────────────────────

        private static readonly Type _engineType = typeof(KlothoEngine);

        private static readonly FieldInfo _resyncStateField =
            _engineType.GetField("_resyncState", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _stateField =
            _engineType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _serverDrivenNetworkField =
            _engineType.GetField("_serverDrivenNetwork", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _dispatcherField =
            _engineType.GetField("_dispatcher", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _executeClientPredictionTickMethod =
            _engineType.GetMethod("ExecuteClientPredictionTick",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _handleVerifiedStateReceivedMethod =
            _engineType.GetMethod("HandleVerifiedStateReceived",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _processVerifiedBatchMethod =
            _engineType.GetMethod("ProcessVerifiedBatch",
                BindingFlags.NonPublic | BindingFlags.Instance);

        // ── Non-ECS simulation that keeps no restorable history ─────────

        // Mirrors the MockSimulation pattern of LateJoin/Spectator/FullStateResync tests:
        // the per-tick save trigger is a no-op and the resolve query honors the result
        // invariant by returning -1 (no tick is restorable on the current timeline).
        private sealed class NoHistorySimulation : ISimulation
        {
            public int CurrentTick { get; private set; }
            public void Initialize() { CurrentTick = 0; }
            public void Tick(List<ICommand> commands) { CurrentTick++; }
            public void SaveSnapshot() { }
            public int GetNearestRollbackTick(int targetTick) => -1;
            public void Rollback(int targetTick) { CurrentTick = targetTick; }
            public long GetStateHash() => 12345L;
            public void Reset() { CurrentTick = 0; }
            public void RestoreFromFullState(byte[] stateData) { }
            public byte[] SerializeFullState() => BitConverter.GetBytes(12345L);
            public (byte[] data, long hash) SerializeFullStateWithHash() => (SerializeFullState(), 12345L);
            public void EmitSyncEvents() { }
            public event Action<int> OnPlayerJoinedNotification;
            public void OnPlayerJoined(int playerId, int tick) => OnPlayerJoinedNotification?.Invoke(playerId);
        }

        // ── Minimal SD network stub (EventDispatchSDClientTests pattern + request counter) ──

        private sealed class MockSDNetworkService : IServerDrivenNetworkService
        {
            public SessionPhase Phase => SessionPhase.Playing;
            private SharedTimeClock _sharedClock = new SharedTimeClock(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
            public SharedTimeClock SharedClock => _sharedClock;
            public int PlayerCount => 1;
            public int SpectatorCount => 0;
            public int PendingLateJoinCatchupCount => 0;
            public bool AllPlayersReady => true;
            public int LocalPlayerId => 0;
            public bool IsHost => false;
            public int RandomSeed => 42;
            public IReadOnlyList<IPlayerInfo> Players => Array.Empty<IPlayerInfo>();
            public bool IsServer => false;

            public int SendFullStateRequestCount { get; private set; }

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
            public event Action<int, IReadOnlyList<ICommand>, long> OnVerifiedStateReceived;
            public event Action<int> OnInputAckReceived;
            public event Action<int, byte[], long> OnServerFullStateReceived;
            public event Action<int, long> OnBootstrapBegin;
            public event Action<int, int, RejectionReason> OnCommandRejected;
            public event Action<SessionPhase> OnPhaseChanged;
            public event Action<int> OnPlayerCountChanged;
            public event Action<bool> OnAllPlayersReadyChanged;

            public void Initialize(INetworkTransport t, ICommandFactory f, IKLogger l) { }
            public void CreateRoom(string n, int m) { }
            public void JoinRoom(string n) { }
            public void LeaveRoom(bool keepReconnectCredentials = false) { }
            public void SetReady(bool r) { }
            public void SendCommand(ICommand c) { }
            public void RequestCommandsForTick(int tick) { }
            public void SendSyncHash(int tick, long hash, long cmdHash) { }
            public void InvalidateLocalSyncHashes(int fromTick) { }
            public void InvalidateSyncHashes(int fromTick) { }
            public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash) { }
            public void BroadcastMatchAbort(byte reason) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SetLocalTick(int tick) { }
            public void SetLocalAdvantage(int advantage) { }
            public void SendFullStateRequest(int currentTick) { SendFullStateRequestCount++; }
            public void SendFullStateResponse(int peerId, int tick, byte[] data, long hash) { }
            public void BroadcastFullState(int tick, byte[] data, long hash, FullStateKind k = FullStateKind.Unicast) { }
            public void SendPlayerConfig(int playerId, PlayerConfigBase config) { }
            public void SendClientInput(int tick, ICommand command) { }
            public void SendReliableCommand(ICommand command) { }
            public void SendBootstrapReady(int playerId) { }
            public int GetMinClientAckedTick() => 0;
            public void ClearUnackedInputs() { }
            public byte[] GetPlayerEntitlement(int playerId) => null;
        }

        private IKLogger _logger;
        private KlothoTestHarness _harness;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("SimSnapshotContractTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _harness = new KlothoTestHarness(_logger);
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        private void StartDeterministicP2PSession()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.Host.Simulation.UseDeterministicHash = true;
            _harness.Guests[0].Simulation.UseDeterministicHash = true;
            _harness.StartPlaying();
        }

        // ── (1) Non-ECS engine-driven rollback success e2e (Step 4-1) ──

        [Test]
        public void NonEcs_EngineDrivenSaveTrigger_RollbackSucceeds_ResimMatchesStraightLine()
        {
            StartDeterministicP2PSession();
            _harness.AdvanceAllToTick(20);

            var host = _harness.Host;
            int tickAtRequest = host.CurrentTick;

            // (a) The engine triggered the per-tick save: at least one engine-side SaveSnapshot
            // per executed tick (tick 0 initial save + one save per ExecuteTick; internal resim
            // adds more). The counter only counts engine-triggered calls, not Tick auto-saves.
            Assert.GreaterOrEqual(host.Simulation.EngineSaveSnapshotCallCount, tickAtRequest,
                "Engine must trigger ISimulation.SaveSnapshot at least once per executed tick");

            int rollbackTarget = tickAtRequest - 5;
            int executedCount = 0;
            int resolvedTickArg = -1;
            host.Engine.OnRollbackExecuted += (fromTick, resolvedTick) =>
            {
                executedCount++;
                resolvedTickArg = resolvedTick;
            };
            int failedCount = 0;
            host.Engine.OnRollbackFailed += (tick, reason) => failedCount++;

            // (b) Deferred request flushes at frame end (FlushPendingRollback) — advance to flush.
            host.Engine.RequestRollback(rollbackTarget);
            _harness.AdvanceAllToTick(tickAtRequest + 3);

            Assert.AreEqual(1, executedCount,
                "Rollback must execute against the simulation's own engine-fed history");
            Assert.AreEqual(0, failedCount, "Rollback must not fail — history has every tick");
            Assert.AreEqual(rollbackTarget, resolvedTickArg,
                "Per-tick history must resolve the exact requested tick");

            // (c) Post-resim state matches the untouched straight-line guest at the same tick.
            _harness.AdvanceAllToTick(tickAtRequest + 10);
            _harness.AssertStateHashConsistent();
        }

        // ── (2a) P2P NoSnapshot failure: notify only, no rollback, no resync (Step 4-2) ──

        [Test]
        public void P2P_NoSnapshot_NotifiesFailure_NoRollback_NoResync()
        {
            StartDeterministicP2PSession();
            _harness.AdvanceAllToTick(20);

            var host = _harness.Host;

            // Drop all rollback history without changing state — RestoreFromFullState clears
            // the snapshot dictionary (timeline invariant) and round-trips the same state.
            host.Simulation.RestoreFromFullState(host.Simulation.SerializeFullState());

            int rollbackTarget = host.CurrentTick - 3;
            int executedCount = 0;
            int failedCount = 0;
            string failedReason = null;
            host.Engine.OnRollbackExecuted += (fromTick, resolvedTick) => executedCount++;
            host.Engine.OnRollbackFailed += (tick, reason) =>
            {
                failedCount++;
                failedReason = reason;
            };

            host.Engine.RequestRollback(rollbackTarget);
            _harness.AdvanceAllToTick(rollbackTarget + 6);

            Assert.AreEqual(1, failedCount, "NoSnapshot resolve failure must notify exactly once");
            Assert.AreEqual(RollbackFailureReason.NoSnapshot, failedReason,
                "Within-window resolve failure must report NoSnapshot (not TooFar)");
            Assert.AreEqual(0, executedCount, "No rollback may execute without a restorable tick");

            // Current behavior (deviation note): the P2P NoSnapshot branch does not
            // escalate to a FullState resync — _resyncState stays None.
            Assert.AreEqual(0, Convert.ToInt32(_resyncStateField.GetValue(host.Engine)),
                "P2P NoSnapshot failure must not request a resync (current behavior)");

            // The session keeps running on the straight-line state.
            _harness.AssertStateHashConsistent();
        }

        // ── (2b) SD-client batch restore without history: FullState request (Step 4-2) ──

        [Test]
        public void SDClient_BatchRestore_NoHistory_RequestsFullState()
        {
            var sim = new NoHistorySimulation();
            var engine = new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.ServerDriven,
                    TickIntervalMs = 25,
                    MaxRollbackTicks = 50,
                },
                new SessionConfig());
            engine.Initialize(sim, _logger);

            var sdNetwork = new MockSDNetworkService();
            _serverDrivenNetworkField.SetValue(engine, sdNetwork);
            if (_dispatcherField.GetValue(engine) == null)
                _dispatcherField.SetValue(engine, new EventDispatcher(_logger, warnMs: int.MaxValue));
            _stateField.SetValue(engine, KlothoState.Running);

            // Predict ticks 0..2 → CurrentTick = 3. Each predicted tick calls the unified
            // save trigger, which this simulation deliberately no-ops.
            for (int t = 0; t < 3; t++)
                _executeClientPredictionTickMethod.Invoke(engine, Array.Empty<object>());

            // Verified entry at frame tick 2 (executionTick 1, already predicted) forces the
            // restore branch: GetNearestRollbackTick(1) == -1 → FullState request + batch discard.
            _handleVerifiedStateReceivedMethod.Invoke(engine,
                new object[] { 2, (IReadOnlyList<ICommand>)new List<ICommand>(), 0L });
            _processVerifiedBatchMethod.Invoke(engine, Array.Empty<object>());

            Assert.AreEqual(1, sdNetwork.SendFullStateRequestCount,
                "SD-client batch restore without a restorable tick must request a FullState");
        }

        // ── (3) Timeline invariant: restore drops pre-restore history (Step 4-3) ──

        [Test]
        public void RestoreFromFullState_DropsPreRestoreHistory()
        {
            var sim = new TestSimulation { UseDeterministicHash = true };
            sim.Initialize();

            // Engine-like driving: save trigger before each tick (invariant).
            for (int t = 0; t < 10; t++)
            {
                sim.SaveSnapshot();
                sim.Tick(new List<ICommand>());
            }
            Assert.GreaterOrEqual(sim.GetNearestRollbackTick(5), 0,
                "Sanity: history must exist before the restore");

            sim.RestoreFromFullState(sim.SerializeFullState());

            Assert.AreEqual(-1, sim.GetNearestRollbackTick(5),
                "Stale pre-restore history must not be exposed after RestoreFromFullState (D3 timeline invariant)");
        }
    }
}
