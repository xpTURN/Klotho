using NUnit.Framework;
using UnityEngine;

using Brawler;
using xpTURN.Klotho;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.View.Tests
{
    /// <summary>
    /// The render-candidate gate for adopted platform views.
    ///
    /// Platform entities exist in every Brawler session, but their visual is a scene-placed object the
    /// factory adopts — there is no platform prefab to instantiate. With nothing placed, saying "yes, render
    /// this" put EVU in a permanent loop: CreateAsync finds nothing to adopt, the base path's ResolvePrefab
    /// returns null, EVU discards the spawn and re-dispatches on the next tick, forever and with no log.
    ///
    /// The gate reads a value fixed at BindPlacedPlatforms, and the third test is why: a gate on the
    /// REMAINING count flips the moment the last instance is adopted, and EVU re-reads the decision every
    /// tick — the entity drops out of the present set, DestroyStale kills the live view, Destroy hands the
    /// instance back, and the next tick spawns it again. That oscillation is worse than the silent retry,
    /// so the "still true after the pool drains" case is a guard against fixing this the obvious way.
    ///
    /// These call the factory's decision directly: no EVU, no scene, no engine — TryGetBindBehaviour
    /// short-circuits on PlatformComponent before it reaches the base implementation, which is the only
    /// part that would need an attached engine.
    /// </summary>
    [TestFixture]
    public class PlatformAdoptionGateTests
    {
        private EcsSimulation _sim;
        private BrawlerEntityViewFactory _factory;
        private GameObject _placedGo;

        [SetUp]
        public void SetUp()
        {
            _sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 4, deltaTimeMs: 25);
            _factory = ScriptableObject.CreateInstance<BrawlerEntityViewFactory>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_factory != null) Object.DestroyImmediate(_factory);
            if (_placedGo != null) Object.DestroyImmediate(_placedGo);
            foreach (var go in _extraPlaced)
                if (go != null) Object.DestroyImmediate(go);
            _extraPlaced.Clear();
            _sim = null;
        }

        private EntityRef NewPlatformEntity()
        {
            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent());
            _sim.Frame.Add(entity, new PlatformComponent());
            return entity;
        }

        private EntityRef NewPlatformEntityAt(Vector3 position, float yawRadians = 0f)
        {
            var entity = NewPlatformEntity();
            ref var t = ref _sim.Frame.Get<TransformComponent>(entity);
            t.Position = new Deterministic.Math.FPVector3(
                Deterministic.Math.FP64.FromFloat(position.x),
                Deterministic.Math.FP64.FromFloat(position.y),
                Deterministic.Math.FP64.FromFloat(position.z));
            t.Rotation = Deterministic.Math.FP64.FromFloat(yawRadians);
            return entity;
        }

        private PlatformView NewPlacedView()
        {
            _placedGo = new GameObject("PlacedPlatform");
            return _placedGo.AddComponent<PlatformView>();
        }

        private readonly System.Collections.Generic.List<GameObject> _extraPlaced = new();

        private PlatformView NewPlacedView(string name)
        {
            var go = new GameObject(name);
            _extraPlaced.Add(go);
            return go.AddComponent<PlatformView>();
        }

        /// <summary>
        /// Nothing placed — the entity must not be a render candidate. This is the repository's current
        /// state: no stage scene contains a MovingPlatform instance, so before the gate every tick
        /// dispatched a spawn that could not possibly produce a view.
        /// </summary>
        [Test]
        public void NoPlacedView_PlatformIsNotARenderCandidate()
        {
            _factory.BindPlacedPlatforms(null);
            var entity = NewPlatformEntity();

            Assert.That(_factory.TryGetBindBehaviour(_sim.Frame, entity, out _), Is.False,
                "with nothing to adopt the platform has no view — collecting it only re-dispatches a spawn "
                + "that ResolvePrefab cannot satisfy, once per tick, silently");
        }

        /// <summary>An array of nulls is the same as nothing — a scene reload leaves exactly that.</summary>
        [Test]
        public void PlacedArrayOfNulls_IsTreatedAsNothingPlaced()
        {
            _factory.BindPlacedPlatforms(new PlatformView[] { null, null });
            var entity = NewPlatformEntity();

            Assert.That(_factory.TryGetBindBehaviour(_sim.Frame, entity, out _), Is.False);
        }

        /// <summary>With an instance placed, the platform renders on the predicted timeline.</summary>
        [Test]
        public void PlacedView_PlatformRendersOnThePredictedTimeline()
        {
            _factory.BindPlacedPlatforms(new[] { NewPlacedView() });
            var entity = NewPlatformEntity();

            Assert.That(_factory.TryGetBindBehaviour(_sim.Frame, entity, out var behaviour), Is.True);
            Assert.That(behaviour, Is.EqualTo(BindBehaviour.NonVerified),
                "a platform the local player stands on belongs on the same timeline as the predicted character");
            Assert.That(_factory.GetViewFlags(_sim.Frame, entity), Is.EqualTo(ViewFlags.None),
                "the other half of the same decision — EVU evaluates the two independently");
        }

        /// <summary>
        /// The oscillation guard. Once the only placed instance is adopted the pool is empty, but the
        /// decision must NOT flip: EVU reads it every tick, and a "no" here drops the entity from the
        /// present set, so DestroyStale destroys the live view and the next tick spawns it again.
        /// </summary>
        [Test]
        public void AfterTheLastPlacedViewIsTaken_TheDecisionDoesNotFlip()
        {
            _factory.BindPlacedPlatforms(new[] { NewPlacedView() });
            var entity = NewPlatformEntity();

            // Drain the pool the way CreateAsync does.
            _factory.CreateAsync(_sim.Frame, entity, BindBehaviour.NonVerified, ViewFlags.None);

            Assert.That(_factory.TryGetBindBehaviour(_sim.Frame, entity, out var behaviour), Is.True,
                "the gate reads what was placed at bind time, not what is left — gating on the remaining "
                + "count makes EVU destroy and respawn the view on alternating ticks");
            Assert.That(behaviour, Is.EqualTo(BindBehaviour.NonVerified));
        }

        /// <summary>
        /// Which scene object adopts which entity has to be decided by the caller's array order, not by
        /// whatever FindObjectsByType happened to return.
        ///
        /// The factory takes from the TAIL, so BrawlerViewSync sorts descending and the first entity
        /// gets the first name. Neither half is meaningful alone: flip the pop order without flipping
        /// the sort and the pairing silently reverses. Nothing desyncs — this is view-only — but once
        /// platforms carry their own authoring the visual and the collision geometry stop agreeing.
        /// The sort itself lives in BrawlerViewSync and needs a scene; this pins the half the factory owns.
        /// </summary>
        [Test]
        public void AdoptionOrder_TakesFromTheTailOfTheBoundArray()
        {
            var first  = NewPlacedView("Platform_B");
            var second = NewPlacedView("Platform_A");
            _factory.BindPlacedPlatforms(new[] { first, second });   // as ViewSync sorts it: descending

            var e0 = NewPlatformEntity();
            var e1 = NewPlatformEntity();

            var v0 = _factory.CreateAsync(_sim.Frame, e0, BindBehaviour.NonVerified, ViewFlags.None).GetAwaiter().GetResult();
            var v1 = _factory.CreateAsync(_sim.Frame, e1, BindBehaviour.NonVerified, ViewFlags.None).GetAwaiter().GetResult();

            Assert.That(v0, Is.SameAs(second), "the first entity must get the array's last entry — the sort is chosen to make that the first name");
            Assert.That(v1, Is.SameAs(first));
        }

        /// <summary>
        /// Adoption must place the view where the entity is.
        ///
        /// Every other spawn path does: base.CreateAsync reads the entity's transform and hands it to
        /// Instantiate / Pool.Rent so a view never appears at the prefab's authored position or at its
        /// previous occupant's. Adopting returns before any of that, so the scene-placed
        /// object stayed wherever the level designer left it until the first ApplyTransform — and for a
        /// view whose position line never runs (DisableUpdate / DisablePositionUpdate) that is forever.
        /// </summary>
        [Test]
        public void AdoptedView_IsPlacedAtTheEntitysPose()
        {
            var placed = NewPlacedView();
            placed.transform.SetPositionAndRotation(new Vector3(-40f, 7f, 13f), Quaternion.Euler(0f, 210f, 0f));
            _factory.BindPlacedPlatforms(new[] { placed });

            var entity = NewPlatformEntityAt(new Vector3(3f, 1.5f, -8f), yawRadians: Mathf.PI * 0.5f);

            var task = _factory.CreateAsync(_sim.Frame, entity, BindBehaviour.NonVerified, ViewFlags.None);
            Assert.That(task.GetAwaiter().GetResult(), Is.SameAs(placed), "precondition: the scene view was adopted");

            Assert.That(Vector3.Distance(placed.transform.position, new Vector3(3f, 1.5f, -8f)), Is.LessThan(0.001f),
                "the adopted view has to start at the entity, not where the scene put it");
            Assert.That(Quaternion.Angle(placed.transform.rotation, Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(0.1f),
                "and facing the way the entity faces — the yaw rides on the same component");
        }
    }
}
