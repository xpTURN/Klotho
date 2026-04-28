using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Computes impulses and positional correction for collision contact points.
    /// </summary>
    public static class FPCollisionResponse
    {
        static readonly FP64 Slop = FP64.FromDouble(0.01);
        static readonly FP64 CorrectionPercent = FP64.FromDouble(0.8);

        public static void ResolveContact(
            ref FPRigidBody bodyA, ref FPVector3 positionA,
            ref FPRigidBody bodyB, ref FPVector3 positionB,
            in FPContact contact)
        {
            ResolveContact(ref bodyA, ref positionA, ref bodyB, ref positionB, in contact, FP64.Zero);
        }

        public static void ResolveContact(
            ref FPRigidBody bodyA, ref FPVector3 positionA,
            ref FPRigidBody bodyB, ref FPVector3 positionB,
            in FPContact contact, FP64 dt)
        {
            bool immovableA = bodyA.isStatic || bodyA.isKinematic;
            bool immovableB = bodyB.isStatic || bodyB.isKinematic;
            if (immovableA && immovableB)
                return;

            FP64 invMassA = bodyA.inverseMass;
            FP64 invMassB = bodyB.inverseMass;
            FP64 invMassSum = invMassA + invMassB;

            if (invMassSum == FP64.Zero)
                return;

            FPVector3 normal = contact.normal;
            FPVector3 rA = contact.point - positionA;
            FPVector3 rB = contact.point - positionB;

            FPVector3 velAtA = bodyA.velocity + FPVector3.Cross(bodyA.angularVelocity, rA);
            FPVector3 velAtB = bodyB.velocity + FPVector3.Cross(bodyB.angularVelocity, rB);
            FPVector3 vRel = velAtA - velAtB;

            FP64 vRelNormal = FPVector3.Dot(vRel, normal);

            if (contact.isSpeculative)
            {
                if (dt == FP64.Zero || vRelNormal <= FP64.Zero)
                    return;

                FPVector3 crossRA = FPVector3.Cross(rA, normal);
                FPVector3 crossRB = FPVector3.Cross(rB, normal);
                FP64 angTermA = FPVector3.Dot(FPVector3.Cross(crossRA * invMassA, rA), normal);
                FP64 angTermB = FPVector3.Dot(FPVector3.Cross(crossRB * invMassB, rB), normal);
                FP64 denom = invMassSum + angTermA + angTermB;

                if (denom == FP64.Zero)
                    return;

                FP64 maxApproachVel = -contact.depth / dt;
                if (vRelNormal <= maxApproachVel)
                    return;

                FP64 j = -(vRelNormal - maxApproachVel) / denom;
                FPVector3 impulse = normal * j;

                if (!immovableA)
                {
                    bodyA.velocity = bodyA.velocity + impulse * invMassA;
                    bodyA.angularVelocity = bodyA.angularVelocity + FPVector3.Cross(rA, impulse) * invMassA;
                }
                if (!immovableB)
                {
                    bodyB.velocity = bodyB.velocity - impulse * invMassB;
                    bodyB.angularVelocity = bodyB.angularVelocity - FPVector3.Cross(rB, impulse) * invMassB;
                }
                return;
            }

            if (vRelNormal < FP64.Zero)
            {
                CorrectPosition(ref positionA, ref positionB, invMassA, invMassB, invMassSum, normal, contact.depth);
                return;
            }

            FP64 restitution = FP64.Min(bodyA.restitution, bodyB.restitution);

            {
                FPVector3 crossRA = FPVector3.Cross(rA, normal);
                FPVector3 crossRB = FPVector3.Cross(rB, normal);
                FP64 angTermA = FPVector3.Dot(FPVector3.Cross(crossRA * invMassA, rA), normal);
                FP64 angTermB = FPVector3.Dot(FPVector3.Cross(crossRB * invMassB, rB), normal);
                FP64 denom = invMassSum + angTermA + angTermB;

                if (denom == FP64.Zero)
                {
                    CorrectPosition(ref positionA, ref positionB, invMassA, invMassB, invMassSum, normal, contact.depth);
                    return;
                }

                FP64 j = -(FP64.One + restitution) * vRelNormal / denom;
                FPVector3 impulse = normal * j;

                if (!immovableA)
                {
                    bodyA.velocity = bodyA.velocity + impulse * invMassA;
                    bodyA.angularVelocity = bodyA.angularVelocity + FPVector3.Cross(rA, impulse) * invMassA;
                }
                if (!immovableB)
                {
                    bodyB.velocity = bodyB.velocity - impulse * invMassB;
                    bodyB.angularVelocity = bodyB.angularVelocity - FPVector3.Cross(rB, impulse) * invMassB;
                }

                FPVector3 tangent = vRel - normal * vRelNormal;
                FP64 tangentSqrMag = tangent.sqrMagnitude;

                if (tangentSqrMag > FP64.Epsilon)
                {
                    tangent = tangent.normalized;
                    FP64 jt = -FPVector3.Dot(vRel, tangent) / denom;

                    FP64 combinedFriction = FP64.Sqrt(bodyA.friction * bodyB.friction);
                    FP64 absJ = FP64.Abs(j);
                    FP64 frictionBound = combinedFriction * absJ;

                    jt = FP64.Clamp(jt, -frictionBound, frictionBound);

                    FPVector3 frictionImpulse = tangent * jt;

                    if (!immovableA)
                    {
                        bodyA.velocity = bodyA.velocity + frictionImpulse * invMassA;
                        bodyA.angularVelocity = bodyA.angularVelocity + FPVector3.Cross(rA, frictionImpulse) * invMassA;
                    }
                    if (!immovableB)
                    {
                        bodyB.velocity = bodyB.velocity - frictionImpulse * invMassB;
                        bodyB.angularVelocity = bodyB.angularVelocity - FPVector3.Cross(rB, frictionImpulse) * invMassB;
                    }
                }

                CorrectPosition(ref positionA, ref positionB, invMassA, invMassB, invMassSum, normal, contact.depth);
            }
        }

        static void CorrectPosition(
            ref FPVector3 positionA, ref FPVector3 positionB,
            FP64 invMassA, FP64 invMassB, FP64 invMassSum,
            FPVector3 normal, FP64 depth)
        {
            if (depth <= Slop || invMassSum == FP64.Zero)
                return;

            FP64 correctionMag = (depth - Slop) * CorrectionPercent / invMassSum;
            FPVector3 correction = normal * correctionMag;

            positionA = positionA - correction * invMassA;
            positionB = positionB + correction * invMassB;
        }
    }
}
