using System;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Tests.Network
{
    /// <summary>
    /// Verifies SDClientService buffers the handshake-time extra-delay value when
    /// the engine has not yet been subscribed, and drains it on SubscribeEngine.
    /// Backs ISS-09 fix (Sync seed lost when handshake handler fires before engine ready).
    /// </summary>
    [TestFixture]
    public class PendingExtraDelayTests
    {
        private ServerDrivenClientService _service;
        private RecordingEngine _engine;

        [SetUp]
        public void SetUp()
        {
            _service = new ServerDrivenClientService();
            _engine = new RecordingEngine();
        }

        [Test]
        public void ApplyOrPend_EngineNull_BuffersValue_NoApply()
        {
            // Engine not yet subscribed — should not throw, should not invoke ApplyExtraDelay.
            Assert.DoesNotThrow(() => _service.ApplyOrPendExtraDelay(12, ExtraDelaySource.Sync));
            Assert.AreEqual(0, _engine.ApplyCount,
                "ApplyExtraDelay must not fire while no engine is subscribed");
        }

        [Test]
        public void SubscribeEngine_WithPending_DrainsAndApplies()
        {
            _service.ApplyOrPendExtraDelay(12, ExtraDelaySource.Sync);
            _service.SubscribeEngine(_engine);

            Assert.AreEqual(1, _engine.ApplyCount, "Pending value must drain into ApplyExtraDelay once");
            Assert.AreEqual(12, _engine.LastDelay);
            Assert.AreEqual(ExtraDelaySource.Sync, _engine.LastSource);
        }

        [Test]
        public void SubscribeEngine_NoPending_DoesNotApply()
        {
            _service.SubscribeEngine(_engine);

            Assert.AreEqual(0, _engine.ApplyCount,
                "ApplyExtraDelay must not fire when no value was buffered");
        }

        [Test]
        public void ApplyOrPend_EngineReady_AppliesDirect_NoBuffer()
        {
            _service.SubscribeEngine(_engine);
            _service.ApplyOrPendExtraDelay(7, ExtraDelaySource.DynamicPush);

            Assert.AreEqual(1, _engine.ApplyCount);
            Assert.AreEqual(7, _engine.LastDelay);
            Assert.AreEqual(ExtraDelaySource.DynamicPush, _engine.LastSource);
        }

        [Test]
        public void ApplyOrPend_BufferThenApplyOnce_DoesNotReapplyOnSecondSubscribe()
        {
            // Defensive: drain must clear the buffer so a (theoretical) re-subscribe does not double-apply.
            _service.ApplyOrPendExtraDelay(12, ExtraDelaySource.Sync);
            _service.SubscribeEngine(_engine);
            Assert.AreEqual(1, _engine.ApplyCount);

            var second = new RecordingEngine();
            _service.SubscribeEngine(second);
            Assert.AreEqual(0, second.ApplyCount, "Drained buffer must not re-apply on subsequent subscribe");
        }

        [Test]
        public void ApplyOrPend_OverwritePendingWithLatest()
        {
            // Two pre-engine handshake handlers in sequence — latest wins (single int? slot by design).
            _service.ApplyOrPendExtraDelay(8, ExtraDelaySource.Sync);
            _service.ApplyOrPendExtraDelay(12, ExtraDelaySource.LateJoin);

            _service.SubscribeEngine(_engine);

            Assert.AreEqual(1, _engine.ApplyCount);
            Assert.AreEqual(12, _engine.LastDelay);
            Assert.AreEqual(ExtraDelaySource.LateJoin, _engine.LastSource);
        }

        // ── Minimal IKlothoEngine stub that records ApplyExtraDelay invocations ──

        private sealed class RecordingEngine : IKlothoEngine
        {
            public int ApplyCount { get; private set; }
            public int LastDelay { get; private set; }
            public ExtraDelaySource LastSource { get; private set; }

            public void ApplyExtraDelay(int delay, ExtraDelaySource source)
            {
                ApplyCount++;
                LastDelay = delay;
                LastSource = source;
            }

            public ISimulationConfig SimulationConfig { get; set; } = new SimulationConfig();
            public ISessionConfig SessionConfig { get; set; } = new SessionConfig();
            public KlothoState State { get; set; } = KlothoState.Idle;
            public ISimulation Simulation { get; set; }
            public Microsoft.Extensions.Logging.ILogger Logger => null;

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

            public FrameRef VerifiedFrame => FrameRef.None(FrameKind.Verified);
            public FrameRef PredictedFrame => FrameRef.None(FrameKind.Predicted);
            public FrameRef PredictedPreviousFrame => FrameRef.None(FrameKind.PredictedPrevious);
            public FrameRef PreviousUpdatePredictedFrame => FrameRef.None(FrameKind.PreviousUpdatePredicted);
            public RenderClockState RenderClock => default;
            public bool TryGetFrameAtTick(int tick, out xpTURN.Klotho.ECS.Frame frame) { frame = null; return false; }

#pragma warning disable CS0067
            public event Action<int, bool> OnPlayerConfigReceived;
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
            public event Action<int> OnExtraDelayChanged;
            public event Action OnChainAdvanceBreak;
            public event Action<int> OnDisconnectedInputNeeded;
            public event Action OnCatchupComplete;
#pragma warning restore CS0067

            public T GetPlayerConfig<T>(int playerId) where T : PlayerConfigBase => null;
            public bool TryGetPlayerConfig<T>(int playerId, out T config) where T : PlayerConfigBase { config = null; return false; }

            public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, Microsoft.Extensions.Logging.ILogger logger) { }
            public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, Microsoft.Extensions.Logging.ILogger logger, ISimulationCallbacks simulationCallbacks, IViewCallbacks viewCallbacks = null) { }
            public void Initialize(ISimulation simulation, Microsoft.Extensions.Logging.ILogger logger) { }
            public void Start() { }
            public void Update(float deltaTime) { }
            public void InputCommand(ICommand command, int extraDelay = 0) { }
            public void EscalateExtraDelay(int step, int max) { }
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
            public void ForceInsertEmptyCommandsRange(int playerId, int fromTick, int toTickInclusive) { }
            public bool HasCommand(int tick, int playerId) => false;
            public bool IsCommandSealed(int tick, int playerId) => false;
            public void RequestRollback(int targetTick) { }
            public void StartCatchingUp() { }
            public void StopCatchingUp() { }
            public void ConfirmCatchupTick(int tick) { }
            public void ExpectFullState() { }
            public void CancelExpectFullState() { }
            public ErrorCorrectionSettings ErrorCorrectionSettings { get; set; } = ErrorCorrectionSettings.Default;
            public (float x, float y, float z) GetPositionDelta(int entityIndex) => (0f, 0f, 0f);
            public float GetYawDelta(int entityIndex) => 0f;
            public bool HasEntityTeleported(int entityIndex) => false;
        }
    }
}
