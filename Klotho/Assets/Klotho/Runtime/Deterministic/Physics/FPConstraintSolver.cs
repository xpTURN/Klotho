using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Joint and contact constraint solver.
    /// </summary>
    public static class FPConstraintSolver
    {
        static readonly FP64 BaumgarteBeta = FP64.FromDouble(0.2);
        static readonly FP64 TangentFrameThreshold = FP64.FromDouble(0.9);

        public static void SolveDistanceJoint(
            ref FPRigidBody bodyA, ref FPVector3 positionA, in FPQuaternion rotationA,
            ref FPRigidBody bodyB, ref FPVector3 positionB, in FPQuaternion rotationB,
            in FPDistanceJoint joint, FP64 dt)
        {
            if (dt <= FP64.Zero)
                return;

            bool immovableA = bodyA.isStatic || bodyA.isKinematic;
            bool immovableB = bodyB.isStatic || bodyB.isKinematic;
            if (immovableA && immovableB)
                return;

            FP64 invMassA = bodyA.inverseMass;
            FP64 invMassB = bodyB.inverseMass;
            FP64 invMassSum = invMassA + invMassB;

            if (invMassSum == FP64.Zero)
                return;

            FPVector3 worldAnchorA = positionA + rotationA * joint.anchorA;
            FPVector3 worldAnchorB = positionB + rotationB * joint.anchorB;

            FPVector3 delta = worldAnchorB - worldAnchorA;
            FP64 currentDist = delta.magnitude;

            if (currentDist <= FP64.Epsilon)
                return;

            FPVector3 n = delta / currentDist;
            FP64 error = currentDist - joint.distance;

            FPVector3 rA = worldAnchorA - positionA;
            FPVector3 rB = worldAnchorB - positionB;

            FPVector3 velAtA = bodyA.velocity + FPVector3.Cross(bodyA.angularVelocity, rA);
            FPVector3 velAtB = bodyB.velocity + FPVector3.Cross(bodyB.angularVelocity, rB);
            FP64 relVel = FPVector3.Dot(velAtB - velAtA, n);

            FP64 bias = (BaumgarteBeta / dt) * error;

            FPVector3 crossRA = FPVector3.Cross(rA, n);
            FPVector3 crossRB = FPVector3.Cross(rB, n);
            FP64 angTermA = FPVector3.Dot(FPVector3.Cross(crossRA * invMassA, rA), n);
            FP64 angTermB = FPVector3.Dot(FPVector3.Cross(crossRB * invMassB, rB), n);
            FP64 effectiveMass = invMassSum + angTermA + angTermB;

            if (effectiveMass <= FP64.Epsilon)
                return;

            FP64 lambda = -(relVel + bias) / effectiveMass;
            FPVector3 impulse = n * lambda;

            if (!immovableA)
            {
                bodyA.velocity = bodyA.velocity - impulse * invMassA;
                bodyA.angularVelocity = bodyA.angularVelocity - FPVector3.Cross(rA, impulse) * invMassA;
            }
            if (!immovableB)
            {
                bodyB.velocity = bodyB.velocity + impulse * invMassB;
                bodyB.angularVelocity = bodyB.angularVelocity + FPVector3.Cross(rB, impulse) * invMassB;
            }
        }
        public static void SolveHingeJoint(
            ref FPRigidBody bodyA, ref FPVector3 positionA, in FPQuaternion rotationA,
            ref FPRigidBody bodyB, ref FPVector3 positionB, in FPQuaternion rotationB,
            in FPHingeJoint joint, FP64 dt)
        {
            if (dt <= FP64.Zero)
                return;

            bool immovableA = bodyA.isStatic || bodyA.isKinematic;
            bool immovableB = bodyB.isStatic || bodyB.isKinematic;
            if (immovableA && immovableB)
                return;

            FP64 invMassA = bodyA.inverseMass;
            FP64 invMassB = bodyB.inverseMass;
            FP64 invMassSum = invMassA + invMassB;

            if (invMassSum == FP64.Zero)
                return;

            // Part 1: point-to-point (3 linear DOF)
            FPVector3 worldPivotA = positionA + rotationA * joint.pivotA;
            FPVector3 worldPivotB = positionB + rotationB * joint.pivotB;
            FPVector3 rA = worldPivotA - positionA;
            FPVector3 rB = worldPivotB - positionB;
            FPVector3 error = worldPivotB - worldPivotA;

            SolveLinearAxis(ref bodyA, ref bodyB, rA, rB, error,
                FPVector3.Right, invMassA, invMassB, invMassSum,
                immovableA, immovableB, dt);
            SolveLinearAxis(ref bodyA, ref bodyB, rA, rB, error,
                FPVector3.Up, invMassA, invMassB, invMassSum,
                immovableA, immovableB, dt);
            SolveLinearAxis(ref bodyA, ref bodyB, rA, rB, error,
                FPVector3.Forward, invMassA, invMassB, invMassSum,
                immovableA, immovableB, dt);

            // Part 2: angular (2 rotational DOF)
            FPVector3 worldAxisA = (rotationA * joint.axisA).normalized;
            FPVector3 worldAxisB = (rotationB * joint.axisB).normalized;

            BuildTangentFrame(worldAxisA, out FPVector3 t1, out FPVector3 t2);

            SolveAngularAxis(ref bodyA, ref bodyB, worldAxisB,
                t1, invMassA, invMassB, invMassSum,
                immovableA, immovableB, dt);
            SolveAngularAxis(ref bodyA, ref bodyB, worldAxisB,
                t2, invMassA, invMassB, invMassSum,
                immovableA, immovableB, dt);

            // Part 3: angular limits
            if (joint.useLimits)
            {
                FPVector3 worldRefA = (rotationA * joint.refAxisA).normalized;
                FPVector3 worldRefB = (rotationB * joint.refAxisB).normalized;

                FPVector3 projA = (worldRefA - worldAxisA * FPVector3.Dot(worldRefA, worldAxisA)).normalized;
                FPVector3 projB = (worldRefB - worldAxisA * FPVector3.Dot(worldRefB, worldAxisA)).normalized;

                FP64 angle = FPVector3.SignedAngle(projA, projB, worldAxisA);

                FP64 angError = FP64.Zero;
                if (angle < joint.lowerAngle)
                    angError = angle - joint.lowerAngle;
                else if (angle > joint.upperAngle)
                    angError = angle - joint.upperAngle;

                if (angError != FP64.Zero)
                {
                    FP64 angErrorRad = angError * FP64.Deg2Rad;
                    FP64 Cdot = FPVector3.Dot(bodyB.angularVelocity - bodyA.angularVelocity, worldAxisA);
                    FP64 bias = (BaumgarteBeta / dt) * angErrorRad;

                    FP64 effectiveMass = invMassSum;
                    if (effectiveMass <= FP64.Epsilon)
                        return;

                    FP64 lambda = -(Cdot + bias) / effectiveMass;
                    FPVector3 angImpulse = worldAxisA * lambda;

                    if (!immovableA)
                        bodyA.angularVelocity = bodyA.angularVelocity - angImpulse * invMassA;
                    if (!immovableB)
                        bodyB.angularVelocity = bodyB.angularVelocity + angImpulse * invMassB;
                }
            }
        }

        static void SolveLinearAxis(
            ref FPRigidBody bodyA, ref FPRigidBody bodyB,
            FPVector3 rA, FPVector3 rB, FPVector3 error,
            FPVector3 axis,
            FP64 invMassA, FP64 invMassB, FP64 invMassSum,
            bool immovableA, bool immovableB, FP64 dt)
        {
            FPVector3 velAtA = bodyA.velocity + FPVector3.Cross(bodyA.angularVelocity, rA);
            FPVector3 velAtB = bodyB.velocity + FPVector3.Cross(bodyB.angularVelocity, rB);
            FP64 Cdot = FPVector3.Dot(velAtB - velAtA, axis);

            FP64 posError = FPVector3.Dot(error, axis);
            FP64 bias = (BaumgarteBeta / dt) * posError;

            FPVector3 crossRA = FPVector3.Cross(rA, axis);
            FPVector3 crossRB = FPVector3.Cross(rB, axis);
            FP64 angTermA = FPVector3.Dot(FPVector3.Cross(crossRA * invMassA, rA), axis);
            FP64 angTermB = FPVector3.Dot(FPVector3.Cross(crossRB * invMassB, rB), axis);
            FP64 effectiveMass = invMassSum + angTermA + angTermB;

            if (effectiveMass <= FP64.Epsilon)
                return;

            FP64 lambda = -(Cdot + bias) / effectiveMass;
            FPVector3 impulse = axis * lambda;

            if (!immovableA)
            {
                bodyA.velocity = bodyA.velocity - impulse * invMassA;
                bodyA.angularVelocity = bodyA.angularVelocity - FPVector3.Cross(rA, impulse) * invMassA;
            }
            if (!immovableB)
            {
                bodyB.velocity = bodyB.velocity + impulse * invMassB;
                bodyB.angularVelocity = bodyB.angularVelocity + FPVector3.Cross(rB, impulse) * invMassB;
            }
        }

        static void SolveAngularAxis(
            ref FPRigidBody bodyA, ref FPRigidBody bodyB,
            FPVector3 worldAxisB, FPVector3 tangent,
            FP64 invMassA, FP64 invMassB, FP64 invMassSum,
            bool immovableA, bool immovableB, FP64 dt)
        {
            FP64 angError = FPVector3.Dot(worldAxisB, tangent);
            FP64 Cdot = FPVector3.Dot(bodyB.angularVelocity - bodyA.angularVelocity, tangent);
            FP64 bias = (BaumgarteBeta / dt) * angError;

            FP64 effectiveMass = invMassSum;
            if (effectiveMass <= FP64.Epsilon)
                return;

            FP64 lambda = -(Cdot + bias) / effectiveMass;
            FPVector3 angImpulse = tangent * lambda;

            if (!immovableA)
                bodyA.angularVelocity = bodyA.angularVelocity - angImpulse * invMassA;
            if (!immovableB)
                bodyB.angularVelocity = bodyB.angularVelocity + angImpulse * invMassB;
        }

        static void BuildTangentFrame(FPVector3 axis, out FPVector3 t1, out FPVector3 t2)
        {
            FPVector3 seed;
            FP64 absX = FP64.Abs(axis.x);
            if (absX < TangentFrameThreshold)
                seed = FPVector3.Right;
            else
                seed = FPVector3.Up;

            FPVector3 normal = axis;
            t1 = seed;
            FPVector3.OrthoNormalize(ref normal, ref t1);
            t2 = FPVector3.Cross(normal, t1);
        }
    }
}
