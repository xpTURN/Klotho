using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// An agent standing on ground its own mask forbids has to be able to walk OUT, while nothing
    /// may walk IN. `Navigation.md` promised the first half before the code did it.
    ///
    /// <para><b>Why two buildings placed flush.</b> A single small footprint is four triangles and
    /// every one of them touches ordinary ground, so an agent inside escapes the moment it has a
    /// path — which measures the plan side and nothing else. Two footprints snapped flush produce
    /// ten building triangles of which <b>four have no accepted neighbour at all</b>, and those are
    /// the only ones that measure the traversal rule. Every gate below that needs a trapped
    /// triangle asserts that it found one, because picking the wrong one passes silently.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMaskedStartEscapeTests
    {
        private const int Cells = 8;                 // open field spans 0..16
        private const int Permissive = FPNavMeshAreas.ALL_AREAS;
        private const int Restrictive = FPNavAgentSystem.DEFAULT_AREA_MASK;

        private static FPVector3 P(double x, double z) =>
            new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));

        private static bool IsBuilding(FPNavMesh mesh, int tri) =>
            tri >= 0 && mesh.Triangles[tri].areaMask == FPNavMeshAreas.BUILDING_MASK;

        /// <summary>One retained box at the field's middle — four triangles, all of them escapable.</summary>
        private static FPNavMesh OneBox()
        {
            var basemesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            return NavAgentTestHelper.RebakeWithBuildings(
                basemesh, NavAgentTestHelper.Building(8.0, 8.0, retain: true));
        }

        /// <summary>Two retained boxes snapped flush — the layout that produces trapped triangles.</summary>
        private static FPNavMesh TwoFlushBoxes()
        {
            var basemesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            double half = NavAgentTestHelper.ExpandedBuildingHalf(basemesh);
            return NavAgentTestHelper.RebakeWithBuildings(
                basemesh,
                NavAgentTestHelper.Building(8.0 - half, 8.0, retain: true),
                NavAgentTestHelper.Building(8.0 + half, 8.0, retain: true));
        }

        /// <summary>
        /// True when every neighbour of <paramref name="tri"/> is also building ground — i.e. an
        /// agent standing there cannot reach accepted ground in one step whatever the plan says.
        /// </summary>
        private static bool IsTrapped(FPNavMesh mesh, int tri)
        {
            if (!IsBuilding(mesh, tri)) return false;
            ref readonly var t = ref mesh.Triangles[tri];
            return NeighbourIsBuildingOrWall(mesh, t.neighbor0)
                && NeighbourIsBuildingOrWall(mesh, t.neighbor1)
                && NeighbourIsBuildingOrWall(mesh, t.neighbor2);
        }

        private static bool NeighbourIsBuildingOrWall(FPNavMesh mesh, int n) =>
            n < 0 || mesh.Triangles[n].areaMask == FPNavMeshAreas.BUILDING_MASK;

        /// <summary>
        /// The centroid of a trapped triangle, and the assertion that one exists. Returning the
        /// centroid rather than a hardcoded coordinate keeps the gate honest if the fixture's
        /// expansion arithmetic ever moves.
        /// </summary>
        private static FPVector3 TrappedInteriorPoint(FPNavMesh mesh, out int tri)
        {
            var vs = mesh.Vertices;
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                if (!IsTrapped(mesh, i)) continue;
                ref readonly var t = ref mesh.Triangles[i];
                tri = i;
                return new FPVector3(
                    (vs[t.v0].x + vs[t.v1].x + vs[t.v2].x) / FP64.FromInt(3),
                    FP64.Zero,
                    (vs[t.v0].z + vs[t.v1].z + vs[t.v2].z) / FP64.FromInt(3));
            }
            tri = -1;
            Assert.Fail("the fixture produced no trapped triangle — every gate that depends on one "
                + "would pass without measuring the traversal rule. Check the flush spacing.");
            return default;
        }

        /// <summary>
        /// The centroid of an ORDINARY triangle that shares an edge with building ground — the only
        /// place an agent's corridor steps straight into the footprint, which is what the
        /// stop-on-contact predicate reads.
        /// </summary>
        private static FPVector3 BaseTriangleTouchingBuilding(FPNavMesh mesh, out int tri)
        {
            var vs = mesh.Vertices;
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                if (IsBuilding(mesh, i)) continue;
                ref readonly var t = ref mesh.Triangles[i];
                if (!IsBuilding(mesh, t.neighbor0)
                    && !IsBuilding(mesh, t.neighbor1)
                    && !IsBuilding(mesh, t.neighbor2)) continue;
                tri = i;
                return new FPVector3(
                    (vs[t.v0].x + vs[t.v1].x + vs[t.v2].x) / FP64.FromInt(3),
                    FP64.Zero,
                    (vs[t.v0].z + vs[t.v1].z + vs[t.v2].z) / FP64.FromInt(3));
            }
            tri = -1;
            Assert.Fail("no ordinary triangle borders the footprint — the fixture cannot place an "
                + "agent one step outside it");
            return default;
        }

        private static Frame AgentAt(FPNavMesh mesh, FPVector3 at, int startTri,
            int planMask, int walkMask, out EntityRef e, out EntityRef[] es)
        {
            var frame = NavAgentTestHelper.CreateFrameWithAgent(at, startTri, out e, out es);
            ref var nav = ref frame.Get<NavAgentComponent>(e);
            NavAgentComponent.SetAreaMask(ref nav, planMask, walkMask);
            return frame;
        }

        // ── E-1: the escape ──────────────────────────────────────────────────

        [Test]
        public void E1_AnAgentInATrappedTriangle_WalksOutUnderItsOwnMask()
        {
            var mesh = TwoFlushBoxes();
            FPVector3 start = TrappedInteriorPoint(mesh, out int trappedTri);

            // Teeth: the whole point is that this triangle has no accepted neighbour. Without the
            // assertion the gate could land on one of the six escapable building triangles and
            // pass on the start exemption alone.
            Assert.IsTrue(IsTrapped(mesh, trappedTri),
                "the chosen triangle is not trapped, so this measures the plan side only");

            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var frame = AgentAt(mesh, start, trappedTri, Restrictive, Restrictive,
                out EntityRef e, out EntityRef[] es);
            NavAgentComponent.SetDestination(ref frame.Get<NavAgentComponent>(e), P(2.0, 2.0));

            for (int t = 1; t <= 600; t++)
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);

            ref var cur = ref frame.Get<NavAgentComponent>(e);
            Assert.AreEqual(FPNavAgentStatus.Arrived, (FPNavAgentStatus)cur.Status,
                "an agent standing on ground its mask forbids must be able to leave it — the walk "
                + "never gates the triangle it is standing on, and Navigation.md promises the rest");
            Assert.IsFalse(IsBuilding(mesh, cur.CurrentTriangleIndex),
                "and it must actually be out, not merely re-planned");
        }

        // ── E-2: nothing walks in ────────────────────────────────────────────

        [Test]
        public void E2_ControlGroup_AnAgentOutside_StillCannotEnter()
        {
            // The plan mask MUST be permissive here. With the default plan mask the corridor routes
            // around the footprint and the agent never approaches it, so "0 ticks inside" would be
            // true without measuring the walk at all — the mistake the per-agent mask gates in
            // FPNavAgentAreaMaskTests were rewritten to avoid, for this same reason.
            var mesh = OneBox();
            var query = new FPNavMeshQuery(mesh, null);
            var system = NavAgentTestHelper.CreateSystem(mesh, null);

            FPVector3 start = P(2.0, 8.0);
            var frame = AgentAt(mesh, start, query.FindTriangle(start.ToXZ()),
                Permissive, Restrictive, out EntityRef e, out EntityRef[] es);
            NavAgentComponent.SetDestination(ref frame.Get<NavAgentComponent>(e), P(8.0, 8.0));

            int ticksInside = 0;
            bool corridorEnteredBuilding = false;
            for (int t = 1; t <= 400; t++)
            {
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);
                ref var cur = ref frame.Get<NavAgentComponent>(e);
                if (IsBuilding(mesh, cur.CurrentTriangleIndex)) ticksInside++;
                unsafe
                {
                    fixed (int* p = cur.Corridor)
                        for (int i = 0; i < cur.CorridorLength; i++)
                            if (IsBuilding(mesh, p[i])) corridorEnteredBuilding = true;
                }
            }

            Assert.IsTrue(corridorEnteredBuilding,
                "fixture: the permissive plan mask must actually route into the footprint, or this "
                + "gate proves nothing about the walk");
            Assert.AreEqual(0, ticksInside,
                "the escape rule must not become a way IN — entry from accepted ground stays a wall");
        }

        // ── E-3: the plan stands, and the destination still does not ─────────

        [Test]
        public void E3_TheStartIsExempt_ButTheDestinationIsNot()
        {
            var mesh = OneBox();
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            FPVector3 inside = P(8.0, 8.0), outside = P(2.0, 8.0);
            Assert.IsTrue(IsBuilding(mesh, query.FindTriangle(inside.ToXZ())), "fixture: not inside");

            Assert.IsTrue(pathfinder.FindPath(inside, outside, Restrictive,
                    out int[] corridor, out int corridorLength),
                "out of a footprint must plan under the agent's own mask — that is the start exemption");
            Assert.IsTrue(IsBuilding(mesh, corridor[0]),
                "the corridor must start on the retained triangle the agent stands in");
            Assert.IsFalse(IsBuilding(mesh, corridor[corridorLength - 1]));

            // And the other direction is unchanged: a destination inside is still refused.
            Assert.IsFalse(pathfinder.FindPath(outside, inside, Restrictive, out _, out _),
                "a destination inside a retained footprint must STILL be refused — only the start "
                + "is exempt, and this pair is what proves it");
            Assert.AreEqual(1, pathfinder.DebugAreaMaskRejectedCount,
                "and that refusal is what the endpoint counter now counts, alone");
        }

        // ── E-4 / E-4b / E-4c: Blocked belongs to agents that tried ──────────

        /// <summary>
        /// Speed 0 is the deterministic stand-in for "stopped for some other reason" — a crowd, a
        /// deceleration, an ORCA hold. The agent keeps a valid corridor whose next triangle is
        /// building ground; what it does not do is attempt to move.
        /// </summary>
        private static FPNavAgentStatus RunWithSpeed(
            FPNavMesh mesh, FPVector3 start, int startTri, FPVector3 dest,
            int planMask, int walkMask, FP64 speed, int ticks)
        {
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var frame = AgentAt(mesh, start, startTri, planMask, walkMask,
                out EntityRef e, out EntityRef[] es);
            ref var nav = ref frame.Get<NavAgentComponent>(e);
            nav.Speed = speed;
            NavAgentComponent.SetDestination(ref nav, dest);

            for (int t = 1; t <= ticks; t++)
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);
            return (FPNavAgentStatus)frame.Get<NavAgentComponent>(e).Status;
        }

        [Test]
        public void E4_AnAgentThatNeverTriedToMove_IsNotReportedBlocked()
        {
            // This one fails with none of this plan's code applied: it is a defect in the shipped
            // stop-on-contact predicate, which asks "did the position change" rather than "was the
            // step refused". A stalled agent one triangle short of a footprint reads as contact.
            //
            // The start has to be the triangle that BORDERS building ground, not merely a point
            // near the footprint: the predicate looks at the corridor's NEXT entry, so from a
            // triangle one further out the next entry is ordinary ground and nothing misfires. A
            // hand-picked coordinate lands there by luck, so the fixture searches for the adjacency
            // and then asserts the corridor really does step into the footprint.
            var mesh = OneBox();
            FPVector3 start = BaseTriangleTouchingBuilding(mesh, out int startTri);

            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var frame = AgentAt(mesh, start, startTri, Permissive, Restrictive,
                out EntityRef e, out EntityRef[] es);
            ref var nav = ref frame.Get<NavAgentComponent>(e);
            nav.Speed = FP64.Zero;
            NavAgentComponent.SetDestination(ref nav, P(8.0, 8.0));

            for (int t = 1; t <= 20; t++)
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);

            ref var cur = ref frame.Get<NavAgentComponent>(e);
            unsafe
            {
                Assert.Greater(cur.CorridorLength, 1, "fixture: no corridor to step along");
                fixed (int* p = cur.Corridor)
                    Assert.IsTrue(IsBuilding(mesh, p[1]),
                        "fixture: the corridor's next entry is not building ground, so the shipped "
                        + "predicate has nothing to misfire on and this gate measures nothing");
            }

            Assert.AreNotEqual(FPNavAgentStatus.Blocked, (FPNavAgentStatus)cur.Status,
                "an agent that requested no displacement has touched nothing — Blocked means "
                + "'ran into ground your mask refuses', not 'is not moving'");
        }

        [Test]
        public void E4b_InsideTheFootprint_AStalledAgentIsNotBlockedEither()
        {
            var mesh = TwoFlushBoxes();
            FPVector3 start = TrappedInteriorPoint(mesh, out int trappedTri);

            var status = RunWithSpeed(mesh, start, trappedTri, P(2.0, 2.0),
                Permissive, Restrictive, FP64.Zero, 20);

            Assert.AreNotEqual(FPNavAgentStatus.Blocked, status,
                "standing still inside a footprint is not contact with it either");
        }

        [Test]
        public void E4c_ControlGroup_AnAgentThatDoesPushAgainstTheEdge_IsStillBlocked()
        {
            // Without this, the two gates above pass if Blocked were deleted outright. This is the
            // half of the per-agent mask contract that has to survive: an agent that walks up to
            // a footprint it may not enter stops there and says so.
            var mesh = OneBox();
            var query = new FPNavMeshQuery(mesh, null);
            FPVector3 start = P(2.0, 8.0);

            var status = RunWithSpeed(mesh, start, query.FindTriangle(start.ToXZ()), P(8.0, 8.0),
                Permissive, Restrictive, FP64.FromInt(5), 300);

            Assert.AreEqual(FPNavAgentStatus.Blocked, status,
                "an agent that actually reached the edge must still report Blocked");
        }

        // ── E-7: escaping is confined to your own region ─────────────────────

        [Test]
        public void E7_TheEscapeIsConfinedToTheRegionYouStandIn()
        {
            // The A* argument in the plan: admissibility depends on the parent, but a refused node
            // is only ever reached from a refused parent, so the reachable set is "the refused
            // component you are in, plus accepted ground". Two footprints separated by ordinary
            // ground make that observable.
            var basemesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var mesh = NavAgentTestHelper.RebakeWithBuildings(
                basemesh,
                NavAgentTestHelper.Building(5.0, 8.0, retain: true),
                NavAgentTestHelper.Building(11.0, 8.0, retain: true));
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            FPVector3 insideA = P(5.0, 8.0), insideB = P(11.0, 8.0), outside = P(2.0, 2.0);
            Assert.IsTrue(IsBuilding(mesh, query.FindTriangle(insideA.ToXZ())), "fixture: A");
            Assert.IsTrue(IsBuilding(mesh, query.FindTriangle(insideB.ToXZ())), "fixture: B");

            Assert.IsTrue(pathfinder.FindPath(insideA, outside, Restrictive, out _, out _),
                "out of your own region: allowed");
            Assert.IsFalse(pathfinder.FindPath(insideA, insideB, Restrictive, out _, out _),
                "into ANOTHER region: refused — the endpoint check still applies, and the escape "
                + "rule cannot carry an agent across accepted ground into forbidden ground");
            Assert.IsFalse(pathfinder.FindPath(outside, insideB, Restrictive, out _, out _),
                "and from outside, still refused");
        }

        // ── E-6: determinism ─────────────────────────────────────────────────

        [Test]
        public void E6_TheEscapeIsDeterministic()
        {
            var mesh = TwoFlushBoxes();
            FPVector3 start = TrappedInteriorPoint(mesh, out int trappedTri);
            var query = new FPNavMeshQuery(mesh, null);

            int[] Walk()
            {
                var system = NavAgentTestHelper.CreateSystem(mesh, null);
                var frame = AgentAt(mesh, start, trappedTri, Restrictive, Restrictive,
                    out EntityRef e, out EntityRef[] es);
                NavAgentComponent.SetDestination(ref frame.Get<NavAgentComponent>(e), P(2.0, 2.0));
                var seen = new int[60];
                for (int t = 1; t <= 60; t++)
                {
                    system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);
                    seen[t - 1] = frame.Get<NavAgentComponent>(e).CurrentTriangleIndex;
                }
                return seen;
            }

            CollectionAssert.AreEqual(Walk(), Walk(),
                "the same input must produce the same triangle sequence — the escape rule is a "
                + "simulation input like any other filter");
        }
    }
}
