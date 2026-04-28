namespace xpTURN.Klotho.Deterministic.Math
{
    public static class FPVector4Extensions
    {
        public static FPVector4 FromVector4(this ref FPVector4 @this, UnityEngine.Vector4 v)
        {
            @this.x = FP64.FromFloat(v.x);
            @this.y = FP64.FromFloat(v.y);
            @this.z = FP64.FromFloat(v.z);
            @this.w = FP64.FromFloat(v.w);
            return @this;
        }

        public static FPVector4 ToFPVector4(this UnityEngine.Vector4 @this)
        {
             return new FPVector4(
                @this.x.ToFP64(),
                @this.y.ToFP64(),
                @this.z.ToFP64(),
                @this.w.ToFP64()
            );
        }

        public static UnityEngine.Vector4 ToVector4(this FPVector4 @this)
        {
            return new UnityEngine.Vector4(@this.x.ToFloat(), @this.y.ToFloat(), @this.z.ToFloat(), @this.w.ToFloat());
        }
    }
}