using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;

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
            throw new NUnit.Framework.AssertionException($"stat '{name}' not found");
        }

        // ── T1: off ⇒ monitor not created, systems still run ──────────

        [Test]
        public void Off_MonitorNull_SystemsStillRun()
        {
            var runner = new SystemRunner();
            var log = new List<string>();
            runner.AddSystem(new RecordingSystem { Name = "A", Log = log }, SystemPhase.Update);

            Assert.IsNull(runner.PerfMonitor);   // never enabled

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            Assert.IsNull(runner.PerfMonitor);            // still off
            Assert.AreEqual(new[] { "A" }, log.ToArray()); // default path executed the system
        }

        // Builtin perf slots: SavePrevTransforms + the two [KlothoCleanup] passes. Bound unconditionally
        // (the indices are fixed consts), so every expected Stats length below is systems + this.
        private const int BuiltinSlots = 3;

        // ── T2: UpdateCount == #executions, count == M + builtins ─────

        [Test]
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
            Assert.AreEqual(2 + BuiltinSlots, m.Stats.Length);
            foreach (var s in m.Stats)
                Assert.AreEqual(K, s.UpdateCount);
        }

        // ── T2r: Stats accessor exposes names incl. builtin ───────────

        [Test]
        public void Stats_ReadableWithBuiltinEntry()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            var m = runner.PerfMonitor;
            Assert.AreEqual(1 + BuiltinSlots, m.Stats.Length);
            Assert.AreEqual("(builtin) SavePrevTransforms", m.Stats[0].Name);   // BuiltinPrevXformIndex
            Assert.AreEqual("(builtin) CleanupClear", m.Stats[1].Name);         // BuiltinCleanupClearIndex
            Assert.AreEqual("(builtin) CleanupDestroy", m.Stats[2].Name);       // BuiltinCleanupDestroyIndex
            // The system list starts right after the builtins — this is the alignment that a
            // mis-adjusted FirstSystemPerfIndex would shift.
            Assert.AreEqual(nameof(NoOpSystem), m.Stats[BuiltinSlots].Name);    // FirstSystemPerfIndex
        }

        // ── T3: determinism — off/on final hash bit-identical ─────────

        [Test]
        public void Determinism_OffVsOn_StateHashBitIdentical()
        {
            long hashOff = RunMoverSim(monitor: false);
            long hashOn = RunMoverSim(monitor: true);
            Assert.AreEqual(hashOff, hashOn);
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

        [Test]
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
            Assert.AreEqual(verified + resim, s.UpdateCount);
            Assert.IsTrue(s.UpdateCount > verified, "resim executions must be counted (D2)");
        }

        // ── T5: allocation-regression detector ────────────────────────

        [Test]
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

            Assert.IsTrue(alloc.SumMemory > 0, "allocating system must show SumMemory > 0");
            Assert.IsTrue(alloc.MaxMemory > 0, "allocating system must show MaxMemory > 0");
            Assert.AreEqual(0, clean.SumMemory);   // single-threaded tick: clean system allocates nothing
            Assert.AreEqual(0, clean.MaxMemory);
        }

        // ── T9: peak (D9) — MaxTicks exceeds avg on a spiky system ─────

        [Test]
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
            Assert.IsTrue(s.MaxTicks > 2 * avg,
                $"peak (MaxTicks={s.MaxTicks}) must exceed 2x avg ({avg:F0}) — spike invisible to avg");
        }

        // ── T6: re-sort rebinds and resets accumulated counters ────────

        [Test]
        public void Rebind_OnAddSystem_ResetsCountersAndRealigns()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            for (int i = 0; i < 3; i++) runner.RunUpdateSystems(ref frame);
            Assert.AreEqual(1 + BuiltinSlots, runner.PerfMonitor.Stats.Length);

            runner.AddSystem(new AllocSystem(), SystemPhase.Update);   // marks _dirty -> rebind next tick
            runner.RunUpdateSystems(ref frame);

            var m = runner.PerfMonitor;
            Assert.AreEqual(2 + BuiltinSlots, m.Stats.Length);     // 2 systems after rebind
            foreach (var s in m.Stats)
                Assert.AreEqual(1, s.UpdateCount);                 // counters reset on rebind
        }

        // ── T7: steady-state (post-warmup) records allocate nothing ────

        // Group labels ride along here rather than in a second probe: the "allocated bytes" delta of
        // the FIRST such measurement in a process carries a one-time ~24B cost (JIT/statics settling),
        // so a duplicate zero-alloc test would just hand that cost to whichever of the two runs first
        // (NUnit orders alphabetically). One measurement, covering the labelled path too.
        [Test]
        public void SteadyState_RecordPathZeroAlloc()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update, group: "combat");
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update, group: "world");
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            // Warmup: consumes _dirty (EnsureSorted) + _perfBindDirty (RebindPerf) so the buffers
            // are sized and no rare-path allocation remains.
            for (int i = 0; i < 4; i++) runner.RunUpdateSystems(ref frame);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) runner.RunUpdateSystems(ref frame);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0, allocated);
        }

        // ── T8: ToText smoke — header + all names, dynamic name width ──

        [Test]
        public void ToText_ContainsHeaderAndAllNames_AlignedForLongNames()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.AddSystem(new AllocSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            string text = runner.PerfMonitor.ToText();
            XAssert.Contains("system", text);
            XAssert.Contains("peak us", text);
            XAssert.Contains("(builtin) SavePrevTransforms", text);
            XAssert.Contains(nameof(NoOpSystem), text);
            XAssert.Contains(nameof(AllocSystem), text);
        }

        // ── T11..T16: group labels (IMP102 Plan-SystemGroupLabel) ────

        // The label reaches the report as its OWN field, not encoded into the display name: the name
        // stays the type name so every existing name-based assertion keeps working.
        [Test]
        public void Group_Label_LandsInStatField_NameUnchanged()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update, group: "combat");
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            var stat = StatByName(runner.PerfMonitor, nameof(NoOpSystem));
            Assert.AreEqual("combat", stat.Group);
            Assert.AreEqual(nameof(NoOpSystem), stat.Name, "the name must not carry the group");

            // ...but the report PREPENDS it for display, which is what makes two systems sharing a
            // short type name (engine + game both ship a CombatSystem) tellable apart.
            XAssert.Contains("combat/" + nameof(NoOpSystem), runner.PerfMonitor.ToText());
        }

        // Same type name registered twice under different groups: the rows must be distinguishable.
        [Test]
        public void Group_DisambiguatesRowsWithTheSameTypeName()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update, group: "engine");
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update, group: "combat");
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            string text = runner.PerfMonitor.ToText();
            XAssert.Contains("engine/NoOpSystem", text);
            XAssert.Contains("combat/NoOpSystem", text);
        }

        // Unlabelled registration is byte-identical to before: null/empty/whitespace all mean "none".
        [Test]
        public void Group_Unspecified_IsNull_AndReportUnchanged()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update);
            runner.AddSystem(new AllocSystem(), SystemPhase.Update, group: "   ");
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            Assert.IsNull(StatByName(runner.PerfMonitor, nameof(NoOpSystem)).Group);
            Assert.IsNull(StatByName(runner.PerfMonitor, nameof(AllocSystem)).Group,
                "whitespace-only is 'no label'");
            // No group anywhere -> no totals section at all.
            Assert.IsFalse(runner.PerfMonitor.ToText().Contains("group totals"));
        }

        // The label must never become a sort key: execution order is (Phase, registration order) only.
        [Test]
        public void Group_DoesNotReorderSystems()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update, group: "zzz");
            runner.AddSystem(new AllocSystem(), SystemPhase.Update, group: "aaa");
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            var stats = runner.PerfMonitor.Stats;
            Assert.AreEqual("(builtin) SavePrevTransforms", stats[0].Name);   // builtin block first
            Assert.AreEqual(nameof(NoOpSystem), stats[BuiltinSlots].Name,     // registration order kept
                "labels must not reorder systems");
            Assert.AreEqual(nameof(AllocSystem), stats[BuiltinSlots + 1].Name);
        }

        // Builtin passes keep their exact names but DO get the implicit "(builtin)" group, so every
        // measured row belongs to some group and the totals add up to the report's own total.
        [Test]
        public void Group_BuiltinSlots_KeepNames_AndFormImplicitGroup()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update, group: "combat");
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            var stats = runner.PerfMonitor.Stats;
            Assert.AreEqual("(builtin) SavePrevTransforms", stats[0].Name);
            Assert.AreEqual("(builtin)", stats[0].Group);

            long total = 0, grouped = 0;
            foreach (var st in stats)
            {
                if (st.UpdateCount == 0) continue;
                total += st.SumTicks;
                if (st.Group != null) grouped += st.SumTicks;
            }
            Assert.AreEqual(total, grouped, "every measured row must belong to a group");
        }

        // Two spellings are two groups — deliberate (nothing beyond trimming is validated), fixed here
        // so it is not later "fixed" as a normalization bug.
        [Test]
        public void Group_SpellingVariants_AreDistinctGroups()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new NoOpSystem(), SystemPhase.Update, group: "combat");
            runner.AddSystem(new AllocSystem(), SystemPhase.Update, group: " Combat ");
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            runner.RunUpdateSystems(ref frame);

            Assert.AreEqual("combat", StatByName(runner.PerfMonitor, nameof(NoOpSystem)).Group);
            Assert.AreEqual("Combat", StatByName(runner.PerfMonitor, nameof(AllocSystem)).Group,
                "trimmed, but not case-folded");

            string text = runner.PerfMonitor.ToText();
            XAssert.Contains("= combat", text);
            XAssert.Contains("= Combat", text);
        }

        // Totals fill additive columns only. peak/updates are per-system and are left blank rather than
        // reporting a number that does not exist.
        [Test]
        public void Group_Totals_FillAdditiveColumnsOnly()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new AllocSystem(), SystemPhase.Update, group: "combat");
            runner.AddSystem(new AllocSystem(), SystemPhase.Update, group: "combat");
            runner.EnablePerfMonitor();

            var frame = NewFrame();
            for (int i = 0; i < 5; i++) runner.RunUpdateSystems(ref frame);

            string text = runner.PerfMonitor.ToText();
            string totals = null;
            foreach (var line in text.Split('\n'))
                if (line.StartsWith("= combat")) totals = line;
            Assert.IsNotNull(totals, "a totals row for 'combat' must exist");

            // sum mem is additive -> equals the sum of the two systems' steady memory.
            long expected = 0;
            foreach (var st in runner.PerfMonitor.Stats)
                if (st.Group == "combat") expected += st.SumMemory;
            XAssert.Contains(expected.ToString(), totals);

            // The caveat line has to be there: these totals are not the whole tick.
            XAssert.Contains("not the whole tick", text);
        }

        // ── T10: warmup segregation — early allocation folds into Warmup*, not steady Sum* ──

        [Test]
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
            Assert.AreEqual(ticks, alloc.UpdateCount);              // total count still all executions
            Assert.IsTrue(alloc.WarmupMemory > 0, "first warmup executions must land in WarmupMemory");
            Assert.IsTrue(alloc.SumMemory > 0, "post-warmup executions must land in steady SumMemory");
            // Per-tick allocation is uniform, so steady sum ≈ (ticks-warmup)/warmup × warmup sum.
            Assert.IsTrue(alloc.SumMemory > alloc.WarmupMemory,
                "5 steady executions should allocate more than 3 warmup executions");

            var clean = StatByName(runner.PerfMonitor, nameof(NoOpSystem));
            Assert.AreEqual(0, clean.WarmupMemory);
            Assert.AreEqual(0, clean.SumMemory);
        }

        // ── T11: warmup excludes the first-tick spike from the steady peak ──

        [Test]
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
            Assert.IsTrue(s.MaxTicks < s.WarmupTicks,
                "the warmup-window spike must dominate WarmupTicks, not the steady MaxTicks");
            Assert.IsTrue(s.MaxTicks < steadyAvgTicks * 50,
                "steady peak should be small once the JIT/first-tick spike is excluded");
        }

        // ── T12: warmup default off — all executions fold into steady (no behavior change) ──

        [Test]
        public void Warmup_DefaultOff_AllExecutionsSteady()
        {
            var runner = new SystemRunner();
            runner.AddSystem(new AllocSystem(), SystemPhase.Update);
            runner.EnablePerfMonitor();   // default warmup = 0

            var frame = NewFrame();
            for (int i = 0; i < 5; i++) runner.RunUpdateSystems(ref frame);

            var s = StatByName(runner.PerfMonitor, nameof(AllocSystem));
            Assert.AreEqual(0, s.WarmupTicks);
            Assert.AreEqual(0, s.WarmupMemory);
            Assert.IsTrue(s.SumMemory > 0);   // everything steady, as before
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
