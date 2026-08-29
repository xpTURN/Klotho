#pragma warning disable CS0067
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Helper.Tests;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Error-correction deltas on the P2P path (IMP103 V-4).
    ///
    /// The three delta functions were reachable only from the SD client: CapturePreRollbackTransforms
    /// gated itself on <c>_pendingVerifiedQueue.Count</c>, a queue only the SD client fills. P2P renders
    /// every view on the CSP path, which is the one path that APPLIES the deltas, so
    /// EnableErrorCorrection was a knob that did nothing there — rollbacks from late remote input snapped
    /// instead of decaying.
    ///
    /// The fix promotes that guard to a caller-supplied argument, so each mode passes its own expression
    /// of "a rollback is imminent this frame": SD passes the queue count, P2P passes _hasPendingRollback.
    /// These tests pin the P2P wiring, the frame-boundary Clear, and the equivalence of the promoted
    /// guard.
    /// </summary>
    [TestFixture]
    public class P2pErrorCorrectionDeltaTests
    {
        private const int MaxEntities = 16;
        private const int TickIntervalMs = 50;

        // Above PosMinCorrection (0.001) and below PosTeleportDistance (1.0), so the delta is recorded
        // as an ordinary correction rather than dropped or treated as a teleport.
        private const float Shift = 0.3f;
        private const float Tolerance = 1e-3f;

        private static readonly FieldInfo PreRollbackPosField = typeof(KlothoEngine)
            .GetField("_preRollbackPos", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo StateField = typeof(KlothoEngine)
            .GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo CatchingUpField = typeof(KlothoEngine)
            .GetField("_isCatchingUp", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo SpectatorModeField = typeof(KlothoEngine)
            .GetField("_isSpectatorMode", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo ResyncStateField = typeof(KlothoEngine)
            .GetField("_resyncState", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Type ResyncStateType = typeof(KlothoEngine)
            .GetNestedType("ResyncState", BindingFlags.NonPublic);

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("P2pErrorCorrectionDeltaTests");
        }

        /// <summary>
        /// P2P engine on a real EcsSimulation — CapturePreRollbackTransforms bails out on anything else —
        /// with one entity carrying both components the delta filter requires.
        /// InputDelayTicks pre-fills the early ticks with EmptyCommand so the tick loop takes the
        /// all-real-inputs path instead of stalling on missing input.
        /// </summary>
        private Harness NewHarness(bool enableErrorCorrection = true,
                                   IKLogger logger = null,
                                   bool attachCorrectionTarget = true)
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: TickIntervalMs);
            sim.Initialize();

            var engine = new KlothoEngine(
                new SimulationConfig
                {
                    Mode = NetworkMode.P2P,
                    TickIntervalMs = TickIntervalMs,
                    MaxRollbackTicks = 8,
                    SyncCheckInterval = 8,   // config validation: must be in [1, MaxRollbackTicks]
                    MaxEntities = MaxEntities,
                    InputDelayTicks = 40,
                    EnableErrorCorrection = enableErrorCorrection,
                },
                new SessionConfig());
            var net = new MockNetworkService();
            engine.Initialize(sim, net, logger ?? _logger);
            engine.SetCommandFactory(new CommandFactory());
            engine.Start(enableRecording: false);

            // After Start: it seeds its own deterministic entities (participants -> seed -> matchEnd),
            // so the test entity is created afterwards to keep that order untouched.
            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });
            if (attachCorrectionTarget) sim.Frame.Add(entity, new ErrorCorrectionTargetComponent());

            return new Harness { Engine = engine, Sim = sim, Entity = entity, Net = net };
        }

        /// <summary>
        /// A resync's correction reaches the view, and the frame's own ClearErrorDeltas does not eat it.
        ///
        /// The deferral is the whole difficulty. Both FullState handlers run from the transport poll, and
        /// the driver polls BEFORE it updates the session (KlothoSessionDriver: PollEvents then s.Update)
        /// while ClearErrorDeltas is the first thing the update does — so publishing straight from the
        /// handler is erased in the same frame, before any view reads it. The deltas are parked instead and
        /// handed over immediately after that clear.
        ///
        /// Promoted from measurement-only on two live SD resyncs: 4.6 cm and 5.7 cm position jumps, both
        /// inside the band error correction exists for, the second equal to the median of its own session's
        /// non-zero corrections. The jump is the prediction error at that instant, not a function of what
        /// caused the resync — injecting a real Scale divergence instead of a bare hash salt moved it by
        /// 1 cm (IMP103 #5).
        /// </summary>
        [Test]
        public void Resync_ParksItsCorrection_ThenPublishesItPastTheFrameClear()
        {
            var log = new LogCapture();
            var h = NewHarness(logger: log);
            h.AdvanceTo(4);

            // Serialize BEFORE the misprediction, so applying it restores the pre-shift pose and the
            // correction is exactly Shift — above PosMinCorrection, below the 1 m teleport reset.
            var (state, hash) = h.Sim.SerializeFullStateWithHash();
            h.MispredictBy(Shift);

            h.Net.RaiseFullStateReceived(h.Engine.CurrentTick, state, hash, FullStateKind.CorrectiveReset);

            Assert.That(h.Delta.x, Is.EqualTo(0f).Within(Tolerance),
                "parked, not published — the frame's clear has not run yet, and publishing before it is how "
                + "the first version of this lost every resync correction");

            h.Step();   // ClearErrorDeltas -> PublishPendingResyncDeltas

            Assert.That(System.Math.Abs(h.Delta.x), Is.EqualTo(Shift).Within(Tolerance),
                "the correction survives the clear and reaches the view");
#if DEBUG
            string summary = null;
            foreach (var e in log.Entries)
                if (e.Message.Contains("[EC][RESYNC]") && e.Message.Contains("reason=")) summary = e.Message;
            Assert.That(summary, Does.Contain("corrected=1"), $"log was: {summary}");
#endif
        }

        /// <summary>
        /// A resync the retreat guard rejects costs the frame nothing.
        ///
        /// <c>ApplyFullState</c> returning Skipped means NOTHING was applied, so the captured poses
        /// describe no jump — and a correction already published for this frame has to survive, or a
        /// rejected state would silently drop a frame of smoothing.
        /// </summary>
        [Test]
        public void Resync_SkippedApply_LeavesTheFramesCorrectionAlone()
        {
            var h = NewHarness();
            RunCorrectedFrame(h);
            var published = h.Delta;
            Assert.That(System.Math.Abs(published.x), Is.GreaterThan(Tolerance), "precondition: a correction is published");

            var (state, hash) = h.Sim.SerializeFullStateWithHash();

            // ResyncRequest is the reason that does NOT allow retreat — CorrectiveReset and LateJoin do, so
            // with those the result is never Skipped. Tick 0 is far behind _lastVerifiedTick, which is what
            // the guard rejects.
            ResyncStateField.SetValue(h.Engine, Enum.Parse(ResyncStateType, "Requested"));
            h.Net.RaiseFullStateReceived(0, state, hash, FullStateKind.Unicast);

            Assert.That(h.Delta.x, Is.EqualTo(published.x).Within(Tolerance),
                "a rejected state must not disturb what the view is already smoothing");
        }

        private sealed class Harness
        {
            public KlothoEngine Engine;
            public EcsSimulation Sim;
            public EntityRef Entity;
            public MockNetworkService Net;

            public void Step() => Engine.Update((Engine.TickInterval + 1) / 1000f);

            public void AdvanceTo(int tick)
            {
                while (Engine.CurrentTick < tick)
                    Step();
            }

            /// <summary>
            /// Fakes "the prediction was wrong": moves the live transform off where the snapshot ring
            /// holds it, so the rollback restore produces a measurable difference.
            ///
            /// RefreshPreviousTransform is load-bearing, not tidiness. The engine compares
            /// Lerp(PreviousPosition, Position, PredictedAlpha) before and after the rollback, so moving
            /// Position alone yields alpha * Shift — and alpha is the accumulator remainder left after
            /// the tick loop, which is ~0 when the harness steps by exactly one tick interval. The test
            /// would then read ~0 and fail for a reason that looks exactly like the wiring being absent.
            /// Moving both endpoints makes the delta Shift regardless of alpha.
            /// </summary>
            public void MispredictBy(float dx)
            {
                ref var t = ref Sim.Frame.Get<TransformComponent>(Entity);
                t.Position = new FPVector3(t.Position.x + FP64.FromFloat(dx), t.Position.y, t.Position.z);
                Sim.Frame.RefreshPreviousTransform(Entity);
            }

            public (float x, float y, float z) Delta => Engine.GetPositionDelta(Entity.Index);

            /// <summary>
            /// Yaw twin of <see cref="MispredictBy"/>. Both endpoints move for the same reason: the
            /// engine diffs the yaw at PredictedAlpha, not the raw component.
            /// </summary>
            public void SetYawDegrees(float degrees)
            {
                ref var t = ref Sim.Frame.Get<TransformComponent>(Entity);
                t.Rotation = FP64.FromFloat(degrees * Deg2Rad);
                Sim.Frame.RefreshPreviousTransform(Entity);
            }

            public float YawDeltaDegrees => Engine.GetYawDelta(Entity.Index) * Rad2Deg;
        }

        private const float Deg2Rad = 0.017453292f;
        private const float Rad2Deg = 57.29578f;

        /// <summary>
        /// Drives one rollback-correcting frame: advance, move the live transform, request a rollback to
        /// a tick the ring still holds, then let the frame's FlushPendingRollback restore and re-simulate.
        /// </summary>
        private static void RunCorrectedFrame(Harness h)
        {
            h.AdvanceTo(6);
            h.MispredictBy(Shift);
            h.Engine.RequestRollback(h.Engine.CurrentTick - 2);
            h.Step();
        }

        /// <summary>
        /// The startup transient this guard must not call a misconfiguration.
        ///
        /// The frame the capture reads is the PRE-rollback one, and an SD client running
        /// UsePrediction=false executes tick 0 as EmptyCommand — it only learns about
        /// SpawnCharacterCommand from the very rollback being captured. So the first rollback of an SD
        /// session legitimately sees a world with no characters in it, and the first version of this guard
        /// latched a warning there: both Brawler SD clients printed it at tick 12, sixty-five log lines
        /// before the first [EC][DIAG] proved the component was present all along (IMP103).
        /// </summary>
        [Test]
        public void NoTarget_OnTheFirstRollback_StaysQuiet()
        {
#if !DEBUG
            // Same shape as RollbackWithNoCorrectionTarget_WarnsOnce: the warning is compiled out of a
            // Release `dotnet test`, so "no warning" would pass for the wrong reason.
            Assert.Ignore("the no-target warning is dev-build-only");
#else
            var log = new LogCapture();
            var h = NewHarness(logger: log, attachCorrectionTarget: false);

            RunCorrectedFrame(h);

            Assert.That(CountMatching(log, NoTargetMessage), Is.EqualTo(0),
                "one empty pre-rollback frame is what a normal SD session start looks like");
#endif
        }

        /// <summary>
        /// And the count has to be consecutive, not cumulative — a game whose targets come and go (pooled
        /// projectiles, a respawn gap) must never accumulate its way to the warning.
        /// </summary>
        [Test]
        public void NoTarget_InterruptedByAMatch_NeverWarns()
        {
#if !DEBUG
            // Same shape as RollbackWithNoCorrectionTarget_WarnsOnce: the warning is compiled out of a
            // Release `dotnet test`, so "no warning" would pass for the wrong reason.
            Assert.Ignore("the no-target warning is dev-build-only");
#else
            var log = new LogCapture();
            var h = NewHarness(logger: log, attachCorrectionTarget: false);

            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < WarnThreshold - 1; i++) RunCorrectedFrame(h);

                // One rollback that does find a target resets the run.
                h.Sim.Frame.Add(h.Entity, new ErrorCorrectionTargetComponent());
                RunCorrectedFrame(h);
                h.Sim.Frame.Remove<ErrorCorrectionTargetComponent>(h.Entity);
            }

            Assert.That(CountMatching(log, NoTargetMessage), Is.EqualTo(0),
                $"{3 * (WarnThreshold - 1)} empty rollbacks total, but never {WarnThreshold} in a row");
