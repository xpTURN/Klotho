using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Graph-local obstacle query tests. The graph BFS lives in FPNavAgentSystem, so most cases
    /// drive a seeded, Moving agent through FPNavAgentSystem.Update and inspect the avoidance's
    /// selected-segment snapshot, while the partition case tests the extractor CSR directly.
    /// Fixtures are 3D (vertices carry Y) so multi-floor adjacency is real (BuildAdjacency joins by
    /// shared vertex index, so distinct-vertex floors stay disconnected; shared-edge ramps stay connected).
    /// </summary>
    public class NavGraphLocalObstacleTests
    {
        private const int MaxEntities = 256;
        private static readonly FP64 DT = FP64.FromDouble(0.05);

        // --- fixture / harness helpers ---

        private static FPVector3 V(double x, double y, double z)
            => new FPVector3(FP64.FromDouble(x), FP64.FromDouble(y), FP64.FromDouble(z));

        private static FPNavMesh Build(FPVector3[] verts, int[] idx, double bakeMaxSlopeDeg = 0)
        {
            var areas = new int[idx.Length / 3];
            return FPNavMeshBuildPipeline.Build(verts, idx, areas, 5.0, null, null,
                bakeMaxSlopeDeg: bakeMaxSlopeDeg);
        }

        // Flat square room [0,size]^2 at y=0.
        private static FPNavMesh BuildRoom(double size)
            => Build(new[] { V(0, 0, 0), V(size, 0, 0), V(size, 0, size), V(0, 0, size) },
                     new[] { 0, 1, 2, 0, 2, 3 });

        // 1F [0,20]^2 at y=0 + a disconnected 2F square [6,14]^2 raised to y=5 (distinct vertices →
        // separate graph component; XZ-overlaps the 1F interior so its walls are phantom in XZ).
        private static FPNavMesh BuildStackedFloors()
            => Build(new[]
                {
                    V(0, 0, 0), V(20, 0, 0), V(20, 0, 20), V(0, 0, 20),     // 0-3 1F
                    V(6, 5, 6), V(14, 5, 6), V(14, 5, 14), V(6, 5, 14),     // 4-7 2F
                },
                new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 });

        // Two same-floor rooms separated by an unwalkable gap [10,12] (distinct vertices →
        // disconnected). Room A [0,10]x[0,10], room B [12,22]x[0,10], both y=0.
        private static FPNavMesh BuildSplitRooms()
            => Build(new[]
                {
                    V(0, 0, 0), V(10, 0, 0), V(10, 0, 10), V(0, 0, 10),      // 0-3 room A
                    V(12, 0, 0), V(22, 0, 0), V(22, 0, 10), V(12, 0, 10),    // 4-7 room B
                },
                new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 });

        // 1F [0,10]x[0,10] (y=0) → ramp [10,14]x[0,10] rising 0→4 → 2F [14,24]x[0,10] (y=4).
        // Seams share vertex indices, so all three are one connected component.
        // bakeMaxSlopeDeg tags the mesh's recorded bake slope (0 = unknown → no auto climb cap).
        private static FPNavMesh BuildRamp(double bakeMaxSlopeDeg = 0)
            => Build(new[]
                {
                    V(0, 0, 0), V(10, 0, 0), V(10, 0, 10), V(0, 0, 10),      // 0-3 1F
                    V(14, 4, 0), V(14, 4, 10),                               // 4-5 ramp top / 2F near
                    V(24, 4, 0), V(24, 4, 10),                               // 6-7 2F far
                },
                new[]
                {
                    0, 1, 2, 0, 2, 3,     // 1F
                    1, 2, 5, 1, 5, 4,     // ramp (shares edge 1-2 with 1F, edge 4-5 with 2F)
                    4, 5, 7, 4, 7, 6,     // 2F
                }, bakeMaxSlopeDeg);

        // Flat tessellated grid [0, cells*step]^2 at y=0: cells×cells quads (2 triangles each,
        // 2*cells^2 triangles) sharing edge vertices → one connected component. Used to exceed the
        // BFS frontier cap (256) so the overflow diagnostic can be exercised.
        private static FPNavMesh BuildGrid(int cells, double step)
        {
            int side = cells + 1;
            var verts = new FPVector3[side * side];
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                    verts[j * side + i] = V(i * step, 0, j * step);
            var idx = new int[cells * cells * 6];
            int t = 0;
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    int a = j * side + i, b = j * side + i + 1, c = (j + 1) * side + i + 1, d = (j + 1) * side + i;
                    idx[t++] = a; idx[t++] = b; idx[t++] = c;
                    idx[t++] = a; idx[t++] = c; idx[t++] = d;
                }
            return Build(verts, idx);
        }

        // Fixtures in this file were authored against TimeHorizonObst = 1 (obstRange = speed +
        // radius, e.g. 5.5m at speed 5); pin it so the runtime default can be tuned without
        // re-scaling fixture wall distances.
        private static FPNavAvoidance NewPinnedAvoidance()
        {
            var av = new FPNavAvoidance();
            av.TimeHorizonObst = FP64.FromInt(1);
            return av;
        }

        private static FPNavAgentSystem NewAgentSystem(FPNavMesh nav, out FPNavMeshQuery query)
        {
            query = new FPNavMeshQuery(nav, null);
            var pathfinder = new FPNavMeshPathfinder(nav, query, null);
            var funnel = new FPNavMeshFunnel(nav, query, null);
            return new FPNavAgentSystem(nav, query, pathfinder, funnel, null);
        }

        private static EcsSimulation NewSim()
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 4, deltaTimeMs: 50);
            sim.Initialize();
            return sim;
        }

        // Drives one seeded, Moving agent through `ticks` Updates (graph path) and returns the
        // avoidance for inspection. Seeds CurrentTriangleIndex at spawn so the graph path
        // actually engages; a destination makes ProcessPathRequest flip the agent to Moving.
        private static FPNavAvoidance RunGraph(FPNavMesh nav, FPVector3 pos, FPVector3 dest,
            double speed, FP64? climbCap = null, int ticks = 1, double radius = 0.5)
        {
            var sys = NewAgentSystem(nav, out var query);
            var av = NewPinnedAvoidance();
            sys.SetAvoidance(av);
            sys.LoadNavMeshObstacles();
            if (climbCap.HasValue)
                sys.MaxClimbWithinHorizon = climbCap.Value;

            var sim = NewSim();
            var e = sim.Frame.CreateEntity();
            var nc = default(NavAgentComponent);
            NavAgentComponent.Init(ref nc, pos);
            nc.Speed = FP64.FromDouble(speed);
            nc.Radius = FP64.FromDouble(radius);
            nc.CurrentTriangleIndex = query.FindTriangle(pos.ToXZ(), pos.y);
            NavAgentComponent.SetDestination(ref nc, dest);
            sim.Frame.Add(e, nc);
            var ents = new[] { e };

            var frame = sim.Frame;
            for (int t = 1; t <= ticks; t++)
                sys.Update(ref frame, ents, 1, t, DT);
            return av;
        }

        // Brute-force selection (fallback path) at a fixed position — direct 5-arg ComputeNewVelocity,
        // no Update/pathfinding needed.
        private static HashSet<int> BruteForceSelected(FPNavMesh nav, FPVector3 pos, double speed, double radius = 0.5)
        {
            var av = NewPinnedAvoidance();
            FPNavMeshObstacleExtractor.Extract(nav, out var v, out var offs);
            av.LoadObstacles(v, offs);

            var sim = NewSim();
            var e = sim.Frame.CreateEntity();
            var nc = default(NavAgentComponent);
            NavAgentComponent.Init(ref nc, pos);
            nc.Speed = FP64.FromDouble(speed);
            nc.Radius = FP64.FromDouble(radius);
            sim.Frame.Add(e, nc);
            var ents = new[] { e };

            var frame = sim.Frame;
            av.ComputeNewVelocity(0, ref frame, ents, 1, DT);
            return Selected(av);
        }

        private static HashSet<int> Selected(FPNavAvoidance av)
        {
            var set = new HashSet<int>();
            var idx = av.DebugSelectedObstacleSegments;
            for (int i = 0; i < av.DebugSelectedObstacleCount; i++)
                set.Add(idx[i]);
            return set;
        }

        // segment index -> source triangle (invert the extractor CSR).
        private static int[] SegmentToTriangle(FPNavMesh nav)
        {
            FPNavMeshObstacleExtractor.Extract(nav, out var v, out _, out var triSegStart, out var triSegList);
            var seg2tri = new int[v.Length];
            for (int t = 0; t < nav.Triangles.Length; t++)
                for (int k = triSegStart[t]; k < triSegStart[t + 1]; k++)
                    seg2tri[triSegList[k]] = t;
            return seg2tri;
        }

        // Does the selected set contain any segment whose source triangle sits at/above yMin?
        private static bool AnySelectedAtFloor(HashSet<int> sel, int[] seg2tri, FPNavMesh nav, double yMin, double yMax)
        {
            FP64 lo = FP64.FromDouble(yMin), hi = FP64.FromDouble(yMax);
            foreach (int s in sel)
            {
                FP64 cy = nav.Triangles[seg2tri[s]].centerY;
                if (cy >= lo && cy <= hi)
                    return true;
            }
            return false;
        }

        // --- extractor triangle→segment CSR is a partition covering every boundary segment ---

        [Test]
        public void H1_ExtractorCsr_IsPartitionOverBoundarySegments()
        {
            var nav = BuildStackedFloors();
            FPNavMeshObstacleExtractor.Extract(nav, out var verts, out _, out var triSegStart, out var triSegList);

            int triCount = nav.Triangles.Length;
            Assert.AreEqual(triCount + 1, triSegStart.Length);
            Assert.AreEqual(verts.Length, triSegList.Length);
            Assert.AreEqual(verts.Length, triSegStart[triCount]); // coverage: every segment owned

            // Partition: each segment appears exactly once; and its owner triangle really has a
            // boundary edge starting at that ring vertex.
            var seen = new int[verts.Length];
            for (int t = 0; t < triCount; t++)
            {
                Assert.IsTrue(triSegStart[t] <= triSegStart[t + 1]); // monotone
                for (int k = triSegStart[t]; k < triSegStart[t + 1]; k++)
                {
                    int seg = triSegList[k];
                    seen[seg]++;
                    Assert.IsTrue(HasBoundaryEdgeStartingAt(nav, t, verts[seg]),
                        $"triangle {t} should own a boundary edge starting at segment {seg}");
                }
            }
            for (int s = 0; s < seen.Length; s++)
                Assert.AreEqual(1, seen[s]); // exactly once
        }

        private static bool HasBoundaryEdgeStartingAt(FPNavMesh nav, int t, FPVector2 point)
        {
            ref FPNavMeshTriangle tri = ref nav.Triangles[t];
            for (int e = 0; e < 3; e++)
            {
                if (tri.GetNeighbor(e) != -1)
                    continue;
                tri.GetEdgeVertices(e, out int va, out int vb);
                if (nav.Vertices[va].ToXZ() == point || nav.Vertices[vb].ToXZ() == point)
                    return true;
            }
            return false;
        }

        // --- single level — open (set-equal to brute force) vs occluded (behind-wall excluded) ---

        [Test]
        public void H2a_OpenRoom_GraphSelectsSameSegmentSetAsBruteForce()
        {
            var nav = BuildRoom(10);
            var av = RunGraph(nav, V(5, 0, 4), V(9, 0, 9), speed: 5);
            Assert.IsTrue(av.DebugLastObstaclePathWasGraph, "graph path must have run");

            var graph = Selected(av);
            var brute = BruteForceSelected(nav, V(5, 0, 4), speed: 5);
            Assert.IsTrue(graph.SetEquals(brute),
                $"open room: graph set {{{string.Join(",", graph)}}} != brute {{{string.Join(",", brute)}}}");
            Assert.IsNotEmpty(graph);
        }

        [Test]
        public void H2b_SplitRooms_GraphExcludesFarRoomWallThatBruteForceIncludes()
        {
            var nav = BuildSplitRooms();
            int[] seg2tri = SegmentToTriangle(nav);
            // Room B triangles are indices 2,3 (built after A). Classify by triangle index.
            bool InRoomB(int seg) => seg2tri[seg] >= 2;

            var av = RunGraph(nav, V(9, 0, 5), V(2, 0, 5), speed: 5);
            Assert.IsTrue(av.DebugLastObstaclePathWasGraph, "graph path must have run");
            var graph = Selected(av);
            var brute = BruteForceSelected(nav, V(9, 0, 5), speed: 5);

            XAssert.DoesNotContain(graph, InRoomB);          // graph never crosses the gap
            XAssert.Contains(brute, InRoomB);                // brute-force picks the facing wall (phantom)
            Assert.IsNotEmpty(graph);                         // room A's own near wall still selected
        }

        // --- stacked floors — graph drops the 2F phantom that brute force keeps ---

        [Test]
        public void H3_StackedFloors_GraphDropsUpperFloorPhantom()
        {
            var nav = BuildStackedFloors();
            int[] seg2tri = SegmentToTriangle(nav);

            var av = RunGraph(nav, V(10, 0, 8), V(3, 0, 3), speed: 5);
            Assert.IsTrue(av.DebugLastObstaclePathWasGraph, "graph path must have run");
            var graph = Selected(av);
            var brute = BruteForceSelected(nav, V(10, 0, 8), speed: 5);

            // Brute force sees the 2F walls (XZ within range) as phantom; graph does not.
            Assert.IsTrue(AnySelectedAtFloor(brute, seg2tri, nav, 4.5, 5.5),
                "brute force should include the 2F phantom wall");
            Assert.IsFalse(AnySelectedAtFloor(graph, seg2tri, nav, 4.5, 5.5),
                "graph-local must exclude the disconnected 2F wall");
        }

        // --- ramp connectivity — step-delta lets the query traverse up and down the ramp ---

        [Test]
        public void H4_Ramp_StepDeltaTraversesBothConnectedFloors()
        {
            var nav = BuildRamp();
            int[] seg2tri = SegmentToTriangle(nav);

            // Agent standing on the ramp surface (x=12 → y=2), climbing toward 2F.
            var av = RunGraph(nav, V(12, 2, 5), V(20, 4, 5), speed: 5);
            Assert.IsTrue(av.DebugLastObstaclePathWasGraph, "graph path must have run");
            var graph = Selected(av);

            Assert.IsTrue(AnySelectedAtFloor(graph, seg2tri, nav, -0.5, 0.5),
                "ramp query should reach connected 1F walls in range");
            Assert.IsTrue(AnySelectedAtFloor(graph, seg2tri, nav, 3.5, 4.5),
                "ramp query should reach connected 2F walls in range");
        }

        // --- fallback (two axes) + empty≠fallback ---

        [Test]
        public void H5a_UnseededAgent_FallsBackToBruteForce()
        {
            var nav = BuildStackedFloors();
            int[] seg2tri = SegmentToTriangle(nav);

            var sys = NewAgentSystem(nav, out _);
            var av = NewPinnedAvoidance();
            sys.SetAvoidance(av);
            sys.LoadNavMeshObstacles();

            var sim = NewSim();
            var e = sim.Frame.CreateEntity();
            var nc = default(NavAgentComponent);
            // Destination makes it Moving via pathfinding, but CurrentTriangleIndex is left at -1
            // (unseeded) → pass-2 must take the brute-force fallback, not the graph path.
            NavAgentComponent.Init(ref nc, V(10, 0, 8));
            nc.Speed = FP64.FromDouble(5);
            NavAgentComponent.SetDestination(ref nc, V(3, 0, 3));
            sim.Frame.Add(e, nc);
            var ents = new[] { e };

            var frame = sim.Frame;
            sys.Update(ref frame, ents, 1, 1, DT);

            Assert.IsFalse(av.DebugLastObstaclePathWasGraph, "unseeded (-1) agent must use brute-force");
            // Brute force at this open 1F spot sees the 2F phantom.
            Assert.IsTrue(AnySelectedAtFloor(Selected(av), seg2tri, nav, 4.5, 5.5),
                "fallback brute-force should include the 2F phantom");
        }

        [Test]
        public void H5b_NonNavmeshSource_UsesBruteForce()
        {
            // Obstacles loaded directly (no CSR) → graph unavailable → brute-force, no crash.
            var av = NewPinnedAvoidance();
            av.LoadObstacles(new[]
            {
                new FPVector2(FP64.FromInt(0), FP64.FromInt(0)),
                new FPVector2(FP64.FromInt(4), FP64.FromInt(0)),
                new FPVector2(FP64.FromInt(4), FP64.FromInt(4)),
                new FPVector2(FP64.FromInt(0), FP64.FromInt(4)),
            }, null);

            var sim = NewSim();
            var e = sim.Frame.CreateEntity();
            var nc = default(NavAgentComponent);
            NavAgentComponent.Init(ref nc, V(2, 0, -1));
            nc.Speed = FP64.FromDouble(5);
            sim.Frame.Add(e, nc);
            var ents = new[] { e };

            var frame = sim.Frame;
            av.ComputeNewVelocity(0, ref frame, ents, 1, DT); // 5-arg fallback
            Assert.IsFalse(av.DebugLastObstaclePathWasGraph);
            Assert.IsTrue(av.DebugNumObstLines > 0, "brute-force should still produce obstacle lines");
        }

        [Test]
        public void H5c_OpenAgentUnderPhantom_GraphYieldsZeroLines_NotFallback()
        {
            var nav = BuildStackedFloors();
            // Open 1F interior: no 1F wall in range; only the 2F phantom is XZ-near.
            var av = RunGraph(nav, V(10, 0, 8), V(3, 0, 3), speed: 5);

            Assert.IsTrue(av.DebugLastObstaclePathWasGraph, "graph path must have run (not fallback)");
            Assert.AreEqual(0, av.DebugNumObstLines); // graph: no reachable wall → 0 lines, no phantom
        }

        // --- determinism + hot-path GC-0 (AgentSystem.Update scope) ---

        [Test]
        public void H6_Determinism_And_UpdateIsGcZero()
        {
            var nav = BuildRoom(10);

            var avA = RunGraph(nav, V(5, 0, 4), V(9, 0, 9), speed: 5);
            var avB = RunGraph(nav, V(5, 0, 4), V(9, 0, 9), speed: 5);
            Assert.IsTrue(Selected(avA).SetEquals(Selected(avB)), "repeated identical input must select the identical set");
            // Determinism is order-sensitive: the nearest-first selection order feeds the LP-constraint
            // order (and thus the resulting velocity), so compare the selected segment sequences
            // element-by-element, not merely as sets.
            Assert.AreEqual(avA.DebugSelectedObstacleCount, avB.DebugSelectedObstacleCount);
            for (int k = 0; k < avA.DebugSelectedObstacleCount; k++)
                Assert.AreEqual(avA.DebugSelectedObstacleSegments[k], avB.DebugSelectedObstacleSegments[k]);

            // GC-0: steady-state Update (agent already Moving, path fixed) allocates 0 bytes/tick.
            var sys = NewAgentSystem(nav, out var query);
            var av = NewPinnedAvoidance();
            sys.SetAvoidance(av);
            sys.LoadNavMeshObstacles();
            var sim = NewSim();
            var e = sim.Frame.CreateEntity();
            var nc = default(NavAgentComponent);
            NavAgentComponent.Init(ref nc, V(5, 0, 4));
            nc.Speed = FP64.FromDouble(5);
            nc.CurrentTriangleIndex = query.FindTriangle(new FPVector2(FP64.FromInt(5), FP64.FromInt(4)), FP64.Zero);
            NavAgentComponent.SetDestination(ref nc, V(9, 0, 9));
            sim.Frame.Add(e, nc);
            var ents = new[] { e };
            var frame = sim.Frame;

            for (int t = 1; t <= 2000; t++) // warmup + reach steady state (JIT, path built)
                sys.Update(ref frame, ents, 1, t, DT);

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int t = 2001; t <= 22000; t++)
                sys.Update(ref frame, ents, 1, t, DT);
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0, allocated / 20000); // 0 bytes per tick
        }

        // --- BFS frontier cap is a real coverage cliff — a mesh with far more in-horizon triangles
        //     than the cap must trip the overflow diagnostic (not silently truncate unnoticed) ---
        [Test]
        public void H10_LargeMesh_TripsBfsFrontierOverflowDiagnostic()
        {
            var nav = BuildGrid(24, 2.0); // 24*24*2 = 1152 triangles over [0,48]^2
            Assert.IsTrue(nav.Triangles.Length > 256, "fixture must exceed the frontier cap");

            var sys = NewAgentSystem(nav, out var query);
            var av = NewPinnedAvoidance();
            sys.SetAvoidance(av);
            sys.LoadNavMeshObstacles();

            var sim = NewSim();
            var e = sim.Frame.CreateEntity();
            var nc = default(NavAgentComponent);
            var start = V(24, 0, 24);
            NavAgentComponent.Init(ref nc, start);
            // obstRange = TimeHorizonObst(1)*speed + radius = 60.5 → covers the whole grid (max
            // center-to-corner ≈ 34), so every triangle is in-horizon and BFS must overflow the cap.
            nc.Speed = FP64.FromDouble(60);
            nc.CurrentTriangleIndex = query.FindTriangle(start.ToXZ(), start.y);
            NavAgentComponent.SetDestination(ref nc, V(46, 0, 46));
            sim.Frame.Add(e, nc);
            var ents = new[] { e };

            var frame = sim.Frame;
            for (int tick = 1; tick <= 5; tick++)
                sys.Update(ref frame, ents, 1, tick, DT);

            Assert.IsTrue(sys.DebugBfsFrontierOverflowCount > 0,
                $"BFS over {nav.Triangles.Length} in-horizon triangles must trip the frontier-cap diagnostic " +
                $"(got {sys.DebugBfsFrontierOverflowCount})");
        }

        // --- expansion padding — a near wall on a far-centroid triangle is not missed ---

        [Test]
        public void H7_ExpansionPadding_NearWallOnFarCentroidTriangleSelected()
        {
            // Long room [0,30]x[0,4]: the far triangle's centroid is well beyond obstRange, but its
            // bottom wall passes within range of an agent near the origin. A center-only expansion
            // gate would skip that triangle and miss the wall; the padded (nearest-point) gate must
            // still visit it — so the graph set matches brute force (which is topology-agnostic).
            var nav = Build(new[] { V(0, 0, 0), V(30, 0, 0), V(30, 0, 4), V(0, 0, 4) },
                            new[] { 0, 1, 2, 0, 2, 3 });

            var av = RunGraph(nav, V(2, 0, 2), V(6, 0, 2), speed: 5);
            Assert.IsTrue(av.DebugLastObstaclePathWasGraph, "graph path must have run");
            var graph = Selected(av);
            var brute = BruteForceSelected(nav, V(2, 0, 2), speed: 5);
            Assert.IsTrue(graph.SetEquals(brute),
                $"padding: graph {{{string.Join(",", graph)}}} must match brute {{{string.Join(",", brute)}}}");
        }

        // --- recorded bake slope auto-derives the climb cap (no manual cap set) ---
        // obstRange (pinned horizon 1 × speed 5 + radius 0.5) = 5.5. 2F sits at height 4.
        //   slope 45° → capAuto = 5.5·sin45 ≈ 3.89 < 4 → 2F auto-excluded.
        //   slope 60° → capAuto = 5.5·sin60 ≈ 4.76 > 4 → 2F still reachable.
        //   slope 0 (unknown) → no auto cap → 2F reachable, matching the uncapped climb.
        [Test]
        public void H9_BakeSlope_AutoDerivesClimbCap()
        {
            var start = V(9.5, 0, 0.5);
            var dest = V(2, 0, 8);

            var nav45 = BuildRamp(bakeMaxSlopeDeg: 45);
            int[] seg2tri45 = SegmentToTriangle(nav45);
            var capped = Selected(RunGraph(nav45, start, dest, speed: 5));
            Assert.IsFalse(AnySelectedAtFloor(capped, seg2tri45, nav45, 3.5, 4.5),
                "recorded 45° slope must auto-cap the climb and exclude the 2F wall");
            Assert.IsTrue(AnySelectedAtFloor(capped, seg2tri45, nav45, 1.5, 2.5),
                "auto cap must not cut ramp-local walls");

            var nav60 = BuildRamp(bakeMaxSlopeDeg: 60);
            int[] seg2tri60 = SegmentToTriangle(nav60);
            var loose = Selected(RunGraph(nav60, start, dest, speed: 5));
            Assert.IsTrue(AnySelectedAtFloor(loose, seg2tri60, nav60, 3.5, 4.5),
                "60° slope bound (≈4.76) exceeds the 2F height (4) — wall stays reachable");
        }

        // --- climb cap — bounds the ramp climb, excluding the upper floor while keeping locals ---

        [Test]
        public void H8_ClimbCap_ExcludesUpperFloorButKeepsRampLocalWalls()
        {
            var nav = BuildRamp();
            int[] seg2tri = SegmentToTriangle(nav);

            // Agent on 1F near the ramp base; 2F wall is XZ-within-range and ramp-connected.
            var uncapped = Selected(RunGraph(nav, V(9.5, 0, 0.5), V(2, 0, 8), speed: 5));
            Assert.IsTrue(AnySelectedAtFloor(uncapped, seg2tri, nav, 3.5, 4.5),
                "uncapped (∞) query climbs the ramp and reaches the 2F wall");

            // Cap below the 2F height (4) but above the ramp centerY (2): 2F excluded, ramp kept.
            var capped = Selected(RunGraph(nav, V(9.5, 0, 0.5), V(2, 0, 8), speed: 5,
                climbCap: FP64.FromInt(3)));
            Assert.IsFalse(AnySelectedAtFloor(capped, seg2tri, nav, 3.5, 4.5),
                "climb cap must exclude the 2F wall");
            Assert.IsTrue(AnySelectedAtFloor(capped, seg2tri, nav, 1.5, 2.5),
                "climb cap must still keep the ramp-local walls (climb within cap)");
        }
    }
}
