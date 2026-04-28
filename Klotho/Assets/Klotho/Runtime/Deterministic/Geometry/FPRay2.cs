using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    /// <summary>
    /// Fixed-point 2D ray. Defined by origin and direction.
    /// </summary>
    [Serializable]
    public partial struct FPRay2 : IEquatable<FPRay2>
    {
        public FPVector2 origin;
        public FPVector2 direction;

        public FPRay2(FPVector2 origin, FPVector2 direction)
        {
            this.origin = origin;
            this.direction = direction;
        }

        public FPVector2 GetPoint(FP64 distance)
        {
            return origin + direction * distance;
        }

        public FPVector2 ClosestPoint(FPVector2 point)
        {
            FPVector2 diff = point - origin;
            FP64 t = FPVector2.Dot(diff, direction) / direction.sqrMagnitude;
            if (t < FP64.Zero)
                t = FP64.Zero;
            return origin + direction * t;
        }

        public FP64 SqrDistanceToPoint(FPVector2 point)
        {
            FPVector2 closest = ClosestPoint(point);
            return (point - closest).sqrMagnitude;
        }

        public FP64 DistanceToPoint(FPVector2 point)
        {
            return (point - ClosestPoint(point)).magnitude;
        }

        public bool Equals(FPRay2 other)
        {
            return origin == other.origin && direction == other.direction;
        }

        public override bool Equals(object obj)
        {
            return obj is FPRay2 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(origin, direction);
        }

        public static bool operator ==(FPRay2 a, FPRay2 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(FPRay2 a, FPRay2 b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return $"Ray2D(origin: {origin}, direction: {direction})";
        }
    }
}
