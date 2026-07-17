using System;
using NUnit.Framework;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS.Diagnostics;

namespace xpTURN.Klotho.ECS.Tests
{
    // Component-memory analyzer / GetLiveCount seam / peak sampler.
    // Re-sim safety is an integration test that belongs on the SD/P2P rollback e2e harness
    // (drive prediction -> rollback -> verified, assert peak == verified-path peak) and is not covered here.
    [TestFixture]
    public class ComponentMemoryAnalyzerTests
    {
        private const int MaxEntities = 16;
        private IKLogger _logger = null;
        private Frame _frame;
        private int _ownerTid;
        private int _seedTid;

        [SetUp]
        public void SetUp()
        {
            _frame = new Frame(MaxEntities, _logger);
            _ownerTid = ComponentStorageRegistry.GetTypeId<OwnerComponent>();
            _seedTid = ComponentStorageRegistry.GetTypeId<RandomSeedComponent>();
        }

        private static ComponentMemoryStat Find(ComponentMemoryReport r, int typeId)
        {
            foreach (var s in r.Stats)
                if (s.TypeId == typeId) return s;
            throw new AssertionException($"typeId {typeId} not found in report");
        }

        private void SpawnOwners(int count)
        {
            for (int i = 0; i < count; i++)
                _frame.Add(_frame.CreateEntity(), new OwnerComponent { OwnerId = i });
        }

        // liveBytes/wasteBytes include the dense int (+4); reserved == layout.TotalSize; sum invariant.
        [Test]
        public void T1_Capture_VariableBytes_IncludeDense()
        {
            SpawnOwners(5);
            ref readonly var layout = ref ComponentStorageRegistry.GetLayout(_ownerTid);
            int mem = layout.ComponentSize;

            var stat = Find(ComponentMemoryAnalyzer.Capture(_frame, 1, null), _ownerTid);

            Assert.AreEqual(5, stat.LiveCount);
            Assert.AreEqual(MaxEntities, stat.SlotCapacity);
            Assert.AreEqual(5 * (mem + 4), stat.LiveBytes, "liveBytes = live * (memSize + 4)");
            Assert.AreEqual((MaxEntities - 5) * (mem + 4), stat.WasteBytes, "wasteBytes = (slot - live) * (memSize + 4)");
            Assert.AreEqual(MaxEntities * (mem + 4), stat.LiveBytes + stat.WasteBytes, "live + waste = slot * (memSize + 4)");
            Assert.AreEqual(layout.TotalSize, stat.ReservedBytes);
            Assert.AreNotEqual((MaxEntities - 5) * mem, stat.WasteBytes, "dense (+4) must not be omitted");
        }

        // GetLiveCount matches the generic path; out-of-range / gap guards return 0 (no throw).
        [Test]
        public void T2_GetLiveCount_MatchesGeneric_AndGuards()
        {
            SpawnOwners(3);
            Assert.AreEqual(_frame.GetStorage<OwnerComponent>().Count, _frame.GetLiveCount(_ownerTid));
            Assert.AreEqual(0, _frame.GetLiveCount(999999), "out-of-range typeId -> 0");
            Assert.AreEqual(0, _frame.GetLiveCount(-1), "negative typeId -> 0");
            Assert.AreEqual(0, _frame.GetLiveCount(0), "typeId 0 (unused) -> 0");
        }

        // Singleton: slotCapacity 1, util 100% with one carrier, sparse (fixed) dominates the reserve.
        [Test]
        public void T3_Singleton_SlotAndFixedCost()
        {
            _frame.Add(_frame.CreateEntity(), new RandomSeedComponent { Seed = 42 });
            var stat = Find(ComponentMemoryAnalyzer.Capture(_frame, 1, null), _seedTid);

            Assert.IsTrue(stat.Singleton);
            Assert.AreEqual(1, stat.SlotCapacity);
            Assert.AreEqual(MaxEntities, stat.Capacity, "sparse domain stays maxEntities for singletons");
            Assert.AreEqual(1.0, stat.Util, 1e-9);
            Assert.Greater(stat.Capacity * 4, stat.SlotCapacity * (stat.MemSize + 4), "sparse(fixed) > variable(dense+comp)");
            // Singleton maxCount is structurally 1 — clamp overrides ceil(peak*margin) and the unmeasured sentinel.
            Assert.AreEqual(1, stat.RecommendedMaxCount(1.5), "singleton -> maxCount clamped to 1 (not ceil(peak*margin), not -1)");
        }

