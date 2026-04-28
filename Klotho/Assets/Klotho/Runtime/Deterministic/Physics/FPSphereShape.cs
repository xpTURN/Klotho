using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Fixed-point sphere collision shape. Defined by radius and position.
    /// </summary>
    [Serializable]
    public struct FPSphereShape : IEquatable<FPSphereShape>
    {
        public FP64 radius;
        public FPVector3 position;

        public FPSphereShape(FP64 radius, FPVector3 position)
        {
            this.radius = radius;
            this.position = position;
        }

        public FPBounds3 GetWorldBounds()
        {
            FP64 d = radius * FP64.FromInt(2);
            return new FPBounds3(position, new FPVector3(d, d, d));
        }

        public FPSphere ToFPSphere()
        {
            return new FPSphere(position, radius);
        }

        public bool Contains(FPVector3 point)
        {
            return (point - position).sqrMagnitude <= radius * radius;
        }

        public FPVector3 ClosestPoint(FPVector3 point)
        {
            FPVector3 d = point - position;
            FP64 sqrDist = d.sqrMagnitude;
            if (sqrDist <= radius * radius)
                return point;
            return position + d.normalized * radius;
        }

        public FP64 SqrDistance(FPVector3 point)
        {
            FP64 dist = (point - position).magnitude - radius;
            if (dist <= FP64.Zero)
                return FP64.Zero;
            return dist * dist;
        }

        public bool Equals(FPSphereShape other)
        {
            return radius == other.radius && position == other.position;
        }

        public override bool Equals(object obj) => obj is FPSphereShape other && Equals(other);

        public override int GetHashCode()
        {
            return HashCode.Combine(radius, position);
        }

        public static bool operator ==(FPSphereShape a, FPSphereShape b) => a.Equals(b);
        public static bool operator !=(FPSphereShape a, FPSphereShape b) => !a.Equals(b);

        public override string ToString()
        {
            return $"FPSphereShape(radius:{radius}, pos:{position})";
        }
    }
}
