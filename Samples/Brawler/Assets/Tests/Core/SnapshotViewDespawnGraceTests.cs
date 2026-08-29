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
    /// How long a snapshot-bound view outlives its entity (IMP103, view-despawn grace).
    ///
    /// The EVU decided lifetime from the Verified frame while the view rendered the window the render
    /// clock points at, and that clock converges to <c>LastVerifiedTick - InterpolationDelayTicks</c>.
    /// The two criteria therefore sat `delay` ticks apart, and destroying on the lifetime one discarded
    /// the last `delay` ticks of the entity's motion — silently, because the frames were still in the
    /// ring and nothing reported a miss.
    ///
    /// The second half of the defect was worse than the arithmetic: the active-view dictionary was keyed
    /// by <c>EntityRef.Index</c> alone, so the moment the PREDICTED pass saw a new occupant in the same
    /// slot, <c>TrySpawn</c>'s rebind destroyed the dying view — before the Verified timeline had even
    /// reached the death. That is what the version-carrying key fixes, and the coexistence it allows is
    /// what these tests are mostly about.
    /// </summary>
    [TestFixture]
    public class SnapshotViewDespawnGraceTests
    {
        private const int MaxEntities    = 16;
        private const int TickIntervalMs = 50;
        private const int Delay          = 3;

        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        private static readonly FieldInfo RenderTimeMsField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeInitField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo ViewsField = typeof(EntityViewUpdater)
            .GetField("_viewsByEntity", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo DespawnTickField = typeof(EntityViewUpdater)
            .GetField("_despawnVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo ReconcileMethod = typeof(EntityViewUpdater)
            .GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo OnTickExecutedMethod = typeof(EntityViewUpdater)
            .GetMethod("OnTickExecuted", BindingFlags.NonPublic | BindingFlags.Instance);

        private EcsSimulation     _sim;
        private KlothoEngine      _engine;
        private IKLogger          _logger;
        private GameObject        _evuGo;
        private EntityViewUpdater _evu;
        private GraceProbeFactory _factory;

        [SetUp]
        public void SetUp()
        {
            var loggerFactory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = loggerFactory.CreateLogger("SnapshotViewDespawnGraceTests");

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

            _factory = ScriptableObject.CreateInstance<GraceProbeFactory>();
            _evuGo   = new GameObject("GraceEvu");
            _evu     = _evuGo.AddComponent<EntityViewUpdater>();
            _evu.Initialize(_engine, _factory);
        }

        [TearDown]
        public void TearDown()
        {
            // Cleanup first: OnDestroy only unsubscribes, so destroying the updater's GameObject leaves
            // every view it created alive in the shared EditMode scene (IMP105 T-1).
            if (_evu != null) _evu.Cleanup();
            if (_evuGo != null) Object.DestroyImmediate(_evuGo);
            if (_factory != null) Object.DestroyImmediate(_factory);
            _evu = null;
            _sim = null;
            _engine = null;
        }

        // ── The grace itself ────────────────────────────────────────────────────────────────────

        [Test]
        public void DyingSnapshotView_SurvivesUntilRenderReachesDeath()
        {
            var entity = BuildHistory(aliveThroughTick: 2, snapshotThroughTick: 7);
            SpawnWhileAlive(lastVerifiedTick: 2);

            // Render still at tick 0 while the Verified frame is already past the death at 3.
            SeedClock(lastVerified: 3, baseTick: 0);
            Reconcile();

            Assert.That(ViewCount(), Is.EqualTo(1),
                "the view must outlive the Verified frame's verdict — the render is still three ticks short of the death");
            Assert.That(GraceTickOf(entity), Is.EqualTo(3),
                "the grace must be anchored at the first Verified tick that stopped carrying the entity");

            // One tick short of the death: still inside the window.
            SeedClock(lastVerified: 5, baseTick: 2);
            Reconcile();
            Assert.That(ViewCount(), Is.EqualTo(1), "base 2 has not reached the death tick 3 yet");

            // The render clock arrives.
            SeedClock(lastVerified: 6, baseTick: 3);
            Reconcile();
            Assert.That(ViewCount(), Is.EqualTo(0), "once the render reaches the death tick there is nothing left to draw");
        }

        [Test]
        public void ResurrectedEntity_CancelsThePendingGrace()
        {
            var entity = BuildHistory(aliveThroughTick: 2, snapshotThroughTick: 7);
            SpawnWhileAlive(lastVerifiedTick: 2);

            SeedClock(lastVerified: 3, baseTick: 0);
            Reconcile();
            Assert.That(GraceTickOf(entity), Is.EqualTo(3), "precondition: the grace has started");

            // A rollback can put the entity back on the Verified timeline. The view is an ordinary
            // active entry, so nothing has to be revived — the grace just has to stop.
            LastVerifiedTickField.SetValue(_engine, 2);
            Reconcile();

            Assert.That(ViewCount(), Is.EqualTo(1));
            Assert.That(GraceTickOf(entity), Is.Null, "coming back must clear the recorded despawn tick, not leave it armed");
        }

        [Test]
        public void GraceView_ReceivesNoTickCallback()
        {
            BuildHistory(aliveThroughTick: 2, snapshotThroughTick: 7);
            SpawnWhileAlive(lastVerifiedTick: 2);

            SeedClock(lastVerified: 3, baseTick: 0);
            OnTickExecutedMethod.Invoke(_evu, new object[] { 3 });

            var view = SoleView();
            Assert.That(view.UpdateViewCalls, Is.EqualTo(0),
                "a dying view has no new tick — OnUpdateView would be a callback about an entity the game can no longer look up");
        }

        // ── Coexistence: the half the index key made impossible ─────────────────────────────────

        [Test]
        public void SlotReuseInPredicted_DoesNotEvictTheDyingSnapshotView()
        {
            var dying = BuildHistory(aliveThroughTick: 2, snapshotThroughTick: 7);
            SpawnWhileAlive(lastVerifiedTick: 2);

            // The freed slot is taken on the LIVE frame by a locally-owned entity, so the Predicted
            // pass collects it as NonVerified while the Verified snapshots still hold the old occupant.
            var reused = _sim.Frame.CreateEntity();
            Assume.That(reused.Index, Is.EqualTo(dying.Index), "precondition: the free list must hand back the same slot");
            Assume.That(reused.Version, Is.Not.EqualTo(dying.Version));
            _factory.PredictedOnly.Add(reused.Version);

            SeedClock(lastVerified: 3, baseTick: 0);
            Reconcile();

            Assert.That(ViewCount(), Is.EqualTo(2),
                "the dying view and the new occupant share an entity index — with the version in the key they coexist");
            Assert.That(GraceTickOf(dying), Is.EqualTo(3), "the old view is dying, not evicted");
            Assert.That(GraceTickOf(reused), Is.Null, "the new occupant is not dying at all");
        }

        /// <summary>
        /// The same claim as the test above, asserted through the factory alone (Plan completion
        /// criterion 2). The version-carrying key is only half of what stops the eviction — the other
        /// half is that nothing destroys the dying view — and "nothing destroys it" is a statement
        /// about a call the EVU makes, not about a field it holds.
        ///
        /// That distinction is what makes this the RED-capable form: the fix renamed
        /// `_viewsByEntityIndex` to `_viewsByEntity` and introduced `_despawnVerifiedTick`, so every
        /// other test here reflects on a member the pre-fix updater does not have. Restored via
        /// `git show`, the pre-fix rebind destroys the dying view on the spot (DestroyCalls 1,
        /// LiveViews 1); this asserts 0 and 2.
        /// </summary>
        [Test]
        public void SlotReuseInPredicted_DoesNotDestroyTheDyingView()
        {
            var dying = BuildHistory(aliveThroughTick: 2, snapshotThroughTick: 7);
            SpawnWhileAlive(lastVerifiedTick: 2);

            var reused = _sim.Frame.CreateEntity();
            Assume.That(reused.Index, Is.EqualTo(dying.Index), "precondition: the free list must hand back the same slot");
            Assume.That(reused.Version, Is.Not.EqualTo(dying.Version));
            _factory.PredictedOnly.Add(reused.Version);

            SeedClock(lastVerified: 3, baseTick: 0);
            Reconcile();

            Assert.That(_factory.DestroyCalls, Is.Zero,
                "the predicted pass saw a new occupant in the slot — it must not take that as licence to destroy the old view");
            Assert.That(_factory.CreateCalls, Is.EqualTo(2), "and the new occupant still gets its own view");
            Assert.That(LiveViews(), Is.EqualTo(2), "so both are alive at once");
        }

        /// <summary>
        /// T9 / F-11 — two in-flight spawns for one slot do not cancel each other.
        ///
        /// The overlap window is where this lives: while the Verified frame still carries v1 and the
        /// live frame already holds v2, one reconcile dispatches BOTH. The pre-fix completion guard was
        /// keyed by Index, so the second dispatch overwrote the first one's entry and v1's finished view
        /// was thrown away on arrival (DestroyCalls 1) — in the overlap window that repeated every tick,
        /// so neither view ever appeared. With the version in the key there are two entries and nothing
        /// is cancelled.
        ///
        /// Reachable only when a game overrides CreateAsync with a real load; the shipped pool returns
        /// UniTask.FromResult on every path, which is why this needs DeferCreates to be observable at all.
        /// </summary>
        [Test]
        public void OverlapWindow_BothSpawnsSurvive()
        {
            var dying  = BuildHistory(aliveThroughTick: 2, snapshotThroughTick: 7);
            var reused = _sim.Frame.CreateEntity();
            Assume.That(reused.Index, Is.EqualTo(dying.Index), "precondition: the free list must hand back the same slot");
            Assume.That(reused.Version, Is.Not.EqualTo(dying.Version));
            _factory.PredictedOnly.Add(reused.Version);

            // lastVerified 2 is the overlap window: the entity dies at tick 3, so the Verified pass
            // still collects v1 while the Predicted pass collects v2.
            _factory.DeferCreates = true;
            SeedClock(lastVerified: 2, baseTick: 0);
            Reconcile();
            Assert.That(_factory.CreateCalls, Is.EqualTo(2), "precondition: one reconcile dispatched both spawns");

            _factory.CompleteDeferred();

            Assert.That(_factory.DestroyCalls, Is.Zero,
                "a spawn completing must not be discarded because another spawn for the same slot was dispatched after it");
        }

        // ── Non-goals that must not move ────────────────────────────────────────────────────────

        [Test]
        public void CspView_IsStillDestroyedImmediately()
        {
            var entity = _sim.Frame.CreateEntity();
            _factory.PredictedOnly.Add(entity.Version);
            _sim.SaveSnapshot();
            _sim.Tick(NoCommands);

            SeedClock(lastVerified: 0, baseTick: 0);
            Reconcile();
            Assert.That(ViewCount(), Is.EqualTo(1), "precondition: the CSP view exists");

            _sim.Frame.DestroyEntity(entity);
            Reconcile();

            Assert.That(ViewCount(), Is.EqualTo(0),
                "a CSP view renders the Predicted frame, so its lifetime and its render position are the same tick — no grace");
        }

        [Test]
        public void Cleanup_ReclaimsGraceViews()
        {
            BuildHistory(aliveThroughTick: 2, snapshotThroughTick: 7);
            SpawnWhileAlive(lastVerifiedTick: 2);
            SeedClock(lastVerified: 3, baseTick: 0);
            Reconcile();
            Assert.That(ViewCount(), Is.EqualTo(1), "precondition: a view is inside its grace");

            _evu.Cleanup();

            Assert.That(ViewCount(), Is.EqualTo(0), "teardown must not leak a view just because it was dying");
            Assert.That(DespawnTicks().Count, Is.EqualTo(0), "the despawn-tick map has to be cleared with it");
        }

        // ── Harness ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Snapshots ticks 0..<paramref name="snapshotThroughTick"/>, with the entity present through
        /// <paramref name="aliveThroughTick"/> and gone from <c>aliveThroughTick + 1</c> onward. Snapshots
        /// keep being taken past the death so the tests can advance the chain without the Verified frame
        /// falling off the ring — a null frame is a different case and has its own path.
        /// </summary>
        private EntityRef BuildHistory(int aliveThroughTick, int snapshotThroughTick)
        {
            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent());

            for (int t = 0; t <= aliveThroughTick; t++)
            {
                _sim.SaveSnapshot();
                _sim.Tick(NoCommands);
            }

            _sim.Frame.DestroyEntity(entity);
            for (int t = aliveThroughTick + 1; t <= snapshotThroughTick; t++)
            {
                _sim.SaveSnapshot();
                _sim.Tick(NoCommands);
            }
            return entity;
        }

        /// <summary>
        /// Creates the view while its entity is still on the Verified timeline.
        ///
        /// The grace is only observable on a view that already existed when the death arrived — reconciling
        /// for the first time at a tick the entity is already gone from creates nothing at all, which is
        /// what the first version of these tests actually asserted against.
        /// </summary>
        private void SpawnWhileAlive(int lastVerifiedTick)
        {
            SeedClock(lastVerified: lastVerifiedTick, baseTick: 0);
            Reconcile();
            Assert.That(LiveViews(), Is.EqualTo(1),
                "precondition: the view has to be created while the entity is still in the Verified frame");
        }

        /// <summary>
        /// Pins the verified render clock at <paramref name="baseTick"/> and the chain at
        /// <paramref name="lastVerified"/>. The two are set independently on purpose — the whole defect
        /// lives in the gap between them.
        /// </summary>
        private void SeedClock(int lastVerified, int baseTick)
        {
            RenderTimeMsField.SetValue(_engine, baseTick * (double)TickIntervalMs);
            RenderTimeInitField.SetValue(_engine, true);
            LastVerifiedTickField.SetValue(_engine, lastVerified);
        }

        private void Reconcile() => ReconcileMethod.Invoke(_evu, null);

        private Dictionary<long, EntityView> Views()
            => (Dictionary<long, EntityView>)ViewsField.GetValue(_evu);

        private Dictionary<long, int> DespawnTicks()
            => (Dictionary<long, int>)DespawnTickField.GetValue(_evu);

        private int ViewCount() => Views().Count;

        /// <summary>
        /// Live views counted through the factory instead of the EVU's dictionary. Equal to
        /// <see cref="ViewCount"/>, but reachable on an updater whose internals differ — which is what
        /// lets the RED half of a claim be demonstrated against the pre-fix code.
        /// </summary>
        private int LiveViews() => _factory.CreateCalls - _factory.DestroyCalls;

        private ProbeEntityView SoleView()
        {
            var views = Views();
            Assert.That(views.Count, Is.EqualTo(1), "SoleView expects exactly one view");
            foreach (var v in views.Values) return (ProbeEntityView)v;
            return null;
        }

        private int? GraceTickOf(EntityRef entity)
            => DespawnTicks().TryGetValue(entity.ToId(), out int tick) ? tick : (int?)null;

        // Renders nothing — these tests only ask when the EVU creates and destroys it.
        private class ProbeEntityView : EntityView
        {
            public int UpdateViewCalls;

            // The test GameObject has no EntityViewComponent children; skip the base walk.
            public override void OnInitialize() { }
            public override void OnDeactivate() { }
            public override void OnLateUpdateView() { }
            public override void OnUpdateView() => UpdateViewCalls++;

            // Owner cache + OwnerMatches, the contract every Owner-bearing view has to honour: the base
            // returns false on purpose so a missing override fails loudly. Without this an owner-bearing
            // probe is rebound (destroy + respawn) on EVERY Reconcile, which looks exactly like the
            // registry losing its entry. Additive for the owner-less tests — the EVU short-circuits
            // OwnersMatch to true when the entity carries no OwnerComponent, so it is never called there.
            private int  _ownerId;
            private bool _hasOwner;

            public override void OnActivate(FrameRef frame)
            {
                var f = frame.Frame;
                _hasOwner = f != null && f.Has<OwnerComponent>(EntityRef);
                if (_hasOwner) _ownerId = f.GetReadOnly<OwnerComponent>(EntityRef).OwnerId;
            }

            public override bool OwnerMatches(int ownerId) => _hasOwner && _ownerId == ownerId;
        }

        /// <summary>
        /// Spawns synchronously so a Reconcile call leaves the dictionary settled, and lets a test declare
        /// which entity versions belong to the CSP path — the cross-owner slot reuse this defect needs is
        /// otherwise awkward to arrange from an OwnerComponent.
        /// </summary>
        private class GraceProbeFactory : EntityViewFactory
        {
            public readonly HashSet<int> PredictedOnly = new HashSet<int>();

            // Observable from outside the EVU. The fix renamed _viewsByEntityIndex and added
            // _despawnVerifiedTick, so a test that reflects on either cannot be run against the
            // pre-fix updater at all — these two counters can.
            public int CreateCalls;
            public int DestroyCalls;

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

            // The shipped CreateAsync/Rent complete synchronously on every path, so the pending
            // machinery is unreachable unless a game overrides CreateAsync with a real load
            // (Addressables). Setting this models that game.
            public bool DeferCreates;
            private readonly List<UniTaskCompletionSource<EntityView>> _deferred =
                new List<UniTaskCompletionSource<EntityView>>();
            private readonly List<EntityView> _deferredViews = new List<EntityView>();

            public override UniTask<EntityView> CreateAsync(Frame frame, EntityRef entity, BindBehaviour behaviour, ViewFlags flags)
            {
                CreateCalls++;
                var go = new GameObject($"ProbeView_{entity.Index}_{entity.Version}");
                EntityView view = go.AddComponent<ProbeEntityView>();
                if (!DeferCreates) return UniTask.FromResult(view);

                var src = new UniTaskCompletionSource<EntityView>();
                _deferred.Add(src);
                _deferredViews.Add(view);
                return src.Task;
            }

            /// <summary>Completes every in-flight spawn, in dispatch order.</summary>
            public void CompleteDeferred()
            {
                for (int i = 0; i < _deferred.Count; i++) _deferred[i].TrySetResult(_deferredViews[i]);
                _deferred.Clear();
                _deferredViews.Clear();
            }

            public override void Destroy(EntityView view)
            {
                DestroyCalls++;
                if (view != null) Object.DestroyImmediate(view.gameObject);
            }
        }

        // ── IMP105 C-2: the registry follows the view on screen, not the entity ──

        /// <summary>
        /// Owner-bearing twin of <see cref="BuildHistory"/>. Additive on purpose: the shared builder is
        /// used by six existing tests, and giving THEM an OwnerComponent would quietly change what they
        /// assert (registration and its events would start firing inside them). OwnerId 0 matches the
        /// harness engine's LocalPlayerId — no network service is attached, so it falls back to 0 and
        /// IsSpectatorMode is false, which is what makes the OnLocal* events fire for real.
        /// </summary>
        private EntityRef BuildOwnedHistory(int aliveThroughTick, int snapshotThroughTick, int ownerId = 0)
        {
            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent());
            _sim.Frame.Add(entity, new OwnerComponent { OwnerId = ownerId });

            for (int t = 0; t <= aliveThroughTick; t++)
            {
                _sim.SaveSnapshot();
                _sim.Tick(NoCommands);
            }

            _sim.Frame.DestroyEntity(entity);
            for (int t = aliveThroughTick + 1; t <= snapshotThroughTick; t++)
            {
                _sim.SaveSnapshot();
                _sim.Tick(NoCommands);
            }
            return entity;
        }

        /// <summary>
        /// A view that comes back from a rollback must still be the player's registered view.
        ///
        /// The grace used to unbind on entry, and nothing rebound on the way out: Register has exactly one
        /// call site (the end of SpawnViewAsync) and TrySpawn skips a view that is still alive. So the
        /// player stayed unmapped for the rest of the match — in Brawler that is the camera letting go of
        /// the local character and never taking it back (IMP105 C-2).
        /// </summary>
        [Test]
        public void ResurrectedView_StaysRegistered()
        {
            var entity = BuildOwnedHistory(aliveThroughTick: 2, snapshotThroughTick: 7);
            SpawnWhileAlive(lastVerifiedTick: 2);
            Assert.That(_evu.PlayerViews.Get(0), Is.Not.Null, "precondition: the owner is registered on spawn");

            SeedClock(lastVerified: 3, baseTick: 0);
            Reconcile();
            Assert.That(GraceTickOf(entity), Is.EqualTo(3), "precondition: the grace has started");

            LastVerifiedTickField.SetValue(_engine, 2);   // the rollback puts it back
            Reconcile();

            Assert.That(GraceTickOf(entity), Is.Null, "precondition: the grace was cancelled");
            Assert.That(_evu.PlayerViews.Get(0), Is.SameAs(SoleView()),
                "the view is on screen again, so the player must still map to it — unbinding at grace entry "
                + "left it unmapped for good, because nothing re-registers a view that never died");
        }

        /// <summary>
        /// The control: unbinding did not disappear, it moved. When the grace actually expires and the view
        /// is destroyed, the map is cleared and the local event fires exactly once.
        /// </summary>
        [Test]
        public void GraceExpiry_StillUnregisters()
        {
            var entity = BuildOwnedHistory(aliveThroughTick: 2, snapshotThroughTick: 7);
            SpawnWhileAlive(lastVerifiedTick: 2);

            int unregistered = 0;
            _evu.PlayerViews.OnLocalViewUnregistered += _ => unregistered++;

            SeedClock(lastVerified: 3, baseTick: 0);
            Reconcile();
            Assert.That(_evu.PlayerViews.Get(0), Is.Not.Null, "during the grace the view is still on screen");
            Assert.That(unregistered, Is.Zero, "...so nothing is unbound yet");

            SeedClock(lastVerified: 6, baseTick: 3);      // render clock reaches the despawn tick
            Reconcile();

            Assert.That(ViewCount(), Is.Zero, "precondition: the grace expired and the view was destroyed");
            Assert.That(_evu.PlayerViews.Get(0), Is.Null, "the map follows the view off screen");
            Assert.That(unregistered, Is.EqualTo(1), "exactly once, at the destroy");
        }

        /// <summary>
        /// The one thing deferring the unbind could have broken: a respawn claims the slot while the old
        /// view is still in its grace, and the old view's later unbind must not evict it. It cannot —
        /// Unregister is instance-guarded — and this pins that, because the guard is the entire reason
        /// deferring is safe.
        /// </summary>
        [Test]
        public void RespawnDuringGrace_SurvivesTheOldViewsUnbind()
        {
            // Both entities are built into the snapshot ring up front — a Verified-bound view is only
            // collected from the Verified snapshot, so an entity created in the live frame after the fact
            // would never be seen. The handover is arranged by their lifetimes: `dying` occupies snapshots
            // 0..2 and `respawned` occupies 3..7, so the Reconcile at lastVerified 3 does both halves at
            // once — one enters its grace, the other registers over it.
            var dying = _sim.Frame.CreateEntity();
            _sim.Frame.Add(dying, new TransformComponent());
            _sim.Frame.Add(dying, new OwnerComponent { OwnerId = 0 });
            for (int t = 0; t <= 2; t++) { _sim.SaveSnapshot(); _sim.Tick(NoCommands); }

            _sim.Frame.DestroyEntity(dying);
            var respawned = _sim.Frame.CreateEntity();
            _sim.Frame.Add(respawned, new TransformComponent());
            _sim.Frame.Add(respawned, new OwnerComponent { OwnerId = 0 });
            for (int t = 3; t <= 7; t++) { _sim.SaveSnapshot(); _sim.Tick(NoCommands); }

            SpawnWhileAlive(lastVerifiedTick: 2);
            var dyingView = SoleView();

            SeedClock(lastVerified: 3, baseTick: 0);
            Reconcile();

            Assert.That(GraceTickOf(dying), Is.EqualTo(3), "precondition: the old view is in its grace");
            var newView = _evu.PlayerViews.Get(0);
            Assert.That(newView, Is.Not.Null.And.Not.SameAs(dyingView), "precondition: the respawn claimed the slot");

            SeedClock(lastVerified: 6, baseTick: 3);      // the old view's grace expires now
            Reconcile();

            Assert.That(_evu.PlayerViews.Get(0), Is.SameAs(newView),
                "the dying view's unbind is instance-guarded, so it must not evict the occupant that "
                + "already claimed the slot");
        }
    }
}
