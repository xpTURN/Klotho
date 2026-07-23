using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPPhysicsIntegrationTests
    {
        #region Static/Kinematic Skip

        [Test]
        public void StaticBody_NoMovement()
        {
            var body = FPRigidBody.CreateStatic();
            body.AddForce(new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero));
            var position = new FPVector3(FP64.One, FP64.One, FP64.One);
            var rotation = FPQuaternion.Identity;
            FP64 dt = FP64.FromFloat(0.02f);

            FPPhysicsIntegration.Integrate(ref body, ref position, ref rotation, dt);

            Assert.AreEqual(1f, position.x.ToFloat(), 0.01f);
            Assert.AreEqual(FPVector3.Zero, body.velocity);
        }

        [Test]
        public void KinematicBody_NoMovement()
        {
            var body = FPRigidBody.CreateKinematic();
            body.AddForce(new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero));
            var position = new FPVector3(FP64.One, FP64.One, FP64.One);
            var rotation = FPQuaternion.Identity;
            FP64 dt = FP64.FromFloat(0.02f);

            FPPhysicsIntegration.Integrate(ref body, ref position, ref rotation, dt);

            Assert.AreEqual(1f, position.x.ToFloat(), 0.01f);
            Assert.AreEqual(FPVector3.Zero, body.velocity);
        }

        #endregion

        #region Linear Integration

        [Test]
        public void LinearIntegration_ConstantForce()
        {
            // mass=1, force=(10,0,0), dt=0.02
            // acceleration = 10, velocity = 0+10*0.02 = 0.2, position = 0+0.2*0.02 = 0.004
            var body = FPRigidBody.CreateDynamic(FP64.One);
            body.AddForce(new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero));
            var position = FPVector3.Zero;
            var rotation = FPQuaternion.Identity;
            FP64 dt = FP64.FromFloat(0.02f);

            FPPhysicsIntegration.Integrate(ref body, ref position, ref rotation, dt);

            Assert.AreEqual(0.2f, body.velocity.x.ToFloat(), 0.01f);
            Assert.AreEqual(0.004f, position.x.ToFloat(), 0.001f);
        }

        [Test]
        public void LinearIntegration_HeavyMass()
        {
            // mass=10, force=(10,0,0), dt=0.02
            // acceleration = 10*(1/10) = 1, velocity = 0+1*0.02 = 0.02
            var body = FPRigidBody.CreateDynamic(FP64.FromInt(10));
            body.AddForce(new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero));
            var position = FPVector3.Zero;
            var rotation = FPQuaternion.Identity;
            FP64 dt = FP64.FromFloat(0.02f);

            FPPhysicsIntegration.Integrate(ref body, ref position, ref rotation, dt);

            Assert.AreEqual(0.02f, body.velocity.x.ToFloat(), 0.01f);
        }

        #endregion

        #region Linear Damping

        [Test]
        public void LinearDamping_ReducesVelocity()
        {
            // velocity=10, damping=0.5, dt=0.02 -> velocity = 10*(1-0.5*0.02) = 10*0.99 = 9.9
            var body = FPRigidBody.CreateDynamic(FP64.One);
            body.velocity = new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero);
            body.linearDamping = FP64.Half;
            var position = FPVector3.Zero;
            var rotation = FPQuaternion.Identity;
            FP64 dt = FP64.FromFloat(0.02f);

            FPPhysicsIntegration.Integrate(ref body, ref position, ref rotation, dt);

            Assert.AreEqual(9.9f, body.velocity.x.ToFloat(), 0.01f);
        }

        #endregion

        #region Angular Integration

        [Test]
        public void AngularIntegration_ConstantTorque()
        {
            // mass=1, torque=(0,10,0), dt=0.02
            // angular acceleration = 10, angular velocity = 0+10*0.02 = 0.2
            var body = FPRigidBody.CreateDynamic(FP64.One);
            body.AddTorque(new FPVector3(FP64.Zero, FP64.FromInt(10), FP64.Zero));
            var position = FPVector3.Zero;
            var rotation = FPQuaternion.Identity;
            FP64 dt = FP64.FromFloat(0.02f);

            FPPhysicsIntegration.Integrate(ref body, ref position, ref rotation, dt);

            Assert.AreEqual(0.2f, body.angularVelocity.y.ToFloat(), 0.01f);
            Assert.AreNotEqual(FPQuaternion.Identity, rotation);
        }

        #endregion

        #region Angular Damping

        [Test]
        public void AngularDamping_ReducesAngularVelocity()
        {
            // angularVelocity=10, damping=0.5, dt=0.02 -> angularVelocity = 10*(1-0.5*0.02) = 9.9
            var body = FPRigidBody.CreateDynamic(FP64.One);
            body.angularVelocity = new FPVector3(FP64.Zero, FP64.FromInt(10), FP64.Zero);
            body.angularDamping = FP64.Half;
            var position = FPVector3.Zero;
            var rotation = FPQuaternion.Identity;
            FP64 dt = FP64.FromFloat(0.02f);

            FPPhysicsIntegration.Integrate(ref body, ref position, ref rotation, dt);

            Assert.AreEqual(9.9f, body.angularVelocity.y.ToFloat(), 0.01f);
        }

        #endregion

        #region ClearForces

        [Test]
        public void ClearForces_AfterIntegrate()
        {
            var body = FPRigidBody.CreateDynamic(FP64.One);
            body.AddForce(new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero));
            body.AddTorque(new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(3)));
            var position = FPVector3.Zero;
            var rotation = FPQuaternion.Identity;
            FP64 dt = FP64.FromFloat(0.02f);

            FPPhysicsIntegration.Integrate(ref body, ref position, ref rotation, dt);

            Assert.AreEqual(FPVector3.Zero, body.force);
            Assert.AreEqual(FPVector3.Zero, body.torque);
        }

        #endregion

        #region Inertia

        [Test]
        public void ZeroForce_MaintainsVelocity()
        {
            // velocity=5, no force, no damping -> velocity preserved, position = 5*0.02 = 0.1
            var body = FPRigidBody.CreateDynamic(FP64.One);
            body.velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            var position = FPVector3.Zero;
            var rotation = FPQuaternion.Identity;
            FP64 dt = FP64.FromFloat(0.02f);

            FPPhysicsIntegration.Integrate(ref body, ref position, ref rotation, dt);

            Assert.AreEqual(5f, body.velocity.x.ToFloat(), 0.01f);
            Assert.AreEqual(0.1f, position.x.ToFloat(), 0.01f);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_ConsistentResults()
        {
            FP64 dt = FP64.FromFloat(0.02f);

            var bodyA = FPRigidBody.CreateDynamic(FP64.FromInt(3));
            bodyA.AddForce(new FPVector3(FP64.FromInt(7), FP64.FromInt(-2), FP64.FromInt(4)));
            bodyA.AddTorque(new FPVector3(FP64.FromInt(1), FP64.FromInt(3), FP64.FromInt(-1)));
            bodyA.linearDamping = FP64.FromFloat(0.1f);
            bodyA.angularDamping = FP64.FromFloat(0.05f);
            var posA = new FPVector3(FP64.FromInt(10), FP64.FromInt(20), FP64.FromInt(30));
            var rotA = FPQuaternion.Identity;

            var bodyB = FPRigidBody.CreateDynamic(FP64.FromInt(3));
            bodyB.AddForce(new FPVector3(FP64.FromInt(7), FP64.FromInt(-2), FP64.FromInt(4)));
            bodyB.AddTorque(new FPVector3(FP64.FromInt(1), FP64.FromInt(3), FP64.FromInt(-1)));
            bodyB.linearDamping = FP64.FromFloat(0.1f);
            bodyB.angularDamping = FP64.FromFloat(0.05f);
            var posB = new FPVector3(FP64.FromInt(10), FP64.FromInt(20), FP64.FromInt(30));
            var rotB = FPQuaternion.Identity;

            FPPhysicsIntegration.Integrate(ref bodyA, ref posA, ref rotA, dt);
            FPPhysicsIntegration.Integrate(ref bodyB, ref posB, ref rotB, dt);

            Assert.AreEqual(posA.x.RawValue, posB.x.RawValue);
            Assert.AreEqual(posA.y.RawValue, posB.y.RawValue);
            Assert.AreEqual(posA.z.RawValue, posB.z.RawValue);
            Assert.AreEqual(rotA.x.RawValue, rotB.x.RawValue);
            Assert.AreEqual(rotA.y.RawValue, rotB.y.RawValue);
            Assert.AreEqual(rotA.z.RawValue, rotB.z.RawValue);
            Assert.AreEqual(rotA.w.RawValue, rotB.w.RawValue);
            Assert.AreEqual(bodyA.velocity.x.RawValue, bodyB.velocity.x.RawValue);
            Assert.AreEqual(bodyA.angularVelocity.y.RawValue, bodyB.angularVelocity.y.RawValue);
        }

        #endregion
    }
}
