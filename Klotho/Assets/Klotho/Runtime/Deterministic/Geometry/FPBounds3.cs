using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    /// <summary>
    /// Fixed-point 3D axis-aligned bounding box. Defined by center and extents.
    /// </summary>
    [Serializable]
    public partial struct FPBounds3 : IEquatable<FPBounds3>
    {
        public FPVector3 center;
        public FPVector3 extents;

        public FPBounds3(FPVector3 center, FPVector3 size)
        {
            this.center = center;
            this.extents = size * FP64.Half;
        }

        public FPVector3 size => extents * FP64.FromInt(2);

        public FPVector3 min
        {
            get => center - extents;
            set
            {
                FPVector3 max = this.max;
                center = (value + max) * FP64.Half;
                extents = (max - value) * FP64.Half;
            }
        }

        public FPVector3 max
        {
            get => center + extents;
            set
            {
                FPVector3 min = this.min;
                center = (min + value) * FP64.Half;
                extents = (value - min) * FP64.Half;
            }
        }

        public void SetMinMax(FPVector3 min, FPVector3 max)
        {
            center = (min + max) * FP64.Half;
            extents = (max - min) * FP64.Half;
        }

        public bool Contains(FPVector3 point)
        {
            FPVector3 mn = min;
            FPVector3 mx = max;
            return point.x >= mn.x && point.x <= mx.x
                && point.y >= mn.y && point.y <= mx.y
                && point.z >= mn.z && point.z <= mx.z;
        }

        public bool Intersects(FPBounds3 other)
        {
            FPVector3 mn = min;
            FPVector3 mx = max;
            FPVector3 omn = other.min;
            FPVector3 omx = other.max;
            return mn.x <= omx.x && mx.x >= omn.x
                && mn.y <= omx.y && mx.y >= omn.y
                && mn.z <= omx.z && mx.z >= omn.z;
        }

        public void Encapsulate(FPVector3 point)
        {
            SetMinMax(FPVector3.Min(min, point), FPVector3.Max(max, point));
        }

        public void Encapsulate(FPBounds3 other)
        {
            SetMinMax(FPVector3.Min(min, other.min), FPVector3.Max(max, other.max));
        }

        public void Expand(FP64 amount)
        {
            FP64 half = amount * FP64.Half;
            extents = new FPVector3(extents.x + half, extents.y + half, extents.z + half);
        }

        public void Expand(FPVector3 amount)
        {
            FPVector3 half = amount * FP64.Half;
            extents = extents + half;
        }

        public FPVector3 ClosestPoint(FPVector3 point)
        {
            FPVector3 mn = min;
            FPVector3 mx = max;
            return new FPVector3(
                FP64.Clamp(point.x, mn.x, mx.x),
                FP64.Clamp(point.y, mn.y, mx.y),
                FP64.Clamp(point.z, mn.z, mx.z)
            );
        }

        public FP64 SqrDistance(FPVector3 point)
        {
            FPVector3 closest = ClosestPoint(point);
            return (point - closest).sqrMagnitude;
        }

        public bool IntersectRay(FPRay3 ray, out FP64 distance)
            => IntersectRay(ray, out distance, out _);

        public bool IntersectRay(FPRay3 ray, out FP64 distance, out FP64 tmin)
        {
            FPVector3 mn = min;
            FPVector3 mx = max;
            FPVector3 dirInv = new FPVector3(
                ray.direction.x != FP64.Zero ? FP64.One / ray.direction.x : FP64.MaxValue,
                ray.direction.y != FP64.Zero ? FP64.One / ray.direction.y : FP64.MaxValue,
                ray.direction.z != FP64.Zero ? FP64.One / ray.direction.z : FP64.MaxValue
            );

            FP64 t1 = (mn.x - ray.origin.x) * dirInv.x;
            FP64 t2 = (mx.x - ray.origin.x) * dirInv.x;
            FP64 t3 = (mn.y - ray.origin.y) * dirInv.y;
            FP64 t4 = (mx.y - ray.origin.y) * dirInv.y;
            FP64 t5 = (mn.z - ray.origin.z) * dirInv.z;
            FP64 t6 = (mx.z - ray.origin.z) * dirInv.z;

            tmin = FP64.Max(FP64.Max(FP64.Min(t1, t2), FP64.Min(t3, t4)), FP64.Min(t5, t6));
            FP64 tmax = FP64.Min(FP64.Min(FP64.Max(t1, t2), FP64.Max(t3, t4)), FP64.Max(t5, t6));

            if (tmax < FP64.Zero || tmin > tmax)
            {
                distance = FP64.Zero;
                return false;
            }

            distance = tmin < FP64.Zero ? tmax : tmin;
            return true;
        }

        public bool Equals(FPBounds3 other)
        {
            return center == other.center && extents == other.extents;
        }

        public override bool Equals(object obj)
        {
            return obj is FPBounds3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(center, extents);
        }

        public static bool operator ==(FPBounds3 a, FPBounds3 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(FPBounds3 a, FPBounds3 b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return $"Bounds3(center: {center}, extents: {extents})";
        }
    }
}
