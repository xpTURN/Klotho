using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Fixed-point capsule collision shape. Defined by halfHeight, radius, position, and rotation.
    /// </summary>
    [Serializable]
    public struct FPCapsuleShape : IEquatable<FPCapsuleShape>
    {
        public FP64 halfHeight;
        public FP64 radius;
        public FPVector3 position;
        public FPQuaternion rotation;

        public FPCapsuleShape(FP64 halfHeight, FP64 radius, FPVector3 position, FPQuaternion rotation)
        {
            this.halfHeight = halfHeight;
            this.radius = radius;
            this.position = position;
            this.rotation = rotation;
        }

        public FPCapsuleShape(FP64 halfHeight, FP64 radius, FPVector3 position)
        {
            this.halfHeight = halfHeight;
            this.radius = radius;
            this.position = position;
            this.rotation = FPQuaternion.Identity;
        }

        public FP64 height => (halfHeight + radius) * FP64.FromInt(2);

        public void GetWorldPoints(out FPVector3 pointA, out FPVector3 pointB)
        {
            FPVector3 axis = rotation * FPVector3.Up;
            pointA = position - axis * halfHeight;
            pointB = position + axis * halfHeight;
        }

        public FPCapsule ToFPCapsule()
        {
            GetWorldPoints(out FPVector3 a, out FPVector3 b);
            return new FPCapsule(a, b, radius);
        }

        public FPBounds3 GetWorldBounds()
        {
            GetWorldPoints(out FPVector3 a, out FPVector3 b);

            FPVector3 r = new FPVector3(radius, radius, radius);
            FPVector3 mn = FPVector3.Min(a, b) - r;
            FPVector3 mx = FPVector3.Max(a, b) + r;

            var bounds = default(FPBounds3);
            bounds.SetMinMax(mn, mx);
            return bounds;
        }

        public bool Contains(FPVector3 point)
        {
            return ToFPCapsule().Contains(point);
        }

        public FPVector3 ClosestPoint(FPVector3 point)
        {
            return ToFPCapsule().ClosestPoint(point);
        }

        public FP64 SqrDistance(FPVector3 point)
        {
            return ToFPCapsule().SqrDistance(point);
        }

        public bool Equals(FPCapsuleShape other)
        {
            return halfHeight == other.halfHeight
                && radius == other.radius
                && position == other.position
                && rotation == other.rotation;
        }

        public override bool Equals(object obj) => obj is FPCapsuleShape other && Equals(other);

        public override int GetHashCode()
        {
            return HashCode.Combine(halfHeight, radius, position, rotation);
        }

        public static bool operator ==(FPCapsuleShape a, FPCapsuleShape b) => a.Equals(b);
        public static bool operator !=(FPCapsuleShape a, FPCapsuleShape b) => !a.Equals(b);

        public override string ToString()
        {
            return $"FPCapsuleShape(halfH:{halfHeight}, radius:{radius}, pos:{position}, rot:{rotation})";
        }
    }
}
