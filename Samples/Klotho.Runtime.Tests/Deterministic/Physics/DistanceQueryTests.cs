using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class DistanceQueryTests
    {
        private const float EPSILON = 0.05f;

        #region Sphere-Sphere

        [Test]
        public void DistanceSphereSphere_Separated_ReturnsPositive()
        {
            var a = new FPSphereShape(FP64.FromFloat(2.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(3.0f), new FPVector3(10, 0, 0));

            FP64 dist = CollisionTests.DistanceSphereSphere(ref a, ref b,
                out FPVector3 normal, out FPVector3 closestA, out FPVector3 closestB);

            Assert.AreEqual(5.0f, dist.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, closestA.x.ToFloat(), EPSILON);
            Assert.AreEqual(7.0f, closestB.x.ToFloat(), EPSILON);
        }

        [Test]
        public void DistanceSphereSphere_Overlapping_ReturnsNegative()
        {
            var a = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(8, 0, 0));

            FP64 dist = CollisionTests.DistanceSphereSphere(ref a, ref b,
                out FPVector3 normal, out _, out _);

            Assert.IsTrue(dist.ToFloat() < 0.0f);
            Assert.AreEqual(-2.0f, dist.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void DistanceSphereSphere_Touching_ReturnsZero()
        {
            var a = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(10, 0, 0));

            FP64 dist = CollisionTests.DistanceSphereSphere(ref a, ref b,
                out _, out _, out _);

            Assert.AreEqual(0.0f, dist.ToFloat(), EPSILON);
        }

        [Test]
        public void DistanceSphereSphere_Coincident_ReturnsNegativeRadiusSum()
        {
            var a = new FPSphereShape(FP64.FromFloat(3.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(3.0f), FPVector3.Zero);

            FP64 dist = CollisionTests.DistanceSphereSphere(ref a, ref b,
                out FPVector3 normal, out _, out _);

            Assert.AreEqual(-6.0f, dist.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, normal.y.ToFloat(), EPSILON);
        }

        #endregion

        #region Sphere-Box

        [Test]
        public void DistanceSphereBox_Separated_ReturnsPositive()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(1.0f), new FPVector3(10, 0, 0));
            var box = new FPBoxShape(new FPVector3(3, 3, 3), FPVector3.Zero);

            FP64 dist = CollisionTests.DistanceSphereBox(ref sphere, ref box,
                out FPVector3 normal, out _, out _);

            Assert.AreEqual(6.0f, dist.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void DistanceSphereBox_Overlapping_ReturnsNegative()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(4, 0, 0));
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);

            FP64 dist = CollisionTests.DistanceSphereBox(ref sphere, ref box,
                out _, out _, out _);

            Assert.IsTrue(dist.ToFloat() < 0.0f);
        }

        [Test]
        public void DistanceSphereBox_SphereInsideBox_ReturnsNegative()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(1.0f), new FPVector3(2, 0, 0));
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);

            FP64 dist = CollisionTests.DistanceSphereBox(ref sphere, ref box,
                out FPVector3 normal, out _, out _);

            Assert.IsTrue(dist.ToFloat() < 0.0f);
        }

        #endregion

        #region Sphere-Capsule

        [Test]
        public void DistanceSphereCapsule_Separated_ReturnsPositive()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(1.0f), new FPVector3(10, 0, 0));
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);

            FP64 dist = CollisionTests.DistanceSphereCapsule(ref sphere, ref capsule,
                out FPVector3 normal, out _, out _);

            Assert.AreEqual(8.0f, dist.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void DistanceSphereCapsule_Overlapping_ReturnsNegative()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(2, 0, 0));
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(2.0f), FPVector3.Zero);

            FP64 dist = CollisionTests.DistanceSphereCapsule(ref sphere, ref capsule,
                out _, out _, out _);

            Assert.IsTrue(dist.ToFloat() < 0.0f);
        }

        #endregion

        #region Capsule-Capsule

        [Test]
        public void DistanceCapsuleCapsule_Separated_ReturnsPositive()
        {
            var ca = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(1.0f),
                FPVector3.Zero);
            var cb = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(1.0f),
                new FPVector3(10, 0, 0));

            FP64 dist = CollisionTests.DistanceCapsuleCapsule(ref ca, ref cb,
                out FPVector3 normal, out _, out _);

            Assert.AreEqual(8.0f, dist.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void DistanceCapsuleCapsule_Overlapping_ReturnsNegative()
        {
            var ca = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(2.0f),
                FPVector3.Zero);
            var cb = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(2.0f),
                new FPVector3(3, 0, 0));

            FP64 dist = CollisionTests.DistanceCapsuleCapsule(ref ca, ref cb,
                out _, out _, out _);

            Assert.IsTrue(dist.ToFloat() < 0.0f);
        }

        #endregion

        #region Box-Box

        [Test]
        public void DistanceBoxBox_Separated_ReturnsPositive()
        {
            var a = new FPBoxShape(new FPVector3(2, 2, 2), FPVector3.Zero);
            var b = new FPBoxShape(new FPVector3(2, 2, 2), new FPVector3(10, 0, 0));

            FP64 dist = CollisionTests.DistanceBoxBox(ref a, ref b,
                out FPVector3 normal, out _, out _);

            Assert.AreEqual(6.0f, dist.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void DistanceBoxBox_Overlapping_ReturnsNegative()
        {
            var a = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            var b = new FPBoxShape(new FPVector3(5, 5, 5), new FPVector3(8, 0, 0));

            FP64 dist = CollisionTests.DistanceBoxBox(ref a, ref b,
                out _, out _, out _);

            Assert.IsTrue(dist.ToFloat() < 0.0f);
            Assert.AreEqual(-2.0f, dist.ToFloat(), EPSILON);
        }

        [Test]
        public void DistanceBoxBox_Touching_ReturnsZero()
        {
            var a = new FPBoxShape(new FPVector3(3, 3, 3), FPVector3.Zero);
            var b = new FPBoxShape(new FPVector3(3, 3, 3), new FPVector3(6, 0, 0));

            FP64 dist = CollisionTests.DistanceBoxBox(ref a, ref b,
                out _, out _, out _);

            Assert.AreEqual(0.0f, dist.ToFloat(), EPSILON);
        }

        #endregion

        #region Box-Capsule

        [Test]
        public void DistanceBoxCapsule_Separated_ReturnsPositive()
        {
            var box = new FPBoxShape(new FPVector3(2, 2, 2), FPVector3.Zero);
            var capsule = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(1.0f),
                new FPVector3(10, 0, 0));

            FP64 dist = CollisionTests.DistanceBoxCapsule(ref box, ref capsule,
                out FPVector3 normal, out _, out _);

            Assert.AreEqual(7.0f, dist.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void DistanceBoxCapsule_Overlapping_ReturnsNegative()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            var capsule = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(2.0f),
                new FPVector3(5, 0, 0));

            FP64 dist = CollisionTests.DistanceBoxCapsule(ref box, ref capsule,
                out _, out _, out _);

            Assert.IsTrue(dist.ToFloat() < 0.0f);
        }

        #endregion

        #region Dispatch

        [Test]
        public void Dispatch_SphereSphere_Separated()
        {
            var a = FPCollider.FromSphere(new FPSphereShape(FP64.FromFloat(1.0f), FPVector3.Zero));
            var b = FPCollider.FromSphere(new FPSphereShape(FP64.FromFloat(1.0f), new FPVector3(10, 0, 0)));

            FP64 dist = NarrowphaseDispatch.Distance(ref a, ref b,
                out FPVector3 normal, out _, out _);

            Assert.AreEqual(8.0f, dist.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void Dispatch_BoxSphere_Flipped()
        {
            var a = FPCollider.FromBox(new FPBoxShape(new FPVector3(2, 2, 2), FPVector3.Zero));
            var b = FPCollider.FromSphere(new FPSphereShape(FP64.FromFloat(1.0f), new FPVector3(10, 0, 0)));

            FP64 distAB = NarrowphaseDispatch.Distance(ref a, ref b,
                out FPVector3 normalAB, out _, out _);

            FP64 distBA = NarrowphaseDispatch.Distance(ref b, ref a,
                out FPVector3 normalBA, out _, out _);

            Assert.AreEqual(distAB.ToFloat(), distBA.ToFloat(), EPSILON);
            Assert.AreEqual(normalAB.x.ToFloat(), -normalBA.x.ToFloat(), EPSILON);
        }

        [Test]
        public void Dispatch_MeshMesh_ReturnsMaxValue()
        {
            var meshShape = new FPMeshShape();
            var a = FPCollider.FromMesh(meshShape);
            var b = FPCollider.FromMesh(meshShape);

            FP64 dist = NarrowphaseDispatch.Distance(ref a, ref b,
                out _, out _, out _);

            Assert.AreEqual(FP64.MaxValue, dist);
        }

        #endregion

        #region Consistency

        [Test]
        public void DistanceSphereSphere_ConsistentWithCollision()
        {
            var a = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(8, 0, 0));

            bool hit = CollisionTests.SphereSphere(ref a, ref b, out FPContact contact);
            FP64 dist = CollisionTests.DistanceSphereSphere(ref a, ref b,
                out FPVector3 normal, out _, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(-contact.depth.ToFloat(), dist.ToFloat(), EPSILON);
            Assert.AreEqual(contact.normal.x.ToFloat(), normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void DistanceSphereBox_ConsistentWithCollision()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(6, 0, 0));
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);

            bool hit = CollisionTests.SphereBox(ref sphere, ref box, out FPContact contact);
            FP64 dist = CollisionTests.DistanceSphereBox(ref sphere, ref box,
                out FPVector3 normal, out _, out _);

            Assert.IsTrue(hit);
            Assert.IsTrue(dist.ToFloat() < 0.0f);
            Assert.AreEqual(contact.normal.x.ToFloat(), -normal.x.ToFloat(), EPSILON);
        }

        #endregion

        #region Determinism

        [Test]
        public void Distance_Determinism_BitExact()
        {
            var a = new FPSphereShape(FP64.FromFloat(2.5f),
                new FPVector3(FP64.FromFloat(1.23f), FP64.FromFloat(4.56f), FP64.FromFloat(7.89f)));
            var b = new FPSphereShape(FP64.FromFloat(3.5f),
                new FPVector3(FP64.FromFloat(11.11f), FP64.FromFloat(2.22f), FP64.FromFloat(3.33f)));

            FP64 dist1 = CollisionTests.DistanceSphereSphere(ref a, ref b,
                out FPVector3 normal1, out FPVector3 closestA1, out FPVector3 closestB1);
            FP64 dist2 = CollisionTests.DistanceSphereSphere(ref a, ref b,
                out FPVector3 normal2, out FPVector3 closestA2, out FPVector3 closestB2);

            Assert.AreEqual(dist1.RawValue, dist2.RawValue);
            Assert.AreEqual(normal1.x.RawValue, normal2.x.RawValue);
            Assert.AreEqual(normal1.y.RawValue, normal2.y.RawValue);
            Assert.AreEqual(normal1.z.RawValue, normal2.z.RawValue);
            Assert.AreEqual(closestA1.x.RawValue, closestA2.x.RawValue);
            Assert.AreEqual(closestB1.x.RawValue, closestB2.x.RawValue);
        }

        #endregion
    }
}
