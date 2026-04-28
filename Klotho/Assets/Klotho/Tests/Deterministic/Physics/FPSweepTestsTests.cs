using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPSweepTestsTests
    {
        const float EPSILON = 0.05f;

        #region SweptSphereSphere

        [Test]
        public void SphereSphere_HeadOn_CorrectTOI()
        {
            // Two spheres (radius=1), distance 8, approaching at relative speed 100.
            // Separation distance = 10 - 2 = 8. TOI = 8 / 100 = 0.08
            FPVector3 posA = new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.Zero);
            FPVector3 posB = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            FPVector3 velA = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero);
            FPVector3 velB = FPVector3.Zero;
            FP64 dt = FP64.FromFloat(0.5f);

            bool hit = FPSweepTests.SweptSphereSphere(
                posA, FP64.One, velA, posB, FP64.One, velB,
                dt, out FP64 toi, out FPVector3 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.08f, toi.ToFloat(), EPSILON);
            Assert.AreEqual(1f, normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, normal.y.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereSphere_Miss_ReturnsFalse()
        {
            // Spheres move in parallel and do not intersect
            FPVector3 posA = new FPVector3(FP64.FromInt(-5), FP64.FromInt(5), FP64.Zero);
            FPVector3 posB = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            FPVector3 velA = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero);

            bool hit = FPSweepTests.SweptSphereSphere(
                posA, FP64.One, velA, posB, FP64.One, FPVector3.Zero,
                FP64.One, out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void SphereSphere_Diverging_ReturnsFalse()
        {
            // Moving in directions away from each other
            FPVector3 posA = new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.Zero);
            FPVector3 posB = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            FPVector3 velA = new FPVector3(FP64.FromInt(-100), FP64.Zero, FP64.Zero);

            bool hit = FPSweepTests.SweptSphereSphere(
                posA, FP64.One, velA, posB, FP64.One, FPVector3.Zero,
                FP64.One, out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void SphereSphere_OneStatic_CorrectTOI()
        {
            // A at origin, B at rest at x=10. A moves with speed=50.
            // Separation distance = 10 - 2 = 8. TOI = 8 / 50 = 0.16
            FPVector3 posA = FPVector3.Zero;
            FPVector3 posB = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero);
            FPVector3 velA = new FPVector3(FP64.FromInt(50), FP64.Zero, FP64.Zero);

            bool hit = FPSweepTests.SweptSphereSphere(
                posA, FP64.One, velA, posB, FP64.One, FPVector3.Zero,
                FP64.One, out FP64 toi, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.16f, toi.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereSphere_TOIBeyondDt_ReturnsFalse()
        {
            // TOI = 0.16 but dt = 0.1
            FPVector3 posA = FPVector3.Zero;
            FPVector3 posB = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero);
            FPVector3 velA = new FPVector3(FP64.FromInt(50), FP64.Zero, FP64.Zero);

            bool hit = FPSweepTests.SweptSphereSphere(
                posA, FP64.One, velA, posB, FP64.One, FPVector3.Zero,
                FP64.FromFloat(0.1f), out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void SphereSphere_AlreadyOverlapping_ZeroTOI()
        {
            // Overlapping spheres
            FPVector3 posA = FPVector3.Zero;
            FPVector3 posB = new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
            FPVector3 velA = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero);

            bool hit = FPSweepTests.SweptSphereSphere(
                posA, FP64.One, velA, posB, FP64.One, FPVector3.Zero,
                FP64.One, out FP64 toi, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(0f, toi.ToFloat(), EPSILON);
        }

        #endregion

        #region SweptSphereBox

        [Test]
        public void SphereBox_FaceHit_CorrectTOI()
        {
            // Sphere (x=-5) moves toward a box (halfExtent=1) centered at origin.
            // Sphere radius=1. Expanded halfExtent = 2. Distance from sphere center to face = 5-2=3.
            // Speed=100 → TOI = 3/100 = 0.03
            var box = new FPBoxShape(
                new FPVector3(FP64.One, FP64.One, FP64.One), FPVector3.Zero);

            bool hit = FPSweepTests.SweptSphereBox(
                new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.Zero),
                FP64.One,
                new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero),
                ref box, FP64.One,
                out FP64 toi, out FPVector3 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.03f, toi.ToFloat(), EPSILON);
            // Normal must point from sphere toward box (positive x)
            Assert.AreEqual(1f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereBox_Miss_ReturnsFalse()
        {
            // Sphere moves parallel to the box face
            var box = new FPBoxShape(
                new FPVector3(FP64.One, FP64.One, FP64.One), FPVector3.Zero);

            bool hit = FPSweepTests.SweptSphereBox(
                new FPVector3(FP64.FromInt(-5), FP64.FromInt(5), FP64.Zero),
                FP64.One,
                new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero),
                ref box, FP64.One,
                out _, out _);

            Assert.IsFalse(hit);
        }

        [Test]
        public void SphereBox_RotatedBox_CorrectTOI()
        {
            // Rotate box 45 degrees around Z axis
            var box = new FPBoxShape(
                new FPVector3(FP64.One, FP64.One, FP64.One),
                FPVector3.Zero,
                FPQuaternion.AngleAxis(FP64.FromInt(45), FPVector3.Forward));

            // Sphere (x=-5) moves in +x direction
            bool hit = FPSweepTests.SweptSphereBox(
                new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.Zero),
                FP64.One,
                new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero),
                ref box, FP64.One,
                out FP64 toi, out _);

            Assert.IsTrue(hit);
            Assert.IsTrue(toi.ToFloat() > 0f);
            Assert.IsTrue(toi.ToFloat() < 0.1f);
        }

        #endregion

        #region SweptSphereCapsule

        [Test]
        public void SphereCapsule_HeadOn_CorrectTOI()
        {
            // Sphere (x=-5) moves toward capsule at origin (vertical, halfHeight=2, radius=1)
            // Combined radius = 1 + 1 = 2. Closest point on capsule segment to sphere = (0,0,0)
            // Distance = 5 - 2 = 3. Speed=100 → TOI = 3/100 = 0.03
            var capsule = new FPCapsuleShape(FP64.FromInt(2), FP64.One, FPVector3.Zero);

            bool hit = FPSweepTests.SweptSphereCapsule(
                new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.Zero),
                FP64.One,
                new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero),
                ref capsule, FP64.One,
                out FP64 toi, out FPVector3 normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.03f, toi.ToFloat(), EPSILON);
            Assert.AreEqual(1f, normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereCapsule_Endcap_CorrectTOI()
        {
            // Sphere above capsule, moving down toward end cap
            var capsule = new FPCapsuleShape(FP64.FromInt(2), FP64.One, FPVector3.Zero);
            // Capsule top point (0,2,0), sphere moves down from (0,8,0)
            // Combined radius = 2. Distance to endpoint center = 8 - 2 = 6. After applying radius: 6 - 2 = 4.
            // Speed = 100 → TOI = 4/100 = 0.04

            bool hit = FPSweepTests.SweptSphereCapsule(
                new FPVector3(FP64.Zero, FP64.FromInt(8), FP64.Zero),
                FP64.One,
                new FPVector3(FP64.Zero, FP64.FromInt(-100), FP64.Zero),
                ref capsule, FP64.One,
                out FP64 toi, out _);

            Assert.IsTrue(hit);
            Assert.AreEqual(0.04f, toi.ToFloat(), EPSILON);
        }

        [Test]
        public void SphereCapsule_Miss_ReturnsFalse()
        {
            var capsule = new FPCapsuleShape(FP64.FromInt(2), FP64.One, FPVector3.Zero);

            bool hit = FPSweepTests.SweptSphereCapsule(
                new FPVector3(FP64.FromInt(-5), FP64.FromInt(10), FP64.Zero),
                FP64.One,
                new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero),
                ref capsule, FP64.One,
                out _, out _);

            Assert.IsFalse(hit);
        }

        #endregion

        #region Determinism

        [Test]
        public void AllFunctions_BitExactDeterministic()
        {
            FPVector3 posA = new FPVector3(FP64.FromInt(-10), FP64.One, FP64.FromFloat(0.5f));
            FPVector3 velA = new FPVector3(FP64.FromInt(500), FP64.FromInt(-20), FP64.FromInt(10));
            FPVector3 posB = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            FP64 dt = FP64.FromFloat(0.02f);

            // SphereSphere
            FPSweepTests.SweptSphereSphere(posA, FP64.One, velA, posB, FP64.One, FPVector3.Zero,
                dt, out FP64 toi1a, out FPVector3 n1a);
            FPSweepTests.SweptSphereSphere(posA, FP64.One, velA, posB, FP64.One, FPVector3.Zero,
                dt, out FP64 toi1b, out FPVector3 n1b);
            Assert.AreEqual(toi1a.RawValue, toi1b.RawValue);
            Assert.AreEqual(n1a.x.RawValue, n1b.x.RawValue);
            Assert.AreEqual(n1a.y.RawValue, n1b.y.RawValue);

            // SphereBox
            var box = new FPBoxShape(new FPVector3(FP64.One, FP64.One, FP64.One), posB);
            FPSweepTests.SweptSphereBox(posA, FP64.One, velA, ref box, dt, out FP64 toi2a, out FPVector3 n2a);
            FPSweepTests.SweptSphereBox(posA, FP64.One, velA, ref box, dt, out FP64 toi2b, out FPVector3 n2b);
            Assert.AreEqual(toi2a.RawValue, toi2b.RawValue);
            Assert.AreEqual(n2a.x.RawValue, n2b.x.RawValue);

            // SphereCapsule
            var cap = new FPCapsuleShape(FP64.FromInt(2), FP64.One, posB);
            FPSweepTests.SweptSphereCapsule(posA, FP64.One, velA, ref cap, dt, out FP64 toi3a, out FPVector3 n3a);
            FPSweepTests.SweptSphereCapsule(posA, FP64.One, velA, ref cap, dt, out FP64 toi3b, out FPVector3 n3b);
            Assert.AreEqual(toi3a.RawValue, toi3b.RawValue);
            Assert.AreEqual(n3a.x.RawValue, n3b.x.RawValue);
        }

        #endregion
    }
}
