using System;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Diagnostics;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Pins the IMP94 per-system perf monitor: opt-in gate, index-parallel accumulation, the D9
    /// dump metrics (avg/peak/sum), resim counting (D2), and the determinism/zero-GC invariants
    /// (I1–I4). Driven mostly through <see cref="SystemRunner"/> directly (the monitor lives there
    /// and exposes <c>Stats</c>); the determinism gate (T3) goes through <see cref="EcsSimulation"/>
    /// so it exercises the real GetStateHash. Placed in the .NET/CoreCLR suite because the allocation
    /// probe (GC.GetAllocatedBytesForCurrentThread) is most reliable there (§8 test placement).
    /// </summary>
    [Collection(EcsRegistryCollection.Name)]   // builds Frames over the process-global registry
    public sealed class SystemPerfMonitorTests
    {
        private const int MaxEntities = 32;

        // --- test systems ---

        private sealed class NoOpSystem : ISystem
        {
            public void Update(ref Frame frame) { }
        }

        // Allocates a fresh array every Update -> the allocation-regression detector must catch it.
        private sealed class AllocSystem : ISystem
        {
            public byte[] Sink;
            public void Update(ref Frame frame) { Sink = new byte[1024]; }
        }

        // Burns wall time on exactly one execution (by ordinal) -> peak (MaxTicks) must exceed avg.
        private sealed class SpikySystem : ISystem
        {
            public int SpikeOnExecution;   // 0-based execution ordinal to spin on
            private int _exec;
            public void Update(ref Frame frame)
            {
                if (_exec++ == SpikeOnExecution)
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 3) { /* spin */ }
                }
            }
        }

        // Deterministic state mutation for the determinism gate (T3).
        private sealed class MoverSystem : ISystem
        {
            public void Update(ref Frame frame)
            {
                var filter = frame.Filter<TransformComponent>();
                while (filter.Next(out var e))
                    frame.Get<TransformComponent>(e).Position += FPVector3.One;
            }
        }

        private static Frame NewFrame() => new Frame(MaxEntities, null);

        // Finds a stat by system type name in the monitor's Stats span.
        private static SystemPerfMonitor.SystemPerfStat StatByName(SystemPerfMonitor m, string name)
        {
            foreach (var s in m.Stats)
                if (s.Name == name) return s;
            throw new Xunit.Sdk.XunitException($"stat '{name}' not found");
        }

        // ── T1: off ⇒ monitor not created, systems still run ──────────

        [Fact]
        public void Off_MonitorNull_SystemsStillRun()
        {
            var runner = new SystemRunner();
            var log = new List<string>();
            runner.AddSystem(new RecordingSystem { Name = "A", Log = log }, SystemPhase.Update);

            Assert.Null(runner.PerfMonitor);   // never enabled

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            Assert.Null(runner.PerfMonitor);            // still off
            Assert.Equal(new[] { "A" }, log.ToArray()); // default path executed the system
        }

        // ── T2: UpdateCount == #executions, count == M + builtin ──────

        [Fact]
        public void On_UpdateCountEqualsExecutions_IncludesBuiltin()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();

            const int K = 5;
            var frame = NewFrame();
            for (int i = 0; i < K; i++) runner.RunUpdateSystems(ref frame);

            var m = runner.PerfMonitor;
            Assert.Equal(3, m.Stats.Length);   // 2 systems + builtin
            foreach (var s in m.Stats)
                Assert.Equal(K, s.UpdateCount);
        }

        // ── T2r: Stats accessor exposes names incl. builtin ───────────

        [Fact]
        public void Stats_ReadableWithBuiltinEntry()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            var m = runner.PerfMonitor;
            Assert.Equal(2, m.Stats.Length);
            Assert.Equal("(builtin) SavePrevTransforms", m.Stats[0].Name);   // BuiltinPrevXformIndex
            Assert.Equal(nameof(NoOpSystem), m.Stats[1].Name);               // FirstSystemPerfIndex
        }

        // ── T3: determinism — off/on final hash bit-identical ─────────

        [Fact]
        public void Determinism_OffVsOn_StateHashBitIdentical()
        {
            long hashOff = RunMoverSim(monitor: false);
            long hashOn = RunMoverSim(monitor: true);
            Assert.Equal(hashOff, hashOn);
        }

        private static long RunMoverSim(bool monitor)
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: 50);
            sim.Initialize();
            sim.AddSystem(new MoverSystem(), SystemPhase.Update);
            if (monitor) sim.EnableSystemPerfMonitor();

            var e = sim.Frame.CreateEntity();
            sim.Frame.Add(e, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });

            var noCommands = new List<ICommand>();
            for (int i = 0; i < 10; i++) sim.Tick(noCommands);
            return sim.GetStateHash();
        }

        // ── T4: resim (D2) — extra RunUpdateSystems calls are counted ──

        [Fact]
        public void Resim_CountedBeyondVerifiedTicks()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            const int verified = 4;
            const int resim = 3;   // at SystemRunner granularity, a resim is just another RunUpdateSystems call
            for (int i = 0; i < verified + resim; i++) runner.RunUpdateSystems(ref frame);

            var s = StatByName(runner.PerfMonitor, nameof(NoOpSystem));
            Assert.Equal(verified + resim, s.UpdateCount);
            Assert.True(s.UpdateCount > verified, "resim executions must be counted (D2)");
        }

        // ── T5: allocation-regression detector ────────────────────────

        [Fact]
        public void AllocRegression_NonZeroForAllocatingSystem_ZeroForClean()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new AllocSystem(), SystemPhase.Update);
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            for (int i = 0; i < 8; i++) runner.RunUpdateSystems(ref frame);

            var alloc = StatByName(runner.PerfMonitor, nameof(AllocSystem));
            var clean = StatByName(runner.PerfMonitor, nameof(NoOpSystem));

            Assert.True(alloc.SumMemory > 0, "allocating system must show SumMemory > 0");
            Assert.True(alloc.MaxMemory > 0, "allocating system must show MaxMemory > 0");
            Assert.Equal(0, clean.SumMemory);   // single-threaded tick: clean system allocates nothing
            Assert.Equal(0, clean.MaxMemory);
        }

        // ── T9: peak (D9) — MaxTicks exceeds avg on a spiky system ─────

        [Fact]
        public void Peak_MaxTicksExceedsAverage()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new SpikySystem { SpikeOnExecution = 3 }, SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            const int K = 10;
            for (int i = 0; i < K; i++) runner.RunUpdateSystems(ref frame);

            var s = StatByName(runner.PerfMonitor, nameof(SpikySystem));
            double avg = (double)s.SumTicks / s.UpdateCount;
            Assert.True(s.MaxTicks > 2 * avg,
                $"peak (MaxTicks={s.MaxTicks}) must exceed 2x avg ({avg:F0}) — spike invisible to avg");
        }

        // ── T6: re-sort rebinds and resets accumulated counters ────────

        [Fact]
        public void Rebind_OnAddSystem_ResetsCountersAndRealigns()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            for (int i = 0; i < 3; i++) runner.RunUpdateSystems(ref frame);
            Assert.Equal(2, runner.PerfMonitor.Stats.Length);   // builtin + 1

            runner.AddSystem(new AllocSystem(), SystemPhase.Update);   // marks _dirty -> rebind next tick
            runner.RunUpdateSystems(ref frame);

            var m = runner.PerfMonitor;
            Assert.Equal(3, m.Stats.Length);                    // builtin + 2 after rebind
            foreach (var s in m.Stats)
                Assert.Equal(1, s.UpdateCount);                 // counters reset on rebind
        }

        // ── T7: steady-state (post-warmup) records allocate nothing ────

        [Fact]
        public void SteadyState_RecordPathZeroAlloc()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            // Warmup: consumes _dirty (EnsureSorted) + _perfBindDirty (RebindPerf) so the buffers
            // are sized and no rare-path allocation remains.
            for (int i = 0; i < 4; i++) runner.RunUpdateSystems(ref frame);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) runner.RunUpdateSystems(ref frame);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
        }

        // ── T8: ToText smoke — header + all names, dynamic name width ──

        [Fact]
        public void ToText_ContainsHeaderAndAllNames_AlignedForLongNames()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.AddSystem(new AllocSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            string text = runner.PerfMonitor.ToText();
            Assert.Contains("system", text);
            Assert.Contains("peak us", text);
            Assert.Contains("(builtin) SavePrevTransforms", text);
            Assert.Contains(nameof(NoOpSystem), text);
            Assert.Contains(nameof(AllocSystem), text);
        }

        // ── T10: warmup segregation — early allocation folds into Warmup*, not steady Sum* ──

        [Fact]
        public void Warmup_SegregatesEarlyAllocationFromSteady()
        {
            const int warmup = 3;
            var runner = new SystemRunner();
            runner.AddSystem(new AllocSystem(), SystemPhase.Update);   // allocates every execution
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor(warmup);

            var frame = NewFrame();
            const int ticks = 8;
            for (int i = 0; i < ticks; i++) runner.RunUpdateSystems(ref frame);

            var alloc = StatByName(runner.PerfMonitor, nameof(AllocSystem));
            Assert.Equal(ticks, alloc.UpdateCount);              // total count still all executions
            Assert.True(alloc.WarmupMemory > 0, "first warmup executions must land in WarmupMemory");
            Assert.True(alloc.SumMemory > 0, "post-warmup executions must land in steady SumMemory");
            // Per-tick allocation is uniform, so steady sum ≈ (ticks-warmup)/warmup × warmup sum.
            Assert.True(alloc.SumMemory > alloc.WarmupMemory,
                "5 steady executions should allocate more than 3 warmup executions");

            var clean = StatByName(runner.PerfMonitor, nameof(NoOpSystem));
            Assert.Equal(0, clean.WarmupMemory);
            Assert.Equal(0, clean.SumMemory);
        }

        // ── T11: warmup excludes the first-tick spike from the steady peak ──

        [Fact]
        public void Warmup_ExcludesSpikeInsideWindowFromSteadyPeak()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new SpikySystem { SpikeOnExecution = 0 }, SystemPhase.Update);  // spike on 1st exec
            runner.EnablePerfMonitor(warmupExecutions: 2);

            var frame = NewFrame();
            for (int i = 0; i < 10; i++) runner.RunUpdateSystems(ref frame);

            var s = StatByName(runner.PerfMonitor, nameof(SpikySystem));
            // The spike executed during warmup, so it must not appear in the steady peak.
            double steadyAvgTicks = (double)s.SumTicks / (s.UpdateCount - 2);
            Assert.True(s.MaxTicks < s.WarmupTicks,
                "the warmup-window spike must dominate WarmupTicks, not the steady MaxTicks");
            Assert.True(s.MaxTicks < steadyAvgTicks * 50,
                "steady peak should be small once the JIT/first-tick spike is excluded");
        }

        // ── T12: warmup default off — all executions fold into steady (no behavior change) ──

        [Fact]
        public void Warmup_DefaultOff_AllExecutionsSteady()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new AllocSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();   // default warmup = 0

            var frame = NewFrame();
            for (int i = 0; i < 5; i++) runner.RunUpdateSystems(ref frame);

            var s = StatByName(runner.PerfMonitor, nameof(AllocSystem));
            Assert.Equal(0, s.WarmupTicks);
            Assert.Equal(0, s.WarmupMemory);
            Assert.True(s.SumMemory > 0);   // everything steady, as before
        }

        // --- harness ---

        private sealed class RecordingSystem : ISystem
        {
            public string Name;
            public List<string> Log;
            public void Update(ref Frame frame) => Log.Add(Name);
        }
    }
}
