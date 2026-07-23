using System;
using Xunit;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// ORCA static-obstacle (obstacle ORCA line) tests. Pure C# / CoreCLR.
    /// Covers LoadObstacles structure, obstacle avoidance behavior, determinism, the
    /// MAX_OBST_LINES budget, and hot-path GC-0.
    /// </summary>
    public class NavObstacleAvoidanceTests
    {
        private const int MaxEntities = 256;
        private static readonly FP64 DT = FP64.FromDouble(0.05);

        // --- helpers ---

        private static EcsSimulation NewSim()
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 4, deltaTimeMs: 50);
            sim.Initialize();
            return sim;
        }

        // Fixtures in this file were authored against TimeHorizonObst = 1 (obstRange = speed +
        // radius); pin it so the runtime default can be tuned without re-scaling fixtures.
        private static FPNavAvoidance NewPinnedAvoidance()
        {
            var av = new FPNavAvoidance();
            av.TimeHorizonObst = FP64.FromInt(1);
            return av;
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

        // Axis-aligned CCW square ring of side `size` with lower-left corner at `origin`.
        private static FPVector2[] Square(FP64 ox, FP64 oz, FP64 size)
        {
            return new[]
            {
                new FPVector2(ox, oz),
                new FPVector2(ox + size, oz),
                new FPVector2(ox + size, oz + size),
                new FPVector2(ox, oz + size),
            };
        }

        private static FP64 I(int v) => FP64.FromInt(v);
        private static FP64 D(double v) => FP64.FromDouble(v);

        // Frame is a class; capture it in a local so it can be passed by ref (property isn't ref-returning).
        private static FPVector2 Compute(FPNavAvoidance av, EcsSimulation sim, EntityRef[] entities, int count)
        {
            var frame = sim.Frame;
            return av.ComputeNewVelocity(0, ref frame, entities, count, DT);
        }

        // --- LoadObstacles structure ---

        [Fact]
        public void T1_LoadObstacles_SingleSquareRing_ComputesUnitDirConvexPrevNext()
        {
            var av = NewPinnedAvoidance();
            av.LoadObstacles(Square(I(0), I(0), I(4)), null);

            Assert.Equal(4, av.DebugObstacleCount);
            var o = av.DebugObstacles;

            // prev/next wrap within the single ring
            Assert.Equal(1, o[0].nextIndex);
            Assert.Equal(3, o[0].prevIndex);
            Assert.Equal(0, o[3].nextIndex);
            Assert.Equal(2, o[3].prevIndex);

            // unit directions of the 4 edges (CCW square)
            AssertVec(new FPVector2(I(1), I(0)), o[0].unitDir);   // (0,0)->(4,0)
            AssertVec(new FPVector2(I(0), I(1)), o[1].unitDir);   // (4,0)->(4,4)
            AssertVec(new FPVector2(-I(1), I(0)), o[2].unitDir);  // (4,4)->(0,4)
            AssertVec(new FPVector2(I(0), -I(1)), o[3].unitDir);  // (0,4)->(0,0)

            // all corners convex, all belong to polygon 0
            for (int i = 0; i < 4; i++)
            {
                Assert.True(o[i].isConvex);
                Assert.Equal(0, o[i].polygonIndex);
            }
        }

        [Fact]
        public void T1_LoadObstacles_MultipleRings_PrevNextWrapWithinOwnRing()
        {
            var av = NewPinnedAvoidance();
            // ring0 = [0..4), ring1 = [4..8)
            var verts = new FPVector2[8];
            var r0 = Square(I(0), I(0), I(2));
            var r1 = Square(I(10), I(10), I(2));
            Array.Copy(r0, 0, verts, 0, 4);
            Array.Copy(r1, 0, verts, 4, 4);

            av.LoadObstacles(verts, new[] { 0, 4 });

            Assert.Equal(8, av.DebugObstacleCount);
            var o = av.DebugObstacles;

            // ring0 boundary: last vertex wraps to ring0 start (0), NOT into ring1
            Assert.Equal(0, o[3].nextIndex);
            Assert.Equal(2, o[3].prevIndex);
            Assert.Equal(0, o[0].polygonIndex);

            // ring1 boundary: index 4 is ring1 start; prev wraps to ring1 end (7), not ring0
            Assert.Equal(5, o[4].nextIndex);
            Assert.Equal(7, o[4].prevIndex);
            Assert.Equal(4, o[7].nextIndex); // ring1 last vertex wraps to ring1 start
            Assert.Equal(6, o[7].prevIndex);
            Assert.Equal(1, o[4].polygonIndex);
            Assert.Equal(1, o[7].polygonIndex);
        }

        [Fact]
        public void T1_LoadObstacles_CsrSentinelOffsets_Tolerated()
        {
            var av = NewPinnedAvoidance();
            var verts = new FPVector2[8];
            Array.Copy(Square(I(0), I(0), I(2)), 0, verts, 0, 4);
            Array.Copy(Square(I(10), I(10), I(2)), 0, verts, 4, 4);

            // CSR-style offsets with trailing sentinel == vertices.Length
            av.LoadObstacles(verts, new[] { 0, 4, 8 });

            Assert.Equal(8, av.DebugObstacleCount);
            var o = av.DebugObstacles;
            Assert.Equal(0, o[3].nextIndex); // ring0 wraps
            Assert.Equal(4, o[7].nextIndex); // ring1 wraps
        }

        // --- single wall avoidance + long-wall center detection (point-segment distance) ---

        [Fact]
        public void T2_WallAheadOfAgent_DeflectsVelocity()
        {
            var sim = NewSim();
            var entities = new EntityRef[1];
            // agent at origin, wants to go +Z straight into a wall crossing z=1
            entities[0] = AddAgent(sim, FPVector2.Zero, new FPVector2(I(0), I(3)), new FPVector2(I(0), I(3)));

            var av = NewPinnedAvoidance();
            // horizontal wall segment along X at z = 1, spanning x in [-3, 3]  (CCW-friendly single segment)
            av.LoadObstacles(new[]
            {
                new FPVector2(-I(3), I(1)),
                new FPVector2(I(3), I(1)),
                new FPVector2(I(3), I(1) + D(0.01)),
                new FPVector2(-I(3), I(1) + D(0.01)),
            }, null);

            var result = Compute(av, sim, entities, 1);

            Assert.True(av.DebugNumObstLines > 0, "wall in front should produce an obstacle line");
            // forward (+Z) velocity must be reduced from the desired 3 (blocked by wall)
            Assert.True(result.y.ToFloat() < 3.0f - 0.05f,
                $"expected +Z velocity to be curbed by the wall, got {result.y.ToFloat()}");
        }

        [Fact]
        public void T2b_LongWall_CenterApproach_EndpointsOutOfRange_StillDetected()
        {
            var sim = NewSim();
            var entities = new EntityRef[1];
            entities[0] = AddAgent(sim, FPVector2.Zero, new FPVector2(I(0), I(3)), new FPVector2(I(0), I(3)));

            var av = NewPinnedAvoidance();
            // Very long wall: endpoints far beyond the query range (~5.5), but the body passes
            // right in front (z=1). Point-segment distance must catch it; point-point would not.
            av.LoadObstacles(new[]
            {
                new FPVector2(-I(100), I(1)),
                new FPVector2(I(100), I(1)),
                new FPVector2(I(100), I(1) + D(0.01)),
                new FPVector2(-I(100), I(1) + D(0.01)),
            }, null);

            Compute(av, sim, entities, 1);

            Assert.True(av.DebugNumObstLines > 0,
                "long wall body in range must be detected via point-segment distance");
        }

        [Fact]
        public void T2c_NoObstacles_ReturnsDesiredVelocity()
        {
            var sim = NewSim();
            var entities = new EntityRef[1];
            entities[0] = AddAgent(sim, FPVector2.Zero, new FPVector2(I(2), I(1)), new FPVector2(I(2), I(1)));

            var av = NewPinnedAvoidance();
            var result = Compute(av, sim, entities, 1);

            Assert.Equal(0, av.DebugNumObstLines);
            AssertVec(new FPVector2(I(2), I(1)), result);
        }

        // --- determinism ---

        [Fact]
        public void T4_Determinism_RepeatedCalls_IdenticalResult()
        {
            FPVector2 Run()
            {
                var sim = NewSim();
                var entities = new EntityRef[1];
                entities[0] = AddAgent(sim, FPVector2.Zero, new FPVector2(D(0.5), I(3)), new FPVector2(D(0.5), I(3)));
                var av = NewPinnedAvoidance();
                av.LoadObstacles(Square(I(2), D(0.8), I(3)), null);
                return Compute(av, sim, entities, 1);
            }

            FPVector2 a = Run();
            FPVector2 b = Run();
            Assert.True(a == b, $"nondeterministic: {a} vs {b}"); // exact FP64 equality
        }

        // --- line budget (MAX_OBST_LINES reserves agent slots) ---

        [Fact]
        public void T6_ManySegments_ObstacleLinesCappedAndAgentSlotsPreserved()
        {
            var sim = NewSim();
            var entities = new EntityRef[1];
            entities[0] = AddAgent(sim, FPVector2.Zero, new FPVector2(I(1), I(1)), new FPVector2(I(1), I(1)));

            var av = NewPinnedAvoidance();
            // Build a dense ring of many short segments tightly surrounding the agent so that
            // far more than MAX_OBST_LINES segments fall inside the query range.
            int n = 200;
            var verts = new FPVector2[n];
            for (int i = 0; i < n; i++)
            {
                // CCW circle of radius 3 around origin
                double ang = 2.0 * System.Math.PI * i / n;
                verts[i] = new FPVector2(D(3.0 * System.Math.Cos(ang)), D(3.0 * System.Math.Sin(ang)));
            }
            av.LoadObstacles(verts, null);

            Compute(av, sim, entities, 1);

            Assert.True(av.DebugNumObstLines <= FPNavAvoidance.MAX_OBST_LINES,
                $"obstacle lines {av.DebugNumObstLines} must not exceed MAX_OBST_LINES {FPNavAvoidance.MAX_OBST_LINES}");
            // agent line slots (>= MAX_NEIGHBORS) remain available
            Assert.True(FPNavAvoidance.MAX_ORCA_LINES - av.DebugNumObstLines >= FPNavAvoidance.MAX_NEIGHBORS);
            // Non-vacuous: the 200 in-range segments must actually saturate SELECTION at the cap, so
            // the bounds above are genuinely exercised (a broken impl selecting 0 segments would
            // otherwise satisfy every assert above).
            Assert.Equal(FPNavAvoidance.MAX_OBST_LINES, av.DebugSelectedObstacleCount);
        }

        // Obstacle lines and agent-to-agent lines coexist: with a wall producing an obstacle line and
        // many neighbors competing, the reserved agent slots are still used (agents not starved).
        [Fact]
        public void T6b_ObstacleAndAgentLines_Coexist_AgentSlotsNotStarved()
        {
            var sim = NewSim();
            int agentCount = 1 + 20;
            var entities = new EntityRef[agentCount];
            // Center agent just below a wall (same geometry as T3, which yields an obstacle line).
            entities[0] = AddAgent(sim, new FPVector2(I(0), D(0.6)), FPVector2.Zero, new FPVector2(I(0), I(3)));
            // 20 neighbors packed within NeighborDist (= 5) so each contributes an agent ORCA line.
            for (int k = 1; k < agentCount; k++)
            {
                double ang = 2.0 * System.Math.PI * (k - 1) / (agentCount - 1);
                var p = new FPVector2(D(1.2 * System.Math.Cos(ang)), D(0.6 + 1.2 * System.Math.Sin(ang)));
                entities[k] = AddAgent(sim, p, FPVector2.Zero, new FPVector2(-p.x, -p.y));
            }

            var av = NewPinnedAvoidance();
            av.LoadObstacles(Box(-3, 1, 6, 0.5), null); // wall bottom edge at z = 1

            Compute(av, sim, entities, agentCount);

            Assert.True(av.DebugNumObstLines >= 1, "the wall must still produce an obstacle line");
            // Agent lines occupy the reserved slots on top of the obstacle lines — not starved.
            Assert.True(av.DebugOrcaLineCount > av.DebugNumObstLines,
                $"agent lines starved: total {av.DebugOrcaLineCount} vs obstacle {av.DebugNumObstLines}");
        }

        // --- T11: hot-path GC-0 ---

        [Fact]
        public void T11_ComputeNewVelocity_ObstaclePath_ZeroAllocations()
        {
            var sim = NewSim();
            var entities = new EntityRef[1];
            entities[0] = AddAgent(sim, FPVector2.Zero, new FPVector2(D(0.5), I(3)), new FPVector2(D(0.5), I(3)));
            var av = NewPinnedAvoidance();
            av.LoadObstacles(Square(I(1), D(0.8), I(3)), null);

            // Warm up thoroughly so tiered JIT promotes ComputeNewVelocity (and callees) to Tier-1
            // and the one-time recompilation allocations happen BEFORE the measured window. A bare
            // 50-call warm-up leaves the background recompile landing mid-measurement (~a few KB of
            // one-time JIT bookkeeping), which is not a per-tick allocation.
            for (int i = 0; i < 2000; i++)
                Compute(av, sim, entities, 1);

            const int iterations = 20000;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
                Compute(av, sim, entities, 1);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // Steady-state must allocate 0 bytes PER iteration. Integer-divide by the iteration count
            // so a stray one-time JIT allocation (< iterations bytes) reads as 0, while any real
            // per-iteration allocation (>= 24 bytes/object * iterations) is caught.
            Assert.Equal(0, allocated / iterations);
        }

        // Axis-aligned CCW box [x0,x0+w] x [z0,z0+h].
        private static FPVector2[] Box(double x0, double z0, double w, double h) => new[]
        {
            new FPVector2(D(x0), D(z0)), new FPVector2(D(x0 + w), D(z0)),
            new FPVector2(D(x0 + w), D(z0 + h)), new FPVector2(D(x0), D(z0 + h)),
        };

        // --- LP3 obstacle preservation — a neighbor pushing the agent into a wall must
        //     not push it through: LP2 goes infeasible, LP3 relaxes the AGENT line, never the wall. ---

        [Fact]
        public void T3_NeighborPushesIntoWall_ObstacleLineNeverRelaxed()
        {
            var sim = NewSim();
            // A sits below a wall at z=1; B just below A drives straight up (+Z) into A → into the wall.
            var eA = AddAgent(sim, new FPVector2(I(0), D(0.6)), FPVector2.Zero, FPVector2.Zero);
            var eB = AddAgent(sim, new FPVector2(I(0), D(0.2)), new FPVector2(I(0), I(3)), new FPVector2(I(0), I(3)));
            var entities = new[] { eA, eB };

            var av = NewPinnedAvoidance();
            av.LoadObstacles(Box(-3, 1, 6, 0.5), null); // wall bottom edge at z = 1

            var frame = sim.Frame;
            var result = av.ComputeNewVelocity(0, ref frame, entities, 2, DT);

            Assert.True(av.DebugInfeasibleCount >= 1, "the conflict should drive LP2 infeasible → LP3");
            Assert.True(av.DebugNumObstLines >= 1, "the wall must produce an obstacle line");

            // Every obstacle line must remain satisfied (det >= 0) — the wall is a hard constraint.
            var lines = av.DebugOrcaLines;
            for (int i = 0; i < av.DebugNumObstLines; i++)
            {
                FP64 det = FPVector2.Cross(lines[i].direction, result - lines[i].point);
                Assert.True(det.ToFloat() >= -1e-3f,
                    $"obstacle line {i} violated (det={det.ToFloat()}): agent was pushed through the wall");
            }

            // Concretely: the agent must not be shoved up into the wall despite B's +Z push.
            Assert.True(result.y.ToFloat() < 0.6f,
                $"agent penetrated toward the wall in +Z, got vy={result.y.ToFloat()}");
        }

        // --- convex corner navigation — multi-tick, agent must never enter the box interior
        //     (exercises corner legs + foreign-leg clamp; without them it would jam/penetrate). ---

        [Fact]
        public void T5_CornerApproach_MultiTick_NeverPenetratesBox()
        {
            var sim = NewSim();
            var entities = new[] { AddAgent(sim, new FPVector2(-I(2), -I(2)), new FPVector2(I(2), I(2)), new FPVector2(I(2), I(2))) };

            var av = NewPinnedAvoidance();
            av.LoadObstacles(Box(0, 0, 4, 4), null); // corner at (0,0)

            double worst = -999; // max penetration of the agent center into the box interior
            for (int t = 0; t < 60; t++)
            {
                var frame = sim.Frame;
                var v = av.ComputeNewVelocity(0, ref frame, entities, 1, DT);
                ref var na = ref sim.Frame.Get<NavAgentComponent>(entities[0]);
                na.Velocity = v;
                na.Position = na.Position + new FPVector3(v.x * DT, FP64.Zero, v.y * DT);

                double x = na.Position.x.ToFloat(), z = na.Position.z.ToFloat();
                double pen = System.Math.Min(System.Math.Min(x, 4 - x), System.Math.Min(z, 4 - z));
                if (pen > worst) worst = pen;
            }

            Assert.True(worst < 0.05, $"agent center penetrated the box (worst depth {worst})");
            // Progress: a correct agent slides around the corner rather than freezing; a jammed /
            // over-constrained agent stays at spawn and would pass the penetration check vacuously.
            ref var fin = ref sim.Frame.Get<NavAgentComponent>(entities[0]);
            double moved = FPVector2.Distance(fin.Position.ToXZ(), new FPVector2(-I(2), -I(2))).ToFloat();
            Assert.True(moved > 1.0, $"agent made no progress around the corner (moved {moved:F2} from spawn — jam?)");
        }

        // --- alreadyCovered + nearest-first order independence ---

        [Fact]
        public void T7_NearWallOccludesFar_AndOrderIndependent()
        {
            var sim = NewSim();
            var entities = new[] { AddAgent(sim, FPVector2.Zero, new FPVector2(I(0), I(3)), new FPVector2(I(0), I(3))) };

            // near wall (z=1) and a far stacked wall (z=2) behind it, both facing the agent.
            var near = Box(-3, 1, 6, 0.02);
            var far = Box(-3, 2, 6, 0.02);

            var avNear = NewPinnedAvoidance();
            avNear.LoadObstacles(near, null);
            var f0 = sim.Frame;
            avNear.ComputeNewVelocity(0, ref f0, entities, 1, DT);
            int nearOnly = avNear.DebugNumObstLines;

            // near registered first
            var v1 = new FPVector2[8];
            Array.Copy(near, 0, v1, 0, 4); Array.Copy(far, 0, v1, 4, 4);
            var avNF = NewPinnedAvoidance();
            avNF.LoadObstacles(v1, new[] { 0, 4 });
            var f1 = sim.Frame;
            var rNF = avNF.ComputeNewVelocity(0, ref f1, entities, 1, DT);

            // far registered first (array order reversed) — nearest-first sort must make this identical
            var v2 = new FPVector2[8];
            Array.Copy(far, 0, v2, 0, 4); Array.Copy(near, 0, v2, 4, 4);
            var avFN = NewPinnedAvoidance();
            avFN.LoadObstacles(v2, new[] { 0, 4 });
            var f2 = sim.Frame;
            var rFN = avFN.ComputeNewVelocity(0, ref f2, entities, 1, DT);

            // the far wall is occluded → adds nothing beyond the near wall
            Assert.Equal(nearOnly, avNF.DebugNumObstLines);
            // order independence: registration order does not change the outcome
            Assert.Equal(avNF.DebugNumObstLines, avFN.DebugNumObstLines);
            Assert.True(rNF == rFN, $"registration order changed the result: {rNF} vs {rFN}");
        }

        // --- obstacle lines take full responsibility — no reciprocal +velocity / *0.5.
        //     When overlapping a wall (collision case) the line anchors at the origin regardless
        //     of the agent's velocity; an agent-agent line would be offset by velocity. ---

        [Fact]
        public void T8_OverlappingWall_ObstacleLineAnchoredAtOrigin_NoReciprocal()
        {
            var sim = NewSim();
            // agent overlaps the wall (z=1, radius 0.5 reaches z=1.4) with a large velocity
            var entities = new[] { AddAgent(sim, new FPVector2(I(0), D(0.9)), new FPVector2(I(5), I(5)), new FPVector2(I(5), I(5))) };

            var av = NewPinnedAvoidance();
            av.LoadObstacles(Box(-3, 1, 6, 0.02), null);

            var frame = sim.Frame;
            av.ComputeNewVelocity(0, ref frame, entities, 1, DT);

            Assert.True(av.DebugNumObstLines >= 1);
            // Collision-case obstacle line anchors at the origin — the agent velocity (5,5) is NOT
            // added (no `+agentVelocity`) and no 1/2 sharing is applied.
            Assert.True(av.DebugOrcaLines[0].point == FPVector2.Zero,
                $"obstacle line point should be origin (full responsibility), got {av.DebugOrcaLines[0].point}");
        }

        // --- T10: sqrt guard boundary — agent exactly ~radius from an edge (distSq ~ radiusSq).
        //     The collision branch must fire before the leg sqrt, so no negative-sqrt throw. ---

        [Fact]
        public void T10_AgentAtRadiusDistance_NoNegativeSqrtThrow()
        {
            var sim = NewSim();
            // default radius 0.5; agent 0.5 to the left of the box's left edge (x=0)
            var entities = new[] { AddAgent(sim, new FPVector2(D(-0.5), I(0)), new FPVector2(I(1), I(0)), new FPVector2(I(1), I(0))) };

            var av = NewPinnedAvoidance();
            av.LoadObstacles(Box(0, -0.5, 4, 1), null);

            var frame = sim.Frame;
            var ex = Record.Exception(() => av.ComputeNewVelocity(0, ref frame, entities, 1, DT));

            Assert.Null(ex); // no ArgumentException from FP64.Sqrt on a negative argument
            Assert.True(av.DebugNumObstLines >= 1);
        }

        // --- utils ---

        private static void AssertVec(FPVector2 expected, FPVector2 actual, float tol = 1e-3f)
        {
            Assert.True(System.Math.Abs(expected.x.ToFloat() - actual.x.ToFloat()) < tol
                && System.Math.Abs(expected.y.ToFloat() - actual.y.ToFloat()) < tol,
                $"expected {expected}, got {actual}");
        }
    }
}
