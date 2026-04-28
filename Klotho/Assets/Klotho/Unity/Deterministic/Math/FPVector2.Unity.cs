namespace xpTURN.Klotho.Deterministic.Math
{
    public static class FPVector2Extensions
    {
        public static FPVector2 FromVector2(this ref FPVector2 @this, UnityEngine.Vector2 v)
        {
            @this.x = FP64.FromFloat(v.x);
            @this.y = FP64.FromFloat(v.y);
            return @this;
        }

        public static FPVector2 ToFPVector2(this UnityEngine.Vector2 @this)
        {
            return new FPVector2(
                @this.x.ToFP64(),
                @this.y.ToFP64()
            );
        }

        public static UnityEngine.Vector2 ToVector2(this FPVector2 @this)
        {
            return new UnityEngine.Vector2(@this.x.ToFloat(), @this.y.ToFloat());
        }
    }
}
