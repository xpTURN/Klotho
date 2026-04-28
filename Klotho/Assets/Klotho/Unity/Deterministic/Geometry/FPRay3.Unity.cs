using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    public static class FPRay3Extensions
    {
        public static FPRay3 FromRay(this ref FPRay3 @this, UnityEngine.Ray ray)
        {
            @this.origin = ray.origin.ToFPVector3();
            @this.direction = ray.direction.ToFPVector3();
            return @this;
        }

        public static FPRay3 ToFPRay3(this ref UnityEngine.Ray @this)
        {
            return new FPRay3(
                @this.origin.ToFPVector3(),
                @this.direction.ToFPVector3()
            );
        }

        public static UnityEngine.Ray ToRay(this FPRay3 @this)
        {
            return new UnityEngine.Ray(
                @this.origin.ToVector3(),
                @this.direction.ToVector3()
            );
        }
    }
}
