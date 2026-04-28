using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    /// <summary>
    /// Fixed-point plane. Defined by a normal and a distance.
    /// </summary>
    [Serializable]
    public partial struct FPPlane : IEquatable<FPPlane>
    {
        public FPVector3 normal;
        public FP64 distance;

        public FPPlane(FPVector3 normal, FP64 distance)
        {
            this.normal = normal;
            this.distance = distance;
        }

        public FPPlane(FPVector3 normal, FPVector3 point)
        {
            this.normal = normal;
            this.distance = -FPVector3.Dot(normal, point);
        }

        public static FPPlane Set3Points(FPVector3 a, FPVector3 b, FPVector3 c)
        {
            FPVector3 n = FPVector3.Cross(b - a, c - a).normalized;
            return new FPPlane(n, a);
        }

        public FPPlane flipped => new FPPlane(-normal, -distance);

        public FP64 GetDistanceToPoint(FPVector3 point)
        {
            return FPVector3.Dot(normal, point) + distance;
        }

        public bool GetSide(FPVector3 point)
        {
            return GetDistanceToPoint(point) > FP64.Zero;
        }

        public bool SameSide(FPVector3 a, FPVector3 b)
        {
            FP64 da = GetDistanceToPoint(a);
            FP64 db = GetDistanceToPoint(b);
            return (da > FP64.Zero && db > FP64.Zero) || (da <= FP64.Zero && db <= FP64.Zero);
        }

        public FPVector3 ClosestPointOnPlane(FPVector3 point)
        {
            FP64 d = GetDistanceToPoint(point);
            return point - normal * d;
        }

        public bool Raycast(FPRay3 ray, out FP64 enter)
        {
            FP64 denom = FPVector3.Dot(normal, ray.direction);
            if (denom == FP64.Zero)
            {
                enter = FP64.Zero;
                return false;
            }

            FP64 t = -(FPVector3.Dot(normal, ray.origin) + distance) / denom;
            if (t < FP64.Zero)
            {
                enter = FP64.Zero;
                return false;
            }

            enter = t;
            return true;
        }

        public bool Equals(FPPlane other)
        {
            return normal == other.normal && distance == other.distance;
        }

        public override bool Equals(object obj)
        {
            return obj is FPPlane other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(normal, distance);
        }

        public static bool operator ==(FPPlane a, FPPlane b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(FPPlane a, FPPlane b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return $"Plane(normal: {normal}, distance: {distance})";
        }
    }
}
