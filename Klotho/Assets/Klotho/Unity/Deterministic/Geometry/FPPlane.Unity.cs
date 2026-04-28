using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    public static class FPPlaneExtensions
    {
        public static FPPlane FromPlane(this ref FPPlane @this, UnityEngine.Plane plane)
        {
            @this.normal = plane.normal.ToFPVector3();
            @this.distance = FP64.FromFloat(plane.distance);
            return @this;
        }

        public static FPPlane ToFPPlane(this ref UnityEngine.Plane @this)
        {
            return new FPPlane(
                @this.normal.ToFPVector3(),
                @this.distance.ToFP64()
            );
        }

        public static UnityEngine.Plane ToPlane(this FPPlane @this)
        {
            return new UnityEngine.Plane(
                @this.normal.ToVector3(),
                @this.distance.ToFloat()
            );
        }
    }
}
