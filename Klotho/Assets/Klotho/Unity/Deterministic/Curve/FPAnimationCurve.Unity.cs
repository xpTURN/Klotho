using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Curve
{
    public static class FPAnimationCurveExtensions
    {
        public static void FromAnimationCurve(this FPAnimationCurve @this, UnityEngine.AnimationCurve curve)
        {
            var unityKeys = curve.keys;
            var fpKeys = new FPKeyframe[unityKeys.Length];

            for (int i = 0; i < unityKeys.Length; i++)
            {
                var uk = unityKeys[i];
                fpKeys[i] = new FPKeyframe(
                    FP64.FromFloat(uk.time),
                    FP64.FromFloat(uk.value),
                    float.IsInfinity(uk.inTangent) ? FP64.MaxValue : FP64.FromFloat(uk.inTangent),
                    float.IsInfinity(uk.outTangent) ? FP64.MaxValue : FP64.FromFloat(uk.outTangent)
                );
            }

            var preWrap = ConvertWrapMode(curve.preWrapMode);
            var postWrap = ConvertWrapMode(curve.postWrapMode);

            @this.Assign(fpKeys, preWrap, postWrap);
        }

        public static UnityEngine.AnimationCurve ToAnimationCurve(this FPAnimationCurve @this)
        {
            var keys = new UnityEngine.Keyframe[@this.Length];
            for (int i = 0; i < @this.Length; i++)
            {
                keys[i] = new UnityEngine.Keyframe(
                    @this[i].time.ToFloat(),
                    @this[i].value.ToFloat(),
                    @this[i].inTangent == FP64.MaxValue
                        ? float.PositiveInfinity
                        : @this[i].inTangent.ToFloat(),
                    @this[i].outTangent == FP64.MaxValue
                        ? float.PositiveInfinity
                        : @this[i].outTangent.ToFloat()
                );
            }

            var curve = new UnityEngine.AnimationCurve(keys);
            curve.preWrapMode = ConvertToUnityWrapMode(@this.PreWrapMode);
            curve.postWrapMode = ConvertToUnityWrapMode(@this.PostWrapMode);
            return curve;
        }

        private static FPWrapMode ConvertWrapMode(UnityEngine.WrapMode mode)
        {
            switch (mode)
            {
                case UnityEngine.WrapMode.Loop: return FPWrapMode.Loop;
                case UnityEngine.WrapMode.PingPong: return FPWrapMode.PingPong;
                default: return FPWrapMode.Clamp;
            }
        }

        private static UnityEngine.WrapMode ConvertToUnityWrapMode(FPWrapMode mode)
        {
            switch (mode)
            {
                case FPWrapMode.Loop: return UnityEngine.WrapMode.Loop;
                case FPWrapMode.PingPong: return UnityEngine.WrapMode.PingPong;
                default: return UnityEngine.WrapMode.Clamp;
            }
        }
    }
}