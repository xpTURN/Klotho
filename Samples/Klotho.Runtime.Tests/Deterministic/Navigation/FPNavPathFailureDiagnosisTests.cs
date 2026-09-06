using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The editor visualizers say WHY an agent sits at <c>PathFailed</c> by re-walking
    /// <c>FindPath</c>'s own guard chain against the current mesh. This fixture pins that chain.
    ///
    /// <para><b>What this can and cannot see.</b> The tools' copy of the chain lives in
    /// Editor-platform and Godot-adapter assemblies no test project references, so what is pinned
    /// here is the PATTERN they must copy — the reason enum below is the test's own reimplementation
    /// of it. Two copies can drift; these gates are what make the drift visible, and they only work
    /// because every step of the chain is a public engine query.</para>
    ///
    /// <para><b>Why not the pathfinder's counters.</b> Three reasons, and the third is decisive.
    /// The editor shares one <c>FPNavMeshPathfinder</c> between the agent simulator and the
    /// window's Start/End path preview, so a delta cannot be attributed even with one agent. The
    /// counters never reset. And the failure the tool most needs to explain — an agent the
    /// post-rebake reseed left off-mesh — never calls <c>FindPath</c> at all, so no counter ever
    /// moves for it; <c>ProcessPathRequest</c> then refuses to re-plan a <c>PathFailed</c> agent,
    /// which makes that silence permanent rather than a one-tick gap. The reseed-lost test below
    /// pins that.</para>
    /// </summary>
    [TestFixture]
    public class FPNavPathFailureDiagnosisTests
    {
        private const int Cells = 8;
        private const double Bx = 8.0, Bz = 8.0;
        private const int AgentMask = FPNavMeshAreas.DEFAULT_AGENT_MASK;

        /// <summary>The tool's verdict, reimplemented. Order mirrors <c>FindPath</c> exactly.</summary>
        private enum Reason
        {
            None,
            AgentOffMesh,
            AgentOnBlockedGround,
            DestinationOffMesh,
            DestinationBlocked,
            DestinationAreaMasked,
            StaleFailure,
            NoRouteOrBudget,
        }

        /// <summary>
        /// The chain. <c>FindPath</c> refuses in this order — endpoint lookup, then
        /// <c>isBlocked</c> (start OR end, one condition), then the END's mask — and the start's
        /// mask is deliberately NOT a refusal, only a report, so it must never appear as a reason.
        /// </summary>
        private static Reason Diagnose(
            FPNavMesh mesh, FPNavMeshQuery query, in NavAgentComponent nav, bool failurePredatesSwap)
        {
            if (nav.Status != (byte)FPNavAgentStatus.PathFailed)
                return Reason.None;

            if (nav.CurrentTriangleIndex < 0)
                return Reason.AgentOffMesh;
            if (mesh.Triangles[nav.CurrentTriangleIndex].isBlocked)
                return Reason.AgentOnBlockedGround;

            int mask = FPNavAgentSystem.ResolvePlanMask(nav);
            int endTri = query.FindTriangleForEndpoint(
                nav.Destination.ToXZ(), nav.Destination.y, mask);
            if (endTri < 0)
                return Reason.DestinationOffMesh;
            if (mesh.Triangles[endTri].isBlocked)
                return Reason.DestinationBlocked;
            if ((mask & mesh.Triangles[endTri].areaMask) == 0)
                return Reason.DestinationAreaMasked;

            return failurePredatesSwap ? Reason.StaleFailure : Reason.NoRouteOrBudget;
        }

        private static FPVector2 At(double x, double z)
            => new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z));

        private static FPVector3 At3(double x, double z)
            => new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));

        private static FPNavMesh Retained() => NavAgentTestHelper.RebakeWithBuildings(
            NavAgentTestHelper.CreateOpenFieldNavMesh(Cells),
            NavAgentTestHelper.Building(Bx, Bz, retain: true));

        /// <summary>An agent parked at <c>PathFailed</c> with the given position and destination.</summary>
        private static NavAgentComponent Failed(FPNavMeshQuery query, FPVector3 pos, FPVector3 dest)
        {
            var nav = default(NavAgentComponent);
            NavAgentComponent.Init(ref nav, pos);
            nav.CurrentTriangleIndex = query.FindTriangle(pos.ToXZ(), pos.y);
            NavAgentComponent.SetDestination(ref nav, dest);
            nav.Status = (byte)FPNavAgentStatus.PathFailed;
            return nav;
        }

        #region The verdict agrees with FindPath, guard order included

        /// <summary>
        /// Every refusal <c>FindPath</c> can attribute must come back under the name the tool would
        /// show. Driven off the pathfinder's own counters rather than a hand-written expectation, so
        /// the two cannot agree by coincidence.
        /// </summary>
        [Test]
        public void TheVerdictMatchesWhatFindPathActuallyRefusedOn()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);

            // (a) destination deep inside the retained footprint → mask refusal
            AssertAgrees(mesh, query, At3(3.0, 3.0), At3(Bx, Bz),
                Reason.DestinationAreaMasked, "mask");

            // (b) destination off the mesh entirely → endpoint lookup returns -1
            AssertAgrees(mesh, query, At3(3.0, 3.0), At3(-5.0, -5.0),
                Reason.DestinationOffMesh, "offmesh");

            // (c) destination on ground a game closed through TrianglesMutable → blocked
            var blockedMesh = Retained();
            var blockedQuery = new FPNavMeshQuery(blockedMesh, null);
            FPVector3 goal = At3(13.0, 13.0);
            int goalTri = blockedQuery.FindTriangle(goal.ToXZ(), goal.y);
            Assert.GreaterOrEqual(goalTri, 0, "fixture: the goal is on-mesh before it is closed");
            CloseEveryTriangleContaining(blockedMesh, goal.ToXZ());
            AssertAgrees(blockedMesh, blockedQuery, At3(3.0, 3.0), goal,
                Reason.DestinationBlocked, "blocked");

            // (d) a destination that is simply reachable — nothing to diagnose
            var nav = Failed(query, At3(3.0, 3.0), At3(13.0, 3.0));
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            Assert.IsTrue(pathfinder.FindPath(nav.Position, nav.Destination, AgentMask, out _, out _),
                "fixture: this one should succeed, so the tool must not name an endpoint cause");
            Assert.AreEqual(Reason.NoRouteOrBudget, Diagnose(mesh, query, nav, false),
                "with every endpoint check passing the verdict falls through to the last bucket");

            _ = half;
        }

        /// <summary>
        /// <c>isBlocked</c> and the area mask can both refuse the same triangle, and
        /// <c>FindPath</c> checks blocked FIRST. Reporting the mask there would send a reader to the
        /// mask knob for a gate someone closed in code.
        /// </summary>
        [Test]
        public void WhenBlockedAndMaskedBothApply_BlockedIsNamedFirst()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            FPVector2 centre = At(Bx, Bz);

            int footprint = query.FindTriangle(centre, FP64.Zero);
            Assert.AreEqual(FPNavMeshAreas.BUILDING_MASK, mesh.Triangles[footprint].areaMask,
                "fixture: the centre is on the retained footprint, so the mask already refuses it");
            CloseEveryTriangleContaining(mesh, centre);   // ...and now isBlocked refuses it too

            var nav = Failed(query, At3(3.0, 3.0), At3(Bx, Bz));
            Assert.AreEqual(Reason.DestinationBlocked, Diagnose(mesh, query, nav, false),
                "blocked must win: that is the order FindPath's guards run in");

            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            Assert.IsFalse(pathfinder.FindPath(nav.Position, nav.Destination, AgentMask, out _, out _));
            Assert.AreEqual(1, pathfinder.DebugBlockedEndpointCount,
                "and the engine attributes it the same way");
            Assert.AreEqual(0, pathfinder.DebugAreaMaskRejectedCount, "not to the mask");
        }

        #endregion

        #region The start's mask is a report, never a reason

        /// <summary>
        /// The start is exempt from the mask check on purpose: a unit with a building dropped on it
        /// must still be given a route out. The engine only COUNTS that
        /// (<c>DebugMaskedStartCount</c>). A tool that named it as a failure cause would be lying —
        /// the path succeeds.
        /// </summary>
        [Test]
        public void AMaskedStartIsNeverTheReason()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            FPVector3 inside = At3(Bx, Bz);                 // standing on the footprint
            FPVector3 outside = At3(3.0, 3.0);
            int insideTri = query.FindTriangle(inside.ToXZ(), inside.y);
            Assert.AreEqual(FPNavMeshAreas.BUILDING_MASK, mesh.Triangles[insideTri].areaMask,
                "fixture: the start is on ground this mask forbids");

            Assert.IsTrue(pathfinder.FindPath(inside, outside, AgentMask, out _, out int len),
                "the start exemption must still produce a route out");
            Assert.Greater(len, 0);
            Assert.AreEqual(1, pathfinder.DebugMaskedStartCount, "and report it");

            // The agent is therefore NOT at PathFailed, so there is nothing to diagnose. Parking it
            // there artificially must still not blame the start.
            var nav = Failed(query, inside, outside);
            Assert.AreNotEqual(Reason.AgentOnBlockedGround, Diagnose(mesh, query, nav, false));
            Assert.AreEqual(Reason.NoRouteOrBudget, Diagnose(mesh, query, nav, false),
                "the start's mask must not appear as a cause — it is not one");
        }

        #endregion

        #region The reseed-lost failure, which no counter can explain

        /// <summary>
        /// The case the counters cannot reach, pinned from both sides: the tool names it, and the
        /// pathfinder's five counters are all zero because <c>FindPath</c> was never called.
        ///
        /// <para>And the silence is permanent, not a one-tick gap: <c>ProcessPathRequest</c> returns
        /// early unless the status is <c>PathPending</c>, and nothing puts a <c>PathFailed</c> agent
        /// back there — the reseed revives <c>Moving</c>/<c>PathPending</c>/<c>Blocked</c> only.</para>
        /// </summary>
        [Test]
        public unsafe void TheReseedLostFailureIsNamed_AndNoCounterEverMoves()
        {
            FPNavMesh baseMesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            FPNavAgentSystem system = NavAgentTestHelper.CreateSystem(
                baseMesh, null, out FPNavMeshPathfinder pathfinder);
            var baseQuery = new FPNavMeshQuery(baseMesh, null);

            FPVector3 start = At3(Bx, Bz);
            Frame frame = NavAgentTestHelper.CreateFrameWithAgent(
                start, baseQuery.FindTriangle(start.ToXZ(), start.y),
                out EntityRef entity, out EntityRef[] entities);
            ref var seeded = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref seeded, At3(3.0, 3.0));
            Assert.GreaterOrEqual(seeded.CurrentTriangleIndex, 0, "fixture: on-mesh to begin with");

            // Carve the ground out from under it, exactly as placing a building does.
            FPNavMesh carved = NavAgentTestHelper.RebakeWithBuildings(
                baseMesh, NavAgentTestHelper.Building(Bx, Bz, retain: false));
            var carvedQuery = new FPNavMeshQuery(carved, null);
            int collected = FPNavAgentInstaller.Swap(ref frame, system, carved, ref entities);
            system.ReseedAgents(ref frame, entities, collected);

            ref var lost = ref frame.Get<NavAgentComponent>(entities[0]);
            Assert.AreEqual(-1, lost.CurrentTriangleIndex, "fixture: the reseed lost the triangle");
            Assert.AreEqual((byte)FPNavAgentStatus.PathFailed, lost.Status,
                "fixture: and parked it at PathFailed because it still has a destination");

            Assert.AreEqual(Reason.AgentOffMesh, Diagnose(carved, carvedQuery, lost, false),
                "the tool must name the agent's own off-mesh state, not the destination");

            // The whole point: nothing in the pathfinder saw this.
            Assert.AreEqual(0, pathfinder.DebugBlockedEndpointCount);
            Assert.AreEqual(0, pathfinder.DebugAreaMaskRejectedCount);
            Assert.AreEqual(0, pathfinder.DebugIterationExhaustedCount);
            Assert.AreEqual(0, pathfinder.DebugCorridorTruncatedCount);
            Assert.AreEqual(0, pathfinder.DebugMaskedStartCount);

            // ...and it stays that way: the replan refuses to touch a PathFailed agent.
            for (int tick = 1; tick <= 120; tick++)
                system.Update(ref frame, entities, collected, tick, NavAgentTestHelper.DT);

            ref var still = ref frame.Get<NavAgentComponent>(entities[0]);
            Assert.AreEqual((byte)FPNavAgentStatus.PathFailed, still.Status,
                "PathFailed is terminal for the replan — that is what makes the counter silence permanent");
            Assert.AreEqual(0, pathfinder.DebugBlockedEndpointCount + pathfinder.DebugAreaMaskRejectedCount
                + pathfinder.DebugIterationExhaustedCount + pathfinder.DebugMaskedStartCount,
                "120 ticks and still no counter has moved — no sampling window can produce this reason");
        }

        /// <summary>
        /// And the swap marker is what separates a failure whose cause is gone from one that is
        /// still true. Reading the status AFTER the swap is enough — the reseed does not revive
        /// <c>PathFailed</c>, so anything sitting there predates the mesh now installed.
        /// </summary>
        [Test]
        public void AFailureThatOutlivedItsCauseIsNamedStale()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            var nav = Failed(query, At3(3.0, 3.0), At3(Bx, Bz));

            Assert.AreEqual(Reason.DestinationAreaMasked, Diagnose(mesh, query, nav, false),
                "while the footprint is there the cause is live");

            // The building goes away: rebake the ORIGINAL field, i.e. the mesh without it.
            FPNavMesh cleared = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var clearedQuery = new FPNavMeshQuery(cleared, null);
            Assert.GreaterOrEqual(clearedQuery.FindPassableTriangle(At(Bx, Bz), AgentMask), 0,
                "fixture: the destination is walkable again");

            Assert.AreEqual(Reason.NoRouteOrBudget, Diagnose(cleared, clearedQuery, nav, false),
                "without the marker a stale failure is indistinguishable from a genuine no-route");
            Assert.AreEqual(Reason.StaleFailure, Diagnose(cleared, clearedQuery, nav, true),
                "with it, the tool can say the failure predates this mesh");
        }

        #endregion

        #region Why the public-member approximation was not enough

        /// <summary>
        /// The gate that justifies making <c>FindTriangleForEndpoint</c> public. A destination on a
        /// footprint BOUNDARY is inside both the walkable triangle and the footprint at the same
        /// interpolated height; the plain lookup answers whichever has the lower index. When the
        /// path then fails for an unrelated reason, a tool built on the plain lookup reports "the
        /// mask refused it" and sends the reader to the mask knob.
        /// </summary>
        [Test]
        public void ThePlainLookupWouldMisnameABoundaryDestination()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);

            // Find a boundary point the plain lookup resolves to the footprint — the projection
            // produces exactly these, so this is the ordinary output of a destination snap.
            FPVector2 boundary = default;
            bool found = false;
            for (double d = 0.05; d < half && !found; d += 0.05)
            {
                foreach (var probe in new[]
                {
                    At(Bx, Bz - half + d), At(Bx, Bz + half - d),
                    At(Bx - half + d, Bz), At(Bx + half - d, Bz),
                })
                {
                    FPVector2 p = query.ProjectToPassable(probe, FP64.FromInt(4), AgentMask, out int cert);
                    if (cert < 0) continue;
                    int plain = query.FindTriangle(p, query.SampleHeight(p, cert));
                    if (plain < 0 || mesh.Triangles[plain].areaMask != FPNavMeshAreas.BUILDING_MASK)
                        continue;
                    boundary = p;
                    found = true;
                    break;
                }
            }
            Assert.IsTrue(found,
                "fixture: no snapped boundary point resolved to the footprint under the plain lookup "
                + "— the misnaming this gate is about cannot occur on this mesh");

            FP64 y = query.SampleHeight(boundary, query.FindTriangleForEndpoint(boundary, FP64.Zero, AgentMask));
            int plainTri = query.FindTriangle(boundary, y);
            int engineTri = query.FindTriangleForEndpoint(boundary, y, AgentMask);

            Assert.AreEqual(FPNavMeshAreas.BUILDING_MASK, mesh.Triangles[plainTri].areaMask,
                "the plain lookup lands on the footprint...");
            Assert.AreNotEqual(FPNavMeshAreas.BUILDING_MASK, mesh.Triangles[engineTri].areaMask,
                "...while the lookup FindPath actually uses lands on walkable ground");
            Assert.AreNotEqual(plainTri, engineTri,
                "so a tool built on the plain lookup would name a mask refusal the engine never made");
        }

        #endregion

        #region The diagnosis leaves nothing behind

        /// <summary>
        /// The verdict is read-only. The queries it uses must not move the pathfinder's counters or
        /// an agent's corridor — the editor runs this per repaint, and the pathfinder it would
        /// disturb is the one the tool also uses for its Start/End path preview.
        /// </summary>
        [Test]
        public void TheQueriesTheVerdictUsesChangeNothing()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            FPVector3 start = At3(3.0, 3.0);
            Assert.IsTrue(pathfinder.FindPath(start, At3(13.0, 13.0), AgentMask,
                out int[] corridor, out int len), "fixture: a path to compare against");
            int[] before = new int[len];
            System.Array.Copy(corridor, before, len);
            int c1 = pathfinder.DebugBlockedEndpointCount, c2 = pathfinder.DebugAreaMaskRejectedCount,
                c3 = pathfinder.DebugIterationExhaustedCount, c4 = pathfinder.DebugCorridorTruncatedCount,
                c5 = pathfinder.DebugMaskedStartCount;

            var nav = Failed(query, start, At3(Bx, Bz));
            for (int i = 0; i < 30; i++)
                Diagnose(mesh, query, nav, false);

            Assert.AreEqual(c1, pathfinder.DebugBlockedEndpointCount);
            Assert.AreEqual(c2, pathfinder.DebugAreaMaskRejectedCount);
            Assert.AreEqual(c3, pathfinder.DebugIterationExhaustedCount);
            Assert.AreEqual(c4, pathfinder.DebugCorridorTruncatedCount);
            Assert.AreEqual(c5, pathfinder.DebugMaskedStartCount);
            for (int i = 0; i < len; i++)
                Assert.AreEqual(before[i], corridor[i], "the corridor must be untouched");
        }

        #endregion

        private static void AssertAgrees(
            FPNavMesh mesh, FPNavMeshQuery query, FPVector3 from, FPVector3 to,
            Reason expected, string label)
        {
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            var nav = Failed(query, from, to);

            Assert.IsFalse(pathfinder.FindPath(from, to, AgentMask, out _, out _),
                $"fixture ({label}): FindPath must actually refuse this");
            Assert.AreEqual(expected, Diagnose(mesh, query, nav, false),
                $"({label}) the tool's verdict disagrees with the engine's refusal");
        }

        private static void CloseEveryTriangleContaining(FPNavMesh mesh, FPVector2 p)
        {
            // Every triangle, not just the one a lookup names: a point on a shared edge is inside
            // two, and closing one leaves it standing on open ground.
            int closed = 0;
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                ref readonly var tri = ref mesh.Triangles[i];
                if (!FPNavMeshQuery.PointInTriangle2D(
                        p,
                        mesh.Vertices[tri.v0].ToXZ(),
                        mesh.Vertices[tri.v1].ToXZ(),
                        mesh.Vertices[tri.v2].ToXZ()))
                    continue;
                mesh.TrianglesMutable[i].isBlocked = true;
                closed++;
            }
            Assert.GreaterOrEqual(closed, 1, "fixture: the gate must actually close something");
        }
    }
}
