using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    public static class FPBounds3Extensions
    {
        public static FPBounds3 FromBounds(this ref FPBounds3 @this, UnityEngine.Bounds bounds)
        {
            @this.center = bounds.center.ToFPVector3();
            @this.extents = bounds.size.ToFPVector3() * FP64.Half;
            return @this;
        }

        public static FPBounds3 ToFPBounds3(this ref UnityEngine.Bounds @this)
        {
            return new FPBounds3(
                @this.center.ToFPVector3(),
                @this.extents.ToFPVector3()
            );
        }

        public static UnityEngine.Bounds ToBounds(this FPBounds3 @this)
        {
            return new UnityEngine.Bounds(
                @this.center.ToVector3(),
                @this.size.ToVector3()
            );
        }
    }
}