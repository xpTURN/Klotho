using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Recovery ladder rungs 3-4 unit tests:
    ///   - host attempt budget: a performed corrective reset consumes one attempt,
    ///     a cooldown-suppressed one does not;
    ///   - budget exhaustion broadcasts MatchAbort + aborts with StateDivergence
    ///     (opt-out via AutoAbortOnRecoveryExhausted);
    ///   - attempt decay after a quiet period of max(CorrectiveResetCooldownMs x 2,
    ///     ResyncMaxRetries x RESYNC_TIMEOUT_MS + CorrectiveResetCooldownMs) — derived from the
    ///     worst-case report cadence; time-separated transient episodes must not
    ///     accumulate into an abort, and the in-flight reset cooldown absorbs reports before the
    ///     exhaustion check so a reset is never aborted before it can prove itself;
    ///   - guest send triggers: post-apply hash mismatch (ApplyMismatch) and resync retry
    ///     exhaustion (RetryExhausted, which also resets the retry counter so the
    ///     report -> reset -> retry cycle can run).
    /// Wall-clock cooldowns are decoupled via reflection on _lastCorrectiveResetMs /
    /// _lastResyncFailureReportMs so the tests stay deterministic in editmode.
    /// </summary>
    [TestFixture]
    public class RecoveryLadderUnitTests
    {
        #region Mock network service (trimmed to the ladder surface)

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
            public int PlayerCount => 2;
            public int SpectatorCount => 0;
            public int PendingLateJoinCatchupCount => 0;
            public bool AllPlayersReady => true;
            public int LocalPlayerId { get; set; } = 0;
            public bool IsHost { get; set; }
            public int RandomSeed => 42;
            public IReadOnlyList<IPlayerInfo> Players { get; } =
                new List<IPlayerInfo> { new MockPlayerInfo { PlayerId = 0 }, new MockPlayerInfo { PlayerId = 1 } };

            public int SendResyncFailureReportCount { get; private set; }
            public ResyncFailureReason LastFailureReason { get; private set; }
            public int BroadcastMatchAbortCount { get; private set; }
            public byte LastAbortReason { get; private set; }
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

            public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash)
            {
                SendResyncFailureReportCount++;
                LastFailureReason = reason;
            }

            public void BroadcastMatchAbort(byte reason)
            {
                BroadcastMatchAbortCount++;
                LastAbortReason = reason;
            }

            public void FireResyncFailureReported(int playerId, int tick)
                => OnResyncFailureReported?.Invoke(playerId, tick);

            public void FireFullStateReceived(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast)
                => OnFullStateReceived?.Invoke(tick, stateData, stateHash, kind);

            // Suppress unused-event warnings without behavior.
            internal void SuppressWarnings()
            {
                OnGameStart?.Invoke();
                OnCountdownStarted?.Invoke(0);
                OnPlayerJoined?.Invoke(null);
                OnPlayerLeft?.Invoke(null);
                OnCommandReceived?.Invoke(null);
                OnDesyncDetected?.Invoke(0, 0, 0, 0);
                OnSyncHashCompared?.Invoke(0, 0, false);
                OnMatchAbortReceived?.Invoke(0);
                OnFrameAdvantageReceived?.Invoke(0, 0, 0);
                OnLocalPlayerIdAssigned?.Invoke(0);
                OnFullStateRequested?.Invoke(0, 0);
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

        private static readonly FieldInfo AttemptsField = typeof(KlothoEngine)
            .GetField("_correctiveResetAttempts", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastResetMsField = typeof(KlothoEngine)
            .GetField("_lastCorrectiveResetMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastReportMsField = typeof(KlothoEngine)
            .GetField("_lastResyncFailureReportMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastResetTickField = typeof(KlothoEngine)
            .GetField("_lastCorrectiveResetTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RetryCountField = typeof(KlothoEngine)
            .GetField("_resyncRetryCount", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo ResyncStateField = typeof(KlothoEngine)
            .GetField("_resyncState", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo RequestResyncMethod = typeof(KlothoEngine)
            .GetMethod("RequestFullStateResync", BindingFlags.NonPublic | BindingFlags.Instance);

        private static int Attempts(KlothoEngine e) => (int)AttemptsField.GetValue(e);
        private static void BypassResetCooldown(KlothoEngine e) => LastResetMsField.SetValue(e, 0L);
        private static int RetryCount(KlothoEngine e) => (int)RetryCountField.GetValue(e);

        private static void SetResyncStateRequested(KlothoEngine e)
        {
            // private enum ResyncState { None, Requested, Applying }
            var enumType = ResyncStateField.FieldType;
            ResyncStateField.SetValue(e, Enum.ToObject(enumType, 1));
        }

        #endregion

        private KlothoEngine _engine;
        private TestSimulation _simulation;
        private MockNetworkService _networkService;
        private SimulationConfig _config;
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("RecoveryLadderUnitTests");
        }

        private void CreateEngine(bool isHost, SimulationConfig config = null)
        {
            _config = config ?? new SimulationConfig();
            _simulation = new TestSimulation();
            _networkService = new MockNetworkService { IsHost = isHost };
            _engine = new KlothoEngine(_config, new SessionConfig());
            _engine.Initialize(_simulation, _networkService, _logger);
            _engine.Start(enableRecording: false);
        }

        private void FireReportWithCooldownBypassed(int tick)
        {
            BypassResetCooldown(_engine);
            _networkService.FireResyncFailureReported(1, tick);
        }

        [Test]
        public void Report_PerformedReset_ConsumesOneAttempt()
        {
            CreateEngine(isHost: true);

            FireReportWithCooldownBypassed(10);

            Assert.AreEqual(1, Attempts(_engine),
                "A performed corrective reset must consume exactly one attempt");
            Assert.AreEqual(KlothoState.Running, _engine.State);
        }

        [Test]
        public void Report_CooldownSuppressedReset_DoesNotConsumeAttempt()
        {
            CreateEngine(isHost: true);

            // First report performs a reset (consumes attempt 1) and stamps _lastCorrectiveResetMs.
            FireReportWithCooldownBypassed(10);
            Assert.AreEqual(1, Attempts(_engine));

            // Second report arrives inside the reset cooldown — suppressed, no attempt consumed.
            // (N guests reporting the same episode must cost a single attempt.)
            _networkService.FireResyncFailureReported(1, 11);
            Assert.AreEqual(1, Attempts(_engine),
                "A cooldown-suppressed corrective reset must not consume an attempt");
        }

        [Test]
        public void Report_BudgetExhausted_BroadcastsAbortAndAborts()
        {
            CreateEngine(isHost: true);

            AbortReason? abortedWith = null;
            _engine.OnMatchAborted += reason => abortedWith = reason;

            // Default CorrectiveResetMaxAttempts = 2: two performed resets exhaust the budget.
            FireReportWithCooldownBypassed(10);
            FireReportWithCooldownBypassed(11);
            Assert.AreEqual(2, Attempts(_engine));
            Assert.AreEqual(KlothoState.Running, _engine.State, "Budget not yet exhausted — no abort");

            // Third report: budget exhausted -> rung 4.
            FireReportWithCooldownBypassed(12);

            Assert.AreEqual(1, _networkService.BroadcastMatchAbortCount,
                "Exhaustion must broadcast MatchAbort to guests");
            Assert.AreEqual((byte)AbortReason.StateDivergence, _networkService.LastAbortReason);
            Assert.AreEqual(KlothoState.Aborted, _engine.State);
            Assert.AreEqual(AbortReason.StateDivergence, abortedWith);
        }

        [Test]
        public void Report_AutoAbortOptOut_DoesNotAbort()
        {
            CreateEngine(isHost: true, new SimulationConfig { AutoAbortOnRecoveryExhausted = false });

            FireReportWithCooldownBypassed(10);
            FireReportWithCooldownBypassed(11);
            FireReportWithCooldownBypassed(12); // exhausted, but opt-out

            Assert.AreEqual(0, _networkService.BroadcastMatchAbortCount,
                "AutoAbortOnRecoveryExhausted=false must leave the abort decision to the game layer");
            Assert.AreEqual(KlothoState.Running, _engine.State);
        }

        [Test]
        public void Report_QuietPeriod_DecaysAttemptsBeforeExhaustionCheck()
        {
            CreateEngine(isHost: true);

            // Budget already at the limit, but the last failure report is older than the derived
            // decay window — the decay must zero the budget BEFORE the exhaustion check, so a fresh
            // transient episode performs a reset instead of aborting.
            // window = max(cooldown x 2, ResyncMaxRetries x RESYNC_TIMEOUT_MS + cooldown);
            // for the default config that is max(10s, 20s) = 20s (RESYNC_TIMEOUT_MS = 5000).
            AttemptsField.SetValue(_engine, 2);
            long decayWindow = Math.Max(
                _config.CorrectiveResetCooldownMs * 2L,
                (long)_config.ResyncMaxRetries * 5000L + _config.CorrectiveResetCooldownMs);
            long stale = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - decayWindow - 1000L;
            LastReportMsField.SetValue(_engine, stale);

            FireReportWithCooldownBypassed(10);

            Assert.AreEqual(KlothoState.Running, _engine.State,
                "Time-separated transient episodes must not accumulate into an abort");
            Assert.AreEqual(0, _networkService.BroadcastMatchAbortCount);
            Assert.AreEqual(1, Attempts(_engine), "Decayed to 0, then the fresh reset consumed 1");
        }

        [Test]
        public void GuestApplyMismatch_SendsFailureReport()
        {
            CreateEngine(isHost: false);

            // Arm the resync-receive path, then deliver a full state whose advertised hash
            // disagrees with the post-restore local hash (TestSimulation stateless hash = 12345).
            SetResyncStateRequested(_engine);
            int tick = _engine.CurrentTick + 5; // past the retreat guard
            _networkService.FireFullStateReceived(tick, BitConverter.GetBytes(12345L), stateHash: 999L);

            Assert.AreEqual(1, _networkService.SendResyncFailureReportCount,
                "A post-apply hash mismatch on a guest must report to the host (rung-3 feedback)");
            Assert.AreEqual(ResyncFailureReason.ApplyMismatch, _networkService.LastFailureReason);
            Assert.AreEqual(KlothoState.Running, _engine.State, "Guests never abort on their own");
        }

        [Test]
        public void GuestRetryExhausted_SendsReportAndResetsRetryCount()
        {
            CreateEngine(isHost: false);

            // Seed the retry counter at the limit; the next request exceeds it.
            RetryCountField.SetValue(_engine, _config.ResyncMaxRetries);
            bool resyncFailed = false;
            _engine.OnResyncFailed += () => resyncFailed = true;

            RequestResyncMethod.Invoke(_engine, null);

            Assert.IsTrue(resyncFailed, "OnResyncFailed must still fire for game-layer observers");
            Assert.AreEqual(1, _networkService.SendResyncFailureReportCount);
            Assert.AreEqual(ResyncFailureReason.RetryExhausted, _networkService.LastFailureReason);
            Assert.AreEqual(0, RetryCount(_engine),
                "Exhaustion must reset the retry counter so the report -> reset -> retry cycle can run");
        }

        // Rung 3<->4 time-arithmetic fixes (defect A: cooldown absorption ordering;
        // defect B: decay window derived from the worst-case report cadence). Wall-clock is
        // decoupled via reflection on the last-timestamp fields, as in the suite above.

        [Test]
        public void DefectA_StaleExhaustedReportBeforeResetTick_Absorbed()
        {
            CreateEngine(isHost: true);

            // Budget exhausted, last corrective reset broadcast at tick 100. A report whose
            // divergence tick (50) PREDATES that reset is stale — it was in flight before the guest
            // could apply the reset, so it must be absorbed, not abort a reset yet to converge.
            AttemptsField.SetValue(_engine, _config.CorrectiveResetMaxAttempts);
            LastResetTickField.SetValue(_engine, 100);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastReportMsField.SetValue(_engine, now);   // recent — no decay

            _networkService.FireResyncFailureReported(1, 50); // reportTick 50 < resetTick 100

            Assert.AreEqual(0, _networkService.BroadcastMatchAbortCount,
                "A stale report predating the latest reset must be absorbed, not abort (defect A)");
            Assert.AreEqual(KlothoState.Running, _engine.State);
            Assert.AreEqual(_config.CorrectiveResetMaxAttempts, Attempts(_engine),
                "An absorbed report must not change the budget");
        }

        [Test]
        public void DefectA_FreshExhaustedReportAtOrAfterResetTick_Aborts()
        {
            CreateEngine(isHost: true);

            // Budget exhausted, last reset at tick 50. A report at/after that tick means the guest
            // applied the reset and STILL diverges — the reset failed, so the abort must fire even
            // though a reset was just performed (this is the persistent-divergence sweep scenario:
            // zero-latency apply-and-re-mismatch lands on the reset tick).
            AttemptsField.SetValue(_engine, _config.CorrectiveResetMaxAttempts);
            LastResetTickField.SetValue(_engine, 50);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastResetMsField.SetValue(_engine, now);   // reset just performed — cooldown irrelevant now
            LastReportMsField.SetValue(_engine, now);   // recent — no decay

            _networkService.FireResyncFailureReported(1, 80); // reportTick 80 >= resetTick 50

            Assert.AreEqual(1, _networkService.BroadcastMatchAbortCount,
                "A report at/after the reset tick proves the reset failed — exhausted budget must abort");
            Assert.AreEqual((byte)AbortReason.StateDivergence, _networkService.LastAbortReason);
            Assert.AreEqual(KlothoState.Aborted, _engine.State);
        }

        [Test]
        public void DefectB_GapWithinDerivedWindow_AccumulatesInsteadOfDecaying()
        {
            // Default: cooldown=5000, retries=3 -> window = max(10s, 3x5s + 5s) = 20s.
            CreateEngine(isHost: true);

            AttemptsField.SetValue(_engine, 1);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 15s gap: beyond the OLD window (cooldown x 2 = 10s) but within the derived window (20s).
            // Pre-fix this decayed the budget on every report so rung 4 was unreachable (defect B).
            LastReportMsField.SetValue(_engine, now - 15000L);

            FireReportWithCooldownBypassed(10);

            Assert.AreEqual(2, Attempts(_engine),
                "A 15s gap is within the worst-case-derived window (20s) — the budget must accumulate, not decay (defect B)");
            Assert.AreEqual(KlothoState.Running, _engine.State);
        }

        [Test]
        public void DefectB_GapBeyondDerivedWindow_StillDecays()
        {
            CreateEngine(isHost: true); // window = 20s

            AttemptsField.SetValue(_engine, 2);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastReportMsField.SetValue(_engine, now - 21000L); // > 20s window -> genuinely quiet

            FireReportWithCooldownBypassed(10);

            Assert.AreEqual(0, _networkService.BroadcastMatchAbortCount,
                "A gap beyond the derived window is genuinely quiet — decay must zero the budget before the exhaustion check");
            Assert.AreEqual(1, Attempts(_engine), "Decayed to 0, then the fresh reset consumed 1");
            Assert.AreEqual(KlothoState.Running, _engine.State);
        }

        [Test]
        public void DefectB_DerivedWindowShrinksWhenRetriesZero()
        {
            // retries=0 -> window = max(cooldown x 2, 0 + cooldown) = cooldown x 2 = 10s (floor).
            // The same 15s gap that ACCUMULATES at retries=3 must DECAY here: a fast RetryExhausted
            // cadence does not warrant extending the quiet period.
            CreateEngine(isHost: true, new SimulationConfig { ResyncMaxRetries = 0 });

            AttemptsField.SetValue(_engine, 1);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LastReportMsField.SetValue(_engine, now - 15000L);

            FireReportWithCooldownBypassed(10);

            Assert.AreEqual(1, Attempts(_engine),
                "With retries=0 the window stays at the cooldown x 2 floor (10s), so a 15s gap decays then performs one reset");
        }
    }
}
