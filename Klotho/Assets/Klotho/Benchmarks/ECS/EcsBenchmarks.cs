using System.Runtime.CompilerServices;
using NUnit.Framework;
using Unity.PerformanceTesting;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using xpTURN.Klotho.ECS;
using Brawler;

namespace xpTURN.Klotho.ECS.Benchmarks
{
    // ECS performance benchmarks. Collects measurement data for regression detection.
    // Maintains measurement conditions based on maxEntities = 256.
    [TestFixture]
    public class EcsBenchmarks
    {
        const int MaxEntities          = 256;
        const int WarmupCount          = 5;
        const int MeasurementCount     = 20;
        const int IterPerMeasure       = 1000;    // CopyFrom / SaveFrame burst
        const int IterPerMeasureLight  = 10000;   // Get / Filter — simple operations

        Frame _src;
        Frame _dst;
        FrameRingBuffer _ring;
        ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var lf = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                b.AddZLoggerUnityDebug();
            });
            _logger = lf.CreateLogger("Bench");
        }

        [SetUp]
        public void SetUp()
        {
            _src  = new Frame(MaxEntities, _logger);
            _dst  = new Frame(MaxEntities, _logger);
            _ring = new FrameRingBuffer(50, MaxEntities, _logger);

            // Create 256 entities; assign Health + Owner to 100, Transform to all
            for (int i = 0; i < MaxEntities; i++)
            {
                var e = _src.CreateEntity();
                _src.Add(e, new TransformComponent());
                if (i < 100)
                {
                    _src.Add(e, new HealthComponent { MaxHealth = 100, CurrentHealth = 100 });
                    _src.Add(e, new OwnerComponent  { OwnerId = i });
                }
                if (i < 60)
                {
                    _src.Add(e, new CharacterComponent { PlayerId = i });
                }
            }
        }

        // ── Frame.CopyFrom Avg/P95/P99 ──────────────────────────────────────

        [Test, Performance]
        public void CopyFrom_Avg_P95_P99()
        {
            Measure.Method(() =>
            {
                _dst.CopyFrom(_src);
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterPerMeasure)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        // ── FrameRingBuffer.SaveFrame burst (Late Join catchup 100 ticks) ──────

        [Test, Performance]
        public void SaveFrame_Burst_100Ticks()
        {
            Measure.Method(() =>
            {
                for (int tick = 0; tick < 100; tick++)
                {
                    _src.Tick = tick;
                    _ring.SaveFrame(tick, _src);
                }
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(100)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        // ── frame.Get<T>(entity) 1M calls ns ─────────────────────────────────

        [Test, Performance]
        public void Get_HealthComponent_1M()
        {
            var e0 = new EntityRef(0, _src.Entities.GetVersion(0));

            Measure.Method(() =>
            {
                ref var h = ref _src.Get<HealthComponent>(e0);
                _ = h.CurrentHealth;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterPerMeasureLight)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        // ── Filter<T1> single-type iteration (100 of 256 entities match) ─────

        [Test, Performance]
        public void Filter_SingleType_100Matching()
        {
            Measure.Method(() =>
            {
                var filter = _src.Filter<HealthComponent>();
                while (filter.Next(out var e))
                    _ = e.Index;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterPerMeasureLight)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        // ── Filter<T1,T2,T3> multi-type (includes Has checks) ────────────────

        [Test, Performance]
        public void Filter_MultiType_T1T2T3()
        {
            // Health(100) ∩ Owner(100) ∩ Character(60) = 60 matches
            Measure.Method(() =>
            {
                var filter = _src.Filter<HealthComponent, OwnerComponent, CharacterComponent>();
                while (filter.Next(out var e))
                    _ = e.Index;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterPerMeasureLight)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        // ── FilterWithout<T1, TExclude> exclusion filter ─────────────────────

        [Test, Performance]
        public void FilterWithout_T1_Exclude()
        {
            // Of those with Transform (256), entities without Health (156)
            Measure.Method(() =>
            {
                var filter = _src.FilterWithout<TransformComponent, HealthComponent>();
                while (filter.Next(out var e))
                    _ = e.Index;
            })
                .WarmupCount(WarmupCount)
                .IterationsPerMeasurement(IterPerMeasureLight)
                .MeasurementCount(MeasurementCount)
                .GC()
                .Run();
        }

        // ── Heap memory measurement ──────────────────────────────────────────
        //
        // Total Frame count = main(1) + FrameRingBuffer(50) = 51 Frames
        // Estimate (Brawler basis): 51 × ~280KB ≈ ~14MB
        // Success criterion: increase vs. legacy Dictionary-based < 5%
        //
        // This test only provides the _current_ heap total measurement. The Before comparison
        // is performed in a separate git worktree by checking out the legacy commit and running
        // the same logic.
        [Test]
        public void Heap_Memory_51Frames()
        {
            // Baseline — clear current test fixture's _src/_dst/_ring and re-measure
            _src = null; _dst = null; _ring = null;
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long before = System.GC.GetTotalMemory(forceFullCollection: true);

            const int RingCapacity = 50;
            var ring = new FrameRingBuffer(RingCapacity, MaxEntities, _logger);
            var main = new Frame(MaxEntities, _logger);   // 51st (main)

            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long after = System.GC.GetTotalMemory(forceFullCollection: true);
            long totalBytes = after - before;
            int totalFrames = RingCapacity + 1;
            double mb = totalBytes / 1024.0 / 1024.0;
            double perFrameKb = totalBytes / (double)totalFrames / 1024.0;

            // Log layout info — for filling in Benchmark-Results.md "GC size per Frame" column
            int heapSize = ComponentStorageRegistry.GetHeapSize(MaxEntities);
            UnityEngine.Debug.Log(
                $"[Heap] maxEntities={MaxEntities}, frames={totalFrames}, " +
                $"heapSize(per layout)={heapSize}B ({heapSize / 1024.0:F1}KB), " +
                $"measured GC total increase={totalBytes:N0}B ({mb:F2}MB), " +
                $"per-frame={perFrameKb:F1}KB");

            // Keep GC anchor (eligible for GC after measurement completes)
            System.GC.KeepAlive(ring);
            System.GC.KeepAlive(main);

            // sanity: occupies at least 51 × heapSize. Includes GC.GetTotalMemory tolerance, Entities, and dispatch table overhead.
            long lowerBound = (long)totalFrames * heapSize;
            Assert.GreaterOrEqual(totalBytes, lowerBound,
                $"measured({totalBytes}B) < minimum expected({lowerBound}B = {totalFrames} × {heapSize}B)");
        }
    }
}
