using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.ECS
{
    public class ArithmeticStressSystem : ISystem
    {
        public void Update(ref Frame frame)
        {
            var filter = frame.Filter<TransformComponent, ArithmeticResultComponent>();
            while (filter.Next(out var entity))
            {
                ref var transform = ref frame.Get<TransformComponent>(entity);
                ref var result = ref frame.Get<ArithmeticResultComponent>(entity);

                var pos = transform.Position;
                var tick = FP64.FromInt(frame.Tick + 1);

                // FP64 arithmetic stress
                var a = pos.x * tick + FP64.FromRaw(123456789L);
                var b = pos.y * tick - FP64.FromRaw(987654321L);
                var c = (a * b) / (tick + FP64.One);
                result.ScalarResult = c + FP64.Sqrt(FP64.Abs(a) + FP64.One);

                // FPVector3 operations
                var v1 = new FPVector3(a, b, c);
                var v2 = new FPVector3(c, a, b);
                var cross = FPVector3.Cross(v1, v2);
                var dot = FPVector3.Dot(v1, v2);
                var crossNorm = cross + new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
                result.VectorResult = crossNorm.normalized * dot;

                // FPQuaternion operations
                var axisRaw = v1 + new FPVector3(FP64.One, FP64.Zero, FP64.Zero);
                var axis = axisRaw.normalized;
                var angle = FP64.FromInt(frame.Tick % 360);
                result.QuatResult = FPQuaternion.AngleAxis(angle, axis);

                // Write back to transform (feeds next tick)
                var offset = (result.VectorResult + new FPVector3(FP64.One, FP64.Zero, FP64.Zero)).normalized;
                transform.Position += offset * FP64.FromRaw(4295); // ~0.001
            }
        }
    }
}