        // Peak = max over sampled ticks; reset clears; RecommendedMaxCount = ceil(peak * margin).
        [Test]
        public void T4_PeakSampler_MaxAndReset_AndRecommended()
        {
            SpawnOwners(5);
            var sampler = new ComponentMemoryPeakSampler(ComponentStorageRegistry.MaxTypeId);
            sampler.Sample(_frame);                                  // 5
            var a = _frame.CreateEntity(); _frame.Add(a, new OwnerComponent());
            var b = _frame.CreateEntity(); _frame.Add(b, new OwnerComponent());
            sampler.Sample(_frame);                                  // 7
            _frame.Remove<OwnerComponent>(a); _frame.Remove<OwnerComponent>(b);
            sampler.Sample(_frame);                                  // 5, peak stays 7

            Assert.AreEqual(7, sampler.PeakCounts[_ownerTid]);
            Assert.AreEqual(3, sampler.ObservedTicks);
            sampler.Reset();
            Assert.AreEqual(0, sampler.PeakCounts[_ownerTid]);
            Assert.AreEqual(0, sampler.ObservedTicks);

            var s31 = new ComponentMemoryStat(1, "x", false, 8, 8, 16, 16, 0, 31, 0, 0, 0);
            var s8 = new ComponentMemoryStat(1, "x", false, 8, 8, 16, 16, 0, 8, 0, 0, 0);
            var s10 = new ComponentMemoryStat(1, "x", false, 8, 8, 16, 16, 0, 10, 0, 0, 0);
            Assert.AreEqual(47, s31.RecommendedMaxCount(1.5));
            Assert.AreEqual(12, s8.RecommendedMaxCount(1.5));
            Assert.AreEqual(15, s10.RecommendedMaxCount(1.5), "integer boundary, no over-round");
        }

        // x-ring aggregate is long and equals (long)perFrame * ring (overflow-safe for large configs).
        [Test]
        public void T5_RingAggregate_IsLong_NoOverflow()
        {
            SpawnOwners(2);
            var report = ComponentMemoryAnalyzer.Capture(_frame, 53, null);
            Assert.AreEqual((long)report.TotalReservedPerFrame * report.RingHeaps, report.TotalReservedAllHeaps);
            Assert.IsInstanceOf<long>(report.TotalReservedAllHeaps);

            // Arithmetic-level overflow guard: a synthetic per-frame value near int.MaxValue x ring must
            // not overflow (a real 64k x large-type sim is multi-GB and cannot be allocated).
            var synth = new ComponentMemoryReport();
            synth.Reset(1_000_000, 53);
            synth.Add(new ComponentMemoryStat(1, "big", false, 0, 0, 0, 0, 0, -1,
                reservedBytes: 2_000_000_000, liveBytes: 0, wasteBytes: 0));
            Assert.AreEqual(2_000_000_000L * 53, synth.TotalReservedAllHeaps);
        }

        // Sample() is read-only: it must not change the simulation state hash.
        [Test]
        public void T6_Sampler_IsReadOnly()
        {
            SpawnOwners(4);
            ulong h1 = _frame.CalculateHash(null);
            var sampler = new ComponentMemoryPeakSampler(ComponentStorageRegistry.MaxTypeId);
            sampler.Sample(_frame);
            ulong h2 = _frame.CalculateHash(null);
            Assert.AreEqual(h1, h2, "peak sampling reads only; sim hash unchanged");
        }

        // Peak sentinel: unmeasured (null) -> PeakCount -1 / Recommended -1 (N/A), distinct from observed 0.
        [Test]
        public void T7_PeakSentinel_NullVsZero()
        {
            SpawnOwners(3);
            var nullRep = ComponentMemoryAnalyzer.Capture(_frame, 1, null);
            foreach (var s in nullRep.Stats)
            {
                Assert.AreEqual(-1, s.PeakCount, "null peakCounts -> PeakCount -1");
                if (s.Singleton)
                    Assert.AreEqual(1, s.RecommendedMaxCount(1.5), "singleton -> maxCount 1 (structural), overrides sentinel");
                else
                    Assert.AreEqual(-1, s.RecommendedMaxCount(1.5), "non-singleton unmeasured -> Recommended N/A (-1), not 0");
            }

            var sampler = new ComponentMemoryPeakSampler(ComponentStorageRegistry.MaxTypeId);
            sampler.Sample(_frame);
            var obsRep = ComponentMemoryAnalyzer.Capture(_frame, 1, sampler.PeakCounts);
            var owner = Find(obsRep, _ownerTid);
            Assert.GreaterOrEqual(owner.PeakCount, 0, "measured type -> PeakCount >= 0");
            Assert.AreEqual((int)Math.Ceiling(owner.PeakCount * 1.5), owner.RecommendedMaxCount(1.5));
        }

        // ToText renders rows, header, totals, and "-" for unmeasured peak/maxCount.
        [Test]
        public void T8_ToText_Format()
        {
            SpawnOwners(2);
            string text = ComponentMemoryAnalyzer.Capture(_frame, 53, null).ToText(1.5);
            StringAssert.Contains("[Mem]", text);
            StringAssert.Contains("->maxCount", text);
            StringAssert.Contains("per-frame total", text);
            StringAssert.Contains(" -", text);   // unmeasured peak / ->maxCount rendered as "-"
        }

        // memSize vs serSize: waste uses in-memory ComponentSize, never the serialized size.
        [Test]
        public void T10_Waste_UsesMemSize_NotSerSize()
        {
            SpawnOwners(3);
            var report = ComponentMemoryAnalyzer.Capture(_frame, 1, null);
            bool sawDivergent = false;
            foreach (var s in report.Stats)
            {
                Assert.AreEqual((s.SlotCapacity - s.LiveCount) * (s.MemSize + 4), s.WasteBytes,
                    $"{s.Name}: waste must use memSize (+dense), not serSize");
                if (s.MemSize != s.SerSize) sawDivergent = true;
            }
            // At least document whether a divergent type exists; the per-stat assert already guards the trap.
            Assert.Pass(sawDivergent ? "verified on a memSize!=serSize type" : "no divergent type registered; formula guard holds");
        }
    }
}
