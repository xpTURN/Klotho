using NUnit.Framework;
using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.View.Tests
{
    /// <summary>
    /// <see cref="EntityView.ApplyTransform"/> against an assigned interpolation target.
    ///
    /// The split is: the root keeps the tick-accurate transform (collision / raycast reference) while the
    /// child carries the interpolated render pose. Every value ApplyTransform receives is world-space, so
    /// the child must end up at exactly those world values — regardless of how the root is oriented or
    /// scaled, and regardless of how deep the target sits. Writing a world delta into `localPosition`
    /// satisfies that only while the root is unrotated and unscaled, and the root carries the simulation
    /// yaw, so it almost never is.
    ///
    /// These are Unity EditMode rather than dotnet tests because the behaviour under test *is*
    /// `Transform`'s local/world conversion; there is nothing to assert without it.
    /// </summary>
    [TestFixture]
    public class EntityViewInterpolationTargetTests
    {
        private const float Tolerance = 1e-3f;

        private GameObject _root;
        private ProbeEntityView _view;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Root");
            _view = _root.AddComponent<ProbeEntityView>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private Transform AddChild(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            return go.transform;
        }

        // Root at (10, 0, 10) facing 90 deg, render pose 3 units away from it — an offset large enough
        // that a yaw-rotated one lands somewhere obviously different.
        private static UpdatePositionParameter Pose() => new UpdatePositionParameter
        {
            UninterpolatedPosition = new Vector3(10f, 0f, 10f),
            UninterpolatedRotation = Quaternion.Euler(0f, 90f, 0f),
            NewPosition            = new Vector3(12f, 0f, 12f),
            NewRotation            = Quaternion.Euler(0f, 45f, 0f),
            ErrorVisualVector      = new Vector3(1f, 0f, 0f),
            ErrorVisualQuaternion  = Quaternion.Euler(0f, 10f, 0f),
        };

        private static Vector3 ExpectedWorldPosition(in UpdatePositionParameter p)
            => p.NewPosition + p.ErrorVisualVector;

        private static Quaternion ExpectedWorldRotation(in UpdatePositionParameter p)
            => p.ErrorVisualQuaternion * p.NewRotation;

        /// <summary>
        /// The core case. The root is yaw-rotated, so a world delta written into `localPosition`
        /// would come back out rotated by that yaw — this test fails on the pre-fix code.
        /// </summary>
        [Test]
        public void RotatedRoot_ChildLandsOnTheWorldRenderPose()
        {
            _view.SetInterpolationTarget(AddChild("Mesh", _root.transform));
            var param = Pose();

            _view.Apply(ref param);

            Assert.That(Vector3.Distance(_view.Target.position, ExpectedWorldPosition(param)),
                Is.LessThan(Tolerance),
                "child world position must be the render pose itself, not that pose rotated by the root yaw");
            Assert.That(Quaternion.Angle(_view.Target.rotation, ExpectedWorldRotation(param)),
                Is.LessThan(0.1f));
        }

        /// <summary>The root keeps the tick-accurate pose — that is what collision/raycasts read.</summary>
        [Test]
        public void RootKeepsTheUninterpolatedPose()
        {
            _view.SetInterpolationTarget(AddChild("Mesh", _root.transform));
            var param = Pose();

            _view.Apply(ref param);

            Assert.That(Vector3.Distance(_root.transform.position, param.UninterpolatedPosition),
                Is.LessThan(Tolerance));
            Assert.That(Quaternion.Angle(_root.transform.rotation, param.UninterpolatedRotation),
                Is.LessThan(0.1f));
        }

        /// <summary>
        /// The target need not be a direct child. An intermediate transform that is itself moved and
        /// rotated is the case a hand-rolled parent-space conversion gets wrong.
        /// </summary>
        [Test]
        public void NestedAndRotatedIntermediate_ChildStillLandsOnTheWorldRenderPose()
        {
            var mid = AddChild("Pivot", _root.transform);
            mid.localPosition = new Vector3(0f, 2f, 0f);
            mid.localRotation = Quaternion.Euler(0f, 33f, 0f);
            _view.SetInterpolationTarget(AddChild("Mesh", mid));
            var param = Pose();

            _view.Apply(ref param);

            Assert.That(Vector3.Distance(_view.Target.position, ExpectedWorldPosition(param)),
                Is.LessThan(Tolerance));
            Assert.That(Quaternion.Angle(_view.Target.rotation, ExpectedWorldRotation(param)),
                Is.LessThan(0.1f));
        }

        /// <summary>A scaled root scales a locally-written offset. World values are immune.</summary>
        [Test]
        public void ScaledRoot_ChildStillLandsOnTheWorldRenderPose()
        {
            _root.transform.localScale = new Vector3(2f, 2f, 2f);
            _view.SetInterpolationTarget(AddChild("Mesh", _root.transform));
            var param = Pose();

            _view.Apply(ref param);

            Assert.That(Vector3.Distance(_view.Target.position, ExpectedWorldPosition(param)),
                Is.LessThan(Tolerance));
        }

        /// <summary>Teleport snaps the root and drops the child offset — pins existing behaviour.</summary>
        [Test]
        public void Teleport_SnapsRootAndClearsChildOffset()
        {
            var child = AddChild("Mesh", _root.transform);
            child.localPosition = new Vector3(5f, 5f, 5f);
            child.localRotation = Quaternion.Euler(0f, 77f, 0f);
            _view.SetInterpolationTarget(child);

            var param = Pose();
            param.Teleported = true;
            _view.Apply(ref param);

            Assert.That(Vector3.Distance(_root.transform.position, param.UninterpolatedPosition),
                Is.LessThan(Tolerance));
            Assert.That(child.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(Quaternion.Angle(child.localRotation, Quaternion.identity), Is.LessThan(0.1f));
        }

        /// <summary>
        /// Pool re-rent must not inherit the previous occupant's offset. The pool only toggles
        /// SetActive, which leaves local transforms alone, so InternalActivate is the one place that can
        /// clear it — the same reasoning that already resets ErrorVisualState there.
        /// </summary>
        [Test]
        public void InternalActivate_ClearsChildOffsetLeftByThePreviousLife()
        {
            var child = AddChild("Mesh", _root.transform);
            child.localPosition = new Vector3(3f, 0f, -4f);
            child.localRotation = Quaternion.Euler(0f, 120f, 0f);
            _view.SetInterpolationTarget(child);

            _view.InternalActivate(FrameRef.None(FrameKind.Predicted));

            Assert.That(child.localPosition, Is.EqualTo(Vector3.zero),
                "a re-rented view must not wear the offset of whatever occupied it before");
            Assert.That(Quaternion.Angle(child.localRotation, Quaternion.identity), Is.LessThan(0.1f));
        }

        /// <summary>
        /// The other half of the same reset: an offset the PREFAB authored is not residue, and must
        /// survive.
        ///
        /// The two are the same field, so the reset cannot tell them apart by looking — it has to
        /// remember. EnsureInitialized is where: it runs once per instance, before anything has written
        /// to the child, and a pooled re-rent deliberately skips it. Only views whose position line never
        /// runs can observe either value, since every other view has its child written in world space
        /// each frame — which is exactly why zeroing here was permanent for the ones it hit.
        /// </summary>
        [Test]
        public void InternalActivate_KeepsTheAuthoredChildOffset()
        {
            var child = AddChild("Mesh", _root.transform);
            child.localPosition = new Vector3(0f, 1.2f, 0f);          // authored: root at the feet
            child.localRotation = Quaternion.Euler(0f, 30f, 0f);
            _view.SetInterpolationTarget(child);
            _view.EnsureInitialized();                                 // captures the authoring

            child.localPosition = new Vector3(9f, 9f, 9f);             // residue from a previous life
            child.localRotation = Quaternion.Euler(0f, 200f, 0f);

            _view.InternalActivate(FrameRef.None(FrameKind.Predicted));

            Assert.That(Vector3.Distance(child.localPosition, new Vector3(0f, 1.2f, 0f)), Is.LessThan(Tolerance),
                "the authored offset is the view's own shape, not something a previous occupant left behind");
            Assert.That(Quaternion.Angle(child.localRotation, Quaternion.Euler(0f, 30f, 0f)), Is.LessThan(0.1f));
        }

        /// <summary>A null target must stay the supported configuration — the root is interpolated directly.</summary>
        [Test]
        public void NullTarget_InterpolatesTheRootItself()
        {
            var param = Pose();

            _view.Apply(ref param);

            Assert.That(Vector3.Distance(_root.transform.position, ExpectedWorldPosition(param)),
                Is.LessThan(Tolerance));
            Assert.That(Quaternion.Angle(_root.transform.rotation, ExpectedWorldRotation(param)),
                Is.LessThan(0.1f));
        }

        // ── Test-only concrete EntityView subclass ──
        // ApplyTransform and _interpolationTarget are protected; this exposes both without widening them.
        private class ProbeEntityView : EntityView
        {
            public Transform Target => _interpolationTarget;

            public void SetInterpolationTarget(Transform t) => _interpolationTarget = t;

            public void Apply(ref UpdatePositionParameter param) => ApplyTransform(ref param);

            // The test GameObject has no EntityViewComponent children; skip the base walk.
            public override void OnInitialize() { }
            public override void OnActivate(FrameRef frame) { }
        }
    }
}
