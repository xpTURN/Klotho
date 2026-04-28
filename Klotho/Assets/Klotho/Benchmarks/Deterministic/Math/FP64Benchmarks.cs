using NUnit.Framework;
using Unity.PerformanceTesting;

namespace xpTURN.Klotho.Deterministic.Math.Benchmarks
{
    [TestFixture]
    public class FP64Benchmarks
    {
        const int WarmupCount = 5;
        const int MeasurementCount = 20;
        const int IterationsPerMeasurement = 100000;

        // Fast path operands: |raw| < 2^31 (fractional value < 1.0)
        static readonly FP64 SmallA = FP64.FromFloat(0.7f);
        static readonly FP64 SmallB = FP64.FromFloat(0.3f);

        // Slow path operands: |raw| >= 2^31 (integer part >= 1)
        static readonly FP64 LargeA = FP64.FromFloat(1234.5f);
        static readonly FP64 LargeB = FP64.FromFloat(678.9f);

        // Vector operands
        static readonly FPVector2 Vec2A = new FPVector2(3.0f, 4.0f);
        static readonly FPVector2 Vec2B = new FPVector2(1.0f, 2.0f);
        static readonly FPVector3 Vec3A = new FPVector3(3.0f, 4.0f, 5.0f);
        static readonly FPVector3 Vec3B = new FPVector3(1.0f, 2.0f, 3.0f);

        #region Multiplication

        [Test, Performance]
        public void Multiply_FastPath()
        {
            Measure.Method(() =>
            {
                var r = SmallA * SmallB;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void Multiply_SlowPath()
        {
            Measure.Method(() =>
            {
                var r = LargeA * LargeB;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        #endregion

        #region Division

        [Test, Performance]
        public void Divide_FastPath()
        {
            Measure.Method(() =>
            {
                var r = SmallA / SmallB;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void Divide_SlowPath()
        {
            Measure.Method(() =>
            {
                var r = LargeA / LargeB;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        #endregion

        #region Sqrt

        [Test, Performance]
        public void Sqrt_SmallValue()
        {
            var v = FP64.FromFloat(2.0f);
            Measure.Method(() =>
            {
                var r = FP64.Sqrt(v);
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void Sqrt_LargeValue()
        {
            var v = FP64.FromFloat(10000.0f);
            Measure.Method(() =>
            {
                var r = FP64.Sqrt(v);
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        #endregion

        #region Trigonometric

        [Test, Performance]
        public void Sin_LUT()
        {
            var angle = FP64.FromFloat(1.23f);
            Measure.Method(() =>
            {
                var r = FP64.Sin(angle);
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void Cos_LUT()
        {
            var angle = FP64.FromFloat(1.23f);
            Measure.Method(() =>
            {
                var r = FP64.Cos(angle);
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void Atan2_CORDIC()
        {
            var y = FP64.FromFloat(3.0f);
            var x = FP64.FromFloat(4.0f);
            Measure.Method(() =>
            {
                var r = FP64.Atan2(y, x);
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        #endregion

        #region Vector Operations

        [Test, Performance]
        public void FPVector2_Dot()
        {
            Measure.Method(() =>
            {
                var r = FPVector2.Dot(Vec2A, Vec2B);
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void FPVector2_Magnitude()
        {
            Measure.Method(() =>
            {
                var r = Vec2A.magnitude;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void FPVector2_Normalize()
        {
            Measure.Method(() =>
            {
                var r = Vec2A.normalized;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void FPVector3_SqrMagnitude()
        {
            Measure.Method(() =>
            {
                var r = Vec3A.sqrMagnitude;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void FPVector3_Normalize()
        {
            Measure.Method(() =>
            {
                var r = Vec3A.normalized;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void FPVector3_Cross()
        {
            Measure.Method(() =>
            {
                var r = FPVector3.Cross(Vec3A, Vec3B);
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        #endregion

        #region Mixed Simulation Pattern

        [Test, Performance]
        public void SimulationTick_MixedOps()
        {
            var pos = new FPVector2(10.0f, 20.0f);
            var vel = new FPVector2(0.5f, -0.3f);
            var dt = FP64.FromFloat(0.016f);
            var target = new FPVector2(100.0f, 50.0f);

            Measure.Method(() =>
            {
                // Typical per-entity tick: move + distance + normalize
                var newPos = pos + vel * dt;
                var diff = target - newPos;
                var dist = diff.magnitude;
                if (dist > FP64.One)
                {
                    var dir = diff.normalized;
                    newPos = newPos + dir * dt;
                }
                pos = newPos;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterationsPerMeasurement)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        #endregion
    }
}
