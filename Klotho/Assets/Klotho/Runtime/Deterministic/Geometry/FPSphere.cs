using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    /// <summary>
    /// Fixed-point sphere. Defined by center and radius.
    /// </summary>
    [Serializable]
    public struct FPSphere : IEquatable<FPSphere>
    {
        public FPVector3 center;
        public FP64 radius;

        public FPSphere(FPVector3 center, FP64 radius)
        {
            this.center = center;
            this.radius = radius;
        }

        public FP64 diameter => radius * FP64.FromInt(2);

        public bool Contains(FPVector3 point)
        {
            return (point - center).sqrMagnitude <= radius * radius;
        }

        public bool Contains(FPSphere other)
        {
            FP64 dist = (other.center - center).magnitude;
            return dist + other.radius <= radius;
        }

        public bool Intersects(FPSphere other)
        {
            FP64 rSum = radius + other.radius;
            return (center - other.center).sqrMagnitude <= rSum * rSum;
        }

        public bool Intersects(FPBounds3 bounds)
        {
            FPVector3 closest = bounds.ClosestPoint(center);
            return (closest - center).sqrMagnitude <= radius * radius;
        }

        public FPVector3 ClosestPoint(FPVector3 point)
        {
            FPVector3 diff = point - center;
            FP64 sqrMag = diff.sqrMagnitude;
            if (sqrMag <= radius * radius)
                return point;
            return center + diff.normalized * radius;
        }

        public FP64 SqrDistance(FPVector3 point)
        {
            FPVector3 diff = point - center;
            FP64 dist = diff.magnitude;
            if (dist <= radius)
                return FP64.Zero;
            FP64 surfaceDist = dist - radius;
            return surfaceDist * surfaceDist;
        }

        public void Encapsulate(FPVector3 point)
        {
            FPVector3 diff = point - center;
            FP64 dist = diff.magnitude;
            if (dist <= radius)
                return;
            FP64 newRadius = (radius + dist) * FP64.Half;
            FP64 offset = newRadius - radius;
            center = center + diff / dist * offset;
            radius = newRadius;
        }

        public void Encapsulate(FPSphere other)
        {
            FPVector3 diff = other.center - center;
            FP64 dist = diff.magnitude;

            if (dist + other.radius <= radius)
                return;
            if (dist + radius <= other.radius)
            {
                center = other.center;
                radius = other.radius;
                return;
            }

            FP64 newRadius = (dist + radius + other.radius) * FP64.Half;
            if (dist > FP64.Zero)
                center = center + diff / dist * (newRadius - radius);
            radius = newRadius;
        }

        public FPBounds3 GetBounds()
        {
            FPVector3 size = new FPVector3(diameter, diameter, diameter);
            return new FPBounds3(center, size);
        }

        public static FPSphere CreateFromPoints(FPVector3[] points)
        {
            if (points == null || points.Length == 0)
                return new FPSphere(FPVector3.Zero, FP64.Zero);

            FPVector3 mn = points[0];
            FPVector3 mx = points[0];
            for (int i = 1; i < points.Length; i++)
            {
                mn = FPVector3.Min(mn, points[i]);
                mx = FPVector3.Max(mx, points[i]);
            }

            FPVector3 c = (mn + mx) * FP64.Half;
            FP64 maxSqrDist = FP64.Zero;
            for (int i = 0; i < points.Length; i++)
            {
                FP64 sqrDist = (points[i] - c).sqrMagnitude;
                if (sqrDist > maxSqrDist)
                    maxSqrDist = sqrDist;
            }

            FP64 r = FP64.Sqrt(maxSqrDist);
            // Ensure r*r >= maxSqrDist even with fixed-point rounding
            if (r * r < maxSqrDist)
                r = FP64.FromRaw(r.RawValue + 1);
            return new FPSphere(c, r);
        }

        public bool Equals(FPSphere other)
        {
            return center == other.center && radius == other.radius;
        }

        public override bool Equals(object obj)
        {
            return obj is FPSphere other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(center, radius);
        }

        public static bool operator ==(FPSphere a, FPSphere b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(FPSphere a, FPSphere b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return $"Sphere(center: {center}, radius: {radius})";
        }
    }
}
