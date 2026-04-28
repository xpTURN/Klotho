using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    /// <summary>
    /// Fixed-point 2D axis-aligned bounding box. Defined by center and extents.
    /// </summary>
    [Serializable]
    public partial struct FPBounds2 : IEquatable<FPBounds2>
    {
        public FPVector2 center;
        public FPVector2 extents;

        public FPBounds2(FPVector2 center, FPVector2 size)
        {
            this.center = center;
            this.extents = size * FP64.Half;
        }

        public FPVector2 size => extents * FP64.FromInt(2);

        public FPVector2 min
        {
            get => center - extents;
            set
            {
                FPVector2 max = this.max;
                center = (value + max) * FP64.Half;
                extents = (max - value) * FP64.Half;
            }
        }

        public FPVector2 max
        {
            get => center + extents;
            set
            {
                FPVector2 min = this.min;
                center = (min + value) * FP64.Half;
                extents = (value - min) * FP64.Half;
            }
        }

        public void SetMinMax(FPVector2 min, FPVector2 max)
        {
            center = (min + max) * FP64.Half;
            extents = (max - min) * FP64.Half;
        }

        public bool Contains(FPVector2 point)
        {
            FPVector2 mn = min;
            FPVector2 mx = max;
            return point.x >= mn.x && point.x <= mx.x
                && point.y >= mn.y && point.y <= mx.y;
        }

        public bool Intersects(FPBounds2 other)
        {
            FPVector2 mn = min;
            FPVector2 mx = max;
            FPVector2 omn = other.min;
            FPVector2 omx = other.max;
            return mn.x <= omx.x && mx.x >= omn.x
                && mn.y <= omx.y && mx.y >= omn.y;
        }

        public void Encapsulate(FPVector2 point)
        {
            SetMinMax(FPVector2.Min(min, point), FPVector2.Max(max, point));
        }

        public void Encapsulate(FPBounds2 other)
        {
            SetMinMax(FPVector2.Min(min, other.min), FPVector2.Max(max, other.max));
        }

        public void Expand(FP64 amount)
        {
            FP64 half = amount * FP64.Half;
            extents = new FPVector2(extents.x + half, extents.y + half);
        }

        public void Expand(FPVector2 amount)
        {
            FPVector2 half = amount * FP64.Half;
            extents = extents + half;
        }

        public FPVector2 ClosestPoint(FPVector2 point)
        {
            FPVector2 mn = min;
            FPVector2 mx = max;
            return new FPVector2(
                FP64.Clamp(point.x, mn.x, mx.x),
                FP64.Clamp(point.y, mn.y, mx.y)
            );
        }

        public FP64 SqrDistance(FPVector2 point)
        {
            FPVector2 closest = ClosestPoint(point);
            return (point - closest).sqrMagnitude;
        }

        public bool IntersectRay(FPRay2 ray, out FP64 distance)
        {
            FPVector2 mn = min;
            FPVector2 mx = max;
            FP64 dirInvX = ray.direction.x != FP64.Zero ? FP64.One / ray.direction.x : FP64.MaxValue;
            FP64 dirInvY = ray.direction.y != FP64.Zero ? FP64.One / ray.direction.y : FP64.MaxValue;

            FP64 t1 = (mn.x - ray.origin.x) * dirInvX;
            FP64 t2 = (mx.x - ray.origin.x) * dirInvX;
            FP64 t3 = (mn.y - ray.origin.y) * dirInvY;
            FP64 t4 = (mx.y - ray.origin.y) * dirInvY;

            FP64 tmin = FP64.Max(FP64.Min(t1, t2), FP64.Min(t3, t4));
            FP64 tmax = FP64.Min(FP64.Max(t1, t2), FP64.Max(t3, t4));

            if (tmax < FP64.Zero || tmin > tmax)
            {
                distance = FP64.Zero;
                return false;
            }

            distance = tmin < FP64.Zero ? tmax : tmin;
            return true;
        }

        public bool Equals(FPBounds2 other)
        {
            return center == other.center && extents == other.extents;
        }

        public override bool Equals(object obj)
        {
            return obj is FPBounds2 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(center, extents);
        }

        public static bool operator ==(FPBounds2 a, FPBounds2 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(FPBounds2 a, FPBounds2 b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return $"Bounds2(center: {center}, extents: {extents})";
        }
    }
}
