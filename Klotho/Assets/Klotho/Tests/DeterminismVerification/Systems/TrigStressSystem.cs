using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS
{
    public class TrigStressSystem : ISystem
    {
        public void Update(ref Frame frame)
        {
            var filter = frame.Filter<ArithmeticResultComponent>();
            while (filter.Next(out var entity))
            {
                ref var result = ref frame.Get<ArithmeticResultComponent>(entity);

                var input = result.ScalarResult;

                // Trig stress: Sin, Cos, Atan2 with varied inputs
                var sin = FP64.Sin(input);
                var cos = FP64.Cos(input);
                var atan = FP64.Atan2(sin, cos + FP64.FromRaw(1));

                // Extreme values
                var smallVal = FP64.FromRaw(1); // smallest representable
                var sinSmall = FP64.Sin(smallVal);
                var cosSmall = FP64.Cos(smallVal);

                // Accumulate into result (feeds hash)
                result.ScalarResult = atan + sinSmall + cosSmall + sin * cos;
            }
        }
    }
}
