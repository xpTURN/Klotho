using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry.Tests
{
    [TestFixture]
    public class FPContactTests
    {
        private const float EPSILON = 0.01f;

        #region Constructor

        [Test]
        public void Constructor_SetsAllFields()
        {
            var point = new FPVector3(1, 2, 3);
            var normal = new FPVector3(0, 1, 0);
            var depth = FP64.FromFloat(0.5f);

            var c = new FPContact(point, normal, depth, 10, 20);

            Assert.AreEqual(1.0f, c.point.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, c.point.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, c.point.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, c.normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(1.0f, c.normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, c.normal.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.5f, c.depth.ToFloat(), EPSILON);
            Assert.AreEqual(10, c.entityA);
            Assert.AreEqual(20, c.entityB);
        }

        [Test]
        public void Default_AllZero()
        {
            var c = default(FPContact);

            Assert.AreEqual(FPVector3.Zero, c.point);
            Assert.AreEqual(FPVector3.Zero, c.normal);
            Assert.AreEqual(FP64.Zero, c.depth);
            Assert.AreEqual(0, c.entityA);
            Assert.AreEqual(0, c.entityB);
        }

        #endregion

        #region Flipped

        [Test]
        public void Flipped_ReversesNormalAndSwapsEntities()
        {
            var c = new FPContact(
                new FPVector3(1, 2, 3),
                new FPVector3(0, 1, 0),
                FP64.FromFloat(0.5f),
                10, 20
            );

            var f = c.Flipped();

            Assert.AreEqual(1.0f, f.point.x.ToFloat(), EPSILON);
            Assert.AreEqual(2.0f, f.point.y.ToFloat(), EPSILON);
            Assert.AreEqual(3.0f, f.point.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, f.normal.x.ToFloat(), EPSILON);
            Assert.AreEqual(-1.0f, f.normal.y.ToFloat(), EPSILON);
            Assert.AreEqual(0.0f, f.normal.z.ToFloat(), EPSILON);
            Assert.AreEqual(0.5f, f.depth.ToFloat(), EPSILON);
            Assert.AreEqual(20, f.entityA);
            Assert.AreEqual(10, f.entityB);
        }

        [Test]
        public void Flipped_DepthUnchanged()
        {
            var c = new FPContact(
                FPVector3.Zero,
                new FPVector3(1, 0, 0),
                FP64.FromFloat(2.5f),
                1, 2
            );

            var f = c.Flipped();
            Assert.AreEqual(c.depth.RawValue, f.depth.RawValue);
        }

        [Test]
        public void Flipped_DoubleFlip_RestoresOriginal()
        {
            var c = new FPContact(
                new FPVector3(5, -3, 7),
                new FPVector3(0, 0, 1),
                FP64.FromFloat(1.0f),
                42, 99
            );

            var restored = c.Flipped().Flipped();
            Assert.AreEqual(c, restored);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_Same_ReturnsTrue()
        {
            var a = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);
            var b = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
            Assert.IsTrue(a.Equals(b));
        }

        [Test]
        public void Equality_DifferentPoint_ReturnsFalse()
        {
            var a = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);
            var b = new FPContact(new FPVector3(9, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);

            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
        }

        [Test]
        public void Equality_DifferentNormal_ReturnsFalse()
        {
            var a = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);
            var b = new FPContact(new FPVector3(1, 2, 3), new FPVector3(1, 0, 0), FP64.FromFloat(0.5f), 10, 20);

            Assert.IsFalse(a == b);
        }

        [Test]
        public void Equality_DifferentDepth_ReturnsFalse()
        {
            var a = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);
            var b = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(1.0f), 10, 20);

            Assert.IsFalse(a == b);
        }

        [Test]
        public void Equality_DifferentEntityA_ReturnsFalse()
        {
            var a = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);
            var b = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 99, 20);

            Assert.IsFalse(a == b);
        }

        [Test]
        public void Equality_DifferentEntityB_ReturnsFalse()
        {
            var a = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);
            var b = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 99);

            Assert.IsFalse(a == b);
        }

        #endregion

        #region GetHashCode

        [Test]
        public void GetHashCode_SameValues_SameHash()
        {
            var a = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);
            var b = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);

            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        #endregion

        #region ToString

        [Test]
        public void ToString_ContainsAllFieldInfo()
        {
            var c = new FPContact(new FPVector3(1, 2, 3), new FPVector3(0, 1, 0), FP64.FromFloat(0.5f), 10, 20);
            var s = c.ToString();

            Assert.IsTrue(s.Contains("FPContact"));
            Assert.IsTrue(s.Contains("10"));
            Assert.IsTrue(s.Contains("20"));
        }

        #endregion
    }
}
