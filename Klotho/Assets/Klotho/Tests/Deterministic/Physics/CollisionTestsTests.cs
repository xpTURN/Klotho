using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests

{
    [TestFixture]
    public class CollisionTestsTests
    {
        private const float EPSILON = 0.05f;

        #region Sphere-Sphere

        [Test]
        public void SphereSphere_Overlapping_ReturnsContact()
        {
            var a = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(8, 0, 0));

            bool hit = CollisionTests.SphereSphere(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(2.0f, contact.depth.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, contact.normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, contact.normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, contact.normal.z.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereSphere_Touching_ReturnsContactWithZeroDepth()
        {
            var a = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(10, 0, 0));

            bool hit = CollisionTests.SphereSphere(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.0f, contact.depth.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereSphere_Separated_ReturnsFalse()
        {
            var a = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(20, 0, 0));

            bool hit = CollisionTests.SphereSphere(ref a, ref b, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void SphereSphere_Coincident_UsesUpNormal()
        {
            var a = new FPSphereShape(FP64.FromFloat(3.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(3.0f), FPVector3.Zero);

            bool hit = CollisionTests.SphereSphere(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.0f, contact.normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, contact.normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, contact.normal.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Sphere-Box

        [Test]
        public void SphereBox_Overlapping_ReturnsContact()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(6, 0, 0));
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);

            bool hit = CollisionTests.SphereBox(ref sphere, ref box, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
            Assert.AreEqual(1.0f, contact.normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereBox_Separated_ReturnsFalse()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(1.0f), new FPVector3(20, 0, 0));
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);

            bool hit = CollisionTests.SphereBox(ref sphere, ref box, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void SphereBox_SphereInsideBox_ReturnsContact()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(1.0f), new FPVector3(4, 0, 0));
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);

            bool hit = CollisionTests.SphereBox(ref sphere, ref box, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        #endregion

        #region Sphere-Capsule

        [Test]
        public void SphereCapsule_Overlapping_ReturnsContact()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(2, 0, 0));
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);

            bool hit = CollisionTests.SphereCapsule(ref sphere, ref capsule, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
            Assert.AreEqual(1.0f, contact.normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereCapsule_Separated_ReturnsFalse()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(1.0f), new FPVector3(20, 0, 0));
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);

            bool hit = CollisionTests.SphereCapsule(ref sphere, ref capsule, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void SphereCapsule_NearEndpoint_ReturnsContact()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(0, 5, 0));
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);

            bool hit = CollisionTests.SphereCapsule(ref sphere, ref capsule, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.0f, contact.normal.x.ToFloat(), EPSILON);
            Assert.IsTrue(contact.normal.y.ToFloat() > 0.5f);
        }

        #endregion

        #region Capsule-Capsule

        [Test]
        public void CapsuleCapsule_ParallelOverlapping_ReturnsContact()
        {
            var a = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            var b = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), new FPVector3(1, 0, 0));

            bool hit = CollisionTests.CapsuleCapsule(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        [Test]
        public void CapsuleCapsule_Separated_ReturnsFalse()
        {
            var a = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            var b = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), new FPVector3(20, 0, 0));

            bool hit = CollisionTests.CapsuleCapsule(ref a, ref b, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void CapsuleCapsule_Perpendicular_ReturnsContact()
        {
            var a = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            var rot = FPQuaternion.Euler(FP64.Zero, FP64.Zero, FP64.FromInt(90));
            var b = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero, rot);

            bool hit = CollisionTests.CapsuleCapsule(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
        }

        #endregion

        #region Box-Box

        [Test]
        public void BoxBox_Overlapping_ReturnsContact()
        {
            var a = new FPBoxShape(new FPVector3(2, 2, 2), FPVector3.Zero);
            var b = new FPBoxShape(new FPVector3(2, 2, 2), new FPVector3(3, 0, 0));

            bool hit = CollisionTests.BoxBox(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(1.0f, contact.depth.ToFloat(), EPSILON);
        }

        [Test]
        public void BoxBox_Separated_ReturnsFalse()
        {
            var a = new FPBoxShape(new FPVector3(2, 2, 2), FPVector3.Zero);
            var b = new FPBoxShape(new FPVector3(2, 2, 2), new FPVector3(10, 0, 0));

            bool hit = CollisionTests.BoxBox(ref a, ref b, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void BoxBox_RotatedSeparated_ReturnsFalse()
        {
            var rot = FPQuaternion.Euler(FP64.Zero, FP64.FromInt(45), FP64.Zero);
            var a = new FPBoxShape(new FPVector3(1, 1, 1), FPVector3.Zero, rot);
            var b = new FPBoxShape(new FPVector3(1, 1, 1), new FPVector3(5, 0, 0));

            bool hit = CollisionTests.BoxBox(ref a, ref b, out _);

            Assert.IsFalse(hit);
        }

        #endregion

        #region Box-Capsule

        [Test]
        public void BoxCapsule_Overlapping_ReturnsContact()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), new FPVector3(5, 0, 0));

            bool hit = CollisionTests.BoxCapsule(ref box, ref capsule, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        [Test]
        public void BoxCapsule_Separated_ReturnsFalse()
        {
            var box = new FPBoxShape(new FPVector3(2, 2, 2), FPVector3.Zero);
            var capsule = new FPCapsuleShape(FP64.FromFloat(1.0f), FP64.FromFloat(0.5f), new FPVector3(20, 0, 0));

            bool hit = CollisionTests.BoxCapsule(ref box, ref capsule, out _);

            Assert.IsFalse(hit);
        }

        #endregion

        #region Edge Cases

        [Test]
        public void SphereBox_CornerOverlap_ReturnsContact()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(6, 6, 0));
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);

            bool hit = CollisionTests.SphereBox(ref sphere, ref box, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        [Test]
        public void BoxBox_RotatedOverlapping_ReturnsContact()
        {
            var rot = FPQuaternion.Euler(FP64.Zero, FP64.FromInt(45), FP64.Zero);
            var a = new FPBoxShape(new FPVector3(2, 2, 2), FPVector3.Zero, rot);
            var b = new FPBoxShape(new FPVector3(2, 2, 2), new FPVector3(2, 0, 0));

            bool hit = CollisionTests.BoxBox(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        [Test]
        public void SphereSphere_Symmetry_NormalFlips()
        {
            var a = new FPSphereShape(FP64.FromFloat(3.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(3.0f), new FPVector3(4, 0, 0));

            CollisionTests.SphereSphere(ref a, ref b, out FPContact contactAB);
            CollisionTests.SphereSphere(ref b, ref a, out FPContact contactBA);

            Assert.AreEqual(contactAB.depth.RawValue, contactBA.depth.RawValue);
            Assert.AreEqual(contactAB.normal.x.RawValue, -contactBA.normal.x.RawValue);
        }

        [Test]
        public void CapsuleCapsule_Coincident_UsesUpNormal()
        {
            var a = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);
            var b = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);

            bool hit = CollisionTests.CapsuleCapsule(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.0f, contact.normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, contact.normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, contact.normal.z.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereCapsule_Coincident_UsesUpNormal()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(1.0f), FPVector3.Zero);
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);

            bool hit = CollisionTests.SphereCapsule(ref sphere, ref capsule, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.0f, contact.normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, contact.normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, contact.normal.z.ToFloat(), EPSILON);
        }

        [Test]
        public void BoxBox_Touching_ReturnsContactWithZeroDepth()
        {
            var a = new FPBoxShape(new FPVector3(2, 2, 2), FPVector3.Zero);
            var b = new FPBoxShape(new FPVector3(2, 2, 2), new FPVector3(4, 0, 0));

            bool hit = CollisionTests.BoxBox(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.0f, contact.depth.ToFloat(), EPSILON);
        }

        [Test]
        public void BoxCapsule_CapsuleAlongBoxEdge_ReturnsContact()
        {
            var box = new FPBoxShape(new FPVector3(3, 3, 3), FPVector3.Zero);
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(0.5f), new FPVector3(3, 0, 0));

            bool hit = CollisionTests.BoxCapsule(ref box, ref capsule, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_SphereSphere_ConsistentAcrossRuns()
        {
            var a = new FPSphereShape(FP64.FromFloat(3.5f), new FPVector3(FP64.FromFloat(1.23f), FP64.FromFloat(-4.56f), FP64.FromFloat(7.89f)));
            var b = new FPSphereShape(FP64.FromFloat(2.5f), new FPVector3(FP64.FromFloat(4.0f), FP64.FromFloat(-3.0f), FP64.FromFloat(8.0f)));

            CollisionTests.SphereSphere(ref a, ref b, out FPContact first);
            for (int i = 0; i < 100; i++)
            {
                CollisionTests.SphereSphere(ref a, ref b, out FPContact result);
                AssertContactEqual(first, result);
            }
        }

        [Test]
        public void Determinism_SphereBox_ConsistentAcrossRuns()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(2.5f), new FPVector3(FP64.FromFloat(6.3f), FP64.FromFloat(1.2f), FP64.FromFloat(-0.5f)));
            var box = new FPBoxShape(
                new FPVector3(FP64.FromFloat(3.0f), FP64.FromFloat(2.0f), FP64.FromFloat(4.0f)),
                new FPVector3(FP64.FromFloat(4.0f), FP64.FromFloat(0.0f), FP64.FromFloat(0.0f))
            );

            CollisionTests.SphereBox(ref sphere, ref box, out FPContact first);
            for (int i = 0; i < 100; i++)
            {
                CollisionTests.SphereBox(ref sphere, ref box, out FPContact result);
                AssertContactEqual(first, result);
            }
        }

        [Test]
        public void Determinism_SphereCapsule_ConsistentAcrossRuns()
        {
            var sphere = new FPSphereShape(FP64.FromFloat(1.5f), new FPVector3(FP64.FromFloat(1.0f), FP64.FromFloat(2.0f), FP64.FromFloat(0.0f)));
            var capsule = new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero);

            CollisionTests.SphereCapsule(ref sphere, ref capsule, out FPContact first);
            for (int i = 0; i < 100; i++)
            {
                CollisionTests.SphereCapsule(ref sphere, ref capsule, out FPContact result);
                AssertContactEqual(first, result);
            }
        }

        [Test]
        public void Determinism_CapsuleCapsule_ConsistentAcrossRuns()
        {
            var rot = FPQuaternion.Euler(FP64.FromFloat(30.0f), FP64.FromFloat(45.0f), FP64.Zero);
            var a = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(0.5f), new FPVector3(FP64.FromFloat(0.5f), FP64.FromFloat(1.0f), FP64.Zero));
            var b = new FPCapsuleShape(FP64.FromFloat(2.5f), FP64.FromFloat(0.8f), new FPVector3(FP64.FromFloat(1.5f), FP64.FromFloat(0.0f), FP64.FromFloat(0.5f)), rot);

            CollisionTests.CapsuleCapsule(ref a, ref b, out FPContact first);
            for (int i = 0; i < 100; i++)
            {
                CollisionTests.CapsuleCapsule(ref a, ref b, out FPContact result);
                AssertContactEqual(first, result);
            }
        }

        [Test]
        public void Determinism_BoxBox_ConsistentAcrossRuns()
        {
            var rotA = FPQuaternion.Euler(FP64.FromFloat(15.0f), FP64.FromFloat(30.0f), FP64.FromFloat(45.0f));
            var a = new FPBoxShape(
                new FPVector3(FP64.FromFloat(2.0f), FP64.FromFloat(1.5f), FP64.FromFloat(3.0f)),
                new FPVector3(FP64.FromFloat(0.0f), FP64.FromFloat(0.0f), FP64.FromFloat(0.0f)),
                rotA
            );
            var b = new FPBoxShape(
                new FPVector3(FP64.FromFloat(1.5f), FP64.FromFloat(2.0f), FP64.FromFloat(1.0f)),
                new FPVector3(FP64.FromFloat(2.0f), FP64.FromFloat(0.5f), FP64.FromFloat(0.0f))
            );

            CollisionTests.BoxBox(ref a, ref b, out FPContact first);
            for (int i = 0; i < 100; i++)
            {
                CollisionTests.BoxBox(ref a, ref b, out FPContact result);
                AssertContactEqual(first, result);
            }
        }

        [Test]
        public void Determinism_BoxCapsule_ConsistentAcrossRuns()
        {
            var box = new FPBoxShape(
                new FPVector3(FP64.FromFloat(3.0f), FP64.FromFloat(2.0f), FP64.FromFloat(4.0f)),
                FPVector3.Zero
            );
            var capsule = new FPCapsuleShape(FP64.FromFloat(2.0f), FP64.FromFloat(1.0f), new FPVector3(FP64.FromFloat(3.5f), FP64.FromFloat(0.0f), FP64.FromFloat(0.0f)));

            CollisionTests.BoxCapsule(ref box, ref capsule, out FPContact first);
            for (int i = 0; i < 100; i++)
            {
                CollisionTests.BoxCapsule(ref box, ref capsule, out FPContact result);
                AssertContactEqual(first, result);
            }
        }

        private static void AssertContactEqual(FPContact expected, FPContact actual)
        {
            Assert.AreEqual(expected.point.x.RawValue, actual.point.x.RawValue);
            Assert.AreEqual(expected.point.y.RawValue, actual.point.y.RawValue);
            Assert.AreEqual(expected.point.z.RawValue, actual.point.z.RawValue);
            Assert.AreEqual(expected.normal.x.RawValue, actual.normal.x.RawValue);
            Assert.AreEqual(expected.normal.y.RawValue, actual.normal.y.RawValue);
            Assert.AreEqual(expected.normal.z.RawValue, actual.normal.z.RawValue);
            Assert.AreEqual(expected.depth.RawValue, actual.depth.RawValue);
        }

        #endregion
    }
}
