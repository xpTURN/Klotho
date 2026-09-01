using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.View.Tests
{
    /// <summary>
    /// Two holes in the EntityViewUpdater's session lifetime.
    ///
    /// Hole 1 — Initialize only unsubscribed from the previous engine. Everything else — the active-view
    /// dictionary above all — survived, so a game that re-initialized without calling Cleanup() first
    /// kept the previous session's views on screen, drawing a stopped engine's last frame, unreachable
    /// through PlayerViews (the registry IS replaced), and colliding with the new session's keys: a
    /// fresh EntityManager hands out (0, 1) again, so TrySpawn found the old entry and deduped the new
    /// spawn away. The Godot adapter's Initialize has always started with Cleanup().
    ///
    /// Hole 2 — every loop over the view dictionary assumed its entries were alive. A view destroyed from
    /// outside — a reloaded scene invalidating an adopted scene view, or a game destroying one by hand —
    /// threw out of the loop, and the loop that mattered was LateUpdate: it reaches transform.position
    /// through ApplyTransform, so it threw EVERY FRAME regardless of what the game overrides. Cleanup
    /// was only where the consequence landed (nothing got reclaimed).
    ///
    /// The probe view's OnDeactivate reads `transform` on purpose. A deactivate hook that touches the
    /// object's own Unity API is what a real game writes, and without it the destroy-site tests here
    /// cannot go RED at all: calling a virtual method on a destroyed MonoBehaviour is legal C# — the
    /// managed object is still there — so the throw comes from the body, not from the call.
    /// </summary>
    [TestFixture]
    public class EvuSessionLifecycleTests
    {
        // Both sessions must use the same value: EcsSimulation's ctor freezes the component layout
        // process-wide, so two sims with different capacities would fight over it.
        private const int MaxEntities    = 16;
        private const int TickIntervalMs = 50;

        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        private static readonly FieldInfo RenderTimeMsField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeInitField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo FactoryField = typeof(EntityViewUpdater)
            .GetField("_factory", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo PoolField = typeof(EntityViewUpdater)
            .GetField("_pool", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo ViewsField = typeof(EntityViewUpdater)
            .GetField("_viewsByEntity", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo ReconcileMethod = typeof(EntityViewUpdater)
            .GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo LateUpdateMethod = typeof(EntityViewUpdater)
            .GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger          _logger;
        private EcsSimulation     _sim;
        private KlothoEngine      _engine;
        private GameObject        _evuGo;
        private EntityViewUpdater _evu;
        private LifecycleProbeFactory _factory;

        [SetUp]
        public void SetUp()
        {
            var loggerFactory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = loggerFactory.CreateLogger("EvuSessionLifecycleTests");

            _sim    = NewSim();
            _engine = NewEngine(_sim);

            _factory = ScriptableObject.CreateInstance<LifecycleProbeFactory>();
            _evuGo   = new GameObject("LifecycleEvu");
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

        // ── Re-initializing without Cleanup ─────────────────────────────────────────────────────

        [Test]
        public void Reinitialize_WithoutCleanup_ReclaimsThePreviousSessionsViews()
        {
            BuildLiveEntities(1);
            SeedClock(_engine, lastVerified: 2, baseTick: 0);
            Reconcile();
            Assume.That(ViewCount(), Is.EqualTo(1), "precondition: session one has a view");

            var sim2 = NewSim();
            var engine2 = NewEngine(sim2);
            _evu.Initialize(engine2, _factory);

            Assert.That(ViewCount(), Is.EqualTo(0),
                "a second Initialize must not carry the previous session's views into the new one — they draw a stopped engine");
            Assert.That(_factory.DestroyCalls, Is.EqualTo(1),
                "and they must be reclaimed through the factory, not merely dropped from the dictionary");
        }

        [Test]
        public void Reinitialize_WithoutCleanup_LetsTheNewSessionSpawn()
        {
            BuildLiveEntities(1);
            SeedClock(_engine, lastVerified: 2, baseTick: 0);
            Reconcile();
            Assume.That(ViewCount(), Is.EqualTo(1), "precondition: session one has a view");

            // A fresh EntityManager starts versions at 0 and increments before handing one out, so the
            // new session's first entity is (0, 1) again — the exact key the old view is filed under.
            // The entity carries no OwnerComponent, which makes OwnersMatch return true unconditionally:
            // that is what turned the collision into a silent dedup instead of a rebind.
            var sim2 = NewSim();
            var engine2 = NewEngine(sim2);
            _evu.Initialize(engine2, _factory);

            var reborn = sim2.Frame.CreateEntity();
            sim2.Frame.Add(reborn, new TransformComponent());
            for (int t = 0; t <= 2; t++) { sim2.SaveSnapshot(); sim2.Tick(NoCommands); }
            SeedClock(engine2, lastVerified: 2, baseTick: 0);
            Reconcile();

            Assert.That(_factory.CreateCalls, Is.EqualTo(2), "the new session's entity has to get its own view");
            Assert.That(SoleView().Engine, Is.SameAs(engine2),
                "and that view has to be driven by the new engine, not the stopped one");
        }

        [Test]
        public void Reinitialize_RebuildsThePlayerRegistry()
        {
            // Control. The registry has always been replaced per Initialize — which is exactly why the
            // surviving views were unreachable: PlayerViews.Get could not find them any more.
            var first = _evu.PlayerViews;
            var sim2 = NewSim();
            _evu.Initialize(NewEngine(sim2), _factory);

            Assert.That(_evu.PlayerViews, Is.Not.SameAs(first));
        }

        // ── Unity's null is not C#'s ────────────────────────────────────────────────────────────

        /// <summary>
        /// A destroyed serialized reference must not be handed on as if it were live.
        ///
        /// `??` and `?.` compare references; Unity's `==` asks the engine whether the object still
        /// exists. Mixing them inside one method made Initialize answer the same question both ways: it
        /// warned about the missing factory through the overload and then attached it through the
        /// null-conditional, and every later `_factory != null` disagreed with that.
        /// </summary>
        [Test]
        public void Initialize_DoesNotHandTheFactoryADestroyedPool()
        {
            var poolGo = new GameObject("DeadPool");
            PoolField.SetValue(_evu, poolGo.AddComponent<DefaultEntityViewPool>());
            Object.DestroyImmediate(poolGo);

            _evu.Initialize(NewEngine(NewSim()), _factory);

            Assert.That(_factory.Pool, Is.Null,
                "a destroyed pool is not a pool — passing it on means every Rent goes to an object Unity has already reclaimed");
        }

        [Test]
        public void Initialize_DoesNotAttachADestroyedFactory()
        {
            var dead = ScriptableObject.CreateInstance<LifecycleProbeFactory>();
            FactoryField.SetValue(_evu, dead);
            Object.DestroyImmediate(dead);

            var engine2 = NewEngine(NewSim());
            _evu.Initialize(engine2);

            Assert.That(dead.Engine, Is.Null,
                "the warning above it already decided this factory does not exist — attaching it anyway makes the two lines disagree");
        }

        // ── A view destroyed from outside ───────────────────────────────────────────────────────

        [Test]
        public void LateUpdate_SurvivesAnExternallyDestroyedView()
        {
            // The reference RED: this site needs no game override and no log level to throw.
            // ApplyTransform writes transform.position, and InternalLateUpdateView's early returns
            // (DisableUpdate / Engine / EntityRef / null frame) all pass for a destroyed view, because
            // what was destroyed is the GameObject — the entity is still alive on both timelines.
            var views = SpawnViews(2);
            Object.DestroyImmediate(views[0].gameObject);

            Assert.DoesNotThrow(() => LateUpdateMethod.Invoke(_evu, null),
                "one destroyed view must not stop the per-frame pass for every other view");
            Assert.That(((LifecycleProbeView)views[1]).LateUpdateCalls, Is.GreaterThan(0),
                "the surviving view still has to be interpolated");
        }

        [Test]
        public void Cleanup_SurvivesAnExternallyDestroyedView()
        {
            var views = SpawnViews(2);
            Object.DestroyImmediate(views[0].gameObject);
            int destroysBefore = _factory.DestroyCalls;

            Assert.DoesNotThrow(() => _evu.Cleanup());

            Assert.That(ViewCount(), Is.EqualTo(0), "teardown has to empty the dictionary even so");
            Assert.That(_factory.DestroyCalls - destroysBefore, Is.EqualTo(1),
                "and the view that was still alive has to be reclaimed — that is what the escaping exception used to skip");
        }

        [Test]
        public void DestroyStale_ReclaimsAnExternallyDestroyedView()
        {
            // The scan only ever sees a destroyed view whose ENTITY is gone too: while the entity is
            // still collected, CollectPresent reaches TrySpawn first and the slot is handed over there
            // (see TrySpawn_ReplacesAnExternallyDestroyedView_InTheSameTick). So the two halves of the
            // self-healing split by who still owns the entity, and this is the dead-entity half.
            var entities = BuildLiveEntities(2);
            SeedClock(_engine, lastVerified: 2, baseTick: 0);
            Reconcile();
            Assume.That(ViewCount(), Is.EqualTo(2), "precondition: both entities got a view");
            var doomed = Views()[entities[0].ToId()];
            var survivor = Views()[entities[1].ToId()];

            _sim.Frame.DestroyEntity(entities[0]);
            for (int t = 3; t <= 4; t++) { _sim.SaveSnapshot(); _sim.Tick(NoCommands); }
            SeedClock(_engine, lastVerified: 4, baseTick: 4);
            Object.DestroyImmediate(doomed.gameObject);

            Assert.DoesNotThrow(() => Reconcile());

            Assert.That(ViewCount(), Is.EqualTo(1),
                "a destroyed view is stale by definition — the scan has to reclaim it, and only it");
            Assert.That(SoleView(), Is.SameAs(survivor),
                "the entry that is still alive must survive: this reclaims the destroyed one, not the pass");
        }

        [Test]
        public void DestroyStale_UnregistersAnExternallyDestroyedView()
        {
            var entity = BuildOwnedEntity(ownerId: 0);
            SeedClock(_engine, lastVerified: 2, baseTick: 0);
            Reconcile();
            var view = SoleView();
            Assume.That(_evu.PlayerViews.Get(0), Is.SameAs(view), "precondition: the view is registered");

            // Take the entity off the Verified timeline so the scan reaches the destroy loop, then
            // destroy the view from outside.
            _sim.Frame.DestroyEntity(entity);
            for (int t = 3; t <= 4; t++) { _sim.SaveSnapshot(); _sim.Tick(NoCommands); }
            SeedClock(_engine, lastVerified: 4, baseTick: 4);
            Object.DestroyImmediate(view.gameObject);

            Reconcile();

            Assert.That(_evu.PlayerViews.Get(0), Is.Null,
                "the guard skips the calls into the view, not the bookkeeping — a destroyed view left registered outlives the fix it was meant to survive");
            Assert.That(ViewCount(), Is.EqualTo(0));
        }

        [Test]
        public void TrySpawn_ReplacesAnExternallyDestroyedView_InTheSameTick()
        {
            var views = SpawnViews(1);
            Object.DestroyImmediate(views[0].gameObject);

            Reconcile();

            // CollectPresent (which calls TrySpawn) runs BEFORE DestroyStale, so the guard has to fall
            // through to the spawn instead of returning: returning would leave the entity view-less for
            // a whole tick while the scan catches up on the next one.
            Assert.That(_factory.CreateCalls, Is.EqualTo(2), "the slot has to be refilled, not merely emptied");
            Assert.That(ViewCount(), Is.EqualTo(1));
            Assert.That(SoleView(), Is.Not.Null, "and the entry left behind has to be a live view");
        }

        // ── Harness ─────────────────────────────────────────────────────────────────────────────

        private EcsSimulation NewSim()
            => new EcsSimulation(MaxEntities, maxRollbackTicks: 16, deltaTimeMs: TickIntervalMs);

        private KlothoEngine NewEngine(EcsSimulation sim)
        {
            var engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs          = TickIntervalMs,
                    InterpolationDelayTicks = 3,
                    MaxRollbackTicks        = 16,
                    MaxEntities             = MaxEntities,
                },
                new SessionConfig());
            engine.Initialize(sim, _logger);
            return engine;
        }

        /// <summary>Entities that stay alive on every timeline, snapshotted through tick 2.</summary>
        private EntityRef[] BuildLiveEntities(int count)
        {
            var entities = new EntityRef[count];
            for (int i = 0; i < count; i++)
            {
                entities[i] = _sim.Frame.CreateEntity();
                _sim.Frame.Add(entities[i], new TransformComponent());
            }
            for (int t = 0; t <= 2; t++) { _sim.SaveSnapshot(); _sim.Tick(NoCommands); }
            return entities;
        }

        private EntityRef BuildOwnedEntity(int ownerId)
        {
            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent());
            _sim.Frame.Add(entity, new OwnerComponent { OwnerId = ownerId });
            for (int t = 0; t <= 2; t++) { _sim.SaveSnapshot(); _sim.Tick(NoCommands); }
            return entity;
        }

        /// <summary>Builds and spawns <paramref name="count"/> views, returned in creation order.</summary>
        private EntityView[] SpawnViews(int count)
        {
            var entities = BuildLiveEntities(count);
            SeedClock(_engine, lastVerified: 2, baseTick: 0);
            Reconcile();
            Assume.That(ViewCount(), Is.EqualTo(count), "precondition: every entity got a view");

            var views = new EntityView[count];
            var map = Views();
            for (int i = 0; i < count; i++) views[i] = map[entities[i].ToId()];
            return views;
        }

        private static void SeedClock(KlothoEngine engine, int lastVerified, int baseTick)
        {
            RenderTimeMsField.SetValue(engine, baseTick * (double)TickIntervalMs);
            RenderTimeInitField.SetValue(engine, true);
            LastVerifiedTickField.SetValue(engine, lastVerified);
        }

        private void Reconcile() => ReconcileMethod.Invoke(_evu, null);

        private Dictionary<long, EntityView> Views()
            => (Dictionary<long, EntityView>)ViewsField.GetValue(_evu);

        private int ViewCount() => Views().Count;

        private EntityView SoleView()
        {
            var views = Views();
            Assert.That(views.Count, Is.EqualTo(1), "SoleView expects exactly one view");
            foreach (var v in views.Values) return v;
            return null;
        }

        /// <summary>
        /// Reads its own transform in OnDeactivate — see the fixture summary. Everything else is inert.
        /// </summary>
        private class LifecycleProbeView : EntityView
        {
            public int LateUpdateCalls;

            public override void OnInitialize() { }
            public override void OnUpdateView() { }
            public override void OnLateUpdateView() => LateUpdateCalls++;

            public override void OnDeactivate()
            {
                // A real deactivate hook touches the object it belongs to. Keeping that here is what
                // makes the destroy-site tests able to fail on the unguarded code.
                _ = transform.position;
            }

            private int  _ownerId;
            private bool _hasOwner;

            public override void OnActivate(FrameRef frame)
            {
                var f = frame.Frame;
                _hasOwner = f != null && f.Has<OwnerComponent>(EntityRef);
                if (_hasOwner) _ownerId = f.GetReadOnly<OwnerComponent>(EntityRef).OwnerId;
            }

            // The base returns false so a missing override fails loudly; an owner-bearing
            // probe without this is rebound on every Reconcile.
            public override bool OwnerMatches(int ownerId) => _hasOwner && _ownerId == ownerId;
        }

        private class LifecycleProbeFactory : EntityViewFactory
        {
            public int CreateCalls;
            public int DestroyCalls;

            protected override GameObject ResolvePrefab(Frame frame, EntityRef entity) => null;
            protected override bool ShouldRender(Frame frame, EntityRef entity) => true;

            public override bool TryGetBindBehaviour(Frame frame, EntityRef entity, out BindBehaviour behaviour)
            {
                behaviour = BindBehaviour.Verified;
                return true;
            }

            // No EnableSnapshotInterpolation: the CSP render path is the one that reaches ApplyTransform
            // from the live frame alone, which keeps LateUpdate_SurvivesAnExternallyDestroyedView from
            // depending on where the interpolation window happens to sit.
            public override ViewFlags GetViewFlags(Frame frame, EntityRef entity) => ViewFlags.None;

            public override UniTask<EntityView> CreateAsync(Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags)
            {
                CreateCalls++;
                var go = new GameObject($"LifecycleProbe_{entity.Index}_{entity.Version}");
                EntityView view = go.AddComponent<LifecycleProbeView>();
                return UniTask.FromResult(view);
            }

            public override void Destroy(EntityView view)
            {
                DestroyCalls++;
                if (view != null) Object.DestroyImmediate(view.gameObject);
            }
        }
    }
}
