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
    /// The error-visual gate (IMP103 D-1) and the dead accumulation it removes (D-3).
    ///
    /// Godot puts the whole error-visual pipeline behind EnableErrorCorrection and says so in a comment
    /// ("non-EC games pay nothing"); Unity ran it unconditionally. The flag defaults to false, so every
    /// game paid for a feature it had not asked for — two dictionary lookups, a HashSet probe, and the
    /// five-stage smoother, per view per frame. And on the snapshot path the accumulation ran even
    /// though that path deliberately never applies the result.
    ///
    /// The gate is easy to aim wrong in two directions, and four of these six tests exist for that:
    /// wrapping the TeleportTick term would undo V-7, and keying the accumulation skip on skipPosError
    /// instead of the snapshot flag would kill the yaw correction of DisablePositionUpdate-only views.
    /// Those four pass before the patch — that is expected, and it is what makes them useful.
    /// </summary>
    [TestFixture]
    public class ErrorVisualGateTests
    {
        private const int MaxEntities = 16;
        private const int TickIntervalMs = 50;
        private const float Tolerance = 1e-3f;

        private static readonly FieldInfo CurrentTickField = typeof(KlothoEngine)
            .GetField("<CurrentTick>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo AccumulatorField = typeof(KlothoEngine)
            .GetField("_accumulator", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo PosDeltasField = typeof(KlothoEngine)
            .GetField("_posDeltas", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo YawDeltasField = typeof(KlothoEngine)
            .GetField("_yawDeltas", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeMsField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeInitField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        // Large enough to clear PosMinCorrection, small enough to stay under PosTeleportDistance (1.0)
        // so the smoother decays it instead of snapping to zero.
        //
        // The assertions below test for zero vs non-zero rather than a magnitude, because the smoothed
        // value depends on Time.deltaTime and an EditMode run has almost none of it: dt lands around
        // 0.2ms, so blend = 1 - exp(-200*dt) is ~0.035 and one Tick of a 0.3m delta produces ~0.011m.
        // That is a real Tick — it is just a very short frame. Asserting a magnitude here would be
        // asserting the runner's frame timing.
        private static readonly Vector3 InjectedDelta = new Vector3(0.3f, 0f, 0f);
        private static readonly Vector3 Sentinel = new Vector3(0f, 99f, 0f);

        private EcsSimulation _sim;
        private KlothoEngine _engine;
        private IKLogger _logger;

        [SetUp]
        public void SetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("ErrorVisualGateTests");
        }

        /// <summary>
        /// A running engine with one entity. EnableErrorCorrection is the axis under test, so it is a
        /// parameter rather than a fixture constant.
        /// </summary>
        private EntityRef Build(bool enableErrorCorrection)
        {
            _sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: TickIntervalMs);
            _engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = TickIntervalMs,
                    InterpolationDelayTicks = 2,
                    MaxRollbackTicks = 8,
                    MaxEntities = MaxEntities,
                    EnableErrorCorrection = enableErrorCorrection,
                },
                new SessionConfig());
            _engine.Initialize(_sim, _logger);

            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent { Position = FPVector3.Zero });

            // Tick 5 with TeleportTick 0 keeps the V-7 teleport branch quiet (0 != 4), so these tests
            // measure the error visual and nothing else.
            CurrentTickField.SetValue(_engine, 5);
            AccumulatorField.SetValue(_engine, 0.5f * TickIntervalMs);
            return entity;
        }

        /// <summary>
        /// Puts a rollback delta where the view reads it. Going through the engine's own rollback would
        /// exercise far more than the gate; the view only ever sees these dictionaries.
        /// </summary>
        private void InjectDelta(EntityRef entity, Vector3 delta, float yaw = 0.05f)
        {
            var pos = (Dictionary<int, FPVector3>)PosDeltasField.GetValue(_engine);
            pos[entity.Index] = new FPVector3(
                FP64.FromFloat(delta.x), FP64.FromFloat(delta.y), FP64.FromFloat(delta.z));
            var yawMap = (Dictionary<int, FP64>)YawDeltasField.GetValue(_engine);
            yawMap[entity.Index] = FP64.FromFloat(yaw);
        }

        private GameObject NewView(EntityRef entity, ViewFlags flags, out ProbeEntityView view)
        {
            var go = new GameObject("GatedView");
            view = go.AddComponent<ProbeEntityView>();
            view.Engine = _engine;
            view.EntityRef = entity;
            view.SetFlags(flags);
            go.transform.position = Sentinel;
            return go;
        }

        // ── D-1: the gate itself ──

        /// <summary>
        /// The D-1 case. With the flag off the pipeline must not run at all — no dictionary reads, no
        /// accumulation. Pre-fix the accumulator advances, which is the whole point: the feature was
        /// running for games that never enabled it.
        /// </summary>
        [Test]
        public void ErrorCorrectionOff_DoesNotAccumulate()
        {
            var entity = Build(enableErrorCorrection: false);
            InjectDelta(entity, InjectedDelta);
            var go = NewView(entity, ViewFlags.None, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(view.Visual.SmoothedPosError.magnitude, Is.LessThan(Tolerance),
                    "the flag is off, so nothing in the error-visual pipeline should have run");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The other half: the gate must not turn the feature off for games that asked for it. Without
        /// this, an implementation that simply never accumulates would pass the test above.
        /// </summary>
        [Test]
        public void ErrorCorrectionOn_StillAccumulates()
        {
            var entity = Build(enableErrorCorrection: true);
            InjectDelta(entity, InjectedDelta);
            var go = NewView(entity, ViewFlags.None, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(view.Visual.SmoothedPosError.magnitude, Is.GreaterThan(0f),
                    "with the flag on the delta must reach the smoother exactly as before");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── D-3: dead accumulation on the snapshot path ──

        /// <summary>
        /// Snapshot-interpolated views never apply the offset — verified-frame interpolation already
        /// renders the authoritative state, so adding it would double-correct. Accumulating a value that
        /// is then thrown away is work with no consumer.
        /// </summary>
        [Test]
        public void SnapshotView_DoesNotAccumulate_EvenWithErrorCorrectionOn()
        {
            var entity = Build(enableErrorCorrection: true);
            InjectDelta(entity, InjectedDelta);
            var go = NewView(entity, ViewFlags.EnableSnapshotInterpolation, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(view.Visual.SmoothedPosError.magnitude, Is.LessThan(Tolerance),
                    "the snapshot path discards the offset, so accumulating it is dead work");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// Removing the dead accumulation must not move anything on screen. The snapshot view keeps
        /// rendering exactly what the interpolator gives it.
        /// </summary>
        [Test]
        public void SnapshotView_RenderIsUnchanged()
        {
            var entity = Build(enableErrorCorrection: true);

            // Two verified frames one tick apart, and a render clock parked halfway between them.
            ref var t0 = ref _sim.Frame.Get<TransformComponent>(entity);
            t0.Position = new FPVector3(FP64.FromFloat(-10f), FP64.Zero, FP64.Zero);
            _sim.SaveSnapshot();                       // tick 0
            _sim.Tick(NoCommands);
            ref var t1 = ref _sim.Frame.Get<TransformComponent>(entity);
            t1.Position = new FPVector3(FP64.FromFloat(10f), FP64.Zero, FP64.Zero);
            _sim.SaveSnapshot();                       // tick 1
            _sim.Tick(NoCommands);

            RenderTimeMsField.SetValue(_engine, 0.5 * TickIntervalMs);   // baseTick 0, alpha 0.5
            RenderTimeInitField.SetValue(_engine, true);
            LastVerifiedTickField.SetValue(_engine, 2);

            InjectDelta(entity, InjectedDelta);
            var go = NewView(entity, ViewFlags.EnableSnapshotInterpolation, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, Vector3.zero), Is.LessThan(Tolerance),
                    "midway between -10 and +10 is 0 — the interpolator's answer, with no offset on top");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── Aim guards: the two directions the gate can be pointed wrong ──

        /// <summary>
        /// V-7 regression guard. The teleport check must stay outside the gate: TeleportTick is stamped
        /// by the game and has nothing to do with error correction. Pulling the whole `teleported`
        /// computation inside would put CSP teleports back to being lerped across whenever the flag is
        /// off — the defect V-7 just removed.
        /// </summary>
        [Test]
        public void TeleportStillSnaps_WithErrorCorrectionOff()
        {
            var entity = Build(enableErrorCorrection: false);
            ref var t = ref _sim.Frame.Get<TransformComponent>(entity);
            t.Position = new FPVector3(FP64.FromFloat(40f), FP64.Zero, FP64.Zero);
            t.TeleportTick = 4;                        // == CurrentTick(5) - 1

            var go = NewView(entity, ViewFlags.None, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, new Vector3(40f, 0f, 0f)),
                    Is.LessThan(Tolerance),
                    "the teleport snap must survive the gate — it is not an error-correction feature");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The other aiming mistake. A DisablePositionUpdate-only view skips the POSITION offset but
        /// still applies the yaw one, so its accumulation must keep running. Keying the skip on
        /// skipPosError instead of the snapshot flag silently kills its rotation correction — and
        /// nothing else in this file would notice.
        /// </summary>
        [Test]
        public void DisablePositionUpdateOnlyView_StillAccumulates()
        {
            var entity = Build(enableErrorCorrection: true);
            InjectDelta(entity, InjectedDelta);
            var go = NewView(entity, ViewFlags.DisablePositionUpdate, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Mathf.Abs(view.Visual.SmoothedYawError), Is.GreaterThan(0f),
                    "this view applies the yaw offset, so the pipeline must still run for it");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── Test-only concrete EntityView subclass ──
        private class ProbeEntityView : EntityView
        {
            public void SetFlags(ViewFlags flags) => _viewFlags = flags;
            public ErrorVisualState Visual => _errorVisual;

            public override void OnInitialize() { }
            public override void OnActivate(FrameRef frame) { }
            public override void OnLateUpdateView() { }
        }
    }
}