#endif
        }

        // Mirrors NoCorrectionTargetWarnThreshold in KlothoEngine.ErrorCorrection. A literal rather than a
        // reflected value: if the engine raises the threshold these tests should be re-read, not silently
        // follow it.
        private const int WarnThreshold = 8;

        private const string NoTargetMessage = "found no entity carrying ErrorCorrectionTargetComponent";

        private static int CountMatching(LogCapture log, string substring)
        {
            int n = 0;
            foreach (var e in log.Entries)
                if (e.Level == KLogLevel.Warning && e.Message.Contains(substring)) n++;
            return n;
        }

        private static void AssertNoDelta(Harness h, string because)
        {
            var (x, y, z) = h.Delta;
            Assert.That(new Vector3Like(x, y, z).Magnitude, Is.LessThan(Tolerance), because);
        }

        private readonly struct Vector3Like
        {
            private readonly float _x, _y, _z;
            public Vector3Like(float x, float y, float z) { _x = x; _y = y; _z = z; }
            public float Magnitude => MathF.Sqrt(_x * _x + _y * _y + _z * _z);
        }

        // ── The no-target diagnostic ──

        /// <summary>
        /// Error correction on, a rollback happening, and not one entity carrying
        /// ErrorCorrectionTargetComponent: nothing will ever be corrected, and until this warning the
        /// engine said nothing. The view-side check could not cover it — it warns on a delta-free stretch,
        /// which is also what a perfectly predicted session looks like, and it only runs when the flag it
        /// tells you to check is already on (IMP103).
        /// </summary>
        [Test]
        public void RollbackWithNoCorrectionTarget_WarnsOnce()
        {
#if !DEBUG
            // The warning is guarded by DEBUG || DEVELOPMENT_BUILD || UNITY_EDITOR, and a Release
            // `dotnet test` defines none of them — same shape as FPNavMeshRebakePoolOverlapTests.
            Assert.Ignore("the no-target warning is dev-build-only");
#else
            var log = new LogCapture();
            var h = NewHarness(logger: log, attachCorrectionTarget: false);

            // One empty rollback must stay quiet — that is what an SD session start looks like, not a
            // misconfiguration (see NoTarget_OnTheFirstRollback_StaysQuiet).
            RunCorrectedFrame(h);
            Assert.That(CountMatching(log, NoTargetMessage), Is.EqualTo(0),
                "a single empty filter is not evidence");

            // A real misconfiguration stays empty for every rollback there will ever be.
            for (int i = 1; i < WarnThreshold; i++) RunCorrectedFrame(h);
            Assert.That(CountMatching(log, NoTargetMessage), Is.EqualTo(1),
                "the engine knows the flag, the rollback and the empty filter — all three locally");

            // Latched. Counting only this message: the rollback path emits warnings of its own, and
            // CountAt would fold them in.
            for (int i = 0; i < 3; i++) RunCorrectedFrame(h);
            Assert.That(CountMatching(log, NoTargetMessage), Is.EqualTo(1),
                "latched — one line is the whole message, however many rollbacks follow");
#endif
        }

        /// <summary>
        /// The guard must not fire when the wiring is right, and must not fire on a quiet frame either.
        /// Without this a warning that always fired would pass the test above.
        /// </summary>
        [Test]
        public void CorrectionTargetPresent_OrNoRollback_DoesNotWarn()
        {
            var withTarget = new LogCapture();
            RunCorrectedFrame(NewHarness(logger: withTarget));
            Assert.That(withTarget.Contains(KLogLevel.Warning, "ErrorCorrectionTargetComponent"), Is.False,
                "the component is attached, so there is nothing to report");

            var noRollback = new LogCapture();
            var h = NewHarness(logger: noRollback, attachCorrectionTarget: false);
            h.AdvanceTo(6);
            h.Step();
            Assert.That(noRollback.Contains(KLogLevel.Warning, "ErrorCorrectionTargetComponent"), Is.False,
                "no rollback happened, so the absence of targets is not yet a question");
        }

        // ── T1: the defect itself ──

        /// <summary>
        /// A rollback in P2P must produce a position delta. Before the fix the P2P update had no call
        /// site at all, so this read (0,0,0) — the knob was inert and nothing said so.
        /// </summary>
        [Test]
        public void P2pRollback_ProducesAPositionDelta()
        {
            var h = NewHarness();

            RunCorrectedFrame(h);

            var (x, y, z) = h.Delta;
            Assert.That(x, Is.EqualTo(Shift).Within(Tolerance),
                "the pre-rollback pose minus the re-simulated pose is the correction the view must decay");
            Assert.That(y, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(z, Is.EqualTo(0f).Within(Tolerance));
        }

        // ── T2: the trigger really gates ──

        /// <summary>
        /// A frame with no rollback must produce nothing. Without this, a wiring that captured every
        /// frame would pass T1 while paying the filter cost — and would feed the view a stream of
        /// spurious corrections.
        /// </summary>
        [Test]
        public void FrameWithoutARollback_ProducesNoDelta()
        {
            var h = NewHarness();

            h.AdvanceTo(6);
            h.MispredictBy(Shift);
            h.Step();

            AssertNoDelta(h, "no rollback happened, so there is nothing to correct");
        }

        // ── T3 / T3': the frame-boundary Clear ──

        /// <summary>
        /// Deltas belong to the frame that produced them. The next frame must start clean, or the view's
        /// accumulator re-adds the same correction every frame until it crosses the teleport threshold.
        /// </summary>
        [Test]
        public void NextFrame_ClearsThePreviousFrameDelta()
        {
            var h = NewHarness();
            RunCorrectedFrame(h);
            Assert.That(h.Delta.x, Is.EqualTo(Shift).Within(Tolerance), "precondition: the delta exists");

            h.Step();

            AssertNoDelta(h, "ClearErrorDeltas must run at the head of the P2P path");
        }

        /// <summary>
        /// The placement test. A resync-waiting frame returns early before the tick loop, so a Clear
        /// written after that return never runs and the stale delta survives — which the ordinary
        /// next-frame case above cannot distinguish. Starting from a frame that HAS a delta is what
        /// gives this test its teeth.
        /// </summary>
        [Test]
        public void ResyncWaitingFrame_StillClearsThePreviousFrameDelta()
        {
            var h = NewHarness();
            RunCorrectedFrame(h);
            Assert.That(h.Delta.x, Is.EqualTo(Shift).Within(Tolerance), "precondition: the delta exists");

            ResyncStateField.SetValue(h.Engine, Enum.Parse(ResyncStateType, "Requested"));
            h.Step();

            AssertNoDelta(h, "Clear must sit ahead of the resync early return, as it does on the SD path");
        }

        // ── T4: a rejected request is not a rollback ──

        /// <summary>
        /// RequestRollback rejects a target at or beyond the current tick at its own entrance, so the
        /// pending flag never rises and the capture never runs. Complements T2: there the request was
        /// absent, here it was refused.
        /// </summary>
        [Test]
        public void RejectedRollbackRequest_ProducesNoDelta()
        {
            var h = NewHarness();

            h.AdvanceTo(6);
            h.MispredictBy(Shift);
            h.Engine.RequestRollback(h.Engine.CurrentTick + 5);
            h.Step();

            AssertNoDelta(h, "_hasPendingRollback is the trigger, and a rejected request never sets it");
        }

        // ── T5: the knob still governs ──

        /// <summary>
        /// The new call site must not route around EnableErrorCorrection. This is the safeguard that
        /// replaces "the functions are unreachable from P2P" as the reason P2P stays quiet by default.
        /// </summary>
        [Test]
        public void ErrorCorrectionDisabled_ProducesNoDeltaEvenOnRollback()
        {
            var h = NewHarness(enableErrorCorrection: false);

            RunCorrectedFrame(h);

            AssertNoDelta(h, "the config flag is the gate; the P2P wiring must not bypass it");
        }

        // ── T6: the promoted guard ──

        /// <summary>
        /// The guard itself, independent of any mode: false captures nothing, true captures. What this
        /// does NOT pin is that the SD call site passes its queue count — after the promotion the
        /// function cannot see that queue, so that half rests on the existing SD suite staying green.
        /// </summary>
        /// <summary>
        /// A yaw correction that crosses the +/-pi seam is the short way round, not the long way
        /// (IMP105 follow-up).
        ///
        /// The engine reconstructs the rendered yaw on both sides of the rollback and subtracts. Both
        /// steps used to ignore the wrap, so a 20-degree turn from +170 to -170 measured as -340 — and
        /// the view's rotation snap bound is 90, so a correction well inside the smoothing band was
        /// thrown away instead. Found in a live P2P session where every other correction measured 28.8
        /// degrees and the one crossing the seam measured exactly 270.000.
        ///
        /// Asserted through GetYawDelta rather than the wrap helper, because the defect was in the two
        /// call sites agreeing with each other, not in the arithmetic.
        /// </summary>
        [Test]
        public void YawDeltaAcrossTheWrapSeam_TakesTheShortWayRound()
        {
            var h = NewHarness();
            h.SetYawDegrees(170f);
            h.AdvanceTo(6);

            // The mispredicted live yaw sits on the far side of the seam; the rollback restores +170.
            h.SetYawDegrees(-170f);
            h.Engine.RequestRollback(h.Engine.CurrentTick - 2);
            h.Step();

            Assert.That(System.Math.Abs(h.YawDeltaDegrees), Is.EqualTo(20f).Within(0.5f),
                "the two poses are 20 degrees apart — measuring 340 makes the view snap on a correction it should have smoothed");
        }

        [Test]
        public void PromotedGuard_CapturesOnlyWhenTheCallerSaysRollbackIsImminent()
        {
            var h = NewHarness();
            h.AdvanceTo(6);

            var capture = typeof(KlothoEngine).GetMethod(
                "CapturePreRollbackTransforms",
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: new[] { typeof(bool) }, modifiers: null);
            Assert.That(capture, Is.Not.Null,
                "CapturePreRollbackTransforms must take the caller's rollback-imminent answer");

            var preRollbackPos = (System.Collections.IDictionary)PreRollbackPosField.GetValue(h.Engine);

            capture.Invoke(h.Engine, new object[] { false });
            Assert.That(preRollbackPos.Count, Is.Zero, "no rollback imminent — nothing to capture");

            capture.Invoke(h.Engine, new object[] { true });
            Assert.That(preRollbackPos.Count, Is.GreaterThan(0), "rollback imminent — capture the pose");
        }

        #region Minimal network service (P2P Running with no transport)

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
            public bool IsHost { get; set; } = true;
            public int RandomSeed { get; set; } = 42;
            public IReadOnlyList<IPlayerInfo> Players => new List<IPlayerInfo> { new MockPlayerInfo { PlayerId = 0 } };

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

            // A C# event can only be raised from the declaring type, and the resync path is reached only
            // through this one.
            public void RaiseFullStateReceived(int tick, byte[] data, long hash, FullStateKind kind)
                => OnFullStateReceived?.Invoke(tick, data, hash, kind);
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
            public void SendPlayerConfig(int playerId, PlayerConfigBase playerConfig) { }
        }

        #endregion

        // ── IMP105 C-3 / C-6: the per-frame clear must outlive every early return ──

        /// <summary>
        /// A frame that returns early at Paused must still have dropped the previous frame's delta.
        ///
        /// The clear used to sit below four early returns. Views read GetPositionDelta from LateUpdate,
        /// which runs whether or not the engine reached its tick loop, and ErrorVisualState.Tick ADDS the
        /// value it reads to an accumulator every frame — so a delta left published during a stall is
        /// re-applied per frame until the total crosses the teleport threshold and snaps back.
        /// </summary>
        [Test]
        public void PausedFrame_DropsThePreviousFrameDelta()
        {
            var h = NewHarness();
            RunCorrectedFrame(h);
            Assert.That(h.Delta.x, Is.EqualTo(Shift).Within(Tolerance), "precondition: a delta is published");

            StateField.SetValue(h.Engine, KlothoState.Paused);
            h.Step();

            Assert.That(h.Delta.x, Is.EqualTo(0f).Within(Tolerance),
                "a stalled frame must not keep re-publishing last frame's correction — the view "
                + "accumulates it once per frame, not once per rollback");
        }

        /// <summary>
        /// The same for the late-join catch-up return, which is the long window — seconds, not the
        /// milliseconds a Paused frame usually lasts.
        /// </summary>
        [Test]
        public void CatchupFrame_DropsThePreviousFrameDelta()
        {
            var h = NewHarness();
            RunCorrectedFrame(h);
            Assert.That(h.Delta.x, Is.EqualTo(Shift).Within(Tolerance), "precondition: a delta is published");

            CatchingUpField.SetValue(h.Engine, true);
            h.Step();

            Assert.That(h.Delta.x, Is.EqualTo(0f).Within(Tolerance));
        }

        /// <summary>
        /// A spectator captures nothing, so the resync path has nothing to park.
        ///
        /// Every spectator view is on the snapshot path, which draws the authoritative state and discards
        /// these deltas — so the capture had no consumer, and the parked maps it fed are drained by a
        /// publish call the spectator update does not make. Skipping the capture removes the source
        /// rather than adding a publish nobody needs.
        /// </summary>
        [Test]
        public void Spectator_CapturesNothing_SoThereIsNothingToPark()
        {
            var h = NewHarness();
            h.AdvanceTo(6);

            // Called directly: driving this through Update would need a spectator session, and a
            // spectator never advances CurrentTick from this harness (its branch returns before the tick
            // loop until bootstrap). The capture guard is the whole subject, so it is what gets called.
            var capture = typeof(KlothoEngine).GetMethod(
                "CapturePreRollbackTransforms", BindingFlags.NonPublic | BindingFlags.Instance);
            var preRollback = (System.Collections.IDictionary)PreRollbackPosField.GetValue(h.Engine);

            capture.Invoke(h.Engine, new object[] { true });
            Assert.That(preRollback.Count, Is.GreaterThan(0),
                "control: outside spectator mode the same call must capture, or the next assertion proves nothing");

            preRollback.Clear();
            SpectatorModeField.SetValue(h.Engine, true);
            capture.Invoke(h.Engine, new object[] { true });

            Assert.That(preRollback.Count, Is.Zero,
                "capture is skipped for a spectator the same way it is for replay and the SD server — "
                + "every spectator view is on the snapshot path, which discards these deltas, and the "
                + "resync path would otherwise park them with nothing to drain them");
        }

        /// <summary>
        /// Stop() must leave no correction behind for a second session on the same engine instance.
        /// A fresh EcsSimulation hands out entity indices from zero again, so a surviving delta is applied
        /// to whatever occupies that index next.
        /// </summary>
        [Test]
        public void Stop_ClearsTheCorrectionState()
        {
            var h = NewHarness();
            RunCorrectedFrame(h);
            Assert.That(h.Delta.x, Is.EqualTo(Shift).Within(Tolerance), "precondition: a delta is published");

            h.Engine.Stop();

            Assert.That(h.Delta.x, Is.EqualTo(0f).Within(Tolerance),
                "the next session's entity 0 is not this session's entity 0");
        }

        // ── IMP105 C-4 / C-5 / C-20: the baseline is single-use and the results add ──

        private static readonly MethodInfo CaptureMethod = typeof(KlothoEngine)
            .GetMethod("CapturePreRollbackTransforms", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo ComputeMethod = typeof(KlothoEngine)
            .GetMethod("ComputeErrorDeltas", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void Capture(Harness h) => CaptureMethod.Invoke(h.Engine, new object[] { true });
        private static void Compute(Harness h) => ComputeMethod.Invoke(h.Engine, null);
        private static System.Collections.IDictionary PreRollback(Harness h)
            => (System.Collections.IDictionary)PreRollbackPosField.GetValue(h.Engine);

        /// <summary>
        /// Two discontinuities in one frame add up instead of the second replacing the first.
        ///
        /// Driven through the two functions directly rather than through a resync + rollback in one Step:
        /// a FullState restore and a rollback re-simulation both move the same TransformComponent, so their
        /// magnitudes would depend on each other and a wrong implementation could pass by coincidence. The
        /// change under test is one line in ComputeErrorDeltas, so that is what gets called.
        /// </summary>
        [Test]
        public void TwoComputesInAFrame_AddUpInsteadOfReplacing()
        {
            var h = NewHarness();
            h.AdvanceTo(6);

            Capture(h);
            h.MispredictBy(Shift);
            Compute(h);
            Assert.That(h.Delta.x, Is.EqualTo(-Shift).Within(Tolerance), "precondition: first delta lands");

            Capture(h);                 // fresh baseline at the post-move pose
            h.MispredictBy(Shift);
            Compute(h);

            Assert.That(h.Delta.x, Is.EqualTo(-Shift * 2f).Within(Tolerance),
                "a resync correction published earlier in the frame must survive the rollback compute that "
                + "follows it — assigning kept only the last jump and dropped the other one");
        }

        /// <summary>
        /// A capture is a fresh snapshot. An entity that leaves the correction filter between two captures
        /// must not keep its old pose in the baseline, or the next compute diffs it against a world it no
        /// longer belongs to.
        /// </summary>
        [Test]
        public void SecondCapture_DoesNotInheritTheFirstOnesResidue()
        {
            var h = NewHarness();
            h.AdvanceTo(6);

            Capture(h);
            Assert.That(PreRollback(h).Count, Is.EqualTo(1), "precondition: the entity is captured");

            h.Sim.Frame.Remove<ErrorCorrectionTargetComponent>(h.Entity);
            Capture(h);

            Assert.That(PreRollback(h).Count, Is.Zero,
                "the entity dropped out of the filter, so the second capture must not carry its pose over");
        }

        /// <summary>
        /// The compute consumes its baseline. This is what stops the unconditional compute call from
        /// running on a stale one (C-20) — the capture beside it is gated on "a rollback runs this frame",
        /// so on a frame where only a resync filled the maps there is nothing to stop it but this.
        /// </summary>
        [Test]
        public void Compute_ConsumesItsBaseline()
        {
            var h = NewHarness();
            h.AdvanceTo(6);

            Capture(h);
            Assert.That(PreRollback(h).Count, Is.EqualTo(1), "precondition: the baseline is populated");

            h.MispredictBy(Shift);
            Compute(h);

            Assert.That(PreRollback(h).Count, Is.Zero,
                "_preRollback* is a single-use buffer: filled by a capture, emptied by the compute that "
                + "reads it, empty at every other moment");
        }

        /// <summary>
        /// A second compute with no capture in between is a no-op.
        ///
        /// Today's assignment made this harmless by accident — it rewrote the same value. Once the deltas
        /// accumulate it is the only thing standing between a gated-off capture and a doubled correction,
        /// so it is required by D-2 rather than merely nice to have.
        /// </summary>
        [Test]
        public void ComputeWithoutACapture_ChangesNothing()
        {
            var h = NewHarness();
            h.AdvanceTo(6);

            Capture(h);
            h.MispredictBy(Shift);
            Compute(h);
            float after = h.Delta.x;

            h.MispredictBy(Shift);      // the world moves on, but no capture marks a new baseline
            Compute(h);

            Assert.That(h.Delta.x, Is.EqualTo(after).Within(Tolerance),
                "without a fresh capture there is no jump to measure — the compute must add nothing");
        }

        /// <summary>
        /// The control for the two above: one capture, one compute, exactly one delta. Guards against a
        /// fix that doubles the ordinary single-rollback frame.
        /// </summary>
        [Test]
        public void SingleRollbackFrame_ProducesExactlyOneDelta()
        {
            var h = NewHarness();
            RunCorrectedFrame(h);

            Assert.That(h.Delta.x, Is.EqualTo(Shift).Within(Tolerance),
                "the common frame must be untouched by the accumulation change");
        }
    }
}
