using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using ZLogger.Unity;

using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// SD caller post-processing cascade lock-in (F-9 e/f).
    ///   (e) HandleServerDrivenFullStateReceived Late Join  — `_expectingFullState=true` branch
    ///   (f) HandleServerDrivenFullStateReceived Resync/Reconnect — else branch (ClearBefore(tick))
    /// Sibling: P2PFullStateCallerCascadeTests for P2P (a)/(b)/(c)/(d) paths.
    /// Setup follows F-4(f) EventDispatchSDClientTests pattern — direct `new KlothoEngine`
    /// + 2-arg `Initialize` + reflection injection (no `KlothoTestHarness`).
    /// </summary>
    [TestFixture]
    public class SDFullStateCallerCascadeTests
    {
        // ── F-4(f) EventDispatchSDClientTests:120-179 패턴 사본 ────────────────

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
            public event Action<int, IReadOnlyList<ICommand>, long> OnVerifiedStateReceived;
            public event Action<int> OnInputAckReceived;
            public event Action<int, byte[], long> OnServerFullStateReceived;
            public event Action<int, long> OnBootstrapBegin;
            public event Action<int, int, RejectionReason> OnCommandRejected;

            public void Initialize(INetworkTransport t, ICommandFactory f, ILogger l) { }
            public void CreateRoom(string n, int m) { }
            public void JoinRoom(string n) { }
            public void LeaveRoom() { }
            public void SetReady(bool r) { }
            public void SendCommand(ICommand c) { }
            public void RequestCommandsForTick(int tick) { }
            public void SendSyncHash(int tick, long hash) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SetLocalTick(int tick) { }
            public void SendFullStateRequest(int currentTick) { }
            public void SendFullStateResponse(int peerId, int tick, byte[] data, long hash) { }
            public void BroadcastFullState(int tick, byte[] data, long hash, FullStateKind k = FullStateKind.Unicast) { }
            public void SendPlayerConfig(int playerId, PlayerConfigBase config) { }
            public void SendClientInput(int tick, ICommand command) { }
            public void SendBootstrapReady(int playerId) { }
            public int GetMinClientAckedTick() => 0;

            public int ClearUnackedInputsCallCount { get; private set; }
            public void ClearUnackedInputs() { ClearUnackedInputsCallCount++; }
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

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void InvokeHandleServerDrivenFullStateReceived(
            KlothoEngine engine, int tick, byte[] data, long hash)
            => _handleServerDrivenFullStateReceivedMethod.Invoke(
                engine, new object[] { tick, data, hash });

        private static void InjectDispatcher(KlothoEngine engine, ILogger logger)
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

        private static SimulationConfig MakeSDClientConfig()
            => new SimulationConfig
            {
                Mode = NetworkMode.ServerDriven,
                TickIntervalMs = 25,
                MaxRollbackTicks = 50,
            };

        // ── Fixture state ────────────────────────────────────────────────────

        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                b.AddZLoggerUnityDebug();
            });
            _logger = factory.CreateLogger("SDFullStateCallerCascadeTests");
        }

        private (KlothoEngine engine, TestSimulation sim, MockSDNetworkService mockSDNetwork) CreateSDClient()
        {
            var sim = new TestSimulation { UseDeterministicHash = true };
            var engine = new KlothoEngine(MakeSDClientConfig(), new SessionConfig());
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
    }
}
