using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPStaticColliderTests
    {
        #region Data integrity

        [Test]
        public void Default_AllZeroOrFalse()
        {
            var sc = default(FPStaticCollider);
            Assert.AreEqual(0, sc.id);
            Assert.IsFalse(sc.isTrigger);
        }

        [Test]
        public void Fields_StoredAndRetrieved()
        {
            var collider = FPCollider.FromSphere(new FPSphereShape(FP64.One, FPVector3.Zero));
            var sc = new FPStaticCollider
            {
                id        = 42,
                collider  = collider,
                meshData  = default,
                isTrigger = true
            };

            Assert.AreEqual(42, sc.id);
            Assert.IsTrue(sc.isTrigger);
            Assert.AreEqual(collider.type, sc.collider.type);
        }

        [Test]
        public void IsTrigger_DefaultFalse()
        {
            var sc = new FPStaticCollider { id = 1 };
            Assert.IsFalse(sc.isTrigger);
        }

        #endregion

        #region Signed index convention

        [Test]
        public void SignedIndex_BitwiseNot_ZeroMapsToNegativeOne()
        {
            int colliderIndex = 0;
            int leafIndex = ~colliderIndex;
            Assert.AreEqual(-1, leafIndex);
            Assert.IsTrue(leafIndex < 0);
        }

        [Test]
        public void SignedIndex_BitwiseNot_ThreeMapsToNegativeFour()
        {
            int colliderIndex = 3;
            int leafIndex = ~colliderIndex;
            Assert.AreEqual(-4, leafIndex);
            Assert.IsTrue(leafIndex < 0);
        }

        [Test]
        public void SignedIndex_Decode_RecoverOriginalIndex()
        {
            for (int i = 0; i < 8; i++)
            {
                int leafIndex = ~i;
                int recovered = ~leafIndex;
                Assert.AreEqual(i, recovered);
            }
        }

        [Test]
        public void SignedIndex_ArrayAccess_CorrectElement()
        {
            var colliders = new[]
            {
                new FPStaticCollider { id = 100 },
                new FPStaticCollider { id = 101 },
                new FPStaticCollider { id = 102 },
            };

            // leafIndex = ~0 → colliders[0]
            int leafIndex0 = ~0;
            Assert.AreEqual(100, colliders[~leafIndex0].id);

            // leafIndex = ~2 → colliders[2]
            int leafIndex2 = ~2;
            Assert.AreEqual(102, colliders[~leafIndex2].id);
        }

        [Test]
        public void SignedIndex_NegativeLeafIndex_IsStaticCollider()
        {
            // leafIndex >= 0: bodies[], leafIndex < 0: staticColliders[~leafIndex]
            Assert.IsTrue(~0 < 0);
            Assert.IsTrue(~1 < 0);
            Assert.IsTrue(~99 < 0);
        }

        [Test]
        public void SignedIndex_PositiveLeafIndex_IsBody()
        {
            Assert.IsTrue(0  >= 0);
            Assert.IsTrue(1  >= 0);
            Assert.IsTrue(99 >= 0);
        }

        #endregion
    }
}
