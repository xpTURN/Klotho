using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPCollisionResponseTests
    {
        const float EPSILON = 0.05f;

        static FPContact MakeContact(FPVector3 point, FPVector3 normal, FP64 depth)
        {
            return new FPContact(point, normal, depth, 0, 0);
        }

        #region Static/Kinematic Skip

        [Test]
        public void TwoStaticBodies_NoChange()
        {
            var bodyA = FPRigidBody.CreateStatic();
            var bodyB = FPRigidBody.CreateStatic();
            var posA = FPVector3.Zero;
            var posB = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero);
            var contact = MakeContact(
                new FPVector3(FP64.One, FP64.Zero, FP64.Zero),
                FPVector3.Right, FP64.FromFloat(0.1f));

            FPCollisionResponse.ResolveContact(ref bodyA, ref posA, ref bodyB, ref posB, in contact);

            Assert.AreEqual(FPVector3.Zero, bodyA.velocity);
            Assert.AreEqual(FPVector3.Zero, bodyB.velocity);
            Assert.AreEqual(0f, posA.x.ToFloat(), EPSILON);
        }

        #endregion

        #region Dynamic vs Static

        [Test]
        public void DynamicVsStatic_DynamicBounces()
        {
            // bodyA moves from (-1,0,0) toward (5,0,0), heading toward bodyB at (1,0,0)
            // normal = A->B direction (1,0,0), e=1
            // contact point is origin, rA=(1,0,0), cross(rA,n)=0 -> no angular term
            // j = -(1+1)*5 / (1+0) = -10
            // velA += (1,0,0)*(-10)*1 = (-10,0,0) -> final: 5-10 = -5
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            bodyA.velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            bodyA.restitution = FP64.One;
            var bodyB = FPRigidBody.CreateStatic();
            bodyB.restitution = FP64.One;
            var posA = new FPVector3(FP64.FromInt(-1), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
            var contact = MakeContact(FPVector3.Zero, FPVector3.Right, FP64.FromFloat(0.05f));

            FPCollisionResponse.ResolveContact(ref bodyA, ref posA, ref bodyB, ref posB, in contact);

            Assert.AreEqual(-5f, bodyA.velocity.x.ToFloat(), EPSILON);
            Assert.AreEqual(FPVector3.Zero, bodyB.velocity);
        }

        #endregion

        #region Equal Mass

        [Test]
        public void TwoDynamic_EqualMass_SwapVelocities()
        {
            // head-on collision, equal mass, e=1 -> velocity swap
            // contact point is origin, rA=(1,0,0), rB=(-1,0,0)
            // cross(rA,n) = cross((1,0,0),(1,0,0)) = 0 -> no angular term
            // vRel = (5-(-5)) = (10,0,0), vRelNormal=10
            // j = -(1+1)*10 / (1+1) = -10
            // velA += (1,0,0)*(-10)*1 = 5-10=-5, velB -= (1,0,0)*(-10)*1 = -5+10=5
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            bodyA.velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            bodyA.restitution = FP64.One;
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            bodyB.velocity = new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.Zero);
            bodyB.restitution = FP64.One;
            var posA = new FPVector3(FP64.FromInt(-1), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
            var contact = MakeContact(FPVector3.Zero, FPVector3.Right, FP64.FromFloat(0.05f));

            FPCollisionResponse.ResolveContact(ref bodyA, ref posA, ref bodyB, ref posB, in contact);

            Assert.AreEqual(-5f, bodyA.velocity.x.ToFloat(), EPSILON);
            Assert.AreEqual(5f, bodyB.velocity.x.ToFloat(), EPSILON);
        }

        #endregion

        #region Restitution

        [Test]
        public void Restitution_Zero_InelasticCollision()
        {
            // e=0, dynamic vs static, vel=(5,0,0)
            // j = -(1+0)*5 / 1 = -5
            // velA = 5 + (-5) = 0
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            bodyA.velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            var bodyB = FPRigidBody.CreateStatic();
            var posA = new FPVector3(FP64.FromInt(-1), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
            var contact = MakeContact(FPVector3.Zero, FPVector3.Right, FP64.FromFloat(0.05f));

            FPCollisionResponse.ResolveContact(ref bodyA, ref posA, ref bodyB, ref posB, in contact);

            Assert.AreEqual(0f, bodyA.velocity.x.ToFloat(), EPSILON);
        }

        [Test]
        public void Restitution_One_PerfectBounce()
        {
            // e=1, dynamic vs static, vel=(5,0,0)
            // j = -(1+1)*5 / 1 = -10
            // velA = 5 + (-10) = -5
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            bodyA.velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            bodyA.restitution = FP64.One;
            var bodyB = FPRigidBody.CreateStatic();
            bodyB.restitution = FP64.One;
            var posA = new FPVector3(FP64.FromInt(-1), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
            var contact = MakeContact(FPVector3.Zero, FPVector3.Right, FP64.FromFloat(0.05f));

            FPCollisionResponse.ResolveContact(ref bodyA, ref posA, ref bodyB, ref posB, in contact);

            Assert.AreEqual(-5f, bodyA.velocity.x.ToFloat(), EPSILON);
        }

        #endregion

        #region Separating

        [Test]
        public void SeparatingBodies_NoImpulse()
        {
            // bodyA moves away from bodyB -> vRelNormal < 0 -> impulse skipped
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            bodyA.velocity = new FPVector3(FP64.FromInt(-5), FP64.Zero, FP64.Zero);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            bodyB.velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            var posA = new FPVector3(FP64.FromInt(-1), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
            var contact = MakeContact(FPVector3.Zero, FPVector3.Right, FP64.FromFloat(0.05f));

            FPCollisionResponse.ResolveContact(ref bodyA, ref posA, ref bodyB, ref posB, in contact);

            Assert.AreEqual(-5f, bodyA.velocity.x.ToFloat(), EPSILON);
            Assert.AreEqual(5f, bodyB.velocity.x.ToFloat(), EPSILON);
        }

        #endregion

        #region Penetration Correction

        [Test]
        public void PenetrationCorrection_BodiesSeparated()
        {
            // depth=0.1, slop=0.01, percent=0.8
            // correction = (0.1-0.01)*0.8 = 0.072
            // equal mass: each moves by 0.036
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = new FPVector3(FP64.FromFloat(-0.95f), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.FromFloat(0.95f), FP64.Zero, FP64.Zero);
            var contact = MakeContact(FPVector3.Zero, FPVector3.Right, FP64.FromFloat(0.1f));

            FPCollisionResponse.ResolveContact(ref bodyA, ref posA, ref bodyB, ref posB, in contact);

            Assert.IsTrue(posA.x.ToFloat() < -0.95f);
            Assert.IsTrue(posB.x.ToFloat() > 0.95f);
        }

        #endregion

        #region Angular Impulse

        [Test]
        public void AngularImpulse_AppliesSpin()
        {
            // contact point is offset from center -> angular impulse occurs
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            bodyA.velocity = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Zero);
            bodyA.restitution = FP64.One;
            var bodyB = FPRigidBody.CreateStatic();
            bodyB.restitution = FP64.One;
            var posA = new FPVector3(FP64.FromInt(-1), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
            // contact point is offset from A's center along Y
            var contactPoint = new FPVector3(FP64.Zero, FP64.One, FP64.Zero);
            var contact = MakeContact(contactPoint, FPVector3.Right, FP64.FromFloat(0.05f));

            FPCollisionResponse.ResolveContact(ref bodyA, ref posA, ref bodyB, ref posB, in contact);

            Assert.AreNotEqual(FPVector3.Zero, bodyA.angularVelocity);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_ConsistentResults()
        {
            var bodyA1 = FPRigidBody.CreateDynamic(FP64.FromInt(3));
            bodyA1.velocity = new FPVector3(FP64.FromInt(7), FP64.FromInt(-2), FP64.FromInt(4));
            bodyA1.restitution = FP64.Half;
            bodyA1.friction = FP64.FromFloat(0.3f);
            var bodyB1 = FPRigidBody.CreateDynamic(FP64.FromInt(5));
            bodyB1.velocity = new FPVector3(FP64.FromInt(-3), FP64.One, FP64.FromInt(2));
            bodyB1.restitution = FP64.Half;
            bodyB1.friction = FP64.FromFloat(0.5f);
            var posA1 = new FPVector3(FP64.FromInt(-2), FP64.One, FP64.Zero);
            var posB1 = new FPVector3(FP64.FromInt(2), FP64.FromInt(-1), FP64.One);
            var contact1 = MakeContact(
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Half),
                new FPVector3(FP64.One, FP64.Zero, FP64.Zero).normalized,
                FP64.FromFloat(0.08f));

            var bodyA2 = FPRigidBody.CreateDynamic(FP64.FromInt(3));
            bodyA2.velocity = new FPVector3(FP64.FromInt(7), FP64.FromInt(-2), FP64.FromInt(4));
            bodyA2.restitution = FP64.Half;
            bodyA2.friction = FP64.FromFloat(0.3f);
            var bodyB2 = FPRigidBody.CreateDynamic(FP64.FromInt(5));
            bodyB2.velocity = new FPVector3(FP64.FromInt(-3), FP64.One, FP64.FromInt(2));
            bodyB2.restitution = FP64.Half;
            bodyB2.friction = FP64.FromFloat(0.5f);
            var posA2 = new FPVector3(FP64.FromInt(-2), FP64.One, FP64.Zero);
            var posB2 = new FPVector3(FP64.FromInt(2), FP64.FromInt(-1), FP64.One);
            var contact2 = MakeContact(
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Half),
                new FPVector3(FP64.One, FP64.Zero, FP64.Zero).normalized,
                FP64.FromFloat(0.08f));

            FPCollisionResponse.ResolveContact(ref bodyA1, ref posA1, ref bodyB1, ref posB1, in contact1);
            FPCollisionResponse.ResolveContact(ref bodyA2, ref posA2, ref bodyB2, ref posB2, in contact2);

            Assert.AreEqual(bodyA1.velocity.x.RawValue, bodyA2.velocity.x.RawValue);
            Assert.AreEqual(bodyA1.velocity.y.RawValue, bodyA2.velocity.y.RawValue);
            Assert.AreEqual(bodyA1.velocity.z.RawValue, bodyA2.velocity.z.RawValue);
            Assert.AreEqual(bodyB1.velocity.x.RawValue, bodyB2.velocity.x.RawValue);
            Assert.AreEqual(posA1.x.RawValue, posA2.x.RawValue);
            Assert.AreEqual(posB1.x.RawValue, posB2.x.RawValue);
        }

        #endregion

        #region Mass Ratio

        [Test]
        public void HeavyVsLight_LightBounces()
        {
            // heavy body (mass=100) vs light body (mass=1)
            // the light body should receive a much larger velocity change
            var bodyA = FPRigidBody.CreateDynamic(FP64.FromInt(100));
            bodyA.velocity = new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
            bodyA.restitution = FP64.One;
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            bodyB.restitution = FP64.One;
            var posA = new FPVector3(FP64.FromInt(-1), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
            var contact = MakeContact(FPVector3.Zero, FPVector3.Right, FP64.FromFloat(0.05f));

            FP64 velBBefore = bodyB.velocity.x;
            FPCollisionResponse.ResolveContact(ref bodyA, ref posA, ref bodyB, ref posB, in contact);

            float heavyChange = FP64.Abs(bodyA.velocity.x - FP64.One).ToFloat();
            float lightChange = FP64.Abs(bodyB.velocity.x - velBBefore).ToFloat();

            Assert.IsTrue(lightChange > heavyChange * 10f);
        }

        #endregion
    }
}
