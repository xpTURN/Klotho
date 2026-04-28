namespace xpTURN.Klotho.Deterministic.Math
{
    public static class FPQuaternionExtensions
    {
        public static FPQuaternion FromQuaternion(this ref FPQuaternion @this, UnityEngine.Quaternion q)
        {
            @this.x = FP64.FromFloat(q.x);
            @this.y = FP64.FromFloat(q.y);
            @this.z = FP64.FromFloat(q.z);
            @this.w = FP64.FromFloat(q.w);
            return @this;
        }

        public static FPQuaternion ToFPQuaternion(this UnityEngine.Quaternion @this)
        {
            return new FPQuaternion(
                FP64.FromFloat(@this.x),
                FP64.FromFloat(@this.y),
                FP64.FromFloat(@this.z),
                FP64.FromFloat(@this.w)
            );
        }

        public static UnityEngine.Quaternion ToQuaternion(this FPQuaternion @this)
        {
            return new UnityEngine.Quaternion(
                @this.x.ToFloat(), 
                @this.y.ToFloat(),
                @this.z.ToFloat(),
                @this.w.ToFloat()
            );
        }
    }
}