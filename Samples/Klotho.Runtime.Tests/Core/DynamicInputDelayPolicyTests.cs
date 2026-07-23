using System;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// DynamicInputDelayPolicy unit tests:
    ///   - first-window guard: the int.MinValue sentinel no longer makes rejects accumulate
    ///     match-wide (window slides; counts from different eras must not combine);
    ///   - shared escalation cooldown: the PastTick and rollback-burst triggers check AND set
    ///     the same cooldown stamp, so combined they cannot stack more than one step per window;
    ///   - reactive ceiling early-return: at ReactiveMax no further escalation occurs.
    /// </summary>
    [TestFixture]
    public class DynamicInputDelayPolicyTests
    {
        private sealed class PolicyEngine : IKlothoEngine
        {
            public int EscalateCount { get; private set; }
            private int _recommendedExtraDelay;

            public void EscalateExtraDelay(int step, int max)
            {
                int newDelay = Math.Min(_recommendedExtraDelay + step, max);
                if (newDelay > _recommendedExtraDelay)
                {
                    _recommendedExtraDelay = newDelay;
                    EscalateCount++;
                    // Mimic the real engine firing OnExtraDelayChanged so the policy's _selfModifying
                    // guard is exercised (this fires while the policy is mid-self-modify).
                    OnExtraDelayChanged?.Invoke(_recommendedExtraDelay);
                }
            }

            public int DeEscalateCount { get; private set; }
            public void DeEscalateExtraDelay(int step)
            {
                if (_recommendedExtraDelay <= 0) return;
                _recommendedExtraDelay = Math.Max(0, _recommendedExtraDelay - step);
                DeEscalateCount++;
                OnExtraDelayChanged?.Invoke(_recommendedExtraDelay);
            }

            public void SetRecommendedExtraDelay(int value) => _recommendedExtraDelay = value;

            // A server push (DynamicPush) fires OnExtraDelayChanged OUTSIDE the policy's self-modify
            // guard → opens the grace window. Used to contrast with self-escalate.
            public void ApplyExtraDelay(int delay, ExtraDelaySource source)
            {
                _recommendedExtraDelay = delay;
                OnExtraDelayChanged?.Invoke(_recommendedExtraDelay);
            }

            public ISimulationConfig SimulationConfig { get; set; } = new SimulationConfig();
            public ISessionConfig SessionConfig { get; set; } = new SessionConfig();
            public KlothoState State { get; set; } = KlothoState.Running;
            public ISimulation Simulation { get; set; }
            public xpTURN.Klotho.Logging.IKLogger Logger => null;

            public int CurrentTick { get; set; }
            public int RandomSeed { get; set; }
            public bool IsReplayMode => false;
            public bool IsServer => false;
            public bool IsHost => false;
            public bool IsMatchEnded => false;
            public SimulationStage Stage => SimulationStage.Forward;
            public int LocalPlayerId { get; set; }
            public int TickInterval => 25;
            public int InputDelay => 4;
            public int RecommendedExtraDelay => _recommendedExtraDelay;
            public bool IsSpectatorMode { get; set; }
            public int LastVerifiedTick { get; set; } = -1;

            public FrameRef VerifiedFrame => FrameRef.None(FrameKind.Verified);
            public FrameRef PredictedFrame => FrameRef.None(FrameKind.Predicted);
            public xpTURN.Klotho.ECS.Frame InitFrame => null;
            public FrameRef PredictedPreviousFrame => FrameRef.None(FrameKind.PredictedPrevious);
            public FrameRef PreviousUpdatePredictedFrame => FrameRef.None(FrameKind.PreviousUpdatePredicted);
            public RenderClockState RenderClock => default;
            public float PredictionAccuracy => 1.0f;
            public bool TryGetFrameAtTick(int tick, out xpTURN.Klotho.ECS.Frame frame) { frame = null; return false; }

#pragma warning disable CS0067
            public event Action<int, bool> OnPlayerConfigReceived;
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
            public event Action<int, SimulationEvent, SyncedDivergenceKind> OnSyncedEventDivergence;
            public event Action<int> OnResyncCompleted;
            public event Action OnResyncFailed;
            public event Action<AbortReason> OnMatchAborted;
            public event Action<ResetReason> OnMatchReset;
            public event Action<int, int, RejectionReason> OnCommandRejected;
            public event Action<int, int, byte[], int> OnVerifiedInputBatchReady;
            public event Action<int> OnExtraDelayChanged;
            public event Action OnChainAdvanceBreak;
            public event Action<int> OnDisconnectedInputNeeded;
            public event Action OnCatchupComplete;
            public event Action<int, int, WipeKind> OnPendingWipe;
#pragma warning restore CS0067

            public void FirePastTickReject(int tick) => OnCommandRejected?.Invoke(tick, 0, RejectionReason.PastTick);
            public void FireRollback(int fromTick, int toTick) => OnRollbackExecuted?.Invoke(fromTick, toTick);
            public void FireTickExecuted(int tick) => OnTickExecuted?.Invoke(tick);

            public T GetPlayerConfig<T>(int playerId) where T : PlayerConfigBase => null;
            public byte[] GetPlayerEntitlement(int playerId) => null;
            public bool TryGetPlayerConfig<T>(int playerId, out T config) where T : PlayerConfigBase { config = null; return false; }

            public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, xpTURN.Klotho.Logging.IKLogger logger) { }
            public void Initialize(ISimulation simulation, IKlothoNetworkService networkService, xpTURN.Klotho.Logging.IKLogger logger, ISimulationCallbacks simulationCallbacks, IViewCallbacks viewCallbacks = null) { }
            public void Initialize(ISimulation simulation, xpTURN.Klotho.Logging.IKLogger logger) { }
            public void Start() { }
            public void Update(float deltaTime) { }
            public void InputCommand(ICommand command, int extraDelay = 0) { }
            public IReliableCommandHandle IssueOnce(Func<ICommand> commandFactory, ReliabilityPolicy policy = null) => null;
            public void Stop() { }
            public void AbortMatch(AbortReason reason) { }
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

        private PolicyEngine _engine;
        private SimulationConfig _config;
        private DynamicInputDelayPolicy _policy;

        [SetUp]
        public void SetUp()
        {
            _config = new SimulationConfig();
            _engine = new PolicyEngine { SimulationConfig = _config };
            _policy = new DynamicInputDelayPolicy(_engine);
            _policy.Attach();
        }

        [TearDown]
        public void TearDown()
        {
            _policy.Detach();
        }

        private void FireRejects(int count, int atTick)
        {
            _engine.CurrentTick = atTick;
            for (int i = 0; i < count; i++)
                _engine.FirePastTickReject(atTick);
        }

        [Test]
        public void PastTick_ThresholdWithinWindow_EscalatesOnce()
        {
            FireRejects(_config.ReactiveEscalateThreshold, atTick: 100);

            Assert.AreEqual(1, _engine.EscalateCount);
            Assert.AreEqual(_config.ReactiveStep, _engine.RecommendedExtraDelay);
        }

        // After a stable interval with no reactive trigger, the OnTickExecuted
        // hook decays the reactive correction one ReactiveStep toward 0.
        [Test]
        public void DeEscalate_StableInterval_DecaysReactiveToZero()
        {
            FireRejects(_config.ReactiveEscalateThreshold, atTick: 100); // reactive → ReactiveStep
            Assert.AreEqual(_config.ReactiveStep, _engine.RecommendedExtraDelay);
            Assert.AreEqual(0, _engine.DeEscalateCount);

            // Full dwell elapsed with no further PastTick/rollback → one decay step.
            _engine.CurrentTick = 100 + _config.ReactiveDeEscalateStableTicks;
            _engine.FireTickExecuted(_engine.CurrentTick - 1);

            Assert.AreEqual(1, _engine.DeEscalateCount, "stable dwell elapsed → one de-escalation step");
            Assert.AreEqual(0, _engine.RecommendedExtraDelay, "single ReactiveStep decays back to 0");
        }

        // Decay must not fire before the dwell elapses (thrash guard).
        [Test]
        public void DeEscalate_BeforeDwell_DoesNotDecay()
        {
            FireRejects(_config.ReactiveEscalateThreshold, atTick: 100);

            _engine.CurrentTick = 100 + _config.ReactiveDeEscalateStableTicks - 1; // one tick short
            _engine.FireTickExecuted(_engine.CurrentTick - 1);

            Assert.AreEqual(0, _engine.DeEscalateCount, "decay must wait the full stable interval");
            Assert.AreEqual(_config.ReactiveStep, _engine.RecommendedExtraDelay);
        }

        [Test]
        public void PastTick_FirstWindowSlides_StaleRejectsDoNotCombine()
        {
            // Regression pin: with the old int.MinValue sentinel, the window never
            // initialized and rejects accumulated match-wide — counts from different eras
            // combined into a spurious escalation.
            FireRejects(_config.ReactiveEscalateThreshold - 1, atTick: 100);

            // Far past the window: a single fresh reject must start a NEW window (count 1),
            // not complete the stale era's threshold.
            FireRejects(1, atTick: 100 + _config.ReactiveWindowTicks + 10);

            Assert.AreEqual(0, _engine.EscalateCount,
                "Rejects separated by more than ReactiveWindowTicks must not combine");
        }

        [Test]
        public void SharedCooldown_PastTickEscalation_BlocksRollbackBurst()
        {
            // PastTick path escalates and stamps the shared cooldown.
            FireRejects(_config.ReactiveEscalateThreshold, atTick: 100);
            Assert.AreEqual(1, _engine.EscalateCount);

            // A rollback burst inside the cooldown window must NOT stack a second step
            // (the two triggers share check-and-set on _lastReactiveEscalateTick).
            _engine.CurrentTick = 101;
            for (int i = 0; i < _config.RollbackBurstCount; i++)
                _engine.FireRollback(101, 95);
            Assert.AreEqual(1, _engine.EscalateCount,
                "Rollback-burst escalation inside the shared cooldown must be blocked");

            // Past the cooldown the burst trigger escalates normally.
            _engine.CurrentTick = 100 + _config.ReactiveEscalateCooldownTicks + _config.RollbackWindowTicks + 10;
            for (int i = 0; i < _config.RollbackBurstCount; i++)
                _engine.FireRollback(_engine.CurrentTick, _engine.CurrentTick - 5);
            Assert.AreEqual(2, _engine.EscalateCount,
                "After the cooldown elapses the rollback trigger must escalate");
        }

        [Test]
        public void SharedCooldown_RollbackEscalation_BlocksPastTick()
        {
            // Rollback-burst path escalates first.
            _engine.CurrentTick = 100;
            for (int i = 0; i < _config.RollbackBurstCount; i++)
                _engine.FireRollback(100, 95);
            Assert.AreEqual(1, _engine.EscalateCount);

            // PastTick threshold inside the cooldown must be blocked (cross-direction share).
            FireRejects(_config.ReactiveEscalateThreshold, atTick: 102);
            Assert.AreEqual(1, _engine.EscalateCount,
                "PastTick escalation inside the rollback-stamped cooldown must be blocked");
        }

        // The policy's own escalate/decay fires OnExtraDelayChanged but must
        // NOT open the server-push grace window (the _selfModifying guard skips _lastServerPushTick) —
        // else reactive would self-suppress. A real server push (ApplyExtraDelay) DOES open grace.
        [Test]
        public void Grace_SelfEscalateDoesNotOpen_ServerPushDoes()
        {
            _config.ReactiveEscalateCooldownTicks = 0;   // isolate grace from the shared cooldown
            _config.ServerPushGraceTicks = 100;

            // Self-escalate #1 (fires OnExtraDelayChanged while _selfModifying → grace must stay closed).
            FireRejects(_config.ReactiveEscalateThreshold, atTick: 100);
            Assert.AreEqual(1, _engine.EscalateCount);

            // A fresh threshold within the would-be grace window still escalates → self-escalate did
            // NOT open grace.
            FireRejects(_config.ReactiveEscalateThreshold, atTick: 110);
            Assert.AreEqual(2, _engine.EscalateCount,
                "self-escalate must not open the server-push grace window");

            // A real server push (external OnExtraDelayChanged, not self-modify) opens grace → the next
            // threshold inside the window is blocked.
            _engine.CurrentTick = 110;
            _engine.ApplyExtraDelay(12, ExtraDelaySource.DynamicPush); // sets _lastServerPushTick = 110
            FireRejects(_config.ReactiveEscalateThreshold, atTick: 115); // 115 - 110 = 5 < grace 100
            Assert.AreEqual(2, _engine.EscalateCount,
                "a server push opens grace → reactive escalate blocked within the window");
        }

        [Test]
        public void ReactiveMax_NoFurtherEscalation()
        {
            _engine.SetRecommendedExtraDelay(_config.ReactiveMax);

            FireRejects(_config.ReactiveEscalateThreshold, atTick: 100);
            _engine.CurrentTick = 100;
            for (int i = 0; i < _config.RollbackBurstCount; i++)
                _engine.FireRollback(100, 95);

            Assert.AreEqual(0, _engine.EscalateCount,
                "At the reactive ceiling both triggers must early-return");
        }
    }
}
