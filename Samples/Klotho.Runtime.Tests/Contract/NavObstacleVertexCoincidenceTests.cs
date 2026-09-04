using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Runtime.Tests.Contract
{
    /// <summary>
    /// An agent standing EXACTLY on an obstacle ring vertex.
    ///
    /// <para>Reachable by ordinary means, which is why it needs gates: agent positions and obstacle
    /// ring vertices both live on the same snap lattice, so a click, a `ClosestPointOnNavMesh`, or a
    /// runtime rebake that puts a new hole ring where an agent already stands all land on it. It
    /// used to throw — the three collision guards in <c>ComputeObstacleOrcaLine</c> covered
    /// <c>s &lt; 0</c>, <c>s &gt; 1</c> and <c>s ∈ [0, 1)</c>, and <c>s == 1</c> fell through all
    /// three into the leg computation, where the divisor is the distance to the vertex the agent is
    /// standing on.</para>
    ///
    /// <para><b>The asymmetry is the whole bug.</b> Standing on a segment's START gives
    /// <c>s == 0</c>, which the interior guard catches; standing on its END gives <c>s == 1</c>,
    /// which nothing caught. Every shared ring vertex is the end of one segment and the start of the
    /// next, so the same position was safe through one and fatal through the other — the tests below
    /// pin both halves so the fix cannot be "make s == 1 behave like nothing".</para>
    /// </summary>
    [TestFixture]
    public class NavObstacleVertexCoincidenceTests
    {
        private const int MaxEntities = 8;
        private static readonly FP64 DT = FP64.FromDouble(1.0 / 60.0);

        private static FP64 I(int v) => FP64.FromInt(v);

        private static EcsSimulation NewSim()
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 4, deltaTimeMs: 50);
            sim.Initialize();
            return sim;
        }

        private static FPNavAvoidance NewPinnedAvoidance()
        {
            var av = new FPNavAvoidance();
            av.TimeHorizonObst = FP64.FromInt(1);
            return av;
        }

        /// <summary>Axis-aligned CCW square ring, side 4, lower-left at the origin.</summary>
        private static FPVector2[] Square() => new[]
        {
            new FPVector2(I(0), I(0)),
            new FPVector2(I(4), I(0)),
            new FPVector2(I(4), I(4)),
            new FPVector2(I(0), I(4)),
        };

        /// <summary>
        /// One agent standing at <paramref name="posXZ"/>, aiming at <paramref name="desired"/>,
        /// with the square ring loaded. <paramref name="radius"/> is the agent radius; the
        /// obstacle inset stays 0, so it is also the effective obstacle radius.
        /// </summary>
        private static FPVector2 ComputeAt(FPVector2 posXZ, FPVector2 desired, FP64 radius)
        {
            var sim = NewSim();
            var av = NewPinnedAvoidance();
            av.LoadObstacles(Square(), null);

            var e = sim.Frame.CreateEntity();
            var nav = default(NavAgentComponent);
            NavAgentComponent.Init(ref nav, new FPVector3(posXZ.x, FP64.Zero, posXZ.y));
            nav.Radius = radius;
            nav.Velocity = FPVector2.Zero;
            nav.DesiredVelocity = desired;
            sim.Frame.Add(e, nav);

            var entities = new[] { e };
            var frame = sim.Frame;
            return av.ComputeNewVelocity(0, ref frame, entities, 1, DT);
        }

        // ── The crash: exact coincidence, zero effective radius ──────────────

        [Test]
        public void OnARingVertex_WithZeroEffectiveRadius_DoesNotDivideByZero()
        {
            // radius 0 is not a contrivance: LoadNavMeshObstacles auto-sets ObstacleRadiusInset to
            // the mesh's BakeAgentRadius, so under the R_sim = R_bake convention the effective
            // obstacle radius IS zero, and the editor visualizer runs that way by default. With
            // radiusSq == 0 the collision guards only fire on exact coincidence — which is the one
            // case that fell through them.
            Assert.DoesNotThrow(
                () => ComputeAt(new FPVector2(I(4), I(0)), new FPVector2(I(1), I(1)), FP64.Zero),
                "standing exactly on a ring vertex divided by the distance to that vertex");
        }

        [Test]
        public void EveryVertexOfTheRing_IsSafeToStandOn()
        {
            // A shared vertex is the END of one segment and the START of the next, so a fix that
            // only handled one side would still throw on three of these four.
            foreach (FPVector2 v in Square())
            {
                Assert.DoesNotThrow(
                    () => ComputeAt(v, new FPVector2(I(1), I(1)), FP64.Zero),
                    $"vertex ({v.x.ToFloat()}, {v.y.ToFloat()}) threw");
            }
        }

        // ── The second exit from the same hole: negative square root ─────────

        [Test]
        public void NearARingVertex_InsideTheEffectiveRadius_DoesNotTakeASquareRootOfANegative()
        {
            // The same s == 1 fall-through reaches Sqrt(distSq2 - radiusSq) with distSq2 < radiusSq
            // whenever the agent is inside the effective radius of the far vertex rather than
            // exactly on it. FP64.Sqrt throws on a negative argument, so this exits as an
            // ArgumentException instead of a DivideByZeroException — same hole, different door.
            //
            // Sitting on the segment's own line past its end gives s > 1, which IS guarded; the
            // position here is the vertex itself with a radius wide enough that "inside" is
            // non-degenerate.
            Assert.DoesNotThrow(
                () => ComputeAt(new FPVector2(I(4), I(0)), new FPVector2(I(1), I(1)), FP64.One),
                "an agent inside the far vertex's radius took a negative square root");
        }

        // ── The control group: s == 0 was always safe, and must stay so ──────

        [Test]
        public void TheStartOfASegment_WasAlreadySafe_AndAnswersTheSameWay()
        {
            // s == 0 is caught by the interior guard (`s >= 0`), so this passed before the fix. It
            // is here because it is the ORACLE: the two ends of a segment describe the same
            // geometric situation, so whatever s == 1 returns has to match this. If a fix made
            // s == 1 return "no constraint" while s == 0 returns a wall line, this pair diverges.
            var atStart = ComputeAt(new FPVector2(I(0), I(0)), new FPVector2(I(1), I(1)), FP64.Zero);
            var atEnd = ComputeAt(new FPVector2(I(4), I(0)), new FPVector2(I(1), I(1)), FP64.Zero);

            // Same corner geometry, mirrored across the ring's diagonal: both are convex corners of
            // the same square with the same desired velocity, so both must be constrained, and
            // neither may come back as the raw desired velocity.
            Assert.AreNotEqual(new FPVector2(I(1), I(1)), atStart,
                "the agent standing on a corner was left unconstrained");
            Assert.AreNotEqual(new FPVector2(I(1), I(1)), atEnd,
                "the agent standing on the NEXT corner was left unconstrained — s == 1 must be "
                + "treated as a collision, not as an absence of one");
        }

        [Test]
        public void StandingOnAVertex_IsDeterministic()
        {
            // The fix changes which branch a specific configuration takes, so it is a simulation
            // input like any other. Two runs of the same input must agree exactly.
            var a = ComputeAt(new FPVector2(I(4), I(0)), new FPVector2(I(1), I(1)), FP64.Zero);
            var b = ComputeAt(new FPVector2(I(4), I(0)), new FPVector2(I(1), I(1)), FP64.Zero);
            Assert.AreEqual(a, b);
        }
    }
}
