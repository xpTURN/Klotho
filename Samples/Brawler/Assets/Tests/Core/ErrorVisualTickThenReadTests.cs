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
    /// Tick-then-read for the error visual (IMP103 V-9).
    ///
    /// The rollback delta is produced in the engine's Update and the view reads it in the same frame's
    /// LateUpdate — but the view used to fill the transform parameter from the smoother BEFORE advancing
    /// it, so the offset that was supposed to hide the correction jump arrived one frame late. The
    /// jump showed in full, and the next frame added a second jump back. On a teleport frame the
    /// reverse could happen — the previous frame's offset applied once more before Reset cleared it —
    /// but only on Godot: Unity's ApplyTransform takes the Teleported branch and writes the
    /// uninterpolated pose with no offset, so the teleport test here passed before the fix too. It
    /// stays as the guard that keeps it that way once the read moves after the Tick.
    ///
    /// Neither order was ever chosen. IMP19 and the IMP24 plan were tick-then-read; a 2026-04-23 fix
    /// moved only Tick from the per-tick path to LateUpdate and the read stayed behind in Update, and
    /// IMP46 later merged the two and preserved the accidental order as "1-frame smoothing latency".
    ///
    /// Same harness as ErrorVisualGateTests, same zero-vs-non-zero assertions for the same reason
    /// (EditMode Time.deltaTime is ~0.2ms, so one Tick of a 0.3m delta produces ~0.011m).
    /// </summary>
    [TestFixture]
    public class ErrorVisualTickThenReadTests
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

        private static readonly Vector3 InjectedDelta = new Vector3(0.3f, 0f, 0f);
        private static readonly Vector3 Sentinel = new Vector3(0f, 99f, 0f);

        private EcsSimulation _sim;
        private KlothoEngine  _engine;
        private IKLogger      _logger;

        [SetUp]
        public void SetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("ErrorVisualTickThenReadTests");
        }

        private EntityRef Build()
        {
            _sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: TickIntervalMs);
            _engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = TickIntervalMs,
                    InterpolationDelayTicks = 2,
                    MaxRollbackTicks = 8,
                    MaxEntities = MaxEntities,
                    EnableErrorCorrection = true,
                },
                new SessionConfig());
            _engine.Initialize(_sim, _logger);

            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent { Position = FPVector3.Zero });

            CurrentTickField.SetValue(_engine, 5);
            AccumulatorField.SetValue(_engine, 0.5f * TickIntervalMs);
            return entity;
        }

        private void InjectDelta(EntityRef entity, Vector3 delta)
        {
            var pos = (Dictionary<int, FPVector3>)PosDeltasField.GetValue(_engine);
            pos[entity.Index] = new FPVector3(
                FP64.FromFloat(delta.x), FP64.FromFloat(delta.y), FP64.FromFloat(delta.z));
            var yawMap = (Dictionary<int, FP64>)YawDeltasField.GetValue(_engine);
            yawMap[entity.Index] = FP64.Zero;
        }

        private void ClearDelta(EntityRef entity)
        {
            ((Dictionary<int, FPVector3>)PosDeltasField.GetValue(_engine)).Remove(entity.Index);
            ((Dictionary<int, FP64>)YawDeltasField.GetValue(_engine)).Remove(entity.Index);
        }

        private GameObject NewView(EntityRef entity, out ProbeEntityView view)
        {
            var go = new GameObject("TickThenReadView");
            view = go.AddComponent<ProbeEntityView>();
            view.Engine = _engine;
            view.EntityRef = entity;
            view.SetFlags(ViewFlags.None);
            go.transform.position = Sentinel;
            return go;
        }

        /// <summary>
        /// The V-9 case. The delta is present when LateUpdate runs, so the offset must be on the
        /// transform in that same frame. Pre-fix the smoother has advanced (Visual is non-zero) but the
        /// transform was filled from the stale zero — the offset is one frame late.
        /// </summary>
        [Test]
        public void DeltaArrivalFrame_OffsetIsAppliedInTheSameFrame()
        {
            var entity = Build();
            InjectDelta(entity, InjectedDelta);
            var go = NewView(entity, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(view.Visual.SmoothedPosError.magnitude, Is.GreaterThan(0f),
                    "precondition: the smoother advanced this frame");
                Assert.That(go.transform.position.x, Is.GreaterThan(0f),
                    "the offset the smoother produced this frame must be on the transform this frame, " +
                    "not next frame");
                Assert.That(go.transform.position.x,
                    Is.EqualTo(view.Visual.SmoothedPosError.x).Within(Tolerance),
                    "the applied offset is exactly the freshly advanced smoothed value");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// Once the smoother holds an offset, a teleport frame must render the post-teleport pose
        /// exactly. On Unity ApplyTransform's Teleported branch already guarantees that (this passed
        /// pre-fix); with tick-then-read the smoother is also reset before the read, so the guarantee no
        /// longer depends on that branch alone. The Godot adapter had no such branch and did apply the
        /// stale offset — fixed in the same change.
        /// </summary>
        [Test]
        public void TeleportFrame_DoesNotApplyTheStaleOffset()
        {
            var entity = Build();
            var go = NewView(entity, out var view);
            try
            {
                // Frame 1: a delta seeds the smoother.
                InjectDelta(entity, InjectedDelta);
                view.InternalLateUpdateView();
                Assert.That(view.Visual.SmoothedPosError.magnitude, Is.GreaterThan(0f),
                    "precondition: the smoother holds an offset going into the teleport frame");

                // Frame 2: the game teleports on the tick this render interval sits on.
                ClearDelta(entity);
                ref var t = ref _sim.Frame.Get<TransformComponent>(entity);
                t.Position = new FPVector3(FP64.FromFloat(40f), FP64.Zero, FP64.Zero);
                t.TeleportTick = 4;                    // == CurrentTick(5) - 1
                view.InternalLateUpdateView();

                Assert.That(view.Visual.SmoothedPosError.magnitude, Is.LessThan(Tolerance),
                    "precondition: Tick reset the smoother on the teleport");
                Assert.That(Vector3.Distance(go.transform.position, new Vector3(40f, 0f, 0f)),
                    Is.LessThan(Tolerance),
                    "a teleport frame renders the post-teleport pose with no leftover offset");
            }
            finally { Object.DestroyImmediate(go); }
        }

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
