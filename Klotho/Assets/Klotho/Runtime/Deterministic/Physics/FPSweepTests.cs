using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Collection of sweep test functions for CCD.
    /// </summary>
    public static class FPSweepTests
    {
        public static bool SweptSphereSphere(
            FPVector3 posA, FP64 radiusA, FPVector3 velA,
            FPVector3 posB, FP64 radiusB, FPVector3 velB,
            FP64 dt, out FP64 toi, out FPVector3 normal)
        {
            toi = FP64.Zero;
            normal = FPVector3.Up;

            FPVector3 d0 = posA - posB;
            FPVector3 vRel = velA - velB;
            FP64 R = radiusA + radiusB;

            FP64 a = FPVector3.Dot(vRel, vRel);
            FP64 b = FP64.FromInt(2) * FPVector3.Dot(d0, vRel);
            FP64 c = FPVector3.Dot(d0, d0) - R * R;

            if (c <= FP64.Zero)
            {
                toi = FP64.Zero;
                FP64 d0Mag = d0.magnitude;
                normal = d0Mag > FP64.Epsilon ? -d0 / d0Mag : FPVector3.Up;
                return true;
            }

            if (a == FP64.Zero)
                return false;

            FP64 discriminant = b * b - FP64.FromInt(4) * a * c;
            if (discriminant < FP64.Zero)
                return false;

            FP64 sqrtD = FP64.Sqrt(discriminant);
            toi = (-b - sqrtD) / (FP64.FromInt(2) * a);

            if (toi < FP64.Zero || toi > dt)
                return false;

            FPVector3 d = d0 + vRel * toi;
            FP64 dMag = d.magnitude;
            normal = dMag > FP64.Epsilon ? -d / dMag : FPVector3.Up;

            return true;
        }

        public static bool SweptSphereBox(
            FPVector3 spherePos, FP64 sphereRadius, FPVector3 sphereVel,
            ref FPBoxShape box, FP64 dt,
            out FP64 toi, out FPVector3 normal)
        {
            toi = FP64.Zero;
            normal = FPVector3.Up;

            FPQuaternion invRot = FPQuaternion.Inverse(box.rotation);
            FPVector3 localPos = invRot * (spherePos - box.position);
            FPVector3 localVel = invRot * sphereVel;

            FPVector3 expandedHalf = new FPVector3(
                box.halfExtents.x + sphereRadius,
                box.halfExtents.y + sphereRadius,
                box.halfExtents.z + sphereRadius);

            FP64 tEnter = FP64.Zero;
            FP64 tExit = dt;
            int hitAxis = -1;
            int hitSign = 1;

            // X slab
            if (!SlabTest(localPos.x, localVel.x, expandedHalf.x, ref tEnter, ref tExit, 0, ref hitAxis, ref hitSign))
                return false;
            // Y slab
            if (!SlabTest(localPos.y, localVel.y, expandedHalf.y, ref tEnter, ref tExit, 1, ref hitAxis, ref hitSign))
                return false;
            // Z slab
            if (!SlabTest(localPos.z, localVel.z, expandedHalf.z, ref tEnter, ref tExit, 2, ref hitAxis, ref hitSign))
                return false;

            if (tEnter > tExit || tEnter > dt)
                return false;

            if (tEnter < FP64.Zero)
                tEnter = FP64.Zero;

            toi = tEnter;

            FPVector3 localNormal = FPVector3.Zero;
            if (hitAxis == 0) localNormal = new FPVector3(hitSign == 1 ? -FP64.One : FP64.One, FP64.Zero, FP64.Zero);
            else if (hitAxis == 1) localNormal = new FPVector3(FP64.Zero, hitSign == 1 ? -FP64.One : FP64.One, FP64.Zero);
            else localNormal = new FPVector3(FP64.Zero, FP64.Zero, hitSign == 1 ? -FP64.One : FP64.One);

            normal = box.rotation * localNormal;

            return true;
        }

        static bool SlabTest(FP64 pos, FP64 vel, FP64 halfExtent,
            ref FP64 tEnter, ref FP64 tExit, int axis, ref int hitAxis, ref int hitSign)
        {
            if (FP64.Abs(vel) < FP64.Epsilon)
            {
                if (pos < -halfExtent || pos > halfExtent)
                    return false;
                return true;
            }

            FP64 invVel = FP64.One / vel;
            FP64 t1 = (-halfExtent - pos) * invVel;
            FP64 t2 = (halfExtent - pos) * invVel;

            int enterSign;
            if (t1 > t2)
            {
                FP64 tmp = t1; t1 = t2; t2 = tmp;
                enterSign = 1;
            }
            else
            {
                enterSign = -1;
            }

            if (t1 > tEnter)
            {
                tEnter = t1;
                hitAxis = axis;
                hitSign = enterSign;
            }
            if (t2 < tExit)
                tExit = t2;

            if (tEnter > tExit)
                return false;

            return true;
        }

        public static bool SweptSphereCapsule(
            FPVector3 spherePos, FP64 sphereRadius, FPVector3 sphereVel,
            ref FPCapsuleShape capsule, FP64 dt,
            out FP64 toi, out FPVector3 normal)
        {
            toi = FP64.Zero;
            normal = FPVector3.Up;

            capsule.GetWorldPoints(out FPVector3 capA, out FPVector3 capB);
            FP64 R = capsule.radius + sphereRadius;

            FPVector3 seg = capB - capA;
            FP64 segSqrLen = seg.sqrMagnitude;

            if (segSqrLen <= FP64.Epsilon)
            {
                return SweptSphereSphere(
                    spherePos, sphereRadius, sphereVel,
                    capA, capsule.radius, FPVector3.Zero,
                    dt, out toi, out normal);
            }

            FP64 bestToi = dt + FP64.One;
            FPVector3 bestNormal = FPVector3.Up;
            bool found = false;

            // End cap A
            if (SweptSphereSphere(spherePos, sphereRadius, sphereVel,
                capA, capsule.radius, FPVector3.Zero,
                dt, out FP64 t, out FPVector3 n))
            {
                if (t < bestToi) { bestToi = t; bestNormal = n; found = true; }
            }

            // End cap B
            if (SweptSphereSphere(spherePos, sphereRadius, sphereVel,
                capB, capsule.radius, FPVector3.Zero,
                dt, out t, out n))
            {
                if (t < bestToi) { bestToi = t; bestNormal = n; found = true; }
            }

            // Cylinder body: infinite cylinder along the segment
            // Project by removing the segment-direction component
            FPVector3 segDir = seg / FP64.Sqrt(segSqrLen);
            FPVector3 d0 = spherePos - capA;
            FPVector3 vPerp = sphereVel - segDir * FPVector3.Dot(sphereVel, segDir);
            FPVector3 d0Perp = d0 - segDir * FPVector3.Dot(d0, segDir);

            FP64 a = FPVector3.Dot(vPerp, vPerp);
            FP64 b = FP64.FromInt(2) * FPVector3.Dot(d0Perp, vPerp);
            FP64 c = FPVector3.Dot(d0Perp, d0Perp) - R * R;

            if (a > FP64.Epsilon)
            {
                FP64 disc = b * b - FP64.FromInt(4) * a * c;
                if (disc >= FP64.Zero)
                {
                    FP64 sqrtDisc = FP64.Sqrt(disc);
                    FP64 tCyl = (-b - sqrtDisc) / (FP64.FromInt(2) * a);

                    if (tCyl >= FP64.Zero && tCyl <= dt && tCyl < bestToi)
                    {
                        FPVector3 hitPos = spherePos + sphereVel * tCyl;
                        FP64 s = FPVector3.Dot(hitPos - capA, seg) / segSqrLen;

                        if (s >= FP64.Zero && s <= FP64.One)
                        {
                            FPVector3 closestOnSeg = capA + seg * s;
                            FPVector3 diff = hitPos - closestOnSeg;
                            FP64 diffMag = diff.magnitude;
                            bestToi = tCyl;
                            bestNormal = diffMag > FP64.Epsilon ? -diff / diffMag : FPVector3.Up;
                            found = true;
                        }
                    }
                }
            }

            if (found)
            {
                toi = bestToi;
                normal = bestNormal;
            }

            return found;
        }
    }
}
