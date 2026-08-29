using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.View.Tests
{
    /// <summary>
    /// What a snapshot-interpolated view is allowed to read (IMP103 D-2).
    ///
    /// The view head used to gate BOTH render paths on the live Predicted frame. For the CSP path that is
    /// right — its whole basis is this frame. For the snapshot path it is not: that path renders the
    /// Verified timeline, where an entity can be perfectly alive while prediction has already destroyed
    /// it. The view then froze for the entire SD lead, and the same live frame also fed
    /// `Uninterpolated*` — the ROOT transform, documented as the reference for collision/raycasts — with
    /// a prediction, which is the one thing the verified path exists to avoid.
    ///
    /// The fix demotes the predicted check from a gate to a fallback condition. Demoting is not dropping:
    /// two of these tests exist because the fallback still may not read a recycled slot, and skipping the
    /// write is not the same as hiding the object — a pooled view carries the previous occupant's
    /// transform, so "write nothing" is only correct when there is genuinely nothing to write.
    /// </summary>
    [TestFixture]
    public class SnapshotViewBaseFrameTests
    {
        private const int   MaxEntities    = 16;
        private const int   TickIntervalMs = 50;
        private const float Tolerance      = 1e-3f;

        private static readonly FieldInfo RenderTimeMsField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeInitField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);

        // Verified history: the entity sits at A on tick 0 and B on tick 1, so a window over [0, 1] with
        // alpha 0.5 renders the midpoint and the base endpoint is A. Kept far apart so a wrong pick is
        // unmistakable.
        private static readonly Vector3 PosAtTick0 = new Vector3(-30f, 0f, 0f);
        private static readonly Vector3 PosAtTick1 = new Vector3( 30f, 0f, 0f);
        private static readonly Vector3 Midpoint   = new Vector3(  0f, 0f, 0f);

        // Where the entity is on the LIVE predicted frame — off the A↔B axis entirely, so "the view used
        // the live pose" and "the view used a verified pose" can never be confused.
        private static readonly Vector3 LivePos = new Vector3(0f, 0f, 90f);

        // An early return leaves the transform alone, so a sentinel is how the tests see that happen.
        private static readonly Vector3 Sentinel = new Vector3(0f, 99f, 0f);

        // Per-tick displacement for the moving-spawn history. The warmup scan is only observable on an
        // entity that moves after birth — a stationary one has pose(S) == pose(L) and every warmup
        // assertion passes vacuously, which is what the first draft of these tests would have done.
        private static readonly Vector3 MoveStep = new Vector3(10f, 0f, 0f);

        private static Vector3 MovedPose(int ticksAfterBirth) => PosAtTick0 + ticksAfterBirth * MoveStep;

        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        private EcsSimulation _sim;
        private KlothoEngine  _engine;
        private IKLogger      _logger;
        private EntityRef     _entity;

        [SetUp]
        public void SetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("SnapshotViewBaseFrameTests");

            _sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: TickIntervalMs);
            _engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs          = TickIntervalMs,
                    InterpolationDelayTicks = 2,
                    MaxRollbackTicks        = 8,
                    MaxEntities             = MaxEntities,
                },
                new SessionConfig());
            _engine.Initialize(_sim, _logger);
        }

        [TearDown]
        public void TearDown()
        {
            _sim = null;
            _engine = null;
        }

        /// <summary>
        /// Two verified snapshots one tick apart, both holding the entity. Leaves the entity alive on the
        /// live frame at <see cref="LivePos"/>; callers that need it dead call <see cref="KillOnLiveFrame"/>.
        /// </summary>
        private void BuildVerifiedHistory()
        {
            _entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(_entity, new TransformComponent { Position = ToFp(PosAtTick0) });
            _sim.SaveSnapshot();                 // tick 0 — entity at A
            _sim.Tick(NoCommands);

            _sim.Frame.Get<TransformComponent>(_entity).Position = ToFp(PosAtTick1);
            _sim.SaveSnapshot();                 // tick 1 — entity at B
            _sim.Tick(NoCommands);

            _sim.Frame.Get<TransformComponent>(_entity).Position = ToFp(LivePos);
        }

        /// <summary>
        /// Destroys the entity on the LIVE frame only. The verified snapshots keep it — exactly the state
        /// prediction produces when it kills something the server has not confirmed dead yet.
        /// </summary>
        private void KillOnLiveFrame()
        {
            _sim.Frame.DestroyEntity(_entity);
            Assert.That(_sim.Frame.Entities.IsAlive(_entity), Is.False,
                "precondition: the live frame must not hold the entity any more");
        }

        /// <summary>
        /// Pins the verified render clock so VerifiedBaseTick == baseTick with the given alpha.
        ///
        /// `lastVerified` defaults to `baseTick + 1`, which is what every test here wanted until the
        /// warmup-source change: it puts the newest Verified frame right at the window's far endpoint, so
        /// "the window has no data" and "the newest Verified frame has no data" coincide. Decoupling them
        /// is the whole point of the spawn-gap case — the window sits `delay` ticks behind the newest
        /// frame, so the newest frame can hold an entity the window has never seen (IMP103).
        ///
        /// The parameter is additive on purpose: the five tests written before it must keep asserting
        /// exactly what they asserted, or they stop being guards.
        /// </summary>
        private void SeedVerifiedWindow(int baseTick, float alpha, int? lastVerified = null)
        {
            RenderTimeMsField.SetValue(_engine, baseTick * (double)TickIntervalMs + alpha * TickIntervalMs);
            RenderTimeInitField.SetValue(_engine, true);
            LastVerifiedTickField.SetValue(_engine, lastVerified ?? baseTick + 1);
        }

        /// <summary>
        /// History for the spawn gap: ticks 0..birthTick-1 hold no entity at all, the entity is created and
        /// snapshotted at `birthTick`, and the live frame then moves it to <see cref="LivePos"/>.
        ///
        /// Separate from <see cref="BuildVerifiedHistory"/> rather than a parameter on it, for the same
        /// reason the seed parameter is additive — that builder is shared by the guard tests.
        /// </summary>
        private void BuildLateBornHistory(int birthTick)
        {
            for (int t = 0; t < birthTick; t++)
            {
                _sim.SaveSnapshot();             // ticks 0..birthTick-1 — nothing in this slot yet
                _sim.Tick(NoCommands);
            }

            _entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(_entity, new TransformComponent { Position = ToFp(PosAtTick0) });
            _sim.SaveSnapshot();                 // birthTick — the spawn pose
            _sim.Tick(NoCommands);

            _sim.Frame.Get<TransformComponent>(_entity).Position = ToFp(LivePos);
        }

        /// <summary>
        /// History for the warmup scan: no entity before <paramref name="birthTick"/>, born at
        /// <see cref="PosAtTick0"/>, then MOVING by <see cref="MoveStep"/> every tick through
        /// <paramref name="throughTick"/>, each tick snapshotted from <paramref name="firstSnapshotTick"/>
        /// onward (earlier ticks run unsnapshotted — that is how the ring-gap case is built). The live
        /// frame ends at <see cref="LivePos"/>.
        /// </summary>
        private void BuildMovingBornHistory(int birthTick, int throughTick, int firstSnapshotTick = 0)
        {
            for (int t = 0; t < birthTick; t++)
            {
                if (t >= firstSnapshotTick) _sim.SaveSnapshot();
                _sim.Tick(NoCommands);
            }

            _entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(_entity, new TransformComponent { Position = ToFp(PosAtTick0) });
            if (birthTick >= firstSnapshotTick) _sim.SaveSnapshot();
            _sim.Tick(NoCommands);

            for (int t = birthTick + 1; t <= throughTick; t++)
            {
                _sim.Frame.Get<TransformComponent>(_entity).Position = ToFp(MovedPose(t - birthTick));
                if (t >= firstSnapshotTick) _sim.SaveSnapshot();
                _sim.Tick(NoCommands);
            }

            _sim.Frame.Get<TransformComponent>(_entity).Position = ToFp(LivePos);
        }

        private GameObject NewView(ViewFlags flags, EntityRef entity, out ProbeEntityView view)
        {
            var go = new GameObject("SnapshotProbe");
            view = go.AddComponent<ProbeEntityView>();
            view.Engine = _engine;
            view.EntityRef = entity;
            view.SetFlags(flags);
            go.transform.position = Sentinel;
            return go;
        }

        private static FPVector3 ToFp(Vector3 v)
            => new FPVector3(FP64.FromFloat(v.x), FP64.FromFloat(v.y), FP64.FromFloat(v.z));

        // ── The D-2 case ──

        /// <summary>
        /// The defect. Prediction destroyed the entity; the verified window still holds it on both
        /// endpoints, so there is nothing stopping the interpolation — except the gate. Pre-fix the view
        /// returns before writing anything and the sentinel survives (in a running game that reads as the
        /// entity freezing in place for the whole SD lead).
        /// </summary>
        [Test]
        public void PredictedDeadButVerifiedAlive_SnapshotViewKeepsInterpolating()
        {
            BuildVerifiedHistory();
            KillOnLiveFrame();
            SeedVerifiedWindow(baseTick: 0, alpha: 0.5f);

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out _);
            try
            {
                go.GetComponent<ProbeEntityView>().InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, Sentinel), Is.GreaterThan(1f),
                    "the view must have been written — the verified window holds this entity");
                Assert.That(Vector3.Distance(go.transform.position, Midpoint), Is.LessThan(Tolerance),
                    "and it must be the interpolation of the two verified endpoints");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// `Uninterpolated*` on the snapshot path. With an interpolation target the root receives it while
        /// the child receives the interpolated pose, so the two are separable: the root must land on the
        /// window's alpha=0 endpoint (a verified snapshot), not on the live predicted pose it used to get.
        /// The entity stays alive on the live frame here — otherwise pre-fix would return early and this
        /// would be testing the gate again rather than the source.
        /// </summary>
        [Test]
        public void SnapshotView_RootTakesTheBaseTickPose_NotTheLivePose()
        {
            BuildVerifiedHistory();
            SeedVerifiedWindow(baseTick: 0, alpha: 0.5f);

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            var child = new GameObject("InterpolationTarget");
            try
            {
                child.transform.SetParent(go.transform, worldPositionStays: false);
                view.SetInterpolationTarget(child.transform);

                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, LivePos), Is.GreaterThan(1f),
                    "the root of a verified-rendered view must not be a prediction");
                Assert.That(Vector3.Distance(go.transform.position, PosAtTick0), Is.LessThan(Tolerance),
                    "it is the render window's alpha=0 endpoint");
                Assert.That(Vector3.Distance(child.transform.position, Midpoint), Is.LessThan(Tolerance),
                    "the child still carries the interpolated pose");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── The fallback, and its two limits ──

        /// <summary>
        /// Outside the verified window — the ring holds nothing for these ticks — the live pose is used.
        /// This is the deliberate compromise branch (d) always made, and it is what keeps a freshly bound
        /// view off whatever coordinates it inherited: skipping the write does not hide the object, and a
        /// pooled view starts at the previous occupant's transform. Choosing "skip when the endpoints are
        /// not occupied" instead would leave the sentinel here.
        /// </summary>
        [Test]
        public void OutsideTheVerifiedWindow_FallsBackToTheLivePose()
        {
            BuildVerifiedHistory();
            SeedVerifiedWindow(baseTick: 100, alpha: 0f);   // far past the ring — no frame at all

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, Sentinel), Is.GreaterThan(1f),
                    "the sentinel must not survive — a bound view always gets a transform written");
                Assert.That(Vector3.Distance(go.transform.position, LivePos), Is.LessThan(Tolerance),
                    "with no verified data the live pose is the only source there is");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The one case where writing nothing is right: the entity is on no timeline at all. Pre-fix this
        /// was the same early return; post-fix it has to survive the demotion of the predicted check.
        ///
        /// Note why it still passes after the warmup source changed: "no timeline" now includes the newest
        /// Verified frame, and this fixture's seed puts `lastVerified` at 101 — also past the ring — so
        /// that lookup misses too. Move the seed onto a real snapshot and this test would (correctly)
        /// start rendering the verified pose instead.
        /// </summary>
        [Test]
        public void NoDataOnEitherTimeline_WritesNothing()
        {
            BuildVerifiedHistory();
            KillOnLiveFrame();
            SeedVerifiedWindow(baseTick: 100, alpha: 0f);

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, Sentinel), Is.LessThan(Tolerance),
                    "nothing to render from, so nothing may be written");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The reason demoting the predicted check is not the same as dropping it. The handle is stale and
        /// its slot now belongs to a different entity; `Frame.Has` forwards Index alone, so a fallback
        /// that skipped `Entities.IsAlive` would happily hand this view the NEW occupant's live position —
        /// the "stale view reads the new one" direction of IMP103 V-6.
        /// </summary>
        [Test]
        public void StaleSnapshotViewOnRecycledSlot_DoesNotFallBackOntoTheNewOccupant()
        {
            var dead = _sim.Frame.CreateEntity();
            _sim.Frame.Add(dead, new TransformComponent { Position = ToFp(PosAtTick0) });
            _sim.SaveSnapshot();
            _sim.Tick(NoCommands);

            _sim.Frame.DestroyEntity(dead);
            var recycled = _sim.Frame.CreateEntity();
            _sim.Frame.Add(recycled, new TransformComponent { Position = ToFp(LivePos) });
            _sim.SaveSnapshot();
            _sim.Tick(NoCommands);

            Assert.That(recycled.Index, Is.EqualTo(dead.Index),
                "precondition: the slot must actually be recycled, otherwise this proves nothing");
            SeedVerifiedWindow(baseTick: 1, alpha: 0f);   // window holds the NEW occupant only

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, dead, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, LivePos), Is.GreaterThan(1f),
                    "a stale view must never be handed whatever took over its slot");
                Assert.That(Vector3.Distance(go.transform.position, Sentinel), Is.LessThan(Tolerance),
                    "with the handle dead on both timelines the view writes nothing");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The spawn gap. The entity was born at a Verified tick the render window has not reached yet, so
        /// the window holds nothing for it. Rendering the live Predicted pose here — what the code
        /// originally did — put the view lead+delay ticks ahead of its own timeline and then snapped it
        /// backward the moment the window arrived (IMP103, view-lifetime window).
        ///
        /// This fixture has exactly one Verified frame carrying the entity, so the birth pose and the
        /// newest Verified pose coincide — which is why this test survived the source changing under it
        /// twice (newest-verified fallback, then the D-2(f) shadow scan) without its assertions moving.
        /// The name used to say "UsesTheNewestVerifiedPose"; the mechanism is the scan now, and the
        /// tests that distinguish the two need an entity that MOVES after birth (below).
        /// </summary>
        [Test]
        public void BeforeTheWindowReachesBirth_UsesTheBirthPose_NotTheLivePose()
        {
            BuildLateBornHistory(birthTick: 3);

            // Window [1, 2] — before the birth tick, so nothing for this entity. The newest Verified frame
            // is the birth snapshot itself, which keeps the invariant baseTick <= lastVerified - 1.
            SeedVerifiedWindow(baseTick: 1, alpha: 0.5f, lastVerified: 3);

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, LivePos), Is.GreaterThan(1f),
                    "the live pose is lead+delay ticks ahead of this view's own timeline");
                Assert.That(Vector3.Distance(go.transform.position, PosAtTick0), Is.LessThan(Tolerance),
                    "the newest Verified frame holds the spawn pose, and that is what should render");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── The warmup shadow scan (IMP103 D-2(f)) ──

        /// <summary>
        /// The residual jump D-2(f) exists for. The newest-verified fallback ADVANCED with pose(L) during
        /// warmup, then snapped back by max(0, delay - 2) ticks of motion when the window reached the
        /// birth. The scan holds the birth pose instead — the pose the window will show first — so there
        /// is nothing to snap back from. RED on the pre-scan code: this renders MovedPose(3), the newest.
        /// </summary>
        [Test]
        public void WarmupBeforeBirth_HoldsTheBirthPose_NotTheNewestVerified()
        {
            BuildMovingBornHistory(birthTick: 2, throughTick: 5);
            SeedVerifiedWindow(baseTick: 0, alpha: 0.5f, lastVerified: 5);   // window [0,1] — before birth

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, PosAtTick0), Is.LessThan(Tolerance),
                    "the window will show the birth pose first when it arrives — hold that, not a pose it will rewind from");
                Assert.That(Vector3.Distance(go.transform.position, MovedPose(3)), Is.GreaterThan(1f),
                    "the newest Verified pose is three ticks of motion ahead — rendering it is the jump this removes");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The root follows the held pose too. Uninterpolated*'s contract is the render window's alpha=0
        /// endpoint — it follows the RENDER, sitting delay ticks stale in steady state — so during warmup
        /// it takes the same held pose, not the newest Verified one. The first draft of this plan asserted
        /// the opposite and the occupied path's own code refuted it (D-2(f) F-4).
        /// </summary>
        [Test]
        public void WarmupBeforeBirth_UninterpolatedFollowsTheHeldPose()
        {
            BuildMovingBornHistory(birthTick: 2, throughTick: 5);
            SeedVerifiedWindow(baseTick: 0, alpha: 0.5f, lastVerified: 5);

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            var target = new GameObject("interp-target").transform;
            target.SetParent(go.transform, false);
            view.SetInterpolationTarget(target);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, PosAtTick0), Is.LessThan(Tolerance),
                    "the root (collision/raycast reference) follows the render, exactly as the occupied path has it");
                Assert.That(Vector3.Distance(target.position, PosAtTick0), Is.LessThan(Tolerance),
                    "and the render itself holds the birth pose");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The purpose itself: the window arriving at the birth produces no jump, on either transform.
        /// Frame 1 is the last warmup render (scan holds pose(S)); frame 2 is the first occupancy render
        /// (branch (c) holds the same pose(S)). Pre-scan, frame 1 rendered pose(L) and frame 2 snapped
        /// back to pose(S).
        /// </summary>
        [Test]
        public void WindowReachingBirth_ProducesNoJump_RenderAndRoot()
        {
            BuildMovingBornHistory(birthTick: 2, throughTick: 5);

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            var target = new GameObject("interp-target").transform;
            target.SetParent(go.transform, false);
            view.SetInterpolationTarget(target);
            try
            {
                SeedVerifiedWindow(baseTick: 0, alpha: 0.9f, lastVerified: 5);   // last warmup frame
                view.InternalLateUpdateView();
                Vector3 renderBefore = target.position;
                Vector3 rootBefore   = go.transform.position;

                SeedVerifiedWindow(baseTick: 1, alpha: 0.1f, lastVerified: 5);   // window [1,2] reaches the birth — branch (c)
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(target.position, renderBefore), Is.LessThan(Tolerance),
                    "the render continues from the held pose — no snap when the window arrives");
                Assert.That(Vector3.Distance(go.transform.position, rootBefore), Is.LessThan(Tolerance),
                    "and the root is continuous too — pre-scan it took the same backward jump the render did");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// Graceful degradation, not a guarantee of the literal birth pose: when the birth predates the
        /// oldest retained frame, the scan returns the oldest occupied frame at/after the window — the
        /// closest authoritative pose to the window that still exists. That is strictly closer than the
        /// newest (the pre-scan source), which is what the second assertion pins.
        /// </summary>
        [Test]
        public void BirthOlderThanTheRing_HoldsTheOldestRetainedPose()
        {
            // Born at tick 0, but ticks 0..2 were never snapshotted — the ring starts at tick 3.
            BuildMovingBornHistory(birthTick: 0, throughTick: 6, firstSnapshotTick: 3);
            SeedVerifiedWindow(baseTick: 0, alpha: 0.5f, lastVerified: 6);

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, MovedPose(3)), Is.LessThan(Tolerance),
                    "the oldest retained occupied frame is the best available approximation of the window");
                Assert.That(Vector3.Distance(go.transform.position, MovedPose(6)), Is.GreaterThan(1f),
                    "falling through to the newest would reintroduce the jump for the retained part of the history");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The L cap, exercised where it matters: after a backward LastVerifiedTick move the frames at
        /// L+1 .. B+1 still exist in the ring, still carry the entity, and are exactly the predicted,
        /// rollback-mutable snapshots IMP103 #13 taught this function to refuse. A scan without the cap
        /// would hand one of them back. The scan range B+2..L is empty here, so the newest-verified
        /// fallback — whose only remaining firing case this is — serves pose(L).
        /// </summary>
        [Test]
        public void BackwardLastVerified_NeverReadsPastTheBoundary()
        {
            BuildMovingBornHistory(birthTick: 0, throughTick: 5);
            SeedVerifiedWindow(baseTick: 3, alpha: 0f, lastVerified: 1);   // L moved back; frames 2..5 are stale

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, MovedPose(1)), Is.LessThan(Tolerance),
                    "pose(L) is the newest pose that is still authoritative after the backward move");
                Assert.That(Vector3.Distance(go.transform.position, MovedPose(4)), Is.GreaterThan(1f),
                    "the stale frames past the boundary must stay invisible to the scan — they are rollback-mutable");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── The verified boundary (IMP103 #13) ──

        /// <summary>
        /// Session start, <c>LastVerifiedTick == 0</c>. Only one Verified frame exists, so the far endpoint
        /// cannot be Verified whatever the ring returns — and the ring returns it anyway, because
        /// FrameRingBuffer has no notion of verified. The render clock's clamp cannot help here: its
        /// <c>max(0, LastVerifiedTick - 1)</c> floor is what breaks the derivation at 0.
        ///
        /// Pre-fix the view lerps A↔B and lands on the midpoint — a pose half-made of a predicted,
        /// rollback-mutable frame, on the path that exists to avoid exactly that.
        /// </summary>
        [Test]
        public void FarEndpointPastVerifiedBoundary_HoldsTheBasePose()
        {
            BuildVerifiedHistory();
            SeedVerifiedWindow(baseTick: 0, alpha: 0.5f, lastVerified: 0);

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, PosAtTick0), Is.LessThan(Tolerance),
                    "with only tick 0 Verified there is no pair to interpolate — the base pose is the whole answer");
                Assert.That(Vector3.Distance(go.transform.position, Midpoint), Is.GreaterThan(Tolerance),
                    "the midpoint is the signature of having lerped against a predicted frame");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The larger of the two windows this check covers: <c>LastVerifiedTick</c> moved BACKWARD after the
        /// render clock was already advanced for the frame. That happens on every desync-ladder rollback
        /// (<c>_lastVerifiedTick = resolvedTick - 1</c>), on FullState restore and on spectator resync —
        /// and it lands too late to be clamped, because AdvanceVerifiedRenderTime sits at the top of
        /// Update while those assignments come later in the same Update and views read in LateUpdate. The
        /// base is stale-high for exactly one frame, and BOTH endpoints are then past the boundary.
        ///
        /// Checking only the far endpoint would leave this case rendering a predicted pose through branch
        /// (b), which is why the fix invalidates the base slot too. The right answer is the fallback's:
        /// pose(LastVerifiedTick), which is the closest authoritative pose that exists.
        /// </summary>
        [Test]
        public void BothEndpointsPastVerifiedBoundary_FallsBackToNewestVerified()
        {
            BuildVerifiedHistory();
            SeedVerifiedWindow(baseTick: 1, alpha: 0.5f, lastVerified: 0);   // L moved back to 0; base is stale at 1

            var go = NewView(ViewFlags.EnableSnapshotInterpolation, _entity, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, PosAtTick0), Is.LessThan(Tolerance),
                    "the newest Verified pose is tick 0's — holding tick 1's would be holding a prediction");
                Assert.That(Vector3.Distance(go.transform.position, PosAtTick1), Is.GreaterThan(Tolerance),
                    "tick 1 is past the boundary now, so it is not an endpoint any more");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── Scope guard ──

        /// <summary>
        /// The CSP path keeps its gate. Its render basis IS the live frame, so a predicted-dead entity has
        /// nothing to draw from and the early return must stay exactly where it was.
        /// </summary>
        [Test]
        public void CspView_PredictedDead_StillReturnsEarly()
        {
            BuildVerifiedHistory();
            KillOnLiveFrame();
            SeedVerifiedWindow(baseTick: 0, alpha: 0.5f);   // verified data exists — and must not be used

            var go = NewView(ViewFlags.None, _entity, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, Sentinel), Is.LessThan(Tolerance),
                    "the CSP gate is unchanged: no live entity, no write");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── Test-only concrete EntityView subclass ──
        private class ProbeEntityView : EntityView
        {
            public void SetFlags(ViewFlags flags) => _viewFlags = flags;
            public void SetInterpolationTarget(Transform t) => _interpolationTarget = t;

            // The test GameObject has no EntityViewComponent children; skip the base walk.
            public override void OnInitialize() { }
            public override void OnActivate(FrameRef frame) { }
            public override void OnLateUpdateView() { }
        }
    }
}
