using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPRigidBodyTests
    {
        #region CreateDynamic

        [Test]
        public void CreateDynamic_SetsMassAndInverseMass()
        {
            var body = FPRigidBody.CreateDynamic(FP64.FromInt(2));

            Assert.AreEqual(2f, body.mass.ToFloat(), 0.01f);
            Assert.AreEqual(0.5f, body.inverseMass.ToFloat(), 0.01f);
        }

        #endregion

        #region CreateStatic

        [Test]
        public void CreateStatic_SetsFlags()
        {
            var body = FPRigidBody.CreateStatic();

            Assert.IsTrue(body.isStatic);
            Assert.IsFalse(body.isKinematic);
            Assert.AreEqual(0L, body.mass.RawValue);
            Assert.AreEqual(0L, body.inverseMass.RawValue);
        }

        #endregion

        #region CreateKinematic

        [Test]
        public void CreateKinematic_SetsFlags()
        {
            var body = FPRigidBody.CreateKinematic();

            Assert.IsTrue(body.isKinematic);
            Assert.IsFalse(body.isStatic);
            Assert.AreEqual(0L, body.mass.RawValue);
            Assert.AreEqual(0L, body.inverseMass.RawValue);
        }

        #endregion

        #region AddForce

        [Test]
        public void AddForce_Accumulates()
        {
            var body = FPRigidBody.CreateDynamic(FP64.One);
            body.AddForce(new FPVector3(FP64.One, FP64.Zero, FP64.Zero));
            body.AddForce(new FPVector3(FP64.Zero, FP64.FromInt(2), FP64.Zero));

            Assert.AreEqual(1f, body.force.x.ToFloat(), 0.01f);
            Assert.AreEqual(2f, body.force.y.ToFloat(), 0.01f);
            Assert.AreEqual(0f, body.force.z.ToFloat(), 0.01f);
        }

        #endregion

        #region AddTorque

        [Test]
        public void AddTorque_Accumulates()
        {
            var body = FPRigidBody.CreateDynamic(FP64.One);
            body.AddTorque(new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero));
            body.AddTorque(new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)));

            Assert.AreEqual(3f, body.torque.x.ToFloat(), 0.01f);
            Assert.AreEqual(0f, body.torque.y.ToFloat(), 0.01f);
            Assert.AreEqual(4f, body.torque.z.ToFloat(), 0.01f);
        }

        #endregion

        #region ClearForces

        [Test]
        public void ClearForces_ResetsForceAndTorque()
        {
            var body = FPRigidBody.CreateDynamic(FP64.One);
            body.AddForce(new FPVector3(FP64.One, FP64.One, FP64.One));
            body.AddTorque(new FPVector3(FP64.One, FP64.One, FP64.One));

            body.ClearForces();

            Assert.AreEqual(FPVector3.Zero, body.force);
            Assert.AreEqual(FPVector3.Zero, body.torque);
        }

        #endregion

        #region Equality

        [Test]
        public void Equality_SameValues()
        {
            var a = FPRigidBody.CreateDynamic(FP64.FromInt(5));
            var b = FPRigidBody.CreateDynamic(FP64.FromInt(5));

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
            Assert.IsTrue(a.Equals(b));
        }

        [Test]
        public void Equality_DifferentValues()
        {
            var a = FPRigidBody.CreateDynamic(FP64.FromInt(5));
            var b = FPRigidBody.CreateDynamic(FP64.FromInt(10));

            Assert.IsFalse(a == b);
            Assert.IsTrue(a != b);
            Assert.IsFalse(a.Equals(b));
        }

        #endregion

        #region DefaultValues

        [Test]
        public void DefaultValues_AreZero()
        {
            var body = new FPRigidBody();

            Assert.AreEqual(FPVector3.Zero, body.velocity);
            Assert.AreEqual(FPVector3.Zero, body.force);
            Assert.AreEqual(FPVector3.Zero, body.angularVelocity);
            Assert.AreEqual(FPVector3.Zero, body.torque);
            Assert.AreEqual(0L, body.mass.RawValue);
            Assert.AreEqual(0L, body.inverseMass.RawValue);
            Assert.AreEqual(0L, body.linearDamping.RawValue);
            Assert.AreEqual(0L, body.angularDamping.RawValue);
            Assert.IsFalse(body.isKinematic);
            Assert.IsFalse(body.isStatic);
        }

        #endregion
    }
}
