using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    /// <summary>
    /// Fixed-point 3D ray. Defined by origin and direction.
    /// </summary>
    [Serializable]
    public partial struct FPRay3 : IEquatable<FPRay3>
    {
        public FPVector3 origin;
        public FPVector3 direction;

        public FPRay3(FPVector3 origin, FPVector3 direction)
        {
            this.origin = origin;
            this.direction = direction;
        }

        public FPVector3 GetPoint(FP64 distance)
        {
            return origin + direction * distance;
        }

        public FPVector3 ClosestPoint(FPVector3 point)
        {
            FPVector3 diff = point - origin;
            FP64 t = FPVector3.Dot(diff, direction) / direction.sqrMagnitude;
            if (t < FP64.Zero)
                t = FP64.Zero;
            return origin + direction * t;
        }

        public FP64 SqrDistanceToPoint(FPVector3 point)
        {
            FPVector3 closest = ClosestPoint(point);
            return (point - closest).sqrMagnitude;
        }

        public FP64 DistanceToPoint(FPVector3 point)
        {
            return (point - ClosestPoint(point)).magnitude;
        }

        public bool Equals(FPRay3 other)
        {
            return origin == other.origin && direction == other.direction;
        }

        public override bool Equals(object obj)
        {
            return obj is FPRay3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(origin, direction);
        }

        public static bool operator ==(FPRay3 a, FPRay3 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(FPRay3 a, FPRay3 b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return $"Ray(origin: {origin}, direction: {direction})";
        }
    }
}
