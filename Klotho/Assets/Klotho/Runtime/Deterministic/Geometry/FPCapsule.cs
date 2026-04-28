using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    /// <summary>
    /// Fixed-point capsule. Defined by two endpoints (pointA, pointB) and radius.
    /// </summary>
    [Serializable]
    public struct FPCapsule : IEquatable<FPCapsule>
    {
        public FPVector3 pointA;
        public FPVector3 pointB;
        public FP64 radius;

        public FPCapsule(FPVector3 pointA, FPVector3 pointB, FP64 radius)
        {
            this.pointA = pointA;
            this.pointB = pointB;
            this.radius = radius;
        }

        public FPVector3 center => (pointA + pointB) * FP64.Half;
        public FPVector3 direction => pointB - pointA;
        public FP64 segmentLength => direction.magnitude;
        public FP64 height => segmentLength + radius * FP64.FromInt(2);

        public static FPCapsule FromCenterDirection(FPVector3 center, FPVector3 direction, FP64 radius)
        {
            return new FPCapsule(center - direction, center + direction, radius);
        }

        public static FPCapsule FromCenterAxisHeight(FPVector3 center, FPVector3 axis, FP64 height, FP64 radius)
        {
            FPVector3 n = axis.normalized;
            FP64 halfSeg = (height - radius * FP64.FromInt(2)) * FP64.Half;
            if (halfSeg < FP64.Zero)
                halfSeg = FP64.Zero;
            return new FPCapsule(center - n * halfSeg, center + n * halfSeg, radius);
        }

        public FPVector3 ClosestPointOnSegment(FPVector3 point)
        {
            FPVector3 ab = pointB - pointA;
            FP64 sqrLen = ab.sqrMagnitude;
            if (sqrLen == FP64.Zero)
                return pointA;
            FP64 t = FPVector3.Dot(point - pointA, ab) / sqrLen;
            t = FP64.Clamp01(t);
            return pointA + ab * t;
        }

        public bool Contains(FPVector3 point)
        {
            FPVector3 closest = ClosestPointOnSegment(point);
            return (point - closest).sqrMagnitude <= radius * radius;
        }

        public FPVector3 ClosestPoint(FPVector3 point)
        {
            FPVector3 segClosest = ClosestPointOnSegment(point);
            FPVector3 diff = point - segClosest;
            FP64 sqrMag = diff.sqrMagnitude;
            if (sqrMag <= radius * radius)
                return point;
            return segClosest + diff.normalized * radius;
        }

        public FP64 SqrDistance(FPVector3 point)
        {
            FPVector3 segClosest = ClosestPointOnSegment(point);
            FP64 dist = (point - segClosest).magnitude;
            if (dist <= radius)
                return FP64.Zero;
            FP64 surfaceDist = dist - radius;
            return surfaceDist * surfaceDist;
        }

        public bool Intersects(FPSphere sphere)
        {
            FPVector3 closest = ClosestPointOnSegment(sphere.center);
            FP64 rSum = radius + sphere.radius;
            return (sphere.center - closest).sqrMagnitude <= rSum * rSum;
        }

        public bool Intersects(FPCapsule other)
        {
            FP64 sqrDist = SegmentSegmentSqrDistance(pointA, pointB, other.pointA, other.pointB);
            FP64 rSum = radius + other.radius;
            return sqrDist <= rSum * rSum;
        }

        public bool Intersects(FPBounds3 bounds)
        {
            FPVector3 closest = ClosestPointOnSegment(bounds.ClosestPoint(pointA));
            if ((bounds.ClosestPoint(closest) - closest).sqrMagnitude <= radius * radius)
                return true;

            closest = ClosestPointOnSegment(bounds.ClosestPoint(pointB));
            if ((bounds.ClosestPoint(closest) - closest).sqrMagnitude <= radius * radius)
                return true;

            // Sample the midpoint of the segment passing through the box
            FPVector3 mid = center;
            FPVector3 boundsClosestToMid = bounds.ClosestPoint(mid);
            FPVector3 segClosestToBC = ClosestPointOnSegment(boundsClosestToMid);
            return (bounds.ClosestPoint(segClosestToBC) - segClosestToBC).sqrMagnitude <= radius * radius;
        }

        public FPBounds3 GetBounds()
        {
            FPVector3 r = new FPVector3(radius, radius, radius);
            FPVector3 mn = FPVector3.Min(pointA, pointB) - r;
            FPVector3 mx = FPVector3.Max(pointA, pointB) + r;
            FPVector3 c = (mn + mx) * FP64.Half;
            FPVector3 size = mx - mn;
            return new FPBounds3(c, size);
        }

        private static FP64 SegmentSegmentSqrDistance(FPVector3 p1, FPVector3 q1, FPVector3 p2, FPVector3 q2)
        {
            FPVector3 d1 = q1 - p1;
            FPVector3 d2 = q2 - p2;
            FPVector3 r = p1 - p2;

            FP64 a = FPVector3.Dot(d1, d1);
            FP64 e = FPVector3.Dot(d2, d2);
            FP64 f = FPVector3.Dot(d2, r);

            FP64 s, t;

            if (a <= FP64.Epsilon && e <= FP64.Epsilon)
            {
                return (p1 - p2).sqrMagnitude;
            }

            if (a <= FP64.Epsilon)
            {
                s = FP64.Zero;
                t = FP64.Clamp01(f / e);
            }
            else
            {
                FP64 c = FPVector3.Dot(d1, r);
                if (e <= FP64.Epsilon)
                {
                    t = FP64.Zero;
                    s = FP64.Clamp01(-c / a);
                }
                else
                {
                    FP64 b = FPVector3.Dot(d1, d2);
                    FP64 denom = a * e - b * b;

                    if (denom != FP64.Zero)
                        s = FP64.Clamp01((b * f - c * e) / denom);
                    else
                        s = FP64.Zero;

                    t = (b * s + f) / e;

                    if (t < FP64.Zero)
                    {
                        t = FP64.Zero;
                        s = FP64.Clamp01(-c / a);
                    }
                    else if (t > FP64.One)
                    {
                        t = FP64.One;
                        s = FP64.Clamp01((b - c) / a);
                    }
                }
            }

            FPVector3 closest1 = p1 + d1 * s;
            FPVector3 closest2 = p2 + d2 * t;
            return (closest1 - closest2).sqrMagnitude;
        }

        public bool Equals(FPCapsule other)
        {
            return pointA == other.pointA && pointB == other.pointB && radius == other.radius;
        }

        public override bool Equals(object obj)
        {
            return obj is FPCapsule other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(pointA, pointB, radius);
        }

        public static bool operator ==(FPCapsule a, FPCapsule b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(FPCapsule a, FPCapsule b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return $"Capsule(A: {pointA}, B: {pointB}, radius: {radius})";
        }
    }
}
