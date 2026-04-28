using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class NarrowphaseDispatchTests
    {
        private const float EPSILON = 0.05f;

        #region Typed Overloads

        [Test]
        public void SphereSphere_Works()
        {
            var a = new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero);
            var b = new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(8, 0, 0));

            bool hit = NarrowphaseDispatch.Test(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(2.0f, contact.depth.ToFloat(), EPSILON);
        }

        [Test]
        public void BoxSphere_FlipsCorrectly()
        {
            var box = new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero);
            var sphere = new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(6, 0, 0));

            bool hit = NarrowphaseDispatch.Test(ref box, ref sphere, out FPContact contact);

            Assert.IsTrue(hit);
            // Flipped: normal points from B (sphere) to A (box), i.e. -X
            Assert.AreEqual(-1.0f, contact.normal.x.ToFloat(), EPSILON);
        }

        #endregion

        #region FPCollider Dispatch

        [Test]
        public void FPCollider_SphereSphere_Works()
        {
            var a = FPCollider.FromSphere(new FPSphereShape(FP64.FromFloat(5.0f), FPVector3.Zero));
            var b = FPCollider.FromSphere(new FPSphereShape(FP64.FromFloat(5.0f), new FPVector3(8, 0, 0)));

            bool hit = NarrowphaseDispatch.Test(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(2.0f, contact.depth.ToFloat(), EPSILON);
        }

        [Test]
        public void FPCollider_BoxSphere_FlipsCorrectly()
        {
            var a = FPCollider.FromBox(new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero));
            var b = FPCollider.FromSphere(new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(6, 0, 0)));

            bool hit = NarrowphaseDispatch.Test(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(1.0f, contact.normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void FPCollider_CapsuleCapsule_Works()
        {
            var a = FPCollider.FromCapsule(new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero));
            var b = FPCollider.FromCapsule(new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), new FPVector3(1, 0, 0)));

            bool hit = NarrowphaseDispatch.Test(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        [Test]
        public void FPCollider_SphereBox_Works()
        {
            var a = FPCollider.FromSphere(new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(6, 0, 0)));
            var b = FPCollider.FromBox(new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero));

            bool hit = NarrowphaseDispatch.Test(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
            Assert.AreEqual(-1.0f, contact.normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void FPCollider_SphereCapsule_Works()
        {
            var a = FPCollider.FromSphere(new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(2, 0, 0)));
            var b = FPCollider.FromCapsule(new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero));

            bool hit = NarrowphaseDispatch.Test(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        [Test]
        public void FPCollider_CapsuleSphere_FlipsCorrectly()
        {
            var a = FPCollider.FromCapsule(new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), FPVector3.Zero));
            var b = FPCollider.FromSphere(new FPSphereShape(FP64.FromFloat(2.0f), new FPVector3(2, 0, 0)));

            bool hit = NarrowphaseDispatch.Test(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(-1.0f, contact.normal.x.ToFloat(), EPSILON);
        }

        [Test]
        public void FPCollider_BoxBox_Works()
        {
            var a = FPCollider.FromBox(new FPBoxShape(new FPVector3(2, 2, 2), FPVector3.Zero));
            var b = FPCollider.FromBox(new FPBoxShape(new FPVector3(2, 2, 2), new FPVector3(3, 0, 0)));

            bool hit = NarrowphaseDispatch.Test(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.AreEqual(1.0f, contact.depth.ToFloat(), EPSILON);
        }

        [Test]
        public void FPCollider_BoxCapsule_Works()
        {
            var a = FPCollider.FromBox(new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero));
            var b = FPCollider.FromCapsule(new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), new FPVector3(5, 0, 0)));

            bool hit = NarrowphaseDispatch.Test(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        [Test]
        public void FPCollider_CapsuleBox_FlipsCorrectly()
        {
            var a = FPCollider.FromCapsule(new FPCapsuleShape(FP64.FromFloat(3.0f), FP64.FromFloat(1.0f), new FPVector3(5, 0, 0)));
            var b = FPCollider.FromBox(new FPBoxShape(new FPVector3(5, 5, 5), FPVector3.Zero));

            bool hit = NarrowphaseDispatch.Test(ref a, ref b, out FPContact contact);

            Assert.IsTrue(hit);
            Assert.IsTrue(contact.depth.ToFloat() > 0.0f);
        }

        #endregion
    }
}
