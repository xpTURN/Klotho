namespace xpTURN.Klotho.Deterministic.Math
{
    public static class FPVector3Extensions
    {
        public static FPVector3 FromVector3(this ref FPVector3 @this, UnityEngine.Vector3 v)
        {
            @this.x = FP64.FromFloat(v.x);
            @this.y = FP64.FromFloat(v.y);
            @this.z = FP64.FromFloat(v.z);
            return @this;
        }

        public static FPVector3 ToFPVector3(this UnityEngine.Vector3 @this)
        {
             return new FPVector3(
                @this.x.ToFP64(),
                @this.y.ToFP64(),
                @this.z.ToFP64()
            );
       }

        public static UnityEngine.Vector3 ToVector3(this FPVector3 @this)
        {
            return new UnityEngine.Vector3(@this.x.ToFloat(), @this.y.ToFloat(), @this.z.ToFloat());
        }
    }
}