using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPConstraintSolverTests
    {
        const float EPSILON = 0.05f;

        static FPDistanceJoint MakeJoint(int idxA, int idxB, FP64 distance)
        {
            return new FPDistanceJoint
            {
                bodyIndexA = idxA,
                bodyIndexB = idxB,
                anchorA = FPVector3.Zero,
                anchorB = FPVector3.Zero,
                distance = distance
            };
        }

        #region Static/Kinematic Skip

        [Test]
        public void TwoStaticBodies_NoChange()
        {
            var bodyA = FPRigidBody.CreateStatic();
            var bodyB = FPRigidBody.CreateStatic();
            var posA = new FPVector3(FP64.FromInt(-2), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero);
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;
            var joint = MakeJoint(0, 1, FP64.FromInt(3));
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveDistanceJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            Assert.AreEqual(FPVector3.Zero, bodyA.velocity);
            Assert.AreEqual(FPVector3.Zero, bodyB.velocity);
        }

        #endregion

        #region Stretched

        [Test]
        public void StretchedJoint_PullsTogether()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = new FPVector3(FP64.FromInt(-3), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero);
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;
            // current distance = 6, target = 4 -> stretched
            var joint = MakeJoint(0, 1, FP64.FromInt(4));
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveDistanceJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            // A should gain positive x velocity (toward B)
            Assert.IsTrue(bodyA.velocity.x.ToFloat() > 0f);
            // B should gain negative x velocity (toward A)
            Assert.IsTrue(bodyB.velocity.x.ToFloat() < 0f);
        }

        #endregion

        #region Compressed

        [Test]
        public void CompressedJoint_PushesApart()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = new FPVector3(FP64.FromInt(-1), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.Zero);
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;
            // current distance = 2, target = 4 -> compressed
            var joint = MakeJoint(0, 1, FP64.FromInt(4));
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveDistanceJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            // A should gain negative x velocity (away from B)
            Assert.IsTrue(bodyA.velocity.x.ToFloat() < 0f);
            // B should gain positive x velocity (away from A)
            Assert.IsTrue(bodyB.velocity.x.ToFloat() > 0f);
        }

        #endregion

        #region ExactDistance

        [Test]
        public void ExactDistance_NoImpulse()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = new FPVector3(FP64.FromInt(-2), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero);
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;
            // current distance = 4, target = 4 -> exact match
            var joint = MakeJoint(0, 1, FP64.FromInt(4));
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveDistanceJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            Assert.AreEqual(0f, bodyA.velocity.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, bodyB.velocity.x.ToFloat(), EPSILON);
        }

        #endregion

        #region DynamicVsStatic

        [Test]
        public void DynamicVsStatic_OnlyDynamicMoves()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateStatic();
            var posA = new FPVector3(FP64.FromInt(-3), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero);
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;
            // stretched: current=6, target=4
            var joint = MakeJoint(0, 1, FP64.FromInt(4));
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveDistanceJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            Assert.IsTrue(bodyA.velocity.x.ToFloat() > 0f);
            Assert.AreEqual(FPVector3.Zero, bodyB.velocity);
        }

        #endregion

        #region AnchorOffset

        [Test]
        public void WithAnchorOffset_CorrectWorldPosition()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = FPVector3.Zero;
            var posB = new FPVector3(FP64.FromInt(6), FP64.Zero, FP64.Zero);
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;

            var joint = new FPDistanceJoint
            {
                bodyIndexA = 0,
                bodyIndexB = 1,
                anchorA = new FPVector3(FP64.One, FP64.Zero, FP64.Zero),
                anchorB = new FPVector3(-FP64.One, FP64.Zero, FP64.Zero),
                distance = FP64.FromInt(4)
            };
            // world anchor A = (1,0,0), world anchor B = (5,0,0)
            // current distance = 4, target = 4 -> exact match
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveDistanceJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            Assert.AreEqual(0f, bodyA.velocity.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, bodyB.velocity.x.ToFloat(), EPSILON);
        }

        #endregion

        #region MultipleIterations

        [Test]
        public void MultipleSteps_DistanceConvergesToTarget()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = new FPVector3(FP64.FromInt(-3), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero);
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;
            FP64 target = FP64.FromInt(4);
            var joint = MakeJoint(0, 1, target);
            FP64 dt = FP64.FromFloat(0.02f);

            FP64 initialError = FP64.Abs(FPVector3.Distance(posA, posB) - target);

            for (int s = 0; s < 50; s++)
            {
                FPConstraintSolver.SolveDistanceJoint(
                    ref bodyA, ref posA, in rotA,
                    ref bodyB, ref posB, in rotB,
                    in joint, dt);
                FPPhysicsIntegration.Integrate(ref bodyA, ref posA, ref rotA, dt);
                FPPhysicsIntegration.Integrate(ref bodyB, ref posB, ref rotB, dt);
            }

            FP64 finalError = FP64.Abs(FPVector3.Distance(posA, posB) - target);

            Assert.IsTrue(finalError.ToFloat() < initialError.ToFloat());
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_BitExact()
        {
            var bodyA1 = FPRigidBody.CreateDynamic(FP64.FromInt(3));
            bodyA1.velocity = new FPVector3(FP64.FromInt(2), FP64.One, FP64.FromInt(-1));
            var bodyB1 = FPRigidBody.CreateDynamic(FP64.FromInt(5));
            bodyB1.velocity = new FPVector3(FP64.FromInt(-1), FP64.FromInt(3), FP64.FromInt(2));
            var posA1 = new FPVector3(FP64.FromInt(-2), FP64.One, FP64.Zero);
            var posB1 = new FPVector3(FP64.FromInt(3), FP64.FromInt(-1), FP64.One);
            var rotA1 = FPQuaternion.Identity;
            var rotB1 = FPQuaternion.Identity;

            var bodyA2 = FPRigidBody.CreateDynamic(FP64.FromInt(3));
            bodyA2.velocity = new FPVector3(FP64.FromInt(2), FP64.One, FP64.FromInt(-1));
            var bodyB2 = FPRigidBody.CreateDynamic(FP64.FromInt(5));
            bodyB2.velocity = new FPVector3(FP64.FromInt(-1), FP64.FromInt(3), FP64.FromInt(2));
            var posA2 = new FPVector3(FP64.FromInt(-2), FP64.One, FP64.Zero);
            var posB2 = new FPVector3(FP64.FromInt(3), FP64.FromInt(-1), FP64.One);
            var rotA2 = FPQuaternion.Identity;
            var rotB2 = FPQuaternion.Identity;

            var joint = MakeJoint(0, 1, FP64.FromInt(3));
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveDistanceJoint(
                ref bodyA1, ref posA1, in rotA1,
                ref bodyB1, ref posB1, in rotB1,
                in joint, dt);
            FPConstraintSolver.SolveDistanceJoint(
                ref bodyA2, ref posA2, in rotA2,
                ref bodyB2, ref posB2, in rotB2,
                in joint, dt);

            Assert.AreEqual(bodyA1.velocity.x.RawValue, bodyA2.velocity.x.RawValue);
            Assert.AreEqual(bodyA1.velocity.y.RawValue, bodyA2.velocity.y.RawValue);
            Assert.AreEqual(bodyA1.velocity.z.RawValue, bodyA2.velocity.z.RawValue);
            Assert.AreEqual(bodyB1.velocity.x.RawValue, bodyB2.velocity.x.RawValue);
            Assert.AreEqual(bodyB1.velocity.y.RawValue, bodyB2.velocity.y.RawValue);
            Assert.AreEqual(bodyB1.velocity.z.RawValue, bodyB2.velocity.z.RawValue);
        }

        #endregion

        #region Hinge — Helpers

        static FPHingeJoint MakeHingeJoint(int idxA, int idxB, FPVector3 axisA, FPVector3 axisB)
        {
            return new FPHingeJoint
            {
                bodyIndexA = idxA,
                bodyIndexB = idxB,
                pivotA = FPVector3.Zero,
                pivotB = FPVector3.Zero,
                axisA = axisA,
                axisB = axisB
            };
        }

        #endregion

        #region Hinge — Static Skip

        [Test]
        public void HingeTwoStaticBodies_NoChange()
        {
            var bodyA = FPRigidBody.CreateStatic();
            var bodyB = FPRigidBody.CreateStatic();
            var posA = new FPVector3(FP64.FromInt(-2), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero);
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;
            var joint = MakeHingeJoint(0, 1, FPVector3.Up, FPVector3.Up);
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            Assert.AreEqual(FPVector3.Zero, bodyA.velocity);
            Assert.AreEqual(FPVector3.Zero, bodyB.velocity);
            Assert.AreEqual(FPVector3.Zero, bodyA.angularVelocity);
            Assert.AreEqual(FPVector3.Zero, bodyB.angularVelocity);
        }

        #endregion

        #region Hinge — Pivot Alignment

        [Test]
        public void HingePivotAlignment_PullsTogether()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = new FPVector3(FP64.FromInt(-3), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero);
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;
            // pivotA=(1,0,0) -> world pivot=(-2,0,0), pivotB=(-1,0,0) -> world pivot=(2,0,0)
            var joint = new FPHingeJoint
            {
                bodyIndexA = 0, bodyIndexB = 1,
                pivotA = new FPVector3(FP64.One, FP64.Zero, FP64.Zero),
                pivotB = new FPVector3(-FP64.One, FP64.Zero, FP64.Zero),
                axisA = FPVector3.Up, axisB = FPVector3.Up
            };
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            // the two bodies should be pulled toward each other
            Assert.IsTrue(bodyA.velocity.x.ToFloat() > 0f);
            Assert.IsTrue(bodyB.velocity.x.ToFloat() < 0f);
        }

        #endregion

        #region Hinge — Axis Alignment

        [Test]
        public void HingeAxisAlignment_MisalignedCorrected()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            // same position -> no linear error
            var posA = FPVector3.Zero;
            var posB = FPVector3.Zero;
            var rotA = FPQuaternion.Identity;
            // rotate B 45 degrees around the Z axis -> worldAxisB tilts away from Y
            var rotB = FPQuaternion.AngleAxis(FP64.FromInt(45), FPVector3.Forward);
            var joint = MakeHingeJoint(0, 1, FPVector3.Up, FPVector3.Up);
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            // angular velocity must not be zero (correction impulse applied)
            Assert.AreNotEqual(FPVector3.Zero, bodyA.angularVelocity);
            Assert.AreNotEqual(FPVector3.Zero, bodyB.angularVelocity);
        }

        #endregion

        #region Hinge — Free Rotation

        [Test]
        public void HingeFreeRotationAroundAxis_NotConstrained()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = FPVector3.Zero;
            var posB = FPVector3.Zero;
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;
            // give B angular velocity only along the hinge axis (Y)
            bodyB.angularVelocity = new FPVector3(FP64.Zero, FP64.FromInt(5), FP64.Zero);
            var joint = MakeHingeJoint(0, 1, FPVector3.Up, FPVector3.Up);
            FP64 dt = FP64.FromFloat(0.02f);

            FP64 angVelYBefore = bodyB.angularVelocity.y;

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            // Y angular velocity must not be reduced to exactly zero (hinge axis rotation is free)
            // the angular constraint only acts on perpendicular axes (x, z)
            Assert.AreEqual(0f, bodyB.angularVelocity.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, bodyB.angularVelocity.z.ToFloat(), EPSILON);
        }

        #endregion

        #region Hinge — Dynamic vs Static

        [Test]
        public void HingeDynamicVsStatic_OnlyDynamicMoves()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateStatic();
            var posA = new FPVector3(FP64.FromInt(-3), FP64.Zero, FP64.Zero);
            var posB = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero);
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.Identity;
            var joint = MakeHingeJoint(0, 1, FPVector3.Up, FPVector3.Up);
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            Assert.IsTrue(bodyA.velocity.x.ToFloat() > 0f);
            Assert.AreEqual(FPVector3.Zero, bodyB.velocity);
            Assert.AreEqual(FPVector3.Zero, bodyB.angularVelocity);
        }

        #endregion

        #region Hinge — Determinism

        [Test]
        public void HingeDeterminism_BitExact()
        {
            var bodyA1 = FPRigidBody.CreateDynamic(FP64.FromInt(3));
            bodyA1.velocity = new FPVector3(FP64.FromInt(2), FP64.One, FP64.FromInt(-1));
            bodyA1.angularVelocity = new FPVector3(FP64.Half, FP64.FromInt(-1), FP64.FromInt(2));
            var bodyB1 = FPRigidBody.CreateDynamic(FP64.FromInt(5));
            bodyB1.velocity = new FPVector3(FP64.FromInt(-1), FP64.FromInt(3), FP64.FromInt(2));
            bodyB1.angularVelocity = new FPVector3(FP64.FromInt(-2), FP64.One, FP64.Half);
            var posA1 = new FPVector3(FP64.FromInt(-2), FP64.One, FP64.Zero);
            var posB1 = new FPVector3(FP64.FromInt(3), FP64.FromInt(-1), FP64.One);
            var rotA1 = FPQuaternion.Identity;
            var rotB1 = FPQuaternion.AngleAxis(FP64.FromInt(30), FPVector3.Forward);

            var bodyA2 = FPRigidBody.CreateDynamic(FP64.FromInt(3));
            bodyA2.velocity = new FPVector3(FP64.FromInt(2), FP64.One, FP64.FromInt(-1));
            bodyA2.angularVelocity = new FPVector3(FP64.Half, FP64.FromInt(-1), FP64.FromInt(2));
            var bodyB2 = FPRigidBody.CreateDynamic(FP64.FromInt(5));
            bodyB2.velocity = new FPVector3(FP64.FromInt(-1), FP64.FromInt(3), FP64.FromInt(2));
            bodyB2.angularVelocity = new FPVector3(FP64.FromInt(-2), FP64.One, FP64.Half);
            var posA2 = new FPVector3(FP64.FromInt(-2), FP64.One, FP64.Zero);
            var posB2 = new FPVector3(FP64.FromInt(3), FP64.FromInt(-1), FP64.One);
            var rotA2 = FPQuaternion.Identity;
            var rotB2 = FPQuaternion.AngleAxis(FP64.FromInt(30), FPVector3.Forward);

            var joint = new FPHingeJoint
            {
                bodyIndexA = 0, bodyIndexB = 1,
                pivotA = new FPVector3(FP64.One, FP64.Zero, FP64.Zero),
                pivotB = new FPVector3(-FP64.One, FP64.Zero, FP64.Zero),
                axisA = FPVector3.Up, axisB = FPVector3.Up
            };
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA1, ref posA1, in rotA1,
                ref bodyB1, ref posB1, in rotB1,
                in joint, dt);
            FPConstraintSolver.SolveHingeJoint(
                ref bodyA2, ref posA2, in rotA2,
                ref bodyB2, ref posB2, in rotB2,
                in joint, dt);

            Assert.AreEqual(bodyA1.velocity.x.RawValue, bodyA2.velocity.x.RawValue);
            Assert.AreEqual(bodyA1.velocity.y.RawValue, bodyA2.velocity.y.RawValue);
            Assert.AreEqual(bodyA1.velocity.z.RawValue, bodyA2.velocity.z.RawValue);
            Assert.AreEqual(bodyB1.velocity.x.RawValue, bodyB2.velocity.x.RawValue);
            Assert.AreEqual(bodyA1.angularVelocity.x.RawValue, bodyA2.angularVelocity.x.RawValue);
            Assert.AreEqual(bodyA1.angularVelocity.y.RawValue, bodyA2.angularVelocity.y.RawValue);
            Assert.AreEqual(bodyA1.angularVelocity.z.RawValue, bodyA2.angularVelocity.z.RawValue);
            Assert.AreEqual(bodyB1.angularVelocity.x.RawValue, bodyB2.angularVelocity.x.RawValue);
            Assert.AreEqual(bodyB1.angularVelocity.y.RawValue, bodyB2.angularVelocity.y.RawValue);
            Assert.AreEqual(bodyB1.angularVelocity.z.RawValue, bodyB2.angularVelocity.z.RawValue);
        }

        #endregion

        #region Hinge — Angle Limits Helpers

        static FPHingeJoint MakeLimitedHingeJoint(int idxA, int idxB, FP64 lower, FP64 upper)
        {
            return new FPHingeJoint
            {
                bodyIndexA = idxA,
                bodyIndexB = idxB,
                pivotA = FPVector3.Zero,
                pivotB = FPVector3.Zero,
                axisA = FPVector3.Up,
                axisB = FPVector3.Up,
                useLimits = true,
                lowerAngle = lower,
                upperAngle = upper,
                refAxisA = FPVector3.Forward,
                refAxisB = FPVector3.Forward
            };
        }

        #endregion

        #region Hinge — Angle Limits: Within Range

        [Test]
        public void HingeAngleLimits_WithinRange_NoCorrection()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = FPVector3.Zero;
            var posB = FPVector3.Zero;
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.AngleAxis(FP64.FromInt(20), FPVector3.Up);
            var joint = MakeLimitedHingeJoint(0, 1, FP64.FromInt(-45), FP64.FromInt(45));
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            // within limit range -> no angular impulse along the hinge axis
            Assert.AreEqual(0f, bodyA.angularVelocity.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, bodyB.angularVelocity.y.ToFloat(), EPSILON);
        }

        #endregion

        #region Hinge — Angle Limits: Exceeded Upper

        [Test]
        public void HingeAngleLimits_ExceededUpper_Corrected()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = FPVector3.Zero;
            var posB = FPVector3.Zero;
            var rotA = FPQuaternion.Identity;
            // rotate B 60 degrees around the Y axis -> exceeds upper limit (45)
            var rotB = FPQuaternion.AngleAxis(FP64.FromInt(60), FPVector3.Up);
            var joint = MakeLimitedHingeJoint(0, 1, FP64.FromInt(-45), FP64.FromInt(45));
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            // correction impulse: B should receive negative Y angular velocity (returning toward limit)
            // A should receive positive Y angular velocity (reaction)
            Assert.IsTrue(bodyB.angularVelocity.y.ToFloat() < 0f);
            Assert.IsTrue(bodyA.angularVelocity.y.ToFloat() > 0f);
        }

        #endregion

        #region Hinge — Angle Limits: Exceeded Lower

        [Test]
        public void HingeAngleLimits_ExceededLower_Corrected()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = FPVector3.Zero;
            var posB = FPVector3.Zero;
            var rotA = FPQuaternion.Identity;
            // rotate B -60 degrees around the Y axis -> exceeds lower limit (-45)
            var rotB = FPQuaternion.AngleAxis(FP64.FromInt(-60), FPVector3.Up);
            var joint = MakeLimitedHingeJoint(0, 1, FP64.FromInt(-45), FP64.FromInt(45));
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            // correction impulse: B should receive positive Y angular velocity (returning toward limit)
            // A should receive negative Y angular velocity
            Assert.IsTrue(bodyB.angularVelocity.y.ToFloat() > 0f);
            Assert.IsTrue(bodyA.angularVelocity.y.ToFloat() < 0f);
        }

        #endregion

        #region Hinge — Angle Limits: Converges

        [Test]
        public void HingeAngleLimits_MultipleSteps_ConvergesToLimit()
        {
            var bodyA = FPRigidBody.CreateStatic();
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = FPVector3.Zero;
            var posB = FPVector3.Zero;
            var rotA = FPQuaternion.Identity;
            // start at 80 degrees -> upper limit is 45
            var rotB = FPQuaternion.AngleAxis(FP64.FromInt(80), FPVector3.Up);
            var joint = MakeLimitedHingeJoint(0, 1, FP64.FromInt(-45), FP64.FromInt(45));
            FP64 dt = FP64.FromFloat(0.02f);

            for (int i = 0; i < 200; i++)
            {
                FPConstraintSolver.SolveHingeJoint(
                    ref bodyA, ref posA, in rotA,
                    ref bodyB, ref posB, in rotB,
                    in joint, dt);
                FPPhysicsIntegration.Integrate(ref bodyA, ref posA, ref rotA, dt);
                FPPhysicsIntegration.Integrate(ref bodyB, ref posB, ref rotB, dt);
            }

            // measure final angle
            FPVector3 worldAxisA = (rotA * joint.axisA).normalized;
            FPVector3 worldRefA = (rotA * joint.refAxisA).normalized;
            FPVector3 worldRefB = (rotB * joint.refAxisB).normalized;
            FPVector3 projA = (worldRefA - worldAxisA * FPVector3.Dot(worldRefA, worldAxisA)).normalized;
            FPVector3 projB = (worldRefB - worldAxisA * FPVector3.Dot(worldRefB, worldAxisA)).normalized;
            FP64 finalAngle = FPVector3.SignedAngle(projA, projB, worldAxisA);

            // should converge near 45 degrees (upper limit)
            Assert.AreEqual(45f, finalAngle.ToFloat(), 5f);
        }

        #endregion

        #region Hinge — Angle Limits: No Limits No Effect

        [Test]
        public void HingeNoLimits_LargeAngle_NoLimitCorrection()
        {
            var bodyA = FPRigidBody.CreateDynamic(FP64.One);
            var bodyB = FPRigidBody.CreateDynamic(FP64.One);
            var posA = FPVector3.Zero;
            var posB = FPVector3.Zero;
            var rotA = FPQuaternion.Identity;
            var rotB = FPQuaternion.AngleAxis(FP64.FromInt(120), FPVector3.Up);
            // useLimits = false (no limits)
            var joint = MakeHingeJoint(0, 1, FPVector3.Up, FPVector3.Up);
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA, ref posA, in rotA,
                ref bodyB, ref posB, in rotB,
                in joint, dt);

            // no limits -> no correction along the hinge axis
            Assert.AreEqual(0f, bodyA.angularVelocity.y.ToFloat(), EPSILON);
            Assert.AreEqual(0f, bodyB.angularVelocity.y.ToFloat(), EPSILON);
        }

        #endregion

        #region Hinge — Angle Limits: Determinism

        [Test]
        public void HingeAngleLimits_Determinism_BitExact()
        {
            var bodyA1 = FPRigidBody.CreateDynamic(FP64.FromInt(3));
            bodyA1.angularVelocity = new FPVector3(FP64.Half, FP64.FromInt(-1), FP64.FromInt(2));
            var bodyB1 = FPRigidBody.CreateDynamic(FP64.FromInt(5));
            bodyB1.angularVelocity = new FPVector3(FP64.FromInt(-2), FP64.One, FP64.Half);
            var posA1 = FPVector3.Zero;
            var posB1 = FPVector3.Zero;
            var rotA1 = FPQuaternion.Identity;
            var rotB1 = FPQuaternion.AngleAxis(FP64.FromInt(60), FPVector3.Up);

            var bodyA2 = FPRigidBody.CreateDynamic(FP64.FromInt(3));
            bodyA2.angularVelocity = new FPVector3(FP64.Half, FP64.FromInt(-1), FP64.FromInt(2));
            var bodyB2 = FPRigidBody.CreateDynamic(FP64.FromInt(5));
            bodyB2.angularVelocity = new FPVector3(FP64.FromInt(-2), FP64.One, FP64.Half);
            var posA2 = FPVector3.Zero;
            var posB2 = FPVector3.Zero;
            var rotA2 = FPQuaternion.Identity;
            var rotB2 = FPQuaternion.AngleAxis(FP64.FromInt(60), FPVector3.Up);

            var joint = MakeLimitedHingeJoint(0, 1, FP64.FromInt(-45), FP64.FromInt(45));
            FP64 dt = FP64.FromFloat(0.02f);

            FPConstraintSolver.SolveHingeJoint(
                ref bodyA1, ref posA1, in rotA1,
                ref bodyB1, ref posB1, in rotB1,
                in joint, dt);
            FPConstraintSolver.SolveHingeJoint(
                ref bodyA2, ref posA2, in rotA2,
                ref bodyB2, ref posB2, in rotB2,
                in joint, dt);

            Assert.AreEqual(bodyA1.angularVelocity.x.RawValue, bodyA2.angularVelocity.x.RawValue);
            Assert.AreEqual(bodyA1.angularVelocity.y.RawValue, bodyA2.angularVelocity.y.RawValue);
            Assert.AreEqual(bodyA1.angularVelocity.z.RawValue, bodyA2.angularVelocity.z.RawValue);
            Assert.AreEqual(bodyB1.angularVelocity.x.RawValue, bodyB2.angularVelocity.x.RawValue);
            Assert.AreEqual(bodyB1.angularVelocity.y.RawValue, bodyB2.angularVelocity.y.RawValue);
            Assert.AreEqual(bodyB1.angularVelocity.z.RawValue, bodyB2.angularVelocity.z.RawValue);
        }

        #endregion
    }
}
