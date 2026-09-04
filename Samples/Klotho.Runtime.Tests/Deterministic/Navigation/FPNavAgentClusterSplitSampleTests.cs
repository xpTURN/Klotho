using System;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The worked example of the cluster-split call pattern that `Navigation.md` § Crowd Scaling
    /// recommends: a game-side partitioner a caller can copy, plus the properties that make it
    /// safe to copy.
    ///
    /// <para><b>Why a sample and not just the perf harness.</b>
    /// <see cref="FPNavAgentCrowdPerfTests"/> proves the split is cheaper, but it partitions by
    /// ARRAY INDEX — a run of 64 consecutive entries — because the cost shape it measures does not
    /// depend on the rule. The documentation recommends a SPATIAL rule ("sort by grid cell, then by
    /// entity index"), and nothing in the repo showed one. This fixture is that rule, and the tests
    /// are the difference between the two: a spatial partition is a pure function of positions and
    /// entity ids, so it does not depend on the order the game happens to enumerate agents in.
    /// Index chunking does — and two peers that enumerate differently then diverge.</para>
    ///
    /// <para>Runs in the normal suite (no <c>[Explicit]</c>): everything here is a correctness
    /// property, not a measurement. The one byte-level gate self-ignores in DEBUG.</para>
    /// </summary>
    [TestFixture]
    public class FPNavAgentClusterSplitSampleTests
    {
        #region The sample — a spatial partitioner a game can copy

        // Cluster cell size as a power of two in world units (2^2 = 4). Power of two on purpose:
        // the cell index is then an arithmetic shift of the fixed-point raw value — exact integer
        // arithmetic, no division, and a shift floors correctly on negative coordinates, so an
        // agent at x = -0.1 lands in cell -1 rather than sharing cell 0 with x = +0.1.
        //
        // The cell size is the DIAL FOR CLUSTER SIZE, and it is the only tuning here: cells hold
        // whatever the local density puts in them, so a smaller cell means more, smaller calls.
        // The measured sweep in `Navigation.md` says small wins on cost — bounded below by the
        // avoidance cut at cluster boundaries, which is a quality trade and not a number.
        private const int CLUSTER_CELL_LOG2 = 2;

        private static int CellOf(FP64 world)
            => (int)(world.RawValue >> (FP64.FRACTIONAL_BITS + CLUSTER_CELL_LOG2));

        /// <summary>
        /// One agent's sort key. The order is (cell z, cell x, entity index) and it is a TOTAL
        /// order — entity index breaks every remaining tie, so no two agents compare equal and the
        /// sort's stability cannot reach the result. That matters more than it looks: the array
        /// order is a determinism input (ORCA breaks coincident ties on it, and the correction pass
        /// takes the first <see cref="FPNavAgentSystem.MAX_AGENTS"/>), so a key that left ties open
        /// would hand the outcome to the sort implementation.
        /// </summary>
        private readonly struct ClusterKey : IComparable<ClusterKey>
        {
            public readonly int CellZ;
            public readonly int CellX;
            public readonly int EntityIndex;
            public readonly EntityRef Entity;

            public ClusterKey(int cellZ, int cellX, EntityRef entity)
            {
                CellZ = cellZ;
                CellX = cellX;
                EntityIndex = entity.Index;
                Entity = entity;
            }

            public int CompareTo(ClusterKey other)
            {
                if (CellZ != other.CellZ) return CellZ < other.CellZ ? -1 : 1;
                if (CellX != other.CellX) return CellX < other.CellX ? -1 : 1;
                if (EntityIndex != other.EntityIndex) return EntityIndex < other.EntityIndex ? -1 : 1;
                return 0;
            }
        }

        /// <summary>
        /// Sorts the caller's agents into spatially local runs of at most <c>maxPerCall</c> and
        /// hands them out one run at a time. Buffers are allocated once and reused, so a steady
        /// state tick allocates nothing (pinned by
        /// <see cref="Partition_IsAllocationFree_InSteadyState"/>).
        ///
        /// <para><b>Runs break at cell boundaries</b>, and only then at <c>maxPerCall</c>. Cutting
        /// the sorted sequence every <c>maxPerCall</c> agents instead — the obvious shortcut, and
        /// the first version of this sample — merges whole cells into one call whenever the cells
        /// are small relative to the cap, which produced clusters WIDER than plain index chunking
        /// (measured: 89 vs 45 world units² on this fixture's layout). The cap is a ceiling, not a
        /// target; a dense cell is what actually needs the second cut.</para>
        /// </summary>
        private sealed class SpatialClusterPartitioner
        {
            private readonly ClusterKey[] _keys;
            private readonly EntityRef[] _cluster;
            private readonly int[] _starts;      // run boundaries: [ClusterCount] holds the count
            private readonly int _maxPerCall;
            private int _clusterCount;

            public SpatialClusterPartitioner(int maxAgents, int maxPerCall)
            {
                _keys = new ClusterKey[maxAgents];
                _cluster = new EntityRef[maxPerCall];
                _starts = new int[maxAgents + 1];
                _maxPerCall = maxPerCall;
            }

            public int ClusterCount => _clusterCount;

            /// <summary>Reads positions out of frame state and sorts. Call once per tick.</summary>
            public void Prepare(ref Frame frame, EntityRef[] entities, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    ref readonly var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                    _keys[i] = new ClusterKey(
                        CellOf(nav.Position.z), CellOf(nav.Position.x), entities[i]);
                }
                // Struct array + Array.Sort(T[], int, int): in place, no comparison delegate, and
                // no allocation after the first call (the default comparer is cached per type).
                Array.Sort(_keys, 0, count);

                // Runs: a new one at every cell change, and at the per-call cap inside a cell.
                _clusterCount = 0;
                int runStart = 0;
                for (int i = 1; i <= count; i++)
                {
                    bool last = i == count;
                    bool newCell = !last &&
                        (_keys[i].CellZ != _keys[runStart].CellZ ||
                         _keys[i].CellX != _keys[runStart].CellX);
                    if (last || newCell || i - runStart == _maxPerCall)
                    {
                        _starts[_clusterCount++] = runStart;
                        runStart = i;
                    }
                }
                _starts[_clusterCount] = count;
            }

            /// <summary>
            /// Fills the reused buffer with cluster <paramref name="index"/> and returns its
            /// length. The buffer is handed out rather than copied, so it is only valid until the
            /// next call — which is exactly the lifetime <c>Update</c> needs.
            /// </summary>
            public int FillCluster(int index, out EntityRef[] cluster)
            {
                int start = _starts[index];
                int len = _starts[index + 1] - start;
                for (int i = 0; i < len; i++)
                    _cluster[i] = _keys[start + i].Entity;
                cluster = _cluster;
                return len;
            }
        }

        /// <summary>The whole call pattern: sort once, then one <c>Update</c> per cluster.</summary>
        private static void UpdateByCluster(FPNavAgentSystem system, ref Frame frame,
            SpatialClusterPartitioner partitioner, EntityRef[] entities, int count, int tick)
        {
            partitioner.Prepare(ref frame, entities, count);
            int clusters = partitioner.ClusterCount;
            for (int c = 0; c < clusters; c++)
            {
                int len = partitioner.FillCluster(c, out var cluster);
                system.Update(ref frame, cluster, len, tick, NavAgentTestHelper.DT);
            }
        }

        #endregion

        #region Fixture

        private const int FIELD_CELLS = 24;
        private const int AGENTS = 130;
        private const int PER_ROW = 12;
        private const double AGENT_STRIDE = 0.9;

        private sealed class Crowd
        {
            public FPNavAgentSystem System;
            public Frame Frame;
            public EntityRef[] Entities;
        }

        /// <summary>
        /// <paramref name="count"/> agents on an open field, all Moving toward the far corner —
        /// the steady state the split pattern is for. <paramref name="reversed"/> hands the same
        /// agents to the caller in the opposite order, which is how this fixture stands in for
        /// "another peer enumerates its entities differently".
        /// </summary>
        private static Crowd BuildCrowd(int count, bool reversed = false)
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(FIELD_CELLS);
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            system.SetAvoidance(new FPNavAvoidance());

            var positions = new FPVector3[count];
            var velocities = new FPVector2[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = new FPVector3(
                    FP64.FromDouble(6.0 + (i % PER_ROW) * AGENT_STRIDE), FP64.Zero,
                    FP64.FromDouble(6.0 + (i / PER_ROW) * AGENT_STRIDE));
                velocities[i] = FPVector2.Zero;
            }

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                positions, velocities, out var entities, maxEntities: count + 8);

            FPVector3 target = NavAgentTestHelper.CellCenter(FIELD_CELLS - 2, FIELD_CELLS - 2);
            for (int i = 0; i < count; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                NavAgentComponent.SetDestination(ref nav, target);
            }

            if (reversed)
                Array.Reverse(entities);

            return new Crowd { System = system, Frame = frame, Entities = entities };
        }

        /// <summary>The partition as a flat sequence — cluster boundaries included.</summary>
        private static (EntityRef[] order, int[] lengths) Flatten(SpatialClusterPartitioner p, int count)
        {
            var order = new EntityRef[count];
            var lengths = new int[p.ClusterCount];
            int at = 0;
            for (int c = 0; c < p.ClusterCount; c++)
            {
                lengths[c] = p.FillCluster(c, out var cluster);
                Array.Copy(cluster, 0, order, at, lengths[c]);
                at += lengths[c];
            }
            return (order, lengths);
        }

        #endregion

        [Test]
        public void Partition_CoversEveryAgentExactlyOnce()
        {
            // An agent in two arrays moves twice in one tick, and an agent in none stops being
            // simulated — both are silent, so the partition property is the first thing to pin.
            var crowd = BuildCrowd(AGENTS);
            var p = new SpatialClusterPartitioner(AGENTS, FPNavAgentSystem.MAX_AGENTS);
            p.Prepare(ref crowd.Frame, crowd.Entities, AGENTS);

            var (order, lengths) = Flatten(p, AGENTS);
            var seen = new bool[AGENTS + 8];
            foreach (var e in order)
            {
                Assert.IsFalse(seen[e.Index], $"entity {e.Index} appears in two clusters");
                seen[e.Index] = true;
            }
            foreach (var e in crowd.Entities)
                Assert.IsTrue(seen[e.Index], $"entity {e.Index} is in no cluster");

            foreach (int len in lengths)
                Assert.LessOrEqual(len, FPNavAgentSystem.MAX_AGENTS,
                    "a cluster past MAX_AGENTS drops the tail from the correction pass — the cap " +
                    "is per call, so the chunk size is what respects it");
        }

        [Test]
        public void Partition_DoesNotDependOnTheOrderTheGameEnumeratesIn()
        {
            // The property index chunking does NOT have, and the reason to prefer a spatial rule
            // even before the cost: the partition is a function of positions and entity ids, so
            // two peers that enumerate their entities differently still produce the same runs in
            // the same order.
            var a = BuildCrowd(AGENTS);
            var b = BuildCrowd(AGENTS, reversed: true);

            var pa = new SpatialClusterPartitioner(AGENTS, FPNavAgentSystem.MAX_AGENTS);
            var pb = new SpatialClusterPartitioner(AGENTS, FPNavAgentSystem.MAX_AGENTS);
            pa.Prepare(ref a.Frame, a.Entities, AGENTS);
            pb.Prepare(ref b.Frame, b.Entities, AGENTS);

            var (orderA, lengthsA) = Flatten(pa, AGENTS);
            var (orderB, lengthsB) = Flatten(pb, AGENTS);

            Assert.AreEqual(lengthsA, lengthsB, "cluster sizes must not depend on input order");
            for (int i = 0; i < AGENTS; i++)
                Assert.AreEqual(orderA[i].Index, orderB[i].Index,
                    $"position {i} of the partition differs — the rule read input order somewhere");
        }

        [Test]
        public void Partition_IsSpatiallyTighterThanIndexOrderChunking()
        {
            // "Spatial" is a claim about the output, so it gets measured against the alternative
            // rather than asserted. The perf harness chunks by index; on a row-major lattice that
            // produces wide, flat clusters whose members are far apart, which is the opposite of
            // what the ORCA scan wants.
            var crowd = BuildCrowd(AGENTS);
            var p = new SpatialClusterPartitioner(AGENTS, FPNavAgentSystem.MAX_AGENTS);
            p.Prepare(ref crowd.Frame, crowd.Entities, AGENTS);
            var (order, lengths) = Flatten(p, AGENTS);

            double spatial = WorstClusterArea(ref crowd.Frame, order, lengths);
            double indexOrder = WorstClusterArea(ref crowd.Frame, crowd.Entities,
                ChunkLengths(AGENTS, FPNavAgentSystem.MAX_AGENTS));

            TestContext.Out.WriteLine(
                $"worst cluster bounding box: spatial {spatial:F1} vs index-order {indexOrder:F1} " +
                $"world units²  ({lengths.Length} clusters vs " +
                $"{ChunkLengths(AGENTS, FPNavAgentSystem.MAX_AGENTS).Length})");

            Assert.Less(spatial, indexOrder,
                $"the spatial rule must produce tighter clusters than index chunking " +
                $"(spatial {spatial:F1} vs index {indexOrder:F1} world units²)");
        }

        private static int[] ChunkLengths(int count, int size)
        {
            var lengths = new int[(count + size - 1) / size];
            for (int c = 0; c < lengths.Length; c++)
                lengths[c] = System.Math.Min(size, count - c * size);
            return lengths;
        }

        /// <summary>Largest XZ bounding-box area over the clusters of a flat partition.</summary>
        private static double WorstClusterArea(ref Frame frame, EntityRef[] order, int[] lengths)
        {
            double worst = 0;
            int at = 0;
            foreach (int len in lengths)
            {
                double minX = double.MaxValue, maxX = double.MinValue;
                double minZ = double.MaxValue, maxZ = double.MinValue;
                for (int i = 0; i < len; i++)
                {
                    ref readonly var nav = ref frame.Get<NavAgentComponent>(order[at + i]);
                    double x = nav.Position.x.ToDouble(), z = nav.Position.z.ToDouble();
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }
                double area = (maxX - minX) * (maxZ - minZ);
                if (area > worst) worst = area;
                at += len;
            }
            return worst;
        }

        [Test]
        public void SplitCall_CorrectsEveryAgent_WhereOneCallDropsTheRest()
        {
            // The payoff, stated through the counter that made the loss visible in the first place:
            // MAX_AGENTS is a per-CALL cap, so 130 agents in one call leave 66 interpenetrated
            // with nothing logged, while three calls of <= 64 separate all of them.
            var single = BuildCrowd(AGENTS);
            single.System.Update(ref single.Frame, single.Entities, AGENTS, 1, NavAgentTestHelper.DT);

            var split = BuildCrowd(AGENTS);
            var p = new SpatialClusterPartitioner(AGENTS, FPNavAgentSystem.MAX_AGENTS);
            UpdateByCluster(split.System, ref split.Frame, p, split.Entities, AGENTS, 1);

            Assert.AreEqual(AGENTS - FPNavAgentSystem.MAX_AGENTS,
                single.System.DebugCollisionResolveTruncatedCount,
                "one call for everyone drops everything past the cap");
            Assert.AreEqual(0, split.System.DebugCollisionResolveTruncatedCount,
                "clusters at or under the cap drop nobody");
        }

        [Test]
        public void SplitCall_ReplaysBitIdentically()
        {
            Assert.AreEqual(SimulateSpatialSplit(reversed: false), SimulateSpatialSplit(reversed: false),
                "the rule reads frame state only, so two runs must land on the same hash");
        }

        [Test]
        public void SplitCall_ConvergesEvenWhenPeersEnumerateDifferently()
        {
            // The determinism claim at simulation level, and the sample's reason to exist. Under
            // the spatial rule the enumeration order cannot reach the result; the contrast below
            // shows that this is a property of the RULE and not of the engine.
            Assert.AreEqual(SimulateSpatialSplit(reversed: false), SimulateSpatialSplit(reversed: true),
                "a peer that enumerates its agents in another order must still converge");
        }

        [Test]
        public void IndexOrderChunking_DivergesWhenPeersEnumerateDifferently()
        {
            // Why the documentation asks for a spatial rule rather than any partition: index
            // chunking is a pure function of frame state too, but only if every peer builds the
            // array in the same order. That is an assumption about game code, not an invariant, and
            // when it breaks both peers stay internally consistent — the divergence is silent.
            Assert.AreNotEqual(SimulateIndexSplit(reversed: false), SimulateIndexSplit(reversed: true),
                "index chunking carries the enumeration order into the simulation");
        }

        private const int SIM_TICKS = 12;

        private static ulong SimulateSpatialSplit(bool reversed)
        {
            var crowd = BuildCrowd(AGENTS, reversed);
            var p = new SpatialClusterPartitioner(AGENTS, FPNavAgentSystem.MAX_AGENTS);
            for (int tick = 1; tick <= SIM_TICKS; tick++)
                UpdateByCluster(crowd.System, ref crowd.Frame, p, crowd.Entities, AGENTS, tick);
            return crowd.Frame.CalculateHash();
        }

        private static ulong SimulateIndexSplit(bool reversed)
        {
            var crowd = BuildCrowd(AGENTS, reversed);
            var cluster = new EntityRef[FPNavAgentSystem.MAX_AGENTS];
            for (int tick = 1; tick <= SIM_TICKS; tick++)
            {
                for (int start = 0; start < AGENTS; start += FPNavAgentSystem.MAX_AGENTS)
                {
                    int len = System.Math.Min(FPNavAgentSystem.MAX_AGENTS, AGENTS - start);
                    Array.Copy(crowd.Entities, start, cluster, 0, len);
                    crowd.System.Update(ref crowd.Frame, cluster, len, tick, NavAgentTestHelper.DT);
                }
            }
            return crowd.Frame.CalculateHash();
        }

        [Test]
        public void Partition_IsAllocationFree_InSteadyState()
        {
#if DEBUG
            Assert.Ignore(
                "byte-level gate: DEBUG allocates in paths this measurement cannot separate from "
                + "the partitioner. Run: dotnet test -c Release --filter "
                + "FullyQualifiedName~Partition_IsAllocationFree");
#else
            // The buffers are allocated once, and Array.Sort over a struct array with a cached
            // default comparer is the reason no delegate or boxed comparer appears per tick. A game
            // calls this every tick for every crowd, so the steady state has to be free.
            var crowd = BuildCrowd(AGENTS);
            var p = new SpatialClusterPartitioner(AGENTS, FPNavAgentSystem.MAX_AGENTS);

            for (int warm = 0; warm < 32; warm++)
            {
                p.Prepare(ref crowd.Frame, crowd.Entities, AGENTS);
                for (int c = 0; c < p.ClusterCount; c++)
                    p.FillCluster(c, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int rep = 0; rep < 8; rep++)
            {
                p.Prepare(ref crowd.Frame, crowd.Entities, AGENTS);
                for (int c = 0; c < p.ClusterCount; c++)
                    p.FillCluster(c, out _);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0, allocated,
                $"the partition step must not allocate in steady state (measured {allocated} B " +
                "over 8 repetitions)");
#endif
        }
    }
}
