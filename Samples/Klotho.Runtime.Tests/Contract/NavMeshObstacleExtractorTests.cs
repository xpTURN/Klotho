using System;
using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// NavMesh boundary extraction (FPNavMeshObstacleExtractor) and non-convex / winding
    /// behavior tests. Fixtures are built through the public FPNavMeshBuildPipeline.Build so
    /// adjacency (neighbor == -1 boundary) is real.
    /// </summary>
    public class NavMeshObstacleExtractorTests
    {
        private const int MaxEntities = 256;
        private static readonly FP64 DT = FP64.FromDouble(0.05);

        // --- fixture builders ---

        private static FPVector3 V(double x, double z) => new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));

        private static FPNavMesh Build(FPVector3[] verts, int[] idx)
        {
            var areas = new int[idx.Length / 3];
            return FPNavMeshBuildPipeline.Build(verts, idx, areas, 5.0, null);
        }

        // Square room [0,size]^2 (agent inside → outer boundary CW).
        private static FPNavMesh BuildRoom(double size = 10)
            => Build(new[] { V(0, 0), V(size, 0), V(size, size), V(0, size) }, new[] { 0, 1, 2, 0, 2, 3 });

        // Room [0,12]^2 with a square pillar hole [5,7]^2 (annulus).
        private static FPNavMesh BuildRoomWithPillar()
            => Build(
                new[] { V(0, 0), V(12, 0), V(12, 12), V(0, 12), V(5, 5), V(7, 5), V(7, 7), V(5, 7) },
                new[] { 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 });

        // L-shape walkable region; reflex (walkable) elbow at (4,4).
        private static FPNavMesh BuildLShape()
            => Build(
                new[] { V(0, 0), V(10, 0), V(10, 4), V(4, 4), V(4, 10), V(0, 10) },
                new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5 });

        // Two squares sharing vertex index 2 = (4,4): a pinch/bowtie vertex.
        private static FPNavMesh BuildBowtie()
            => Build(
                new[] { V(0, 0), V(4, 0), V(4, 4), V(0, 4), V(8, 4), V(8, 8), V(4, 8) },
                new[] { 0, 1, 2, 0, 2, 3, 2, 4, 5, 2, 5, 6 });

        // Signed area *2 (shoelace). > 0 = CCW, < 0 = CW.
        private static double SignedArea2(FPVector2[] v, int start, int end)
        {
            double a = 0;
            for (int i = start; i < end; i++)
            {
                var p = v[i];
                var q = v[i + 1 < end ? i + 1 : start];
                a += p.x.ToFloat() * q.y.ToFloat() - q.x.ToFloat() * p.y.ToFloat();
            }
            return a;
        }

        // Fixtures in this file were authored against TimeHorizonObst = 1 (obstRange = speed +
        // radius); pin it so the runtime default can be tuned without re-scaling fixtures.
        private static FPNavAvoidance NewPinnedAvoidance()
        {
            var av = new FPNavAvoidance();
            av.TimeHorizonObst = FP64.FromInt(1);
            return av;
        }

        private static int CountBoundaryEdges(FPNavMesh nav)
        {
            int c = 0;
            for (int t = 0; t < nav.Triangles.Length; t++)
                for (int e = 0; e < 3; e++)
                    if (nav.Triangles[t].GetNeighbor(e) == -1) c++;
            return c;
        }

        // --- room + pillar → outer CW, hole CCW, isConvex per vertex ---

        [Test]
        public void G1_RoomWithPillar_OuterCW_HoleCCW_ConvexityCorrect()
        {
            FPNavMeshObstacleExtractor.Extract(BuildRoomWithPillar(), out var v, out var offs);

            Assert.AreEqual(2, offs.Length);          // two rings
            Assert.AreEqual(8, v.Length);             // 4 + 4

            int r0s = offs[0], r0e = offs[1];
            int r1s = offs[1], r1e = v.Length;
            Assert.AreEqual(4, r0e - r0s);
            Assert.AreEqual(4, r1e - r1s);

            // outer boundary (walkable inside) is CW; pillar hole (walkable outside) is CCW
            Assert.IsTrue(SignedArea2(v, r0s, r0e) < 0, "outer ring should be CW");
            Assert.IsTrue(SignedArea2(v, r1s, r1e) > 0, "pillar ring should be CCW");

            var av = NewPinnedAvoidance();
            av.LoadObstacles(v, offs);
            var o = av.DebugObstacles;
            // outer room corners are reflex obstacles (solid wraps 270°) → isConvex false
            for (int i = r0s; i < r0e; i++) Assert.IsFalse(o[i].isConvex, $"outer[{i}]");
            // pillar corners are convex obstacle spikes → isConvex true
            for (int i = r1s; i < r1e; i++) Assert.IsTrue(o[i].isConvex, $"pillar[{i}]");
        }

        // --- extracted segments 1:1 with boundary edges, orientation (winding) correct ---

        [Test]
        public void G4_ExtractedSegments_MatchBoundaryEdges_AndWindingCorrect()
        {
            var nav = BuildRoomWithPillar();
            FPNavMeshObstacleExtractor.Extract(nav, out var v, out var offs);

            // (a) one segment per boundary edge, no loss / dup
            Assert.AreEqual(CountBoundaryEdges(nav), v.Length);

            // (b) orientation: outer CW + hole CCW encodes "walkable on the right" per edge.
            Assert.IsTrue(SignedArea2(v, offs[0], offs[1]) < 0);
            Assert.IsTrue(SignedArea2(v, offs[1], v.Length) > 0);
        }

        // --- deterministic — independent extractions of the same mesh are identical (peer proxy) ---

        [Test]
        public void G6_Deterministic_IndependentExtractions_Identical()
        {
            FPNavMeshObstacleExtractor.Extract(BuildRoomWithPillar(), out var v1, out var o1);
            FPNavMeshObstacleExtractor.Extract(BuildRoomWithPillar(), out var v2, out var o2);

            Assert.AreEqual(o1, o2);
            Assert.AreEqual(v1.Length, v2.Length);
            for (int i = 0; i < v1.Length; i++)
                Assert.IsTrue(v1[i] == v2[i], $"vertex {i} differs"); // exact FP64
        }

        // --- pinch (bowtie) vertex → fan chaining splits into two independent rings ---

        [Test]
        public void G7_PinchVertex_SplitsIntoTwoRings()
        {
            FPNavMeshObstacleExtractor.Extract(BuildBowtie(), out var v, out var offs);

            Assert.AreEqual(2, offs.Length);   // two independent rings, not one figure-8
            Assert.AreEqual(8, v.Length);      // 4 + 4
            Assert.AreEqual(4, offs[1] - offs[0]);
            Assert.AreEqual(4, v.Length - offs[1]);
        }

        // --- non-convex (reflex) handling — L-shape elbow is the only obstacle-convex vertex ---

        [Test]
        public void G2_LShape_ElbowIsObstacleConvex_RestReflex()
        {
            FPNavMeshObstacleExtractor.Extract(BuildLShape(), out var v, out var offs);
            XAssert.Single(offs);       // one ring
            Assert.AreEqual(6, v.Length);

            var av = NewPinnedAvoidance();
            av.LoadObstacles(v, offs);
            var o = av.DebugObstacles;

            int convexCount = 0, elbow = -1;
            for (int i = 0; i < av.DebugObstacleCount; i++)
                if (o[i].isConvex) { convexCount++; elbow = i; }

            Assert.AreEqual(1, convexCount); // exactly the elbow is obstacle-convex
            // elbow is the (4,4) inner corner
            Assert.IsTrue(System.Math.Abs(o[elbow].point.x.ToFloat() - 4) < 1e-3f
                     && System.Math.Abs(o[elbow].point.y.ToFloat() - 4) < 1e-3f);
        }

        // --- winding directionality — agent inside a room never leaves through a wall ---

        [Test]
        public void G3_AgentInsideRoom_StaysInside_MultiTick()
        {
            var sim = NewSim();
            var entities = new EntityRef[1];
            // agent near center, driving hard toward the +X wall (x = 10)
            entities[0] = AddAgent(sim, new FPVector2(FP64.FromInt(5), FP64.FromInt(5)),
                new FPVector2(FP64.FromInt(5), FP64.Zero), new FPVector2(FP64.FromInt(5), FP64.Zero));

            var av = NewPinnedAvoidance();
            FPNavMeshObstacleExtractor.Extract(BuildRoom(10), out var v, out var offs);
            av.LoadObstacles(v, offs);

            double maxX = -999;
            for (int t = 0; t < 80; t++)
            {
                var frame = sim.Frame;
                var vel = av.ComputeNewVelocity(0, ref frame, entities, 1, DT);
                ref var na = ref sim.Frame.Get<NavAgentComponent>(entities[0]);
                na.Velocity = vel;
                na.Position = na.Position + new FPVector3(vel.x * DT, FP64.Zero, vel.y * DT);
                if (na.Position.x.ToFloat() > maxX) maxX = na.Position.x.ToFloat();
            }

            // radius 0.5 → center must stay below the wall (10) minus roughly the radius
            Assert.IsTrue(maxX < 9.7, $"agent left the room through +X wall, maxX={maxX}");
            // Progress: the agent drives hard at the +X wall, so it must actually approach it
            // (a frozen/over-constrained agent stays near spawn x=5 and passes the bound above vacuously).
            Assert.IsTrue(maxX > 8.0, $"agent never approached the +X wall it was driving at (maxX={maxX} — jam?)");
        }

        // --- dual source — Choros block + NavMesh ring merged (concat) into one load ---

        [Test]
        public void G9_DualSource_ConcatMerge_BothObstaclesActive()
        {
            FPNavMeshObstacleExtractor.Extract(BuildRoom(20), out var nv, out var noffs);

            // a Choros-style convex CCW solid block (pillar) at [8,12]^2 inside the room
            var block = new[]
            {
                new FPVector2(FP64.FromInt(8), FP64.FromInt(8)),
                new FPVector2(FP64.FromInt(8), FP64.FromInt(12)),
                new FPVector2(FP64.FromInt(12), FP64.FromInt(12)),
                new FPVector2(FP64.FromInt(12), FP64.FromInt(8)),
            };

            // concat: vertices = navmesh rings ++ block; offsets shifted
            var verts = new FPVector2[nv.Length + block.Length];
            Array.Copy(nv, 0, verts, 0, nv.Length);
            Array.Copy(block, 0, verts, nv.Length, block.Length);
            var offsets = new int[noffs.Length + 1];
            Array.Copy(noffs, offsets, noffs.Length);
            offsets[noffs.Length] = nv.Length; // block ring start

            var av = NewPinnedAvoidance();
            av.LoadObstacles(verts, offsets);

            // agent just left of the block, driving into it → blocked (block obstacle active)
            var sim = NewSim();
            var e = new[] { AddAgent(sim, new FPVector2(FP64.FromInt(6), FP64.FromInt(10)),
                new FPVector2(FP64.FromInt(4), FP64.Zero), new FPVector2(FP64.FromInt(4), FP64.Zero)) };
            var frame = sim.Frame;
            av.ComputeNewVelocity(0, ref frame, e, 1, DT);

            Assert.IsTrue(av.DebugObstacleCount == nv.Length + block.Length);
            Assert.IsTrue(av.DebugNumObstLines > 0, "block/wall should produce obstacle lines");
        }

        // --- game-seam wiring helper (FPNavAgentSystem.LoadNavMeshObstacles) ---

        private static FPNavAgentSystem NewAgentSystem(FPNavMesh nav)
        {
            var query = new FPNavMeshQuery(nav, null);
            var pathfinder = new FPNavMeshPathfinder(nav, query, null);
            var funnel = new FPNavMeshFunnel(nav, query, null);
            return new FPNavAgentSystem(nav, query, pathfinder, funnel, null);
        }

        [Test]
        public void S2_LoadNavMeshObstacles_WiresObstaclesIntoAvoidance()
        {
            var nav = BuildRoomWithPillar();
            var agentSystem = NewAgentSystem(nav);

            // order misuse: LoadNavMeshObstacles BEFORE SetAvoidance → no-op (count stays 0)
            agentSystem.LoadNavMeshObstacles();
            Assert.AreEqual(0, agentSystem.DebugObstacleCount);

            // correct order: after SetAvoidance → obstacles loaded
            var av = NewPinnedAvoidance();
            agentSystem.SetAvoidance(av);
            agentSystem.LoadNavMeshObstacles();

            Assert.IsTrue(agentSystem.DebugObstacleCount > 0, "helper should load NavMesh obstacles");
            Assert.AreEqual(av.DebugObstacleCount, agentSystem.DebugObstacleCount);

            // obstacle lines actually generated for an agent near the wall
            var sim = NewSim();
            var e = new[] { AddAgent(sim, new FPVector2(FP64.FromInt(6), FP64.Zero),
                new FPVector2(FP64.Zero, FP64.FromInt(3)), new FPVector2(FP64.Zero, FP64.FromInt(3))) };
            var frame = sim.Frame;
            av.ComputeNewVelocity(0, ref frame, e, 1, DT);
            Assert.IsTrue(av.DebugNumObstLines > 0, "wall near agent should produce obstacle lines");
        }

        [Test]
        public void S2b_HelperDeterministic_IndependentWirings_Identical()
        {
            // NOTE: determinism re-confirmation (same helper twice), NOT client/server asymmetry —
            // a missing server-side wiring lives in the Brawler assembly, outside Core.Tests.
            var nav = BuildRoomWithPillar();

            var a1 = NewAgentSystem(nav);
            var av1 = NewPinnedAvoidance();
            a1.SetAvoidance(av1);
            a1.LoadNavMeshObstacles();

            var a2 = NewAgentSystem(nav);
            var av2 = NewPinnedAvoidance();
            a2.SetAvoidance(av2);
            a2.LoadNavMeshObstacles();

            Assert.AreEqual(av1.DebugObstacleCount, av2.DebugObstacleCount);
            var o1 = av1.DebugObstacles;
            var o2 = av2.DebugObstacles;
            for (int i = 0; i < av1.DebugObstacleCount; i++)
            {
                Assert.IsTrue(o1[i].point == o2[i].point, $"obstacle {i} point differs");
                Assert.AreEqual(o1[i].isConvex, o2[i].isConvex);
                Assert.AreEqual(o1[i].nextIndex, o2[i].nextIndex);
            }
        }

        // --- helpers ---

        private static EcsSimulation NewSim()
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 4, deltaTimeMs: 50);
            sim.Initialize();
            return sim;
        }

        private static EntityRef AddAgent(EcsSimulation sim, FPVector2 posXZ, FPVector2 vel, FPVector2 desiredVel)
        {
            var e = sim.Frame.CreateEntity();
            var nav = default(NavAgentComponent);
            NavAgentComponent.Init(ref nav, new FPVector3(posXZ.x, FP64.Zero, posXZ.y));
            nav.Velocity = vel;
            nav.DesiredVelocity = desiredVel;
            sim.Frame.Add(e, nav);
            return e;
        }
    }
}
