using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Obstacle-avoidance clearance tests. Unlike the other obstacle test files (which pin
    /// TimeHorizonObst = 1 to keep their fixtures stable), these run on the RUNTIME DEFAULT: with
    /// obstacle avoidance ON, a path-following agent must still reach a destination it reaches with
    /// avoidance OFF. NOTE this is a coarse reachability net, not a sharp default-regression tripwire —
    /// synthetic single-level L/U corridors (1.2-3m wide) do NOT reproduce the stall that the old
    /// TimeHorizonObst=1 default showed on real baked stages (that trap needs stage-specific
    /// geometry: bake inset, slivers, multi-level walls); discriminating the default value stays a
    /// manual check on real bakes.
    /// </summary>
    public class NavObstacleClearanceTests
    {
        private static readonly FP64 DT = FP64.FromDouble(1.0 / 60.0);

        private static FPVector3 V(double x, double z)
            => new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));

        // L-shaped corridor, both arms 3m wide (regime A: width > 2*R_sim = 1.0, so obstacle
        // lines are feasible; the corner is where an over-large horizon used to trap agents).
        // Horizontal arm [0,12]x[0,3], vertical arm [9,12]x[0,12].
        private static FPNavMesh BuildLCorridor()
        {
            var verts = new[] { V(0, 0), V(12, 0), V(12, 12), V(9, 12), V(9, 3), V(0, 3) };
            var idx = new[] { 0, 1, 4, 0, 4, 5, 1, 2, 4, 2, 3, 4 };
            return FPNavMeshBuildPipeline.Build(verts, idx, new int[idx.Length / 3], 5.0, null);
        }

        // Drives one seeded agent from start to dest; returns the closest XZ distance to dest seen.
        // Drives one seeded agent; returns (closest distance to dest seen, minimum cruise speed).
        // Cruise window skips the initial acceleration and the arrival braking so the min speed
        // isolates mid-path slowdowns (e.g. the boundary-hug corner fight).
        private static (double minD, double minCruiseSpeed) Drive(
            FPNavMesh nav, FPVector3 start, FPVector3 dest, bool avoidance, int ticks,
            double obstacleRadiusInset = 0)
        {
            var query = new FPNavMeshQuery(nav, null);
            var pathfinder = new FPNavMeshPathfinder(nav, query, null);
            var funnel = new FPNavMeshFunnel(nav, query, null);
            var sys = new FPNavAgentSystem(nav, query, pathfinder, funnel, null);
            if (avoidance)
            {
                var av = new FPNavAvoidance(); // runtime default TimeHorizonObst on purpose
                sys.SetAvoidance(av);
                sys.LoadNavMeshObstacles();
                // Override AFTER the load: LoadNavMeshObstacles auto-applies the mesh's recorded
                // BakeAgentRadius (0 for these synthetic fixtures) — the explicit value here plays
                // the role of a bake radius the fixture doesn't carry.
                av.ObstacleRadiusInset = FP64.FromDouble(obstacleRadiusInset);
            }

            var sim = new EcsSimulation(64, maxRollbackTicks: 4, deltaTimeMs: 16);
            sim.Initialize();
            var e = sim.Frame.CreateEntity();
            var nc = default(NavAgentComponent);
            NavAgentComponent.Init(ref nc, start);
            nc.Speed = FP64.FromDouble(5);
            nc.Radius = FP64.FromDouble(0.5);
            nc.Acceleration = FP64.FromDouble(10);
            nc.CurrentTriangleIndex = query.FindTriangle(start.ToXZ(), start.y);
            NavAgentComponent.SetDestination(ref nc, dest);
            sim.Frame.Add(e, nc);
            var ents = new[] { e };

            var destXZ = dest.ToXZ();
            double minD = double.MaxValue, minSp = double.MaxValue;
            bool arrived = false;
            var frame = sim.Frame;
            for (int t = 1; t <= ticks; t++)
            {
                sys.Update(ref frame, ents, 1, t, DT);
                ref readonly var n = ref frame.GetReadOnly<NavAgentComponent>(e);
                double d = FPVector2.Distance(n.Position.ToXZ(), destXZ).ToFloat();
                if (d < minD) minD = d;
                if (d < 0.6) arrived = true;
                if (t > 30 && d > 2.0 && !arrived)
                {
                    double sp = n.CurrentSpeed.ToFloat();
                    if (sp < minSp) minSp = sp;
                }
            }
            return (minD, minSp);
        }

        [Test]
        public void Reachability_LCorridor_ObstacleOnReachesLikeOff()
        {
            var nav = BuildLCorridor();
            var start = V(1.5, 1.5);
            var dest = V(10.5, 10.5); // around the corner

            var off = Drive(nav, start, dest, avoidance: false, ticks: 900);
            var on = Drive(nav, start, dest, avoidance: true, ticks: 900);

            Assert.IsTrue(off.minD < 0.6, $"baseline (avoidance OFF) should reach the corner destination (min {off.minD:F2})");
            Assert.IsTrue(on.minD < 0.6, $"avoidance ON (runtime default) must not stall on the corridor corner (min {on.minD:F2})");
        }

        // The funnel is a point-agent path: it hugs boundary corners exactly, while obstacle lines
        // demand agent.Radius clearance from the boundary — the fight shows up as a hard speed dip
        // right at radius distance from every hugged corner (measured ~1.6 vs ~4.0 without
        // avoidance; horizon-independent). ObstacleRadiusInset = bake radius removes the
        // double-charged clearance: with the boundary itself as the constraint (effective radius
        // 0), the hugging path no longer fights the walls.
        [Test]
        public void CornerSlowdown_RemovedByObstacleRadiusInset()
        {
            var nav = BuildLCorridor();
            var start = V(1.5, 1.5);
            var dest = V(10.5, 10.5);

            var off = Drive(nav, start, dest, avoidance: false, ticks: 900);
            var uncorrected = Drive(nav, start, dest, avoidance: true, ticks: 900);
            var corrected = Drive(nav, start, dest, avoidance: true, ticks: 900,
                obstacleRadiusInset: 0.5); // R_sim = R_bake convention → effective radius 0

            Assert.IsTrue(uncorrected.minCruiseSpeed < 2.5,
                $"uncorrected clearance should show the corner dip (min speed {uncorrected.minCruiseSpeed:F2})");
            // Guard: minCruiseSpeed defaults to double.MaxValue and is only set inside the cruise
            // window; assert it was actually sampled so the > 3.0 check below cannot pass vacuously.
            Assert.IsTrue(corrected.minCruiseSpeed < double.MaxValue, "corrected run never entered the cruise window");
            Assert.IsTrue(corrected.minCruiseSpeed > 3.0,
                $"inset-corrected run must not dip at the corner (min speed {corrected.minCruiseSpeed:F2} " +
                $"vs OFF {off.minCruiseSpeed:F2})");
            Assert.IsTrue(corrected.minD < 0.6, "inset-corrected run must still reach");
        }

        // The baked asset records its own bake Agent Radius (serializer VERSION 3) and
        // LoadNavMeshObstacles applies it as the obstacle inset — riding the asset instead of a
        // hand-synced constant. Round-trips through the serializer and lands on the avoidance.
        [Test]
        public void BakeAgentRadius_RoundTripsAndAutoAppliesAsInset()
        {
            var verts = new[] { V(0, 0), V(10, 0), V(10, 10), V(0, 10) };
            var idx = new[] { 0, 1, 2, 0, 2, 3 };
            var built = FPNavMeshBuildPipeline.Build(verts, idx, new int[2], 5.0, null,
                bakeAgentRadius: 0.5, bakeMaxSlopeDeg: 45, bakeAgentHeight: 2, bakeAgentClimb: 0.75);
            Assert.AreEqual(FP64.Half.RawValue, built.BakeAgentRadius.RawValue);

            // serializer round-trip (VERSION 3 bake settings block)
            var buffer = new byte[FPNavMeshSerializer.GetSerializedSize(built)];
            var writer = new SpanWriter(buffer);
            FPNavMeshSerializer.Serialize(ref writer, built);
            var reader = new SpanReader(buffer);
            var loaded = FPNavMeshSerializer.Deserialize(ref reader);
            Assert.AreEqual(FP64.Half.RawValue, loaded.BakeAgentRadius.RawValue);
            Assert.AreEqual(FP64.FromInt(45).RawValue, loaded.BakeMaxSlopeDeg.RawValue);
            Assert.AreEqual(FP64.FromInt(2).RawValue, loaded.BakeAgentHeight.RawValue);
            Assert.AreEqual(FP64.FromDouble(0.75).RawValue, loaded.BakeAgentClimb.RawValue);

            // auto-apply on load
            var query = new FPNavMeshQuery(loaded, null);
            var sys = new FPNavAgentSystem(loaded, query,
                new FPNavMeshPathfinder(loaded, query, null),
                new FPNavMeshFunnel(loaded, query, null), null);
            var av = new FPNavAvoidance();
            sys.SetAvoidance(av);
            sys.LoadNavMeshObstacles();
            Assert.AreEqual(FP64.Half.RawValue, av.ObstacleRadiusInset.RawValue);
        }

        // Degenerate: with effective radius 0, MoveAlongSurface legitimately clamps the agent
        // center EXACTLY onto the obstacle boundary (and corner vertices) — the obstacle block must
        // stay crash-free and produce finite results there (no zero-vector normalization).
        [Test]
        public void EffectiveRadiusZero_OnBoundary_NoCrash()
        {
            var av = new FPNavAvoidance();
            av.TimeHorizonObst = FP64.FromInt(1);
            av.ObstacleRadiusInset = FP64.Half; // == agent radius below → effective radius 0
            av.LoadObstacles(new[]
            {
                new FPVector2(FP64.FromInt(0), FP64.FromInt(0)),
                new FPVector2(FP64.FromInt(4), FP64.FromInt(0)),
                new FPVector2(FP64.FromInt(4), FP64.FromInt(4)),
                new FPVector2(FP64.FromInt(0), FP64.FromInt(4)),
            }, null);

            var sim = new EcsSimulation(64, maxRollbackTicks: 4, deltaTimeMs: 16);
            sim.Initialize();
            var positions = new[]
            {
                new FPVector2(FP64.FromInt(2), FP64.FromInt(0)),   // exactly on an edge
                new FPVector2(FP64.FromInt(0), FP64.FromInt(0)),   // exactly on a (convex) vertex
                new FPVector2(FP64.FromInt(2), FP64.FromDouble(-0.001)), // grazing just outside
            };
            foreach (var p in positions)
            {
                var e = sim.Frame.CreateEntity();
                var nc = default(NavAgentComponent);
                NavAgentComponent.Init(ref nc, new FPVector3(p.x, FP64.Zero, p.y));
                nc.Radius = FP64.Half;
                nc.Velocity = new FPVector2(FP64.Zero, FP64.FromInt(3));        // pushing into the wall
                nc.DesiredVelocity = new FPVector2(FP64.Zero, FP64.FromInt(3));
                sim.Frame.Add(e, nc);
                var ents = new[] { e };
                var frame = sim.Frame;

                var result = av.ComputeNewVelocity(0, ref frame, ents, 1, DT); // must not throw
                Assert.IsTrue(FP64.Abs(result.x) < FP64.FromInt(100) && FP64.Abs(result.y) < FP64.FromInt(100),
                    $"finite result expected at {p}");
            }
        }
    }
}
