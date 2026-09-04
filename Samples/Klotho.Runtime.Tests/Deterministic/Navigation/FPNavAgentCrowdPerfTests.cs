using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Measurement harness for hundreds of units under one nav system: what a move order actually
    /// costs, decomposed into the four places the time goes — the A* storm on the order tick, the
    /// O(N²) ORCA neighbour scan every tick after it, the position-correction pass, and the frame
    /// copy that carries <c>NavAgentComponent</c> through the rollback ring.
    ///
    /// It also measures the one lever that needs no engine change: the game owns the entities
    /// array, so it can call <c>Update</c> once per spatial cluster instead of once for everyone.
    /// The split turns O(N²) into O(Σnᵢ²) — this fixture is where that claim stops being arithmetic
    /// and becomes a number.
    ///
    /// Excluded from the normal suite: run explicitly, in Release (DEBUG builds run Debug.Asserts
    /// and the CDT integrity scan — numbers are meaningless there):
    ///   dotnet test -c Release --filter FullyQualifiedName~FPNavAgentCrowdPerfTests
    /// </summary>
    [TestFixture]
    [Explicit("perf measurement — run in Release with an explicit filter")]
    public class FPNavAgentCrowdPerfTests
    {
        #region Harness

        // Same discipline as FPNavMeshRebakerPerfTests: tiered JIT promotes at ~30 calls, so fewer
        // warmups measure tier-0 cold code and inflate the record several-fold.
        private static (double minMs, double medianMs) Measure(Action action, int warmup = 32, int iterations = 9)
        {
            for (int i = 0; i < warmup; i++)
                action();

            var samples = new List<double>(iterations);
            var sw = new Stopwatch();
            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                action();
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            return (samples[0], samples[samples.Count / 2]);
        }

        private static long MeasureAlloc(Action action, int warmup = 32, int iterations = 5)
        {
            for (int i = 0; i < warmup; i++)
                action();

            var samples = new List<long>(iterations);
            for (int i = 0; i < iterations; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                action();
                samples.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            }
            samples.Sort();
            return samples[samples.Count / 2];
        }

        private static void Report(string label, (double minMs, double medianMs) m, string extra = "")
        {
            TestContext.Out.WriteLine($"{label,-44} min {m.minMs,8:F3} ms   median {m.medianMs,8:F3} ms   {extra}");
        }

        private static readonly int[] Sizes = { 64, 256, 800, 3200 };

        // One field wide enough to hold 3200 agents without stacking them all in one spot: agents
        // are laid out on a lattice at AGENT_STRIDE spacing, which is what makes the neighbour scan
        // cost representative (a heap of coincident agents is a different, easier shape).
        private const int FIELD_CELLS = 96;
        private const double AGENT_STRIDE = 1.5;

        private sealed class Crowd
        {
            public FPNavAgentSystem System;
            public Frame Frame;
            public EntityRef[] Entities;
            public EntityRef[][] Clusters;   // the same agents, partitioned into <= MAX_AGENTS runs
            public FPNavMeshPathfinder Pathfinder;
        }

        /// <summary>
        /// N agents on one mesh, already Moving with a live corridor — the steady state, not the
        /// order tick. <paramref name="avoidance"/> off isolates path following from ORCA.
        /// </summary>
        private static Crowd BuildCrowd(int count, bool avoidance)
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(FIELD_CELLS);
            var system = NavAgentTestHelper.CreateSystem(mesh, null, out var pathfinder);
            if (avoidance)
                system.SetAvoidance(new FPNavAvoidance());

            int perRow = (int)System.Math.Ceiling(System.Math.Sqrt(count));
            var positions = new FPVector3[count];
            var velocities = new FPVector2[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = new FPVector3(
                    FP64.FromDouble(8.0 + (i % perRow) * AGENT_STRIDE), FP64.Zero,
                    FP64.FromDouble(8.0 + (i / perRow) * AGENT_STRIDE));
                velocities[i] = FPVector2.Zero;
            }

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                positions, velocities, out var entities, maxEntities: count + 16);

            // Give everyone a destination across the field and let the first tick resolve paths,
            // so the measured ticks are steady-state following rather than the A* storm.
            FPVector3 target = NavAgentTestHelper.CellCenter(FIELD_CELLS - 2, FIELD_CELLS - 2);
            for (int i = 0; i < count; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                NavAgentComponent.SetDestination(ref nav, target);
            }
            system.Update(ref frame, entities, count, 1, NavAgentTestHelper.DT);

            return new Crowd
            {
                System = system,
                Frame = frame,
                Entities = entities,
                Clusters = Partition(entities, count, FPNavAgentSystem.MAX_AGENTS),
                Pathfinder = pathfinder,
            };
        }

        /// <summary>
        /// Contiguous partition into runs of at most <paramref name="size"/>. Index order is a pure
        /// function of the array the caller already owns, so it satisfies the determinism rule a
        /// real clustering rule has to satisfy — a spatial rule would sort by cell, but the cost
        /// shape being measured here is the same.
        /// </summary>
        private static EntityRef[][] Partition(EntityRef[] entities, int count, int size)
        {
            int clusters = (count + size - 1) / size;
            var result = new EntityRef[clusters][];
            for (int c = 0; c < clusters; c++)
            {
                int start = c * size;
                int len = System.Math.Min(size, count - start);
                result[c] = new EntityRef[len];
                Array.Copy(entities, start, result[c], 0, len);
            }
            return result;
        }

        private static void RunSplit(Crowd crowd, int tick)
        {
            for (int c = 0; c < crowd.Clusters.Length; c++)
                crowd.System.Update(ref crowd.Frame, crowd.Clusters[c], crowd.Clusters[c].Length,
                    tick, NavAgentTestHelper.DT);
        }

        #endregion

        [Test]
        public void SteadyState_SingleCallVersusClusterSplit()
        {
            TestContext.Out.WriteLine(
                "=== steady-state Update, one call for everyone vs one call per <=64 cluster ===");
            TestContext.Out.WriteLine(
                $"field {FIELD_CELLS}x{FIELD_CELLS} cells, agents on a {AGENT_STRIDE} lattice, ORCA on");

            foreach (int n in Sizes)
            {
                var single = BuildCrowd(n, avoidance: true);
                var split = BuildCrowd(n, avoidance: true);
                int tick = 2;

                var whole = Measure(() => single.System.Update(
                    ref single.Frame, single.Entities, n, tick++, NavAgentTestHelper.DT));
                var clustered = Measure(() => RunSplit(split, tick++));

                double ratio = clustered.medianMs > 0 ? whole.medianMs / clustered.medianMs : 0;
                Report($"{n,5} agents  single call", whole);
                Report($"{n,5} agents  {split.Clusters.Length,3} clusters", clustered,
                    $"{ratio,6:F2}x cheaper than the single call");
            }
        }

        [Test]
        public void CostDecomposition_AStarStorm_Orca_Correction_FrameCopy()
        {
            TestContext.Out.WriteLine("=== where the time goes, per tick ===");

            foreach (int n in Sizes)
            {
                // (1) A* storm: every agent asks for a path on the same tick. This is the order
                //     tick, not the steady state — the cooldown does not gate a first request.
                //     The tick must jump past PathRepathCooldown between samples: a +1 tick leaves
                //     `ticksSinceLast < cooldown` true for every agent that already repathed once,
                //     so ProcessPathRequest returns before FindPath and the sample measures an
                //     empty storm. An early record here was exactly that: it read 0.27 ms at 800
                //     agents against the 966 ms below, understating the storm by three orders of
                //     magnitude and making the cheapest cost look like the second most expensive.
                var storm = BuildCrowd(n, avoidance: true);
                FPVector3 target = NavAgentTestHelper.CellCenter(2, FIELD_CELLS - 2);
                int cooldownTicks = 11;
                int stormTick = 100;
                var stormCost = Measure(() =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        ref var nav = ref storm.Frame.Get<NavAgentComponent>(storm.Entities[i]);
                        NavAgentComponent.SetDestination(ref nav, target);
                    }
                    storm.System.Update(ref storm.Frame, storm.Entities, n, stormTick,
                        NavAgentTestHelper.DT);
                    stormTick += cooldownTicks;
                }, warmup: 8, iterations: 5);

                //     The storm is expensive for reasons the new counters can name, so let them:
                //     a corridor longer than the buffer and a search that runs out of budget are
                //     both silent `false`/clamps without this readout.
                var pathfinder = storm.Pathfinder;

                // (2)+(3) ORCA scan + position correction: the delta between avoidance on and off.
                var withOrca = BuildCrowd(n, avoidance: true);
                var withoutOrca = BuildCrowd(n, avoidance: false);
                int t1 = 2, t2 = 2;
                var on = Measure(() => withOrca.System.Update(
                    ref withOrca.Frame, withOrca.Entities, n, t1++, NavAgentTestHelper.DT));
                var off = Measure(() => withoutOrca.System.Update(
                    ref withoutOrca.Frame, withoutOrca.Entities, n, t2++, NavAgentTestHelper.DT));

                // (4) Frame copy: what the rollback ring pays per tick regardless of nav work.
                var source = withOrca.Frame;
                var dest = new Frame(n + 16, null);
                var copy = Measure(() => dest.CopyFrom(source));

                TestContext.Out.WriteLine($"--- {n} agents ---");
                Report("  (1) A* storm tick (all repath)", stormCost,
                    $"corridor-clamped {pathfinder.DebugCorridorTruncatedCount}, " +
                    $"budget-exhausted {pathfinder.DebugIterationExhaustedCount} (cumulative)");
                Report("  (2+3) ORCA + correction", (on.minMs - off.minMs, on.medianMs - off.medianMs),
                    $"(avoidance on {on.medianMs:F3} - off {off.medianMs:F3})");
                Report("  (rest) path follow + movement", off);
                Report("  (4) Frame.CopyFrom", copy, $"{n + 16} entity slots reserved");
            }
        }

        [Test]
        public void ClusterSize_Sweep_ShowsWhatTheSplitTrades()
        {
            // The split is not a free win. Shrinking the cluster cuts the ORCA neighbour scan
            // (each agent only sees its own cluster), but it also un-caps the position-correction
            // pass: one call of 800 corrects the first 64 agents and drops the rest, while 13 calls
            // of 62 correct all 800. The sweep is where those two curves cross.
            TestContext.Out.WriteLine("=== cluster size sweep, 800 agents ===");
            const int n = 800;
            foreach (int size in new[] { 16, 32, 64, 128, 256, n })
            {
                var crowd = BuildCrowd(n, avoidance: true);
                crowd.Clusters = Partition(crowd.Entities, n, size);
                int tick = 2;
                var cost = Measure(() => RunSplit(crowd, tick++));

                int correctedPerCall = System.Math.Min(size, FPNavAgentSystem.MAX_AGENTS);
                Report($"  cluster {size,4}  ({crowd.Clusters.Length,3} calls)", cost,
                    $"corrects {correctedPerCall * crowd.Clusters.Length,5} of {n} agents");
            }
        }

        [Test]
        public void ClusterSplit_AllocatesNothingPerTick()
        {
            // V-2: the split call pattern must keep the zero-GC contract. Allocation here would
            // mean the recommended pattern trades a GC spike for the O(N²) it saves.
            foreach (int n in Sizes)
            {
                var crowd = BuildCrowd(n, avoidance: true);
                int tick = 2;
                long bytes = MeasureAlloc(() => RunSplit(crowd, tick++));
                TestContext.Out.WriteLine($"{n,5} agents  {crowd.Clusters.Length,3} clusters   {bytes,8} B/tick");
                Assert.AreEqual(0, bytes, $"cluster-split Update allocated {bytes} B at {n} agents");
            }
        }
    }
}
