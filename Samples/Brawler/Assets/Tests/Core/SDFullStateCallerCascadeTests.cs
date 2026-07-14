#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// SD caller post-processing cascade lock-in (e/f).
    ///   (e) HandleServerDrivenFullStateReceived Late Join  — `_expectingFullState=true` branch
    ///   (f) HandleServerDrivenFullStateReceived Resync/Reconnect — else branch (ClearBefore(tick))
    /// Sibling: P2PFullStateCallerCascadeTests for P2P (a)/(b)/(c)/(d) paths.
    /// Setup follows the EventDispatchSDClientTests pattern — direct `new KlothoEngine`
    /// + 2-arg `Initialize` + reflection injection (no `KlothoTestHarness`).
    /// </summary>
    [TestFixture]
    public class SDFullStateCallerCascadeTests
    {
        // ── Copy of the EventDispatchSDClientTests:120-179 pattern ────────────────

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
            public event Action<int, IReadOnlyList<ICommand>, long> OnVerifiedStateReceived;
            public event Action<int> OnInputAckReceived;
            public event Action<int, byte[], long> OnServerFullStateReceived;
            public event Action<int, long> OnBootstrapBegin;
            public event Action<int, int, RejectionReason> OnCommandRejected;

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
            public int SendFullStateRequestCallCount { get; private set; }
            public int LastFullStateRequestTick { get; private set; } = -1;
            public void SendFullStateRequest(int currentTick)
            {
                SendFullStateRequestCallCount++;
                LastFullStateRequestTick = currentTick;
            }
            public void SendFullStateResponse(int peerId, int tick, byte[] data, long hash) { }
            public void BroadcastFullState(int tick, byte[] data, long hash, FullStateKind k = FullStateKind.Unicast) { }
            public void SendPlayerConfig(int playerId, PlayerConfigBase config) { }
            public void SendClientInput(int tick, ICommand command) { }
            public void SendReliableCommand(ICommand command) { }
            public void SendBootstrapReady(int playerId) { }
            public int GetMinClientAckedTick() => 0;

            public int ClearUnackedInputsCallCount { get; private set; }
            public void ClearUnackedInputs() { ClearUnackedInputsCallCount++; }
            public byte[] GetPlayerEntitlement(int playerId) => null;
        }

        // ── Reflection handles ───────────────────────────────────────────────

        private static readonly Type _engineType = typeof(KlothoEngine);

        private static readonly MethodInfo _handleServerDrivenFullStateReceivedMethod =
            _engineType.GetMethod("HandleServerDrivenFullStateReceived",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _dispatcherField =
            _engineType.GetField("_dispatcher", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _stateField =
            _engineType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _serverDrivenNetworkField =
            _engineType.GetField("_serverDrivenNetwork", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _inputBufferField =
            _engineType.GetField("_inputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _expectingFullStateField =
            _engineType.GetField("_expectingFullState", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _expectingInitialFullStateField =
            _engineType.GetField("_expectingInitialFullState", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _isCatchingUpField =
            _engineType.GetField("_isCatchingUp", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _pendingVerifiedQueueField =
            _engineType.GetField("_pendingVerifiedQueue", BindingFlags.NonPublic | BindingFlags.Instance);

        // SD resync robustness reflection handles.
        private static readonly FieldInfo _fullStateRequestPendingField =
            _engineType.GetField("_fullStateRequestPending", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _lastVerifiedTickField =
            _engineType.GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _checkSdFullStateRequestTimeoutMethod =
            _engineType.GetMethod("CheckSdFullStateRequestTimeout", BindingFlags.NonPublic | BindingFlags.Instance);

        // Reconnect-aware FullState timer handles.
        private static readonly MethodInfo _handleSdReconnectingMethod =
            _engineType.GetMethod("HandleSdReconnecting", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _handleSdReconnectFailedMethod =
            _engineType.GetMethod("HandleSdReconnectFailed", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _fullStateRequestPausedForReconnectField =
            _engineType.GetField("_fullStateRequestPausedForReconnect", BindingFlags.NonPublic | BindingFlags.Instance);

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void InvokeHandleServerDrivenFullStateReceived(
            KlothoEngine engine, int tick, byte[] data, long hash)
            => _handleServerDrivenFullStateReceivedMethod.Invoke(
                engine, new object[] { tick, data, hash });

        private static void InjectDispatcher(KlothoEngine engine, IKLogger logger)
        {
            if (_dispatcherField.GetValue(engine) != null) return;
            _dispatcherField.SetValue(engine, new EventDispatcher(logger, warnMs: int.MaxValue));
        }

        private static void SetEngineState(KlothoEngine engine, KlothoState state)
            => _stateField.SetValue(engine, state);

        private static void InjectServerDrivenNetwork(KlothoEngine engine, MockSDNetworkService stub)
            => _serverDrivenNetworkField.SetValue(engine, stub);

        private static void SetExpectingFullState(KlothoEngine engine, bool value)
            => _expectingFullStateField.SetValue(engine, value);

        private static void SetExpectingInitialFullState(KlothoEngine engine, bool value)
            => _expectingInitialFullStateField.SetValue(engine, value);

        private static InputBuffer ReadInputBuffer(KlothoEngine engine)
            => (InputBuffer)_inputBufferField.GetValue(engine);

        private static int ReadInputBufferCount(KlothoEngine engine)
            => ReadInputBuffer(engine).Count;

        private static int ReadPendingVerifiedQueueCount(KlothoEngine engine)
        {
            // Queue<VerifiedStateEntry> is a private nested struct; reflect Count property
            // on the underlying Queue<T> instance regardless of element type.
            var queue = _pendingVerifiedQueueField.GetValue(engine);
            return (int)queue.GetType().GetProperty("Count").GetValue(queue);
        }

        private static bool ReadIsCatchingUp(KlothoEngine engine)
            => (bool)_isCatchingUpField.GetValue(engine);

        private static void PopulateInputBuffer(KlothoEngine engine, params int[] ticks)
        {
            var buffer = ReadInputBuffer(engine);
            for (int i = 0; i < ticks.Length; i++)
                buffer.AddCommand(new EmptyCommand(playerId: 1, tick: ticks[i]));
        }

        private static SimulationConfig MakeSDClientConfig(bool autoAbort = true)
            => new SimulationConfig
            {
                Mode = NetworkMode.ServerDriven,
                TickIntervalMs = 25,
                MaxRollbackTicks = 50,
                AutoAbortOnRecoveryExhausted = autoAbort,
            };

        private static void SetFullStateRequestPending(KlothoEngine engine, bool value)
            => _fullStateRequestPendingField.SetValue(engine, value);

        private static bool ReadFullStateRequestPending(KlothoEngine engine)
            => (bool)_fullStateRequestPendingField.GetValue(engine);

        private static void SetLastVerifiedTick(KlothoEngine engine, int value)
            => _lastVerifiedTickField.SetValue(engine, value);

        private static bool InvokeCheckSdFullStateRequestTimeout(KlothoEngine engine, float deltaTime)
            => (bool)_checkSdFullStateRequestTimeoutMethod.Invoke(engine, new object[] { deltaTime });

        private static void InvokeHandleSdReconnecting(KlothoEngine engine)
            => _handleSdReconnectingMethod.Invoke(engine, null);

        private static void InvokeHandleSdReconnectFailed(KlothoEngine engine, ReconnectRejectReason reason)
            => _handleSdReconnectFailedMethod.Invoke(engine, new object[] { reason });

        private static bool ReadPausedForReconnect(KlothoEngine engine)
            => (bool)_fullStateRequestPausedForReconnectField.GetValue(engine);

        // ── Fixture state ────────────────────────────────────────────────────

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            _logger = factory.CreateLogger("SDFullStateCallerCascadeTests");
        }

        private (KlothoEngine engine, TestSimulation sim, MockSDNetworkService mockSDNetwork) CreateSDClient(bool autoAbort = true)
        {
            var sim = new TestSimulation { UseDeterministicHash = true };
            var engine = new KlothoEngine(MakeSDClientConfig(autoAbort), new SessionConfig());
            engine.Initialize(sim, _logger);
            InjectDispatcher(engine, _logger);
            SetEngineState(engine, KlothoState.Running);
            var mockSDNetwork = new MockSDNetworkService();
            InjectServerDrivenNetwork(engine, mockSDNetwork);
            return (engine, sim, mockSDNetwork);
        }

        // ── (e) SD Late Join — _inputBuffer.Clear + StartCatchingUp + ClearUnackedInputs ──

        [Test]
        public void HandleServerDrivenFullStateReceived_LateJoin_ClearsInputBufferAndStartsCatchup()
        {
            var (engine, sim, mockSDNetwork) = CreateSDClient();

            PopulateInputBuffer(engine, 10, 20, 30);
            Assert.AreEqual(3, ReadInputBufferCount(engine), "Setup precondition — input buffer populated");
            Assert.IsFalse(ReadIsCatchingUp(engine), "Setup precondition — not catching up yet");

            SetExpectingInitialFullState(engine, false);
            SetExpectingFullState(engine, true);

            const int applyTick = 60;
            byte[] stateData = sim.SerializeFullState();
            long hash = sim.GetStateHash();

            InvokeHandleServerDrivenFullStateReceived(engine, applyTick, stateData, hash);

            Assert.AreEqual(0, ReadInputBufferCount(engine),
                "SD Late Join path must call _inputBuffer.Clear()");
            Assert.AreEqual(applyTick, engine.LastVerifiedTick,
                "SD Late Join sets _lastVerifiedTick = tick");
            Assert.AreEqual(1, mockSDNetwork.ClearUnackedInputsCallCount,
                "SD Late Join must call _serverDrivenNetwork.ClearUnackedInputs exactly once");
            Assert.AreEqual(0, ReadPendingVerifiedQueueCount(engine),
                "SD Late Join must clear _pendingVerifiedQueue");
            Assert.IsTrue(ReadIsCatchingUp(engine),
                "SD Late Join must call StartCatchingUp → _isCatchingUp == true");
            Assert.IsFalse((bool)_expectingFullStateField.GetValue(engine),
                "SD Late Join must reset _expectingFullState = false");
        }

        // ── (f) SD Resync/Reconnect — ClearBefore(tick) + ClearUnackedInputs + no catchup ──

        [Test]
        public void HandleServerDrivenFullStateReceived_ResyncReconnect_ClearsBeforeTickAndPreservesLocalInputs()
        {
            var (engine, sim, mockSDNetwork) = CreateSDClient();

            const int applyTick = 60;
            // Mix of pre-tick (should be cleared) and post-tick (should be preserved).
            PopulateInputBuffer(engine, 40, 50, applyTick, applyTick + 10);
            int preCount = ReadInputBufferCount(engine);
            Assert.AreEqual(4, preCount, "Setup precondition — 4 entries injected");
            bool isCatchingUpBefore = ReadIsCatchingUp(engine);

            SetExpectingInitialFullState(engine, false);
            SetExpectingFullState(engine, false); // else branch (Resync/Reconnect)

            byte[] stateData = sim.SerializeFullState();
            long hash = sim.GetStateHash();

            InvokeHandleServerDrivenFullStateReceived(engine, applyTick, stateData, hash);

            // ClearBefore(tick) removes entries with command.Tick < tick. Two entries at tick<60 wiped,
            // two at tick>=60 preserved.
            var buffer = ReadInputBuffer(engine);
            Assert.AreEqual(2, buffer.Count,
                "Resync/Reconnect must call _inputBuffer.ClearBefore(tick) — entries at tick<applyTick wiped, entries at tick>=applyTick preserved");

            Assert.AreEqual(applyTick, engine.LastVerifiedTick,
                "SD Resync/Reconnect sets _lastVerifiedTick = tick");
            Assert.AreEqual(1, mockSDNetwork.ClearUnackedInputsCallCount,
                "SD Resync/Reconnect must call _serverDrivenNetwork.ClearUnackedInputs exactly once");
            Assert.AreEqual(0, ReadPendingVerifiedQueueCount(engine),
                "SD Resync/Reconnect must clear _pendingVerifiedQueue");
            Assert.AreEqual(isCatchingUpBefore, ReadIsCatchingUp(engine),
                "SD Resync/Reconnect must NOT call StartCatchingUp — _isCatchingUp unchanged");
        }

        // ════════════════════════════════════════════════════════════════════════
        // SD resync robustness
        // ════════════════════════════════════════════════════════════════════════

        // Defensive: the LateJoin branch consumes the FullState without the determinism
        // fall-through's pending-clear, so a stuck _fullStateRequestPending would freeze
        // ProcessVerifiedBatch. Unreachable with pending=true today — this is a
        // regression guard. Initial branch is symmetric (same ClearFullStateRequestState call).
        [Test]
        public void SdFullState_LateJoinBranchClearsPending_Defensive()
        {
            var (engine, sim, _) = CreateSDClient();

            SetExpectingInitialFullState(engine, false);
            SetExpectingFullState(engine, true);
            SetFullStateRequestPending(engine, true); // forced (cannot occur live)

            byte[] stateData = sim.SerializeFullState();
            long hash = sim.GetStateHash();
            InvokeHandleServerDrivenFullStateReceived(engine, 60, stateData, hash);

            Assert.IsFalse(ReadFullStateRequestPending(engine),
                "LateJoin branch must clear _fullStateRequestPending (ClearFullStateRequestState) so ProcessVerifiedBatch is not frozen");
        }

        // An outstanding FullState request with no response must re-arm on RESYNC_TIMEOUT_MS,
        // resend up to ResyncMaxRetries, then terminate (OnResyncFailed + AbortMatch under AutoAbort).
        [Test]
        public void SdFullStateRequest_TimesOutAndResends()
        {
            var (engine, _, mockSDNetwork) = CreateSDClient(autoAbort: true);

            int resyncFailedCount = 0;
            engine.OnResyncFailed += () => resyncFailedCount++;

            SetFullStateRequestPending(engine, true);

            // Each tick advances elapsed by deltaTime*1000. 5.0s ≥ RESYNC_TIMEOUT_MS (5000ms) fires once per call.
            // ResyncMaxRetries defaults to 3 → 3 resends, then the 4th call exhausts and aborts.
            const float fiveSeconds = 5.0f;
            bool aborted = false;
            for (int i = 0; i < 3; i++)
            {
                aborted = InvokeCheckSdFullStateRequestTimeout(engine, fiveSeconds);
                Assert.IsFalse(aborted, $"retry {i + 1} must not abort yet");
            }
            Assert.AreEqual(3, mockSDNetwork.SendFullStateRequestCallCount,
                "Must resend FullState request once per timeout up to ResyncMaxRetries (3)");
            Assert.AreEqual(KlothoState.Running, engine.State, "Still running before exhaustion");

            LogAssert.Expect(LogType.Error, new Regex(@"FullState request unanswered after \d+ retries"));
            aborted = InvokeCheckSdFullStateRequestTimeout(engine, fiveSeconds);
            Assert.IsTrue(aborted, "Exhaustion must signal abort to the caller");
            Assert.AreEqual(3, mockSDNetwork.SendFullStateRequestCallCount,
                "No further resend after exhaustion");
            Assert.AreEqual(1, resyncFailedCount, "OnResyncFailed fired once on exhaustion");
            Assert.AreEqual(KlothoState.Aborted, engine.State, "AutoAbort terminates the match on exhaustion");
            Assert.IsFalse(ReadFullStateRequestPending(engine), "Terminal clears the request gate");
        }

        // No premature timeout — under RESYNC_TIMEOUT_MS accumulates without resending.
        [Test]
        public void SdFullStateRequest_NoTimeoutBeforeThreshold()
        {
            var (engine, _, mockSDNetwork) = CreateSDClient();
            SetFullStateRequestPending(engine, true);

            // 1s < 5s threshold — accumulate, no resend.
            for (int i = 0; i < 4; i++)
                Assert.IsFalse(InvokeCheckSdFullStateRequestTimeout(engine, 1.0f));

            Assert.AreEqual(0, mockSDNetwork.SendFullStateRequestCallCount,
                "No resend before RESYNC_TIMEOUT_MS reached");
            Assert.IsTrue(ReadFullStateRequestPending(engine), "Request still pending");
        }

        // ════════════════════════════════════════════════════════════════════════
        // Reconnect-aware FullState timer
        // ════════════════════════════════════════════════════════════════════════

        // OnReconnecting suspends the retry/abort timer so the shorter FullState abort window
        // cannot preempt a recoverable reconnect (which has the longer ReconnectTimeoutMs budget).
        [Test]
        public void SdFullStatePending_Reconnecting_PausesTimer_NoAbortPastWindow()
        {
            var (engine, _, mockSDNetwork) = CreateSDClient(autoAbort: true);
            SetFullStateRequestPending(engine, true);

            InvokeHandleSdReconnecting(engine);
            Assert.IsTrue(ReadPausedForReconnect(engine),
                "OnReconnecting with a pending request must pause the timer");

            // Drive well past RESYNC_TIMEOUT_MS×retries (15s) — paused timer must not resend or abort.
            bool aborted = false;
            for (int i = 0; i < 5; i++)
                aborted = InvokeCheckSdFullStateRequestTimeout(engine, 5.0f);

            Assert.IsFalse(aborted, "Paused timer must not abort while reconnect is in progress");
            Assert.AreEqual(KlothoState.Running, engine.State, "Engine stays Running during reconnect");
            Assert.AreEqual(0, mockSDNetwork.SendFullStateRequestCallCount, "Paused timer must not resend");
            Assert.IsTrue(ReadFullStateRequestPending(engine), "Request still pending (awaiting reconnect outcome)");
        }

        // OnReconnecting is a no-op when nothing is pending (a request cannot start mid-reconnect).
        [Test]
        public void SdReconnecting_NoPendingRequest_NoOp()
        {
            var (engine, _, _) = CreateSDClient();
            InvokeHandleSdReconnecting(engine);
            Assert.IsFalse(ReadPausedForReconnect(engine),
                "OnReconnecting with no pending request must not set the pause flag");
        }

        // Reconnect terminal failure (every service failure path converges on OnReconnectFailed)
        // terminates with AbortReason.ReconnectFailed; a duplicate/late signal no-ops (idempotent).
        [Test]
        public void SdFullStatePending_ReconnectFailed_Terminates()
        {
            var (engine, _, _) = CreateSDClient(autoAbort: true);
            int resyncFailedCount = 0;
            engine.OnResyncFailed += () => resyncFailedCount++;
            AbortReason captured = AbortReason.Unknown;
            engine.OnMatchAborted += r => captured = r;

            SetFullStateRequestPending(engine, true);
            InvokeHandleSdReconnecting(engine); // paused

            LogAssert.Expect(LogType.Warning, new Regex(@"Reconnect failed .* FullState request outstanding"));
            InvokeHandleSdReconnectFailed(engine, ReconnectRejectReason.TimedOut);
            // Duplicate/late terminal signal must no-op (pending already cleared).
            InvokeHandleSdReconnectFailed(engine, ReconnectRejectReason.TimedOut);

            Assert.AreEqual(1, resyncFailedCount, "Terminal reconnect failure fires OnResyncFailed exactly once");
            Assert.AreEqual(KlothoState.Aborted, engine.State, "Terminal reconnect failure aborts under AutoAbort");
            Assert.AreEqual(AbortReason.ReconnectFailed, captured,
                "Abort reason is ReconnectFailed (not StateDivergence)");
            Assert.IsFalse(ReadFullStateRequestPending(engine), "Terminal clears the request gate");
        }

        // Reconnect failure with NO pending FullState must NOT abort the engine — reconnect-failure
        // handling stays with the game layer (IKlothoSessionObserver.OnReconnectFailed).
        [Test]
        public void SdReconnectFailed_NoPendingRequest_DoesNotAbort()
        {
            var (engine, _, _) = CreateSDClient(autoAbort: true);
            int resyncFailedCount = 0;
            engine.OnResyncFailed += () => resyncFailedCount++;

            InvokeHandleSdReconnectFailed(engine, ReconnectRejectReason.TimedOut);

            Assert.AreEqual(KlothoState.Running, engine.State,
                "Reconnect failure without a pending FullState must NOT abort (game layer decides)");
            Assert.AreEqual(0, resyncFailedCount, "No OnResyncFailed when not pending");
        }

        // AutoAbort off — terminal reconnect failure signals OnResyncFailed but leaves the engine
        // as-is for the game layer to tear down (mirrors HandleSdResyncFailure's contract).
        [Test]
        public void SdFullStatePending_ReconnectFailed_AutoAbortOff_NoAbort()
        {
            var (engine, _, _) = CreateSDClient(autoAbort: false);
            int resyncFailedCount = 0;
            engine.OnResyncFailed += () => resyncFailedCount++;

            SetFullStateRequestPending(engine, true);
            LogAssert.Expect(LogType.Warning, new Regex(@"Reconnect failed .* FullState request outstanding"));
            InvokeHandleSdReconnectFailed(engine, ReconnectRejectReason.MaxRetries);

            Assert.AreEqual(1, resyncFailedCount, "OnResyncFailed fires so the game layer can tear down");
            Assert.AreEqual(KlothoState.Running, engine.State, "AutoAbort off: engine left as-is (game decides)");
        }

        // Repeated OnReconnecting stays paused — no double-pause corruption.
        [Test]
        public void SdReconnecting_RepeatedSignals_Idempotent()
        {
            var (engine, _, mockSDNetwork) = CreateSDClient(autoAbort: true);
            SetFullStateRequestPending(engine, true);

            InvokeHandleSdReconnecting(engine);
            InvokeHandleSdReconnecting(engine); // duplicate
            Assert.IsTrue(ReadPausedForReconnect(engine));

            bool aborted = InvokeCheckSdFullStateRequestTimeout(engine, 20.0f);
            Assert.IsFalse(aborted, "Still paused after duplicate signals");
            Assert.AreEqual(KlothoState.Running, engine.State);
            Assert.AreEqual(0, mockSDNetwork.SendFullStateRequestCallCount);
        }

        // Once a FullState is consumed (reconnect success), the pause flag is cleared via
        // ClearFullStateRequestState, so a subsequent FullState-pending cycle resumes normal countdown.
        [Test]
        public void SdPauseCleared_NextRequestTimesOutNormally()
        {
            var (engine, sim, mockSDNetwork) = CreateSDClient(autoAbort: true);

            // Cycle 1: pending + paused, then a server FullState (reconnect success) clears everything.
            SetFullStateRequestPending(engine, true);
            InvokeHandleSdReconnecting(engine);
            Assert.IsTrue(ReadPausedForReconnect(engine));

            SetExpectingInitialFullState(engine, false);
            SetExpectingFullState(engine, false); // Resync/Reconnect branch → ClearFullStateRequestState
            InvokeHandleServerDrivenFullStateReceived(engine, 60, sim.SerializeFullState(), sim.GetStateHash());
            Assert.IsFalse(ReadPausedForReconnect(engine),
                "FullState consumption must clear the pause flag (ClearFullStateRequestState)");
            Assert.IsFalse(ReadFullStateRequestPending(engine));

            // Cycle 2 (no reconnect): timer must resume normal countdown and resend.
            SetFullStateRequestPending(engine, true);
            bool aborted = InvokeCheckSdFullStateRequestTimeout(engine, 5.0f);
            Assert.IsFalse(aborted);
            Assert.AreEqual(1, mockSDNetwork.SendFullStateRequestCallCount,
                "After a cleared pause, a fresh request must resume normal countdown (no stale pause)");
        }

        // ResyncRequest apply that the retreat guard Skips (nothing restored) must skip all
        // post-processing and keep pending=true so the timer retries (Skipped contract).
        [Test]
        public void SdResyncApply_SkippedSkipsPostProcessing()
        {
            var (engine, sim, _) = CreateSDClient();

            const int applyTick = 60;
            // _lastVerifiedTick > applyTick → retreat guard (allowRetreat=false for ResyncRequest) → Skipped.
            SetLastVerifiedTick(engine, 70);
            SetExpectingInitialFullState(engine, false);
            SetExpectingFullState(engine, false); // ResyncRequest branch
            SetFullStateRequestPending(engine, true);

            PopulateInputBuffer(engine, 40, 50); // pre-tick entries — ClearBefore(60) would wipe them
            int preCount = ReadInputBufferCount(engine);

            byte[] stateData = sim.SerializeFullState();
            long hash = sim.GetStateHash();
            InvokeHandleServerDrivenFullStateReceived(engine, applyTick, stateData, hash);

            Assert.AreEqual(preCount, ReadInputBufferCount(engine),
                "Skipped must NOT call ClearBefore — input buffer unchanged");
            Assert.AreEqual(70, engine.LastVerifiedTick,
                "Skipped must NOT advance _lastVerifiedTick");
            Assert.IsTrue(ReadFullStateRequestPending(engine),
                "Skipped must keep _fullStateRequestPending so R-5 retries with a newer tick");
        }

        // ResyncRequest apply with a hash mismatch (restored-but-untrusted) is unrecoverable —
        // under AutoAbort it routes to OnResyncFailed + AbortMatch.
        [Test]
        public void SdResyncApply_HashMismatch_AutoAbort_Terminates()
        {
            var (engine, sim, _) = CreateSDClient(autoAbort: true);

            int resyncFailedCount = 0;
            engine.OnResyncFailed += () => resyncFailedCount++;

            SetExpectingInitialFullState(engine, false);
            SetExpectingFullState(engine, false); // ResyncRequest branch
            SetFullStateRequestPending(engine, true);
            SetLastVerifiedTick(engine, 0); // < tick → no retreat skip, reaches hash check

            byte[] stateData = sim.SerializeFullState();
            long wrongHash = sim.GetStateHash() ^ 0x5EED; // claimed hash disagrees with restored state
            LogAssert.Expect(LogType.Error, new Regex(@"\[FullStateResync\] hash mismatch")); // ApplyFullState internal
            LogAssert.Expect(LogType.Error, new Regex(@"\[SD\] FullState apply hash mismatch")); // SD handler terminal
            InvokeHandleServerDrivenFullStateReceived(engine, 60, stateData, wrongHash);

            Assert.AreEqual(1, resyncFailedCount, "HashMismatch fires OnResyncFailed");
            Assert.AreEqual(KlothoState.Aborted, engine.State, "AutoAbort terminates on unrecoverable HashMismatch");
            Assert.IsFalse(ReadFullStateRequestPending(engine), "Terminal clears the request gate");
        }

        // With AutoAbort off the game layer owns teardown — OnResyncFailed fires but the match
        // is NOT aborted; the restored (untrusted) state proceeds through normal post-processing so
        // bookkeeping stays consistent (mirrors P2P).
        [Test]
        public void SdResyncApply_HashMismatch_NoAutoAbort_ProceedsWithSignal()
        {
            var (engine, sim, _) = CreateSDClient(autoAbort: false);

            int resyncFailedCount = 0;
            engine.OnResyncFailed += () => resyncFailedCount++;

            const int applyTick = 60;
            SetExpectingInitialFullState(engine, false);
            SetExpectingFullState(engine, false);
            SetFullStateRequestPending(engine, true);
            SetLastVerifiedTick(engine, 0);
            PopulateInputBuffer(engine, 40, 50, applyTick + 10); // 2 pre-tick, 1 post-tick

            byte[] stateData = sim.SerializeFullState();
            long wrongHash = sim.GetStateHash() ^ 0x5EED;
            LogAssert.Expect(LogType.Error, new Regex(@"\[FullStateResync\] hash mismatch")); // ApplyFullState internal
            LogAssert.Expect(LogType.Error, new Regex(@"\[SD\] FullState apply hash mismatch")); // SD handler signal
            InvokeHandleServerDrivenFullStateReceived(engine, applyTick, stateData, wrongHash);

            Assert.AreEqual(1, resyncFailedCount, "HashMismatch fires OnResyncFailed");
            Assert.AreEqual(KlothoState.Running, engine.State, "AutoAbort off must NOT abort — game layer decides");
            Assert.AreEqual(applyTick, engine.LastVerifiedTick,
                "AutoAbort off falls through to post-processing — _lastVerifiedTick advanced (restored-state bookkeeping)");
            Assert.AreEqual(1, ReadInputBufferCount(engine),
                "Post-processing ClearBefore(60) ran — only the post-tick entry survives");
            Assert.IsFalse(ReadFullStateRequestPending(engine), "Post-processing cleared the request gate");
        }
    }
}
