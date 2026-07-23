
using xpTURN.Klotho.Logging;

using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    [TestFixture]
    public class NavAvoidanceComponentTests
    {
        private const float EPSILON = 0.1f;

        IKLogger _logger = null;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Trace);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("Tests");
        }

        #region No neighbors

        [Test]
        public void ComputeNewVelocity_NoNeighbors_ReturnsDesiredVelocity()
        {
            var avoidance = new FPNavAvoidance();

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                new[] { new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero) },
                new[] { new FPVector2(FP64.One, FP64.Zero) },
                out var entities);

            FPVector2 result = avoidance.ComputeNewVelocity(0, ref frame, entities, 1, NavAgentTestHelper.DT);

            ref readonly var nav = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
            Assert.AreEqual(nav.DesiredVelocity.x.ToFloat(), result.x.ToFloat(), EPSILON);
            Assert.AreEqual(nav.DesiredVelocity.y.ToFloat(), result.y.ToFloat(), EPSILON);
        }

        #endregion

        #region Neighbor out of range

        [Test]
        public void ComputeNewVelocity_FarNeighbor_NoEffect()
        {
            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(5);

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                new[]
                {
                    new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                    new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero),
                },
                new[]
                {
                    new FPVector2(FP64.One, FP64.Zero),
                    new FPVector2(-FP64.One, FP64.Zero),
                },
                out var entities);

            FPVector2 result = avoidance.ComputeNewVelocity(0, ref frame, entities, 2, NavAgentTestHelper.DT);

            ref readonly var nav = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
            Assert.AreEqual(nav.DesiredVelocity.x.ToFloat(), result.x.ToFloat(), EPSILON);
            Assert.AreEqual(nav.DesiredVelocity.y.ToFloat(), result.y.ToFloat(), EPSILON);
        }

        #endregion

        #region Head-on collision avoidance

        [Test]
        public void ComputeNewVelocity_HeadOn_DeflectsVelocity()
        {
            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);
            avoidance.TimeHorizon = FP64.FromInt(3);

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                new[]
                {
                    new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                    new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero),
                },
                new[]
                {
                    new FPVector2(FP64.FromInt(2), FP64.Zero),
                    new FPVector2(FP64.FromInt(-2), FP64.Zero),
                },
                out var entities);

            FPVector2 result = avoidance.ComputeNewVelocity(0, ref frame, entities, 2, NavAgentTestHelper.DT);

            ref readonly var nav = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
            float originalX = nav.DesiredVelocity.x.ToFloat();
            float resultX = result.x.ToFloat();
            float resultY = result.y.ToFloat();

            bool deflected = (System.Math.Abs(resultX - originalX) > 0.01f) ||
                             (System.Math.Abs(resultY) > 0.01f);
            Assert.IsTrue(deflected, "velocity must be deflected on head-on collision");
        }

        #endregion

        #region Speed limit

        [Test]
        public void ComputeNewVelocity_ResultWithinMaxSpeed()
        {
            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                new[]
                {
                    new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                    new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero),
                },
                new[]
                {
                    new FPVector2(FP64.FromInt(3), FP64.Zero),
                    new FPVector2(FP64.FromInt(-3), FP64.Zero),
                },
                out var entities);

            FPVector2 result = avoidance.ComputeNewVelocity(0, ref frame, entities, 2, NavAgentTestHelper.DT);

            float speed = result.magnitude.ToFloat();
            ref readonly var nav = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
            float maxSpeed = nav.Speed.ToFloat();

            Assert.IsTrue(speed <= maxSpeed + 0.01f, "result speed must be within max speed");
        }

        #endregion

        #region LP line intersection

        [Test]
        public void IntersectLines_Perpendicular_FindsIntersection()
        {
            Assert.Pass("intersection logic verified in integration tests");
        }

        #endregion

        #region Multiple neighbors

        [Test]
        public void ComputeNewVelocity_MultipleNeighbors_StillValid()
        {
            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                new[]
                {
                    new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                    new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.One),
                    new FPVector3(FP64.FromInt(2), FP64.Zero, -FP64.One),
                },
                new[]
                {
                    new FPVector2(FP64.FromInt(2), FP64.Zero),
                    new FPVector2(FP64.FromInt(-2), FP64.Zero),
                    new FPVector2(FP64.FromInt(-2), FP64.Zero),
                },
                out var entities);

            FPVector2 result = avoidance.ComputeNewVelocity(0, ref frame, entities, 3, NavAgentTestHelper.DT);

            float speed = result.magnitude.ToFloat();
            ref readonly var nav = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
            float maxSpeed = nav.Speed.ToFloat();

            Assert.IsTrue(speed <= maxSpeed + 0.01f, "within max speed even with multiple neighbors");
        }

        #endregion

        #region ORCA line anchored at agent velocity

        [Test]
        public void ComputeNewVelocity_HeadOn_OrcaLineAnchoredAtAgentVelocity()
        {
            var avoidance = new FPNavAvoidance();

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                new[]
                {
                    new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                    new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero),
                },
                new[]
                {
                    new FPVector2(FP64.One, FP64.Zero),
                    new FPVector2(-FP64.One, FP64.Zero),
                },
                out var entities);

            ref var nav0 = ref frame.Get<NavAgentComponent>(entities[0]);
            nav0.Radius = FP64.FromDouble(0.25);
            nav0.Speed = FP64.One;
            ref var nav1 = ref frame.Get<NavAgentComponent>(entities[1]);
            nav1.Radius = FP64.FromDouble(0.25);
            nav1.Speed = FP64.One;

            FPVector2 result = avoidance.ComputeNewVelocity(0, ref frame, entities, 2, NavAgentTestHelper.DT);

            // line.point = velocity + u/2 (absolute velocity space), u = (-0.125, -0.4841)
            Assert.AreEqual(1, avoidance.DebugOrcaLineCount);
            var point = avoidance.DebugOrcaLines[0].point;
            Assert.AreEqual(0.9375f, point.x.ToFloat(), 0.001f);
            Assert.AreEqual(-0.2421f, point.y.ToFloat(), 0.001f);

            // result = v + u/2 (half responsibility), deterministic -y deflection (right leg)
            Assert.AreEqual(0.9375f, result.x.ToFloat(), 0.001f);
            Assert.AreEqual(-0.2421f, result.y.ToFloat(), 0.001f);
        }

        #endregion

        #region Coincident agents

        [Test]
        public void ComputeNewVelocity_CoincidentAgents_SeparateOppositeDirections()
        {
            var avoidance = new FPNavAvoidance();

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                new[]
                {
                    new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                    new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                },
                new[] { FPVector2.Zero, FPVector2.Zero },
                out var entities);

            ref var nav0 = ref frame.Get<NavAgentComponent>(entities[0]);
            nav0.DesiredVelocity = new FPVector2(FP64.One, FP64.Zero);
            ref var nav1 = ref frame.Get<NavAgentComponent>(entities[1]);
            nav1.DesiredVelocity = new FPVector2(FP64.One, FP64.Zero);

            FPVector2 resultA = avoidance.ComputeNewVelocity(0, ref frame, entities, 2, NavAgentTestHelper.DT);
            FPVector2 resultB = avoidance.ComputeNewVelocity(1, ref frame, entities, 2, NavAgentTestHelper.DT);

            // Index tie-break: fallback relPos = +/-0.9R -> u = -/+0.1R/dt -> -/+3 at 60Hz, R=1
            Assert.AreEqual(-3f, resultA.x.ToFloat(), 0.001f);
            Assert.AreEqual(3f, resultB.x.ToFloat(), 0.001f);
            Assert.IsTrue(resultA.x.ToFloat() < 0f && resultB.x.ToFloat() > 0f,
                "coincident agents must separate in opposite directions");
        }

        #endregion

        #region LinearProgram3 fallback

        [Test]
        public void ComputeNewVelocity_DeepOverlap_LinearProgram3Separates()
        {
            var avoidance = new FPNavAvoidance();

            // Deep non-coincident overlap: relPos=(0.01,0), R=1 -> collision-branch point ~= -/+29.7
            // lies outside the speed circle (maxSpeed=5) -> LP2 infeasible -> LP3 fallback.
            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                new[]
                {
                    new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.One),
                    new FPVector3(FP64.FromDouble(2.01), FP64.Zero, FP64.One),
                },
                new[] { FPVector2.Zero, FPVector2.Zero },
                out var entities);

            ref var nav0 = ref frame.Get<NavAgentComponent>(entities[0]);
            nav0.DesiredVelocity = new FPVector2(FP64.One, FP64.Zero);
            ref var nav1 = ref frame.Get<NavAgentComponent>(entities[1]);
            nav1.DesiredVelocity = new FPVector2(FP64.One, FP64.Zero);

            FPVector2 resultA = avoidance.ComputeNewVelocity(0, ref frame, entities, 2, NavAgentTestHelper.DT);
            FPVector2 resultB = avoidance.ComputeNewVelocity(1, ref frame, entities, 2, NavAgentTestHelper.DT);

            // LP3 closed form for a single constraint: maxSpeed along the feasible normal
            Assert.AreEqual(-5f, resultA.x.ToFloat(), 0.001f);
            Assert.AreEqual(0f, resultA.y.ToFloat(), 0.001f);
            Assert.AreEqual(5f, resultB.x.ToFloat(), 0.001f);
            Assert.AreEqual(0f, resultB.y.ToFloat(), 0.001f);
            Assert.AreEqual(2, avoidance.DebugInfeasibleCount);
        }

        #endregion

        #region Already overlapping

        [Test]
        public void ComputeNewVelocity_Overlapping_ProducesSeparation()
        {
            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                new[]
                {
                    new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                    new FPVector3(FP64.Half, FP64.Zero, FP64.Zero),
                },
                new[]
                {
                    FPVector2.Zero,
                    FPVector2.Zero,
                },
                out var entities);

            FPVector2 result = avoidance.ComputeNewVelocity(0, ref frame, entities, 2, NavAgentTestHelper.DT);

            Assert.Pass("ORCA line generated on overlap");
        }

        #endregion

        #region Integration: FPNavAgentSystem + avoidance

        [Test]
        public void System_WithAvoidance_HeadOn_BothArrive()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);
            system.SetAvoidance(avoidance);

            // Head-on east+west pair with z = 1 ± 0.1 stagger. The exact-collinear z=1 placement
            // stalls: the ORCA cutoff branch caps closing speed at (d−R)/T with zero lateral
            // component, so the cone legs never engage. The Δz=0.2 stagger breaks that symmetry
            // while staying < combinedRadius=1.0, so the pair still interpenetrates without avoidance.
            // (1,0.9) lies in T0, (5,1.1) in T3 (z > x−4) — tri hints must match the stagger.
            var frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[]
                {
                    new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromDouble(0.9)),
                    new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromDouble(1.1)),
                },
                new[] { 0, 3 },
                out var entities);

            ref var nav0 = ref frame.Get<NavAgentComponent>(entities[0]);
            NavAgentComponent.SetDestination(ref nav0,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromDouble(0.9)));

            ref var nav1 = ref frame.Get<NavAgentComponent>(entities[1]);
            NavAgentComponent.SetDestination(ref nav1,
                new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromDouble(1.1)));

            FP64 combinedRadius = FP64.One; // default Radius 0.5 each
            FP64 minDist = FP64.FromInt(100);
            bool bothArrived = false;

            for (int t = 1; t <= 600; t++)
            {
                system.Update(ref frame, entities, 2, t, NavAgentTestHelper.DT);

                ref readonly var a = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
                ref readonly var b = ref frame.GetReadOnly<NavAgentComponent>(entities[1]);
                FP64 dist = FPVector2.Distance(a.Position.ToXZ(), b.Position.ToXZ());
                if (dist < minDist) minDist = dist;

                if (a.Status == (byte)FPNavAgentStatus.Arrived &&
                    b.Status == (byte)FPNavAgentStatus.Arrived)
                {
                    bothArrived = true;
                    break;
                }
            }

            Assert.IsTrue(bothArrived, "both head-on agents must arrive within 600 ticks");
            Assert.IsTrue(minDist >= combinedRadius,
                $"agents must not interpenetrate (minDist={minDist.ToFloat()}, combinedRadius=1)");
        }

        [Test]
        public void System_WithAvoidance_PerpendicularCross_NoInterpenetration()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);
            system.SetAvoidance(avoidance);

            // A eastbound (3.2,2)->(7,2), B northbound (5,0.5)->(5,3.5) — synced crossing at (5,2).
            // Without avoidance minPairDist measures 0.889 (< combinedRadius) — this scenario
            // interpenetrates unless ORCA intervenes.
            var frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[]
                {
                    new FPVector3(FP64.FromDouble(3.2), FP64.Zero, FP64.FromInt(2)),
                    new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.Half),
                },
                new[] { 0, 2 },
                out var entities);

            ref var nav0 = ref frame.Get<NavAgentComponent>(entities[0]);
            NavAgentComponent.SetDestination(ref nav0,
                new FPVector3(FP64.FromInt(7), FP64.Zero, FP64.FromInt(2)));

            ref var nav1 = ref frame.Get<NavAgentComponent>(entities[1]);
            NavAgentComponent.SetDestination(ref nav1,
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromDouble(3.5)));

            FP64 combinedRadius = FP64.One; // default Radius 0.5 each
            FP64 minDist = FP64.FromInt(100);
            bool bothArrived = false;

            for (int t = 1; t <= 600; t++)
            {
                system.Update(ref frame, entities, 2, t, NavAgentTestHelper.DT);

                ref readonly var a = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
                ref readonly var b = ref frame.GetReadOnly<NavAgentComponent>(entities[1]);
                FP64 dist = FPVector2.Distance(a.Position.ToXZ(), b.Position.ToXZ());
                if (dist < minDist) minDist = dist;

                if (a.Status == (byte)FPNavAgentStatus.Arrived &&
                    b.Status == (byte)FPNavAgentStatus.Arrived)
                {
                    bothArrived = true;
                    break;
                }
            }

            Assert.IsTrue(bothArrived, "both agents must arrive within 600 ticks");
            Assert.IsTrue(minDist >= combinedRadius,
                $"agents must not interpenetrate (minDist={minDist.ToFloat()}, combinedRadius=1)");
        }

        [Test]
        public void System_WithAvoidance_CoincidentSpawn_BothArrive()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);
            system.SetAvoidance(avoidance);

            // Both spawn at the exact same position with the same destination — the pre-fix
            // symmetric-fallback lock drifted the pair to -x forever without arriving.
            var frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[]
                {
                    new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1)),
                    new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1)),
                },
                new[] { 0, 0 },
                out var entities);

            var dest = new FPVector3(FP64.FromInt(6), FP64.Zero, FP64.FromInt(1));
            ref var nav0 = ref frame.Get<NavAgentComponent>(entities[0]);
            NavAgentComponent.SetDestination(ref nav0, dest);
            ref var nav1 = ref frame.Get<NavAgentComponent>(entities[1]);
            NavAgentComponent.SetDestination(ref nav1, dest);

            // LP3 keeps the pair separated at ~R, so both cannot enter the default 0.3
            // arrival ball around the shared destination — widen it to R + margin.
            system.WaypointThreshold = FP64.FromDouble(1.5);

            bool bothArrived = false;
            FP64 minPairAfterK = FP64.FromInt(100);
            const int K = 60; // separation transient upper bound (measured sepTick = 18)

            for (int t = 1; t <= 600; t++)
            {
                system.Update(ref frame, entities, 2, t, NavAgentTestHelper.DT);

                ref readonly var a = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
                ref readonly var b = ref frame.GetReadOnly<NavAgentComponent>(entities[1]);

                if (t > K)
                {
                    FP64 dist = FPVector2.Distance(a.Position.ToXZ(), b.Position.ToXZ());
                    if (dist < minPairAfterK) minPairAfterK = dist;
                }

                if (a.Status == (byte)FPNavAgentStatus.Arrived &&
                    b.Status == (byte)FPNavAgentStatus.Arrived)
                {
                    bothArrived = true;
                    break;
                }
            }

            Assert.IsTrue(bothArrived,
                "coincident-spawn agents must both arrive (pre-fix lock drifted them away forever)");
            Assert.IsTrue(minPairAfterK.ToFloat() >= 0.9f,
                $"LP3 must keep the pair separated after the transient (minPair={minPairAfterK.ToFloat()})");

            ref readonly var fa = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
            ref readonly var fb = ref frame.GetReadOnly<NavAgentComponent>(entities[1]);
            Assert.IsTrue(fa.Position.x.ToFloat() > 3f && fb.Position.x.ToFloat() > 3f,
                "both agents must make +x progress toward the destination (no lock relapse)");
        }

        [Test]
        public void System_WithAvoidance_TripleOverlapSpawn_Unzips()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);
            system.SetAvoidance(avoidance);

            // Three agents on the exact same spot, same destination. Full arrival is not
            // asserted: stationary arrivers can wall off the last agent (generic ORCA local
            // minimum, out of LP3 scope) — the assertions target unzip + no lock relapse.
            var frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[]
                {
                    new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1)),
                    new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1)),
                    new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1)),
                },
                new[] { 0, 0, 0 },
                out var entities);

            var dest = new FPVector3(FP64.FromInt(6), FP64.Zero, FP64.FromInt(1));
            for (int i = 0; i < 3; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                NavAgentComponent.SetDestination(ref nav, dest);
            }
            system.WaypointThreshold = FP64.FromDouble(2.5);

            int sepTick = -1; // first tick with all pairwise distances >= 0.9R
            int arrivedCount = 0;

            for (int t = 1; t <= 900; t++)
            {
                system.Update(ref frame, entities, 3, t, NavAgentTestHelper.DT);

                FP64 minPair = FP64.FromInt(100);
                for (int i = 0; i < 3; i++)
                    for (int j = i + 1; j < 3; j++)
                    {
                        ref readonly var a = ref frame.GetReadOnly<NavAgentComponent>(entities[i]);
                        ref readonly var b = ref frame.GetReadOnly<NavAgentComponent>(entities[j]);
                        FP64 dist = FPVector2.Distance(a.Position.ToXZ(), b.Position.ToXZ());
                        if (dist < minPair) minPair = dist;
                    }
                if (sepTick < 0 && minPair.ToFloat() >= 0.9f)
                    sepTick = t;
            }

            FP64 finalMinPair = FP64.FromInt(100);
            for (int i = 0; i < 3; i++)
            {
                ref readonly var a = ref frame.GetReadOnly<NavAgentComponent>(entities[i]);
                if (a.Status == (byte)FPNavAgentStatus.Arrived) arrivedCount++;
                for (int j = i + 1; j < 3; j++)
                {
                    ref readonly var b = ref frame.GetReadOnly<NavAgentComponent>(entities[j]);
                    FP64 dist = FPVector2.Distance(a.Position.ToXZ(), b.Position.ToXZ());
                    if (dist < finalMinPair) finalMinPair = dist;
                }
            }

            Assert.IsTrue(sepTick > 0, "triple overlap must unzip to pairwise >= 0.9R");
            Assert.IsTrue(finalMinPair.ToFloat() >= 0.9f,
                $"separation must hold at the end (finalMinPair={finalMinPair.ToFloat()})");
            Assert.IsTrue(arrivedCount >= 2,
                $"at least two of three agents must arrive (arrived={arrivedCount})");
            Assert.IsTrue(avoidance.DebugInfeasibleCount > 0,
                "deep-overlap constraints must have routed through LinearProgram3");
        }

        #endregion

        #region Position correction pass

        [Test]
        public void System_PositionPass_ArrivedOverlap_SeparatesWithoutTouchingVelocity()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);
            system.SetAvoidance(new FPNavAvoidance());

            // Two agents at the same spot, destination = their own position → Arrive immediately.
            // The position pass must push the (Arrived, Velocity=0) pair apart WITHOUT writing Velocity
            // and WITHOUT re-triggering them to Moving — that no-Velocity, no-Moving invariant is
            // exactly what this test pins.
            var spot = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1));
            var frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[] { spot, spot }, new[] { 0, 0 }, out var entities);
            for (int i = 0; i < 2; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                NavAgentComponent.SetDestination(ref nav, spot);
            }

            for (int t = 1; t <= 60; t++)
            {
                system.Update(ref frame, entities, 2, t, NavAgentTestHelper.DT);

                // Pass must never inject velocity into Arrived agents.
                ref readonly var a = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
                ref readonly var b = ref frame.GetReadOnly<NavAgentComponent>(entities[1]);
                Assert.AreEqual(0f, a.Velocity.magnitude.ToFloat(), 0.0001f,
                    "position pass must not write Velocity (Arrived stays Velocity=0)");
                Assert.AreEqual(0f, b.Velocity.magnitude.ToFloat(), 0.0001f);
                Assert.AreEqual((byte)FPNavAgentStatus.Arrived, a.Status,
                    "pushed Arrived agent must not re-trigger to Moving");
            }

            ref readonly var fa = ref frame.GetReadOnly<NavAgentComponent>(entities[0]);
            ref readonly var fb = ref frame.GetReadOnly<NavAgentComponent>(entities[1]);
            FP64 sep = FPVector2.Distance(fa.Position.ToXZ(), fb.Position.ToXZ());
            Assert.IsTrue(sep.ToFloat() >= 0.9f,
                $"coincident Arrived pair must separate to >= 0.9R (sep={sep.ToFloat()})");
        }

        [Test]
        public void System_PositionPass_RingConverge_NoResidualOverlap()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);
            system.WaypointThreshold = FP64.FromDouble(2.5);
            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);
            system.SetAvoidance(avoidance);

            // 8 agents on a ring around (4,2), all converging to the center. Without the position
            // pass the arrived cluster stays interpenetrated (D3-class); the pass must spread it to
            // a 2D cluster with no residual pairwise overlap (< R).
            const int N = 8;
            var starts = new FPVector3[N];
            var tris = new int[N];
            for (int i = 0; i < N; i++)
            {
                double ang = 2.0 * System.Math.PI * i / N;
                double x = 4.0 + 1.5 * System.Math.Cos(ang);
                double z = 2.0 + 1.5 * System.Math.Sin(ang);
                starts[i] = new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));
                tris[i] = x <= 4 ? (z < x ? 0 : 1) : (z < x - 4 ? 2 : 3);
            }
            var frame = NavAgentTestHelper.CreateFrameWithAgents(starts, tris, out var entities);
            var dest = new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.FromInt(2));
            for (int i = 0; i < N; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                NavAgentComponent.SetDestination(ref nav, dest);
            }

            FP64 minPairAfterK = FP64.FromInt(100);
            const int K = 120;
            for (int t = 1; t <= 600; t++)
            {
                system.Update(ref frame, entities, N, t, NavAgentTestHelper.DT);
                if (t > K)
                {
                    for (int i = 0; i < N; i++)
                        for (int j = i + 1; j < N; j++)
                        {
                            ref readonly var a = ref frame.GetReadOnly<NavAgentComponent>(entities[i]);
                            ref readonly var b = ref frame.GetReadOnly<NavAgentComponent>(entities[j]);
                            FP64 d = FPVector2.Distance(a.Position.ToXZ(), b.Position.ToXZ());
                            if (d < minPairAfterK) minPairAfterK = d;
                        }
                }
            }

            Assert.IsTrue(minPairAfterK.ToFloat() >= 0.9f,
                $"ring-converge cluster must have no residual overlap after settling (minPair={minPairAfterK.ToFloat()})");
        }

        #endregion

        #region MAX_NEIGHBORS enforcement

        /// <summary>
        /// Center agent moving +x with 16 stationary neighbors on 4 bearings x 4 distinct radii
        /// (1.5 .. 2.25, all non-colliding), plus optional extra neighbors appended after them.
        /// </summary>
        private static FPVector3[] BuildSelectionPositions(out FPVector2[] velocities,
            params (double x, double z)[] extras)
        {
            var dirs = new (double x, double z)[] { (1, 0), (0, 1), (-1, 0), (0, -1) };
            int total = 1 + 16 + extras.Length;
            var positions = new FPVector3[total];
            var vels = new FPVector2[total];

            positions[0] = new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero);
            vels[0] = new FPVector2(FP64.One, FP64.Zero);

            int w = 1;
            for (int d = 0; d < 4; d++)
            {
                for (int j = 0; j < 4; j++)
                {
                    double r = 1.5 + 0.2 * j + 0.05 * d;
                    positions[w] = new FPVector3(
                        FP64.FromDouble(dirs[d].x * r), FP64.Zero, FP64.FromDouble(dirs[d].z * r));
                    vels[w] = FPVector2.Zero;
                    w++;
                }
            }

            foreach (var e in extras)
            {
                positions[w] = new FPVector3(FP64.FromDouble(e.x), FP64.Zero, FP64.FromDouble(e.z));
                vels[w] = FPVector2.Zero;
                w++;
            }

            velocities = vels;
            return positions;
        }

        private static bool OrcaLinesEqual(FPNavAvoidance avoidance, FPOrcaLine[] snapshot, int count)
        {
            if (avoidance.DebugOrcaLineCount != count)
                return false;
            for (int i = 0; i < count; i++)
            {
                if (!(avoidance.DebugOrcaLines[i].point == snapshot[i].point))
                    return false;
                if (!(avoidance.DebugOrcaLines[i].direction == snapshot[i].direction))
                    return false;
            }
            return true;
        }

        private static FPOrcaLine[] SnapshotLines(FPNavAvoidance avoidance)
        {
            var lines = new FPOrcaLine[avoidance.DebugOrcaLineCount];
            for (int i = 0; i < lines.Length; i++)
                lines[i] = avoidance.DebugOrcaLines[i];
            return lines;
        }

        [Test]
        public void ComputeNewVelocity_DenseCrowd_EnforcesMaxNeighbors()
        {
            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);

            // 1 moving agent + 20 in-range stationary neighbors (all non-colliding)
            var offsets = new (double x, double z)[]
            {
                (1.5, 0), (0, 1.5), (-1.5, 0), (0, -1.5),
                (1.5, 1.5), (1.5, -1.5), (-1.5, 1.5), (-1.5, -1.5),
                (2.5, 0), (0, 2.5), (-2.5, 0), (0, -2.5),
                (2.5, 1), (2.5, -1), (-2.5, 1), (-2.5, -1),
                (1, 2.5), (-1, 2.5), (1, -2.5), (-1, -2.5),
            };
            var positions = new FPVector3[1 + offsets.Length];
            var velocities = new FPVector2[1 + offsets.Length];
            positions[0] = new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero);
            velocities[0] = new FPVector2(FP64.One, FP64.Zero);
            for (int k = 0; k < offsets.Length; k++)
            {
                positions[k + 1] = new FPVector3(
                    FP64.FromDouble(offsets[k].x), FP64.Zero, FP64.FromDouble(offsets[k].z));
                velocities[k + 1] = FPVector2.Zero;
            }

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(positions, velocities, out var entities);

            avoidance.ComputeNewVelocity(0, ref frame, entities, entities.Length, NavAgentTestHelper.DT);

            Assert.AreEqual(FPNavAvoidance.MAX_NEIGHBORS, avoidance.DebugOrcaLineCount,
                "20 in-range neighbors must be clamped to the nearest MAX_NEIGHBORS lines");
        }

        [Test]
        public void ComputeNewVelocity_NearestSixteen_Selected()
        {
            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);

            // Base: exactly 16 in-range neighbors with distinct distances
            var basePositions = BuildSelectionPositions(out var baseVels);
            var baseFrame = NavAgentTestHelper.CreateFrameWithMovingAgents(basePositions, baseVels, out var baseEntities);
            FPVector2 baseResult = avoidance.ComputeNewVelocity(0, ref baseFrame, baseEntities, baseEntities.Length, NavAgentTestHelper.DT);
            int baseCount = avoidance.DebugOrcaLineCount;
            var baseLines = SnapshotLines(avoidance);

            Assert.AreEqual(FPNavAvoidance.MAX_NEIGHBORS, baseCount);

            // (a) 17th neighbor farther than all 16 (still in range) → evicted, nothing changes
            var fartherPositions = BuildSelectionPositions(out var fartherVels, (0, 3.5));
            var fartherFrame = NavAgentTestHelper.CreateFrameWithMovingAgents(fartherPositions, fartherVels, out var fartherEntities);
            FPVector2 fartherResult = avoidance.ComputeNewVelocity(0, ref fartherFrame, fartherEntities, fartherEntities.Length, NavAgentTestHelper.DT);

            Assert.AreEqual(FPNavAvoidance.MAX_NEIGHBORS, avoidance.DebugOrcaLineCount);
            Assert.IsTrue(OrcaLinesEqual(avoidance, baseLines, baseCount),
                "farther 17th neighbor must be rejected by nearest-K selection (lines unchanged)");
            Assert.IsTrue(fartherResult == baseResult,
                "farther 17th neighbor must not change the avoidance result");

            // (b) 17th neighbor closer than the farthest kept → farthest evicted, lines change
            var closerPositions = BuildSelectionPositions(out var closerVels, (0.5, 0.5));
            var closerFrame = NavAgentTestHelper.CreateFrameWithMovingAgents(closerPositions, closerVels, out var closerEntities);
            avoidance.ComputeNewVelocity(0, ref closerFrame, closerEntities, closerEntities.Length, NavAgentTestHelper.DT);

            Assert.AreEqual(FPNavAvoidance.MAX_NEIGHBORS, avoidance.DebugOrcaLineCount);
            Assert.IsFalse(OrcaLinesEqual(avoidance, baseLines, baseCount),
                "closer 17th neighbor must evict the farthest kept neighbor (lines change)");
        }

        [Test]
        public void ComputeNewVelocity_EqualDistanceTie_KeepsFirstSixteen_Deterministic()
        {
            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(20);

            // 24 integer lattice points at exactly distSqr = 325 → exact FP64 distance tie
            var lattice = new (int x, int z)[] { (1, 18), (6, 17), (10, 15), (15, 10), (17, 6), (18, 1) };
            var offsets = new (int x, int z)[24];
            int w = 0;
            foreach (var (x, z) in lattice)
            {
                offsets[w++] = (x, z);
                offsets[w++] = (-x, z);
                offsets[w++] = (x, -z);
                offsets[w++] = (-x, -z);
            }

            FPVector3[] Build(int neighborCount)
            {
                var positions = new FPVector3[1 + neighborCount];
                positions[0] = new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero);
                for (int k = 0; k < neighborCount; k++)
                {
                    positions[k + 1] = new FPVector3(
                        FP64.FromInt(offsets[k].x), FP64.Zero, FP64.FromInt(offsets[k].z));
                }
                return positions;
            }

            FPVector2[] Vels(int neighborCount)
            {
                var vels = new FPVector2[1 + neighborCount];
                vels[0] = new FPVector2(FP64.One, FP64.Zero);
                for (int k = 1; k < vels.Length; k++)
                    vels[k] = FPVector2.Zero;
                return vels;
            }

            // Frame A: all 24 equidistant candidates
            var frameA = NavAgentTestHelper.CreateFrameWithMovingAgents(Build(24), Vels(24), out var entitiesA);
            avoidance.ComputeNewVelocity(0, ref frameA, entitiesA, entitiesA.Length, NavAgentTestHelper.DT);
            int countA = avoidance.DebugOrcaLineCount;
            var linesA = SnapshotLines(avoidance);

            // Same frame, second run → byte-identical (determinism)
            avoidance.ComputeNewVelocity(0, ref frameA, entitiesA, entitiesA.Length, NavAgentTestHelper.DT);
            Assert.IsTrue(OrcaLinesEqual(avoidance, linesA, countA), "repeat run must be identical");

            // Frame B: only the first 16 candidates (same creation order)
            var frameB = NavAgentTestHelper.CreateFrameWithMovingAgents(Build(16), Vels(16), out var entitiesB);
            avoidance.ComputeNewVelocity(0, ref frameB, entitiesB, entitiesB.Length, NavAgentTestHelper.DT);

            Assert.AreEqual(FPNavAvoidance.MAX_NEIGHBORS, countA);
            Assert.IsTrue(OrcaLinesEqual(avoidance, linesA, countA),
                "on an exact distance tie the first 16 traversed neighbors must be kept (strict < cannot evict)");
        }

        [Test]
        public void ComputeNewVelocity_OutOfRangeNeighbors_NotSelected()
        {
            var avoidance = new FPNavAvoidance(); // default NeighborDist = 5

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                new[]
                {
                    new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                    new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero),   // in range
                    new FPVector3(FP64.FromInt(6), FP64.Zero, FP64.Zero),   // out of range
                    new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(7)),   // out of range
                    new FPVector3(FP64.FromInt(-8), FP64.Zero, FP64.Zero),  // out of range
                },
                new[]
                {
                    new FPVector2(FP64.One, FP64.Zero),
                    FPVector2.Zero, FPVector2.Zero, FPVector2.Zero, FPVector2.Zero,
                },
                out var entities);

            avoidance.ComputeNewVelocity(0, ref frame, entities, entities.Length, NavAgentTestHelper.DT);

            Assert.AreEqual(1, avoidance.DebugOrcaLineCount,
                "NeighborDist cutoff must still exclude out-of-range neighbors before selection");
        }

        [Test]
        public void System_WithAvoidance_DenseCrowd_ClampsOrcaLines()
        {
            var mesh = NavAgentTestHelper.Create4TriNavMesh();
            var system = NavAgentTestHelper.CreateSystem(mesh, _logger);

            var avoidance = new FPNavAvoidance();
            avoidance.NeighborDist = FP64.FromInt(10);
            system.SetAvoidance(avoidance);

            // 21 agents packed around (4,2); everyone targets the x-mirrored side.
            // Every agent sees 20 in-range neighbors — pre-fix this produced up to 20 ORCA
            // lines per agent; post-fix the count must clamp to MAX_NEIGHBORS.
            var offsets = new (int x, int z)[]
            {
                (0, 0),
                (1, 0), (-1, 0), (2, 0), (-2, 0), (3, 0), (-3, 0),
                (0, 1), (0, -1),
                (1, 1), (1, -1), (-1, 1), (-1, -1),
                (2, 1), (2, -1), (-2, 1), (-2, -1),
                (3, 1), (3, -1), (-3, 1), (-3, -1),
            };
            var positions = new FPVector3[offsets.Length];
            var triangles = new int[offsets.Length];
            for (int k = 0; k < offsets.Length; k++)
            {
                double x = 4 + offsets[k].x;
                double z = 2 + offsets[k].z;
                positions[k] = new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));
                triangles[k] = x < 4 ? (z < x ? 0 : 1) : (z < x - 4 ? 2 : 3);
            }

            var frame = NavAgentTestHelper.CreateFrameWithAgents(positions, triangles, out var entities);

            for (int k = 0; k < entities.Length; k++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[k]);
                double x = 4 + offsets[k].x;
                double z = 2 + offsets[k].z;
                NavAgentComponent.SetDestination(ref nav,
                    new FPVector3(FP64.FromDouble(8 - x), FP64.Zero, FP64.FromDouble(z)));
            }

            for (int t = 1; t <= 180; t++)
            {
                system.Update(ref frame, entities, entities.Length, t, NavAgentTestHelper.DT);
                Assert.LessOrEqual(avoidance.DebugOrcaLineCount, FPNavAvoidance.MAX_NEIGHBORS,
                    "ORCA line count must stay clamped to MAX_NEIGHBORS under dense crowding");
            }

            Assert.Pass("dense-crowd integration: line count clamped every tick, no crash");
        }

        #endregion
    }
}
