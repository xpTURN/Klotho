using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// <see cref="FPNavPathFailure"/> — the diagnosis both editor tools now share.
    ///
    /// <para><b>Why this file can exist.</b> The diagnosis used to live twice, once inside Unity's
    /// <c>FPNavMeshAgentSimulator</c> (internal to an Editor-platform assembly) and once inside the
    /// Godot adapter (compiled into the consuming project). Neither was reachable from any test
    /// project, so the behaviour was gated by eye. Folding it into the runtime is what makes it
    /// testable — and these tests are the gate that the fold changed no answer.</para>
    ///
    /// <para>Every branch is pinned, in the order <c>FindPath</c> refuses in, plus the seven
    /// strings <see cref="FPNavPathFailure.Describe"/> returns. The strings are a contract: both
    /// tools print them, so a reworded one is a visible change in two editors.</para>
    /// </summary>
    [TestFixture]
    public class FPNavPathFailureTests
    {
        private const int Cells = 8;                 // 8x8 lattice cells of 2 units = 16x16 world

        private static NavAgentComponent Failed(FPVector3 at, int triIdx, FPVector3 dest)
        {
            var nav = default(NavAgentComponent);
            NavAgentComponent.Init(ref nav, at);
            NavAgentComponent.SetDestination(ref nav, dest);
            nav.CurrentTriangleIndex = triIdx;
            nav.Status = (byte)FPNavAgentStatus.PathFailed;
            return nav;
        }

        private static FPVector3 P(double x, double y, double z) => new FPVector3(
            FP64.FromDouble(x), FP64.FromDouble(y), FP64.FromDouble(z));

        #region The two None guards — a tool asks before it has a mesh, and about healthy agents

        [Test]
        public void NotFailed_IsNone_WhateverElseIsTrue()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var query = new FPNavMeshQuery(mesh, null);

            var nav = Failed(P(1, 0, 1), 0, P(3, 0, 3));
            nav.Status = (byte)FPNavAgentStatus.Moving;

            Assert.AreEqual(FPNavPathFailureReason.None,
                FPNavPathFailure.Diagnose(nav, query, mesh, false),
                "only PathFailed has a cause to name");
        }

        [Test]
        public void NoMeshYet_IsNone_RatherThanThrowing()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var query = new FPNavMeshQuery(mesh, null);
            var nav = Failed(P(1, 0, 1), 0, P(3, 0, 3));

            Assert.AreEqual(FPNavPathFailureReason.None,
                FPNavPathFailure.Diagnose(nav, null, mesh, false), "null query");
            Assert.AreEqual(FPNavPathFailureReason.None,
                FPNavPathFailure.Diagnose(nav, query, null, false), "null mesh");
        }

        #endregion

        #region The agent's own footing — checked before the endpoint

        [Test]
        public void OffMesh_BeatsEveryEndpointReason()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var query = new FPNavMeshQuery(mesh, null);

            // Destination is off the mesh too; footing must still win, because a reseed-lost agent
            // is the case no counter explains and re-clicking will not fix it.
            var nav = Failed(P(1, 0, 1), -1, P(999, 0, 999));

            Assert.AreEqual(FPNavPathFailureReason.AgentOffMesh,
                FPNavPathFailure.Diagnose(nav, query, mesh, false));
        }

        [Test]
        public void AgentOnBlockedGround_IsNamedBeforeTheEndpointIsLookedAt()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            mesh.TrianglesMutable[0].isBlocked = true;
            var query = new FPNavMeshQuery(mesh, null);

            var nav = Failed(P(1, 0, 1), 0, P(5, 0, 5));

            Assert.AreEqual(FPNavPathFailureReason.AgentOnBlockedGround,
                FPNavPathFailure.Diagnose(nav, query, mesh, false));
        }

        #endregion

        #region The endpoint, in FindPath's order: lookup, isBlocked, mask

        [Test]
        public void DestinationOffMesh_WhenTheLookupFinesNothing()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var query = new FPNavMeshQuery(mesh, null);

            var nav = Failed(P(1, 0, 1), 0, P(999, 0, 999));

            Assert.AreEqual(FPNavPathFailureReason.DestinationOffMesh,
                FPNavPathFailure.Diagnose(nav, query, mesh, false));
        }

        [Test]
        public void DestinationBlocked_IsDistinctFromAreaMasked()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var query = new FPNavMeshQuery(mesh, null);

            // Resolve the destination the way the engine does, then close that very triangle.
            // The point must sit strictly INSIDE one triangle: on a cell diagonal both triangles
            // contain it, and closing one only makes the endpoint tie-break pick the other — the
            // diagnosis would then read NoRouteOrBudget and the fixture would be testing nothing.
            FPVector3 dest = P(10.4, 0, 11.6);
            int endTri = query.FindTriangleForEndpoint(
                dest.ToXZ(), dest.y, FPNavAgentSystem.DEFAULT_AREA_MASK);
            Assert.GreaterOrEqual(endTri, 0, "fixture: the destination must be on the mesh");
            mesh.TrianglesMutable[endTri].isBlocked = true;

            var nav = Failed(P(1, 0, 1), 0, dest);

            Assert.AreEqual(FPNavPathFailureReason.DestinationBlocked,
                FPNavPathFailure.Diagnose(nav, query, mesh, false),
                "blocked is closed in code, not by a mask — the two must not be confused");
        }

        [Test]
        public void DestinationAreaMasked_OnAForbiddenUpperFloor()
        {
            // The stacked fixture is the only one where the endpoint lookup's Y disambiguation
            // does any work: the upper floor carries area 3, which the ground-only mask omits.
            var mesh = NavAgentTestHelper.CreateStackedFloorsNavMesh(Cells, floorGap: 4.0);
            var query = new FPNavMeshQuery(mesh, null);
            int groundOnly = ~(1 << 3);

            var nav = Failed(P(3, 0, 3), 0, P(5, 4, 5));
            nav.PlanAreaMaskOverride = groundOnly;

            Assert.AreEqual(FPNavPathFailureReason.DestinationAreaMasked,
                FPNavPathFailure.Diagnose(nav, query, mesh, false),
                "a destination upstairs has no walkable interpretation AT THAT HEIGHT");
        }

        #endregion

        #region Stale vs no-route — the one distinction the caller has to supply

        [Test]
        public void EveryCheckPasses_TheSwapMarkerIsTheOnlyThingThatSeparatesTheLastTwo()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var query = new FPNavMeshQuery(mesh, null);
            var nav = Failed(P(1, 0, 1), 0, P(11, 0, 11));

            Assert.AreEqual(FPNavPathFailureReason.NoRouteOrBudget,
                FPNavPathFailure.Diagnose(nav, query, mesh, false));
            Assert.AreEqual(FPNavPathFailureReason.StaleFailure,
                FPNavPathFailure.Diagnose(nav, query, mesh, true),
                "same agent, same mesh — only the caller's bookkeeping differs");
        }

        #endregion

        #region Describe — the seven strings are a contract, byte for byte

        [Test]
        public void Describe_ReturnsTheStringsBothToolsUsedBeforeTheFold()
        {
            Assert.AreEqual(" ← agent is off the mesh (a rebake left it there; nothing re-acquires it)",
                FPNavPathFailure.Describe(FPNavPathFailureReason.AgentOffMesh));
            Assert.AreEqual(" ← the agent stands on blocked ground",
                FPNavPathFailure.Describe(FPNavPathFailureReason.AgentOnBlockedGround));
            Assert.AreEqual(" ← the destination is off the mesh",
                FPNavPathFailure.Describe(FPNavPathFailureReason.DestinationOffMesh));
            Assert.AreEqual(" ← the destination is blocked (closed in code, not by a mask)",
                FPNavPathFailure.Describe(FPNavPathFailureReason.DestinationBlocked));
            Assert.AreEqual(" ← the destination's area is outside this agent's plan mask",
                FPNavPathFailure.Describe(FPNavPathFailureReason.DestinationAreaMasked));
            Assert.AreEqual(" ← stale: nothing blocks it now (the mesh changed) — set the destination again",
                FPNavPathFailure.Describe(FPNavPathFailureReason.StaleFailure));
            Assert.AreEqual(" ← no route (or the search budget ran out)",
                FPNavPathFailure.Describe(FPNavPathFailureReason.NoRouteOrBudget));
        }

        [Test]
        public void Describe_None_IsEmpty_SoACallerCanAppendUnconditionally()
        {
            Assert.AreEqual("", FPNavPathFailure.Describe(FPNavPathFailureReason.None));
        }

        /// <summary>
        /// The member order is the refusal order, and <see cref="FPNavPathFailure.Diagnose"/> walks
        /// it. Renumbering would reorder the diagnosis silently, so the values are pinned.
        /// </summary>
        [Test]
        public void MemberOrder_IsTheRefusalOrder()
        {
            Assert.AreEqual(0, (int)FPNavPathFailureReason.None);
            Assert.AreEqual(1, (int)FPNavPathFailureReason.AgentOffMesh);
            Assert.AreEqual(2, (int)FPNavPathFailureReason.AgentOnBlockedGround);
            Assert.AreEqual(3, (int)FPNavPathFailureReason.DestinationOffMesh);
            Assert.AreEqual(4, (int)FPNavPathFailureReason.DestinationBlocked);
            Assert.AreEqual(5, (int)FPNavPathFailureReason.DestinationAreaMasked);
            Assert.AreEqual(6, (int)FPNavPathFailureReason.StaleFailure);
            Assert.AreEqual(7, (int)FPNavPathFailureReason.NoRouteOrBudget);
        }

        #endregion
    }
}
