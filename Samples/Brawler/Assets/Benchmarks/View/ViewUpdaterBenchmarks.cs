using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Unity.PerformanceTesting;
using UnityEngine;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.View.Benchmarks
{
    /// <summary>
    /// What the per-tick view reconcile costs, and specifically what the prefilter buys.
    ///
    /// The despawn-grace fix replaced a dictionary lookup in `DestroyStale`'s per-view loop with
    /// `_factory.TryGetBindBehaviour` — a virtual call plus a component lookup — and that loop runs over
    /// every view every tick. The answer was a prefilter: `_presentEntityVersions[index]` matching the
    /// view's Version means the entity was collected this tick, which is sound in the positive direction
    /// only, so a hit ends the question and a miss falls through to the real query.
    ///
    /// The two arms here are both legitimate steady states, which is what makes the A/B possible without
    /// touching the source (the prefilter has no toggle and `IsCarriedByVerified` is private):
    ///
    ///   HIT  — no slot reuse, so the presence snapshot carries each view's own Version.
    ///   MISS — every slot is reused by a locally-owned entity, so the PREDICTED pass overwrites the
    ///          snapshot with a different Version. The prefilter misses on every view and the factory
    ///          query runs — and the views stay alive, because the query answers Verified.
    ///
    /// Reconcile is measured whole rather than DestroyStale alone: it is what a tick actually pays, and
    /// the state is idempotent under repetition only because nothing here is stale. A drained fixture
    /// would measure an empty loop.
    ///
    /// ⚠️ HIT and MISS are not an isolated pair. Producing a miss REQUIRES a renderable new occupant in
    /// the slot — that is the only thing that overwrites the presence snapshot — so the miss arm carries
    /// twice the views (2026-08-27 run: 32 views / 10.86 us vs 64 views / 25.18 us per Reconcile, i.e.
    /// 0.065% and 0.151% of a 60fps frame). The raw 2.3x therefore mixes the prefilter with the view
    /// count. `Reconcile_PrefilterHits_MatchedViewCount` is the control that removes the larger of the
    /// two confounders: same 64 views, none of them reused. What still differs after that subtraction is
    /// that half the miss arm's views take the CSP branch, which is the cheaper one — so the result is a
    /// lower bound on the prefilter, not an exact figure.
    /// </summary>
    [TestFixture]
    public class ViewUpdaterBenchmarks
    {
        private const int MaxEntities    = 256;   // Brawler's SimulationConfig value
        private const int ViewCount      = 32;
        private const int TickIntervalMs = 50;
        private const int Delay          = 3;

        private const int WarmupCount      = 5;
        private const int MeasurementCount = 20;
        private const int IterationsPerMeasurement = 200;

        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        private static readonly FieldInfo RenderTimeMsField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeInitField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo ViewsField = typeof(EntityViewUpdater)
            .GetField("_viewsByEntity", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo ReconcileMethod = typeof(EntityViewUpdater)
            .GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance);

        private EcsSimulation     _sim;
        private KlothoEngine      _engine;
        private IKLogger          _logger;
        private GameObject        _evuGo;
        private EntityViewUpdater _evu;
        private BenchFactory      _factory;

        [SetUp]
        public void SetUp()
        {
            var loggerFactory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = loggerFactory.CreateLogger("ViewUpdaterBenchmarks");

            _sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 16, deltaTimeMs: TickIntervalMs);
            _engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs          = TickIntervalMs,
                    InterpolationDelayTicks = Delay,
                    MaxRollbackTicks        = 16,
                    MaxEntities             = MaxEntities,
                },
                new SessionConfig());
            _engine.Initialize(_sim, _logger);

            _factory = ScriptableObject.CreateInstance<BenchFactory>();
            _evuGo   = new GameObject("BenchEvu");
            _evu     = _evuGo.AddComponent<EntityViewUpdater>();
            _evu.Initialize(_engine, _factory);
        }

        [TearDown]
        public void TearDown()
        {
            // Cleanup first: OnDestroy only unsubscribes, so destroying the updater's GameObject leaves
            // every view it created alive in the shared EditMode scene.
            if (_evu != null) _evu.Cleanup();
            if (_evuGo != null) Object.DestroyImmediate(_evuGo);
            if (_factory != null) Object.DestroyImmediate(_factory);
            _evu = null;
            _sim = null;
            _engine = null;
        }

        [Test, Performance]
        public void Reconcile_PrefilterHits()
        {
            SeedViews(reuseSlots: false);
            Measure.Method(Reconcile)
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        /// <summary>
        /// The matched control for <see cref="Reconcile_PrefilterMisses"/>: the same view count with the
        /// prefilter hitting on every one of them. Without this the miss arm's number is unattributable.
        /// </summary>
        [Test, Performance]
        public void Reconcile_PrefilterHits_MatchedViewCount()
        {
            SeedViews(reuseSlots: false, viewCount: ViewCount * 2);
            Measure.Method(Reconcile)
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void Reconcile_PrefilterMisses()
        {
            SeedViews(reuseSlots: true);
            Measure.Method(Reconcile)
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        // ── Harness ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds <see cref="ViewCount"/> snapshot-bound views over a two-tick Verified window.
        ///
        /// With <paramref name="reuseSlots"/> the entities are destroyed on the LIVE frame and the slots
        /// re-taken by locally-owned entities, so the Predicted pass writes a different Version into the
        /// presence snapshot for every index — the prefilter then misses on every view while the Verified
        /// frames still carry the originals. Without it, each view's own Version is what the snapshot holds.
        /// </summary>
        private void SeedViews(bool reuseSlots, int? viewCount = null)
        {
            int n = viewCount ?? ViewCount;
            var entities = new EntityRef[n];
            for (int i = 0; i < n; i++)
            {
                entities[i] = _sim.Frame.CreateEntity();
                _sim.Frame.Add(entities[i], new TransformComponent());
            }

            _sim.SaveSnapshot();            // tick 0
            _sim.Tick(NoCommands);
            _sim.SaveSnapshot();            // tick 1
            _sim.Tick(NoCommands);

            SeedClock(lastVerified: 1, baseTick: 0);
            Reconcile();
            Assert.That(Views().Count, Is.EqualTo(n), "precondition: every view exists");

            if (!reuseSlots) return;

            for (int i = 0; i < n; i++) _sim.Frame.DestroyEntity(entities[i]);
            for (int i = 0; i < n; i++)
            {
                var reused = _sim.Frame.CreateEntity();
                Assert.That(reused.Version, Is.Not.EqualTo(entities[i].Version),
                    "precondition: the free list must hand back the slot with a new Version");
                _factory.PredictedOnly.Add(reused.Version);
            }

            Reconcile();
            Assert.That(Views().Count, Is.EqualTo(n * 2),
                "precondition: the originals coexist with the new occupants, so the prefilter misses "
                + "on the originals and the factory query keeps them");
        }

        private void SeedClock(int lastVerified, int baseTick)
        {
            RenderTimeMsField.SetValue(_engine, baseTick * (double)TickIntervalMs);
            RenderTimeInitField.SetValue(_engine, true);
            LastVerifiedTickField.SetValue(_engine, lastVerified);
        }

        private void Reconcile() => ReconcileMethod.Invoke(_evu, null);

        private Dictionary<long, EntityView> Views()
            => (Dictionary<long, EntityView>)ViewsField.GetValue(_evu);

        private class BenchFactory : EntityViewFactory
        {
            public readonly HashSet<int> PredictedOnly = new HashSet<int>();

            protected override GameObject ResolvePrefab(Frame frame, EntityRef entity) => null;
            protected override bool ShouldRender(Frame frame, EntityRef entity) => true;

            public override bool TryGetBindBehaviour(Frame frame, EntityRef entity, out BindBehaviour behaviour)
            {
                behaviour = PredictedOnly.Contains(entity.Version)
                    ? BindBehaviour.NonVerified
                    : BindBehaviour.Verified;
                return true;
            }

            public override ViewFlags GetViewFlags(Frame frame, EntityRef entity)
                => PredictedOnly.Contains(entity.Version) ? ViewFlags.None : ViewFlags.EnableSnapshotInterpolation;

            public override UniTask<EntityView> CreateAsync(Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags)
            {
                var go = new GameObject($"BenchView_{entity.Index}_{entity.Version}");
                return UniTask.FromResult<EntityView>(go.AddComponent<BenchEntityView>());
            }

            public override void Destroy(EntityView view)
            {
                if (view != null) Object.DestroyImmediate(view.gameObject);
            }
        }

        // Renders nothing — the benchmark only exercises the EVU's reconcile loop.
        private class BenchEntityView : EntityView
        {
            public override void OnInitialize() { }
            public override void OnActivate(FrameRef frame) { }
            public override void OnUpdateView() { }
            public override void OnLateUpdateView() { }
        }
    }
}
