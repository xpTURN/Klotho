using System;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics.Tests

{
    [TestFixture]
    public class FPBoxShapeTests
    {
        private const float EPSILON = 0.01f;

        #region Constructor

        [Test]
        public void Constructor_SetsAllFields()
        {
            var half = new FPVector3(1, 2, 3);
            var pos = new FPVector3(10, 20, 30);
            var box = new FPBoxShape(half, pos, FPQuaternion.Identity);

            Assert.AreEqual(1.0f, box.halfExtents.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, box.halfExtents.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, box.halfExtents.z.ToFloat(), EPSILON);
            Assert.AreEqual(10.0f, box.position.x.ToFloat(), EPSILON);
            Assert.AreEqual(20.0f, box.position.y.ToFloat(), EPSILON);
            Assert.AreEqual(30.0f, box.position.z.ToFloat(), EPSILON);
        }

        [Test]
        public void Constructor_TwoArgs_DefaultsToIdentityRotation()
        {
            var box = new FPBoxShape(new FPVector3(1, 1, 1), FPVector3.Zero);
            Assert.IsTrue(box.IsAxisAligned);
        }

        #endregion

        #region Properties

        [Test]
        public void Size_ReturnsTwiceHalfExtents()
        {
            var box = new FPBoxShape(new FPVector3(2, 3, 4), FPVector3.Zero);
            Assert.AreEqual(4.0f, box.size.x.ToFloat(), EPSILON);
            Assert.AreEqual(6.0f, box.size.y.ToFloat(), EPSILON);
            Assert.AreEqual(8.0f, box.size.z.ToFloat(), EPSILON);
        }

        [Test]
        public void IsAxisAligned_Identity_ReturnsTrue()
        {
            var box = new FPBoxShape(new FPVector3(1, 1, 1), FPVector3.Zero, FPQuaternion.Identity);
            Assert.IsTrue(box.IsAxisAligned);
        }

        [Test]
        public void IsAxisAligned_Rotated_ReturnsFalse()
        {
            var rot = FPQuaternion.Euler(FP64.Zero, FP64.FromInt(45), FP64.Zero);
            var box = new FPBoxShape(new FPVector3(1, 1, 1), FPVector3.Zero, rot);
            Assert.IsFalse(box.IsAxisAligned);
        }

        #endregion

        #region GetWorldBounds

        [Test]
        public void GetWorldBounds_AxisAligned_MatchesHalfExtents()
        {
            var box = new FPBoxShape(new FPVector3(2, 3, 4), new FPVector3(10, 20, 30));
            var bounds = box.GetWorldBounds();

            Assert.AreEqual(10.0f, bounds.center.x.ToFloat(), EPSILON);
            Assert.AreEqual(20.0f, bounds.center.y.ToFloat(), EPSILON);
            Assert.AreEqual(30.0f, bounds.center.z.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, bounds.extents.x.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, bounds.extents.y.ToFloat(), EPSILON);
            Assert.AreEqual(4.0f, bounds.extents.z.ToFloat(), EPSILON);
        }

        [Test]
        public void GetWorldBounds_Rotated_ExpandedCorrectly()
        {
            var rot = FPQuaternion.Euler(FP64.Zero, FP64.FromInt(90), FP64.Zero);
            var box = new FPBoxShape(new FPVector3(3, 1, 1), FPVector3.Zero, rot);
            var bounds = box.GetWorldBounds();

            // Y-axis 90 deg rotation: X to Z, Z to -X
            Assert.AreEqual(1.0f, bounds.extents.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, bounds.extents.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, bounds.extents.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Contains

        [Test]
        public void Contains_Center_ReturnsTrue()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            Assert.IsTrue(box.Contains(FPVector3.Zero));
        }

        [Test]
        public void Contains_InsidePoint_ReturnsTrue()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            Assert.IsTrue(box.Contains(new FPVector3(3, 3, 3)));
        }

        [Test]
        public void Contains_OnSurface_ReturnsTrue()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            Assert.IsTrue(box.Contains(new FPVector3(5, 0, 0)));
        }

        [Test]
        public void Contains_Outside_ReturnsFalse()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            Assert.IsFalse(box.Contains(new FPVector3(6, 0, 0)));
        }

        [Test]
        public void Contains_RotatedBox_InsidePoint_ReturnsTrue()
        {
            // Y-axis 45 deg rotation, halfExtents (5,1,1)
            var rot = FPQuaternion.Euler(FP64.Zero, FP64.FromInt(45), FP64.Zero);
            var box = new FPBoxShape(new FPVector3(5, 1, 1), FPVector3.Zero, rot);

            // Point in rotated X-axis direction (diagonal in world space)
            var point = rot * new FPVector3(3, 0, 0);
            Assert.IsTrue(box.Contains(point));
        }

        [Test]
        public void Contains_RotatedBox_OutsidePoint_ReturnsFalse()
        {
            var rot = FPQuaternion.Euler(FP64.Zero, FP64.FromInt(45), FP64.Zero);
            var box = new FPBoxShape(new FPVector3(2, 2, 2), FPVector3.Zero, rot);

            Assert.IsFalse(box.Contains(new FPVector3(10, 0, 0)));
        }

        #endregion

        #region ClosestPoint

        [Test]
        public void ClosestPoint_InsidePoint_ReturnsSamePoint()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            var result = box.ClosestPoint(new FPVector3(2, 2, 2));

            Assert.AreEqual(2.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_OutsidePoint_ClampsToSurface()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            var result = box.ClosestPoint(new FPVector3(10, 0, 0));

            Assert.AreEqual(5.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.z.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPoint_RotatedBox_ClampsCorrectly()
        {
            var rot = FPQuaternion.Euler(FP64.Zero, FP64.FromInt(90), FP64.Zero);
            var box = new FPBoxShape(new FPVector3(3, 1, 1), FPVector3.Zero, rot);

            // Y-axis 90 deg rotation: local X to world Z
            // A far point in world Z direction should be clamped to halfExtents.x=3 along the rotated X axis
            var result = box.ClosestPoint(new FPVector3(0, 0, 10));
            Assert.AreEqual(0.0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, result.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, result.z.ToFloat(), EPSILON);
        }

        #endregion

        #region SqrDistance

        [Test]
        public void SqrDistance_InsidePoint_ReturnsZero()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            Assert.AreEqual(0.0f, box.SqrDistance(new FPVector3(2, 0, 0)).ToFloat(), EPSILON);
        }

        [Test]
        public void SqrDistance_OutsidePoint_ReturnsCorrect()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            // Distance to surface = 10-5 = 5, squared = 25
            Assert.AreEqual(25.0f, box.SqrDistance(new FPVector3(10, 0, 0)).ToFloat(), EPSILON);
        }

        #endregion

        #region GetVertices

        [Test]
        public void GetVertices_AxisAligned_Returns8Corners()
        {
            var box = new FPBoxShape(new FPVector3(1, 1, 1), FPVector3.Zero);
            Span<FPVector3> verts = stackalloc FPVector3[8];
            box.GetVertices(verts);

            // All vertex coordinates must be +/-1
            for (int i = 0; i < 8; i++)
            {
                Assert.AreEqual(1.0f, FP64.Abs(verts[i].x).ToFloat(), EPSILON);
                Assert.AreEqual(1.0f, FP64.Abs(verts[i].y).ToFloat(), EPSILON);
                Assert.AreEqual(1.0f, FP64.Abs(verts[i].z).ToFloat(), EPSILON);
            }
        }

        [Test]
        public void GetVertices_WithOffset_AllContainedInBounds()
        {
            var box = new FPBoxShape(new FPVector3(2, 3, 4), new FPVector3(10, 20, 30));
            var bounds = box.GetWorldBounds();
            Span<FPVector3> verts = stackalloc FPVector3[8];
            box.GetVertices(verts);

            for (int i = 0; i < 8; i++)
                Assert.IsTrue(bounds.Contains(verts[i]), $"Vertex {i} not in bounds");
        }

        #endregion

        #region GetAxes

        [Test]
        public void GetAxes_Identity_ReturnsWorldAxes()
        {
            var box = new FPBoxShape(new FPVector3(1, 1, 1), FPVector3.Zero);
            box.GetAxes(out var axisX, out var axisY, out var axisZ);

            Assert.AreEqual(1.0f, axisX.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, axisX.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, axisX.z.ToFloat(), EPSILON);

            Assert.AreEqual(0.0f, axisY.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, axisY.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, axisY.z.ToFloat(), EPSILON);

            Assert.AreEqual(0.0f, axisZ.x.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, axisZ.y.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, axisZ.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_Same_ReturnsTrue()
        {
            var a = new FPBoxShape(new FPVector3(1, 2, 3), new FPVector3(4, 5, 6));
            var b = new FPBoxShape(new FPVector3(1, 2, 3), new FPVector3(4, 5, 6));
            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
        }

        [Test]
        public void Equality_Different_ReturnsFalse()
        {
            var a = new FPBoxShape(new FPVector3(1, 2, 3), new FPVector3(4, 5, 6));
            var b = new FPBoxShape(new FPVector3(1, 2, 9), new FPVector3(4, 5, 6));
            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_ClosestPoint_ConsistentAcrossRuns()
        {
            var rot = FPQuaternion.Euler(
                FP64.FromFloat(30.0f), FP64.FromFloat(45.0f), FP64.FromFloat(60.0f)
            );
            var box = new FPBoxShape(
                new FPVector3(FP64.FromFloat(2.5f), FP64.FromFloat(3.0f), FP64.FromFloat(1.5f)),
                new FPVector3(FP64.FromFloat(10.0f), FP64.FromFloat(-5.0f), FP64.FromFloat(7.0f)),
                rot
            );
            var point = new FPVector3(FP64.FromFloat(15.0f), FP64.FromFloat(0.0f), FP64.FromFloat(12.0f));

            var first = box.ClosestPoint(point);
            for (int i = 0; i < 100; i++)
            {
                var result = box.ClosestPoint(point);
                Assert.AreEqual(first.x.RawValue, result.x.RawValue);
                Assert.AreEqual(first.y.RawValue, result.y.RawValue);
                Assert.AreEqual(first.z.RawValue, result.z.RawValue);
            }
        }

        #endregion
    }
}
