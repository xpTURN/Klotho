using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The three caps that used to fail silently, and the counters that now report them:
    /// the position-correction pass at <c>MAX_AGENTS</c>, the corridor buffer at
    /// <c>MAX_CORRIDOR</c>, and the A* budget at <c>MAX_ITERATIONS</c>.
    ///
    /// Each counter accumulates over the lifetime of the instance that owns it, so every test
    /// here builds its own system — a shared instance would carry the previous test's total.
    /// </summary>
    [TestFixture]
    public class FPNavCrowdDiagnosticsTests
    {
        #region Position-correction cap (MAX_AGENTS)

        private static (FPNavAgentSystem system, Frame frame, EntityRef[] entities) MovingAgents(int count)
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            system.SetAvoidance(new FPNavAvoidance());   // the correction pass is gated on avoidance

            var positions = new FPVector3[count];
            var velocities = new FPVector2[count];
            for (int i = 0; i < count; i++)
            {
                // Deliberately overlapping: the correction pass has real work for every agent.
                positions[i] = new FPVector3(
                    FP64.FromDouble(4.0 + (i % 4) * 0.1), FP64.Zero,
                    FP64.FromDouble(4.0 + (i / 4) * 0.1));
                velocities[i] = FPVector2.Zero;
            }

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                positions, velocities, out var entities, maxEntities: count + 8);
            return (system, frame, entities);
        }

        [Test]
        public void PositionCorrection_UnderTheCap_ReportsNoTruncation()
        {
            var (system, frame, entities) = MovingAgents(FPNavAgentSystem.MAX_AGENTS);
            system.Update(ref frame, entities, entities.Length, 1, NavAgentTestHelper.DT);

            Assert.AreEqual(0, system.DebugCollisionResolveTruncatedCount,
                "exactly MAX_AGENTS agents fit — nothing should be reported as dropped");
        }

        [Test]
        public void PositionCorrection_OneOverTheCap_ReportsTheOneItDropped()
        {
            var (system, frame, entities) = MovingAgents(FPNavAgentSystem.MAX_AGENTS + 1);
            system.Update(ref frame, entities, entities.Length, 1, NavAgentTestHelper.DT);

            Assert.AreEqual(1, system.DebugCollisionResolveTruncatedCount,
                "the 65th agent is steered and moved but never position-corrected, and that used " +
                "to happen with no error and no log");
        }

        [Test]
        public void PositionCorrection_SplitIntoClusters_CountsEveryCallInsteadOfTheLastOne()
        {
            // The pattern this plan recommends: one Update per spatial cluster. A per-tick counter
            // would report only the final call; a lifetime counter sums, which is what makes the
            // number usable when the caller splits the array.
            const int total = 130;
            var (single, frameA, entitiesA) = MovingAgents(total);
            single.Update(ref frameA, entitiesA, total, 1, NavAgentTestHelper.DT);
            Assert.AreEqual(total - FPNavAgentSystem.MAX_AGENTS, single.DebugCollisionResolveTruncatedCount,
                "one call with 130 agents drops 66");

            var (split, frameB, entitiesB) = MovingAgents(total);
            var first = new EntityRef[65];
            var second = new EntityRef[65];
            System.Array.Copy(entitiesB, 0, first, 0, 65);
            System.Array.Copy(entitiesB, 65, second, 0, 65);

            split.Update(ref frameB, first, first.Length, 1, NavAgentTestHelper.DT);
            split.Update(ref frameB, second, second.Length, 1, NavAgentTestHelper.DT);

            Assert.AreEqual(2, split.DebugCollisionResolveTruncatedCount,
                "65 + 65 drops one agent per call — the two calls must sum, not overwrite");
        }

        #endregion

        #region Corridor cap (MAX_CORRIDOR)

        [Test]
        public void Corridor_LongerThanTheBuffer_IsReportedAsTruncated()
        {
            // 16 x 6 serpentine: ~200 triangles along the only route, well past the 128 buffer
            // but far short of the iteration budget, so the search succeeds and truncates.
            var mesh = NavAgentTestHelper.CreateSerpentineNavMesh(16, 6, out var endCell);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            bool found = pathfinder.FindPath(
                NavAgentTestHelper.CellCenter(0, 0),
                NavAgentTestHelper.CellCenter(endCell.gx, endCell.gz),
                FPNavAgentSystem.DEFAULT_AREA_MASK, out _, out int corridorLength);

            Assert.IsTrue(found, "the serpentine is connected end to end");
            Assert.AreEqual(FPNavMeshPathfinder.MAX_CORRIDOR, corridorLength,
                "a longer path comes back clamped to the buffer");
            Assert.AreEqual(1, pathfinder.DebugCorridorTruncatedCount,
                "the clamp drops the far end of the route and used to be invisible");
        }

        [Test]
        public void Corridor_WithinTheBuffer_IsNotReported()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            bool found = pathfinder.FindPath(
                NavAgentTestHelper.CellCenter(0, 0), NavAgentTestHelper.CellCenter(7, 7),
                FPNavAgentSystem.DEFAULT_AREA_MASK, out _, out int corridorLength);

            Assert.IsTrue(found);
            Assert.Less(corridorLength, FPNavMeshPathfinder.MAX_CORRIDOR);
            Assert.AreEqual(0, pathfinder.DebugCorridorTruncatedCount);
        }

        #endregion

        #region A* budget (MAX_ITERATIONS)

        [Test]
        public void Search_ThatRunsOutOfBudget_IsDistinguishedFromHavingNoRoute()
        {
            // 64 x 40 serpentine ≈ 5,200 triangles on a single forced route. The heuristic points
            // at the goal across the rows while the only path doubles back, so the budget goes
            // before the far end does.
            var mesh = NavAgentTestHelper.CreateSerpentineNavMesh(64, 40, out var endCell);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            bool found = pathfinder.FindPath(
                NavAgentTestHelper.CellCenter(0, 0),
                NavAgentTestHelper.CellCenter(endCell.gx, endCell.gz),
                FPNavAgentSystem.DEFAULT_AREA_MASK, out _, out _);

            Assert.IsFalse(found, "the route is longer than the iteration budget");
            Assert.AreEqual(1, pathfinder.DebugIterationExhaustedCount,
                "this is the failure that means 'ask for a shorter path', not 'there is no path'");
        }

        [Test]
        public void Search_ThatExhaustsTheGraph_IsNotCountedAsABudgetOverrun()
        {
            // Two disconnected patches: the search drains its open set long before the budget, so
            // the same silent `false` must NOT be attributed to the iteration cap.
            var mesh = NavAgentTestHelper.CreateSplitFieldNavMesh(12, out var farCell);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            FPVector3 start = NavAgentTestHelper.CellCenter(0, 0);
            FPVector3 goal = NavAgentTestHelper.CellCenter(farCell.gx, farCell.gz);

            // Without this the test would pass for the wrong reason: an off-mesh endpoint bails
            // out before the search loop, leaving the counter at 0 with nothing exercised.
            Assert.GreaterOrEqual(query.FindTriangle(start.ToXZ(), start.y), 0, "start is on-mesh");
            Assert.GreaterOrEqual(query.FindTriangle(goal.ToXZ(), goal.y), 0, "goal is on-mesh");

            bool found = pathfinder.FindPath(
                start, goal, FPNavAgentSystem.DEFAULT_AREA_MASK, out _, out _);

            Assert.IsFalse(found, "the goal patch shares no edge with the start patch");
            Assert.AreEqual(0, pathfinder.DebugIterationExhaustedCount,
                "the budget was never the reason — counting it here would report a cap problem " +
                "that does not exist");
        }

        // The boundary the discriminator is about. A start patch of exactly MAX_ITERATIONS
        // triangles drains its open set on the very iteration the budget runs out: the search
        // finished its work, so nothing was truncated. Reading the iteration count instead of the
        // open set reports a budget overrun here — these two cases pin that apart.
        [TestCase(64, 32, 0, TestName = "Search_DrainingExactlyAtTheBudget_IsNotABudgetOverrun")]
        [TestCase(65, 32, 1, TestName = "Search_StillHoldingWorkAtTheBudget_IsABudgetOverrun")]
        public void Search_AtTheBudgetBoundary(int width, int height, int expectedExhausted)
        {
            var mesh = NavAgentTestHelper.CreateSplitFieldNavMesh(width, height, out var farCell);
            Assert.AreEqual(width * height * 2 + 8, mesh.Triangles.Length,
                "start patch of width*height*2 triangles, plus the 2x2 island holding the goal");

            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            bool found = pathfinder.FindPath(
                NavAgentTestHelper.CellCenter(0, 0),
                NavAgentTestHelper.CellCenter(farCell.gx, farCell.gz),
                FPNavAgentSystem.DEFAULT_AREA_MASK, out _, out _);

            Assert.IsFalse(found);
            Assert.AreEqual(expectedExhausted, pathfinder.DebugIterationExhaustedCount);
        }

        #endregion

        #region Rejections that happen before the search

        // Both of these return before the A* loop, so they cannot show up in the budget counter.
        // From the outside all three look the same — a silent `false` — which is the whole reason
        // they are counted separately.

        [Test]
        public void BlockedEndpoint_IsCountedAsItsOwnFailure_NotAsAMissingRoute()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            FPVector3 start = NavAgentTestHelper.CellCenter(0, 0);
            FPVector3 goal = NavAgentTestHelper.CellCenter(7, 7);

            // Close the goal triangle the way a game closes a gate: nothing in the engine sets this
            // flag, and the rebaker carves geometry instead, so TrianglesMutable is the only
            // producer there is.
            int goalTri = query.FindTriangle(goal.ToXZ(), goal.y);
            Assert.GreaterOrEqual(goalTri, 0, "goal is on-mesh before it is blocked");
            mesh.TrianglesMutable[goalTri].isBlocked = true;

            bool found = pathfinder.FindPath(
                start, goal, FPNavAgentSystem.DEFAULT_AREA_MASK, out _, out _);

            Assert.IsFalse(found);
            Assert.AreEqual(1, pathfinder.DebugBlockedEndpointCount);
            Assert.AreEqual(0, pathfinder.DebugIterationExhaustedCount,
                "the search never started — attributing this to the A* budget would send a reader " +
                "looking at MAX_ITERATIONS for a gate someone closed");
            Assert.AreEqual(0, pathfinder.DebugAreaMaskRejectedCount);
        }

        [Test]
        public void AreaMaskMismatch_IsCountedAsItsOwnFailure()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            // The build pipeline stamps areaMask = 1 << area, and the helper meshes use area 0.
            const int nonMatchingMask = 1 << 3;
            bool found = pathfinder.FindPath(
                NavAgentTestHelper.CellCenter(0, 0), NavAgentTestHelper.CellCenter(7, 7),
                nonMatchingMask, out _, out _);

            Assert.IsFalse(found);
            Assert.AreEqual(1, pathfinder.DebugAreaMaskRejectedCount);
            Assert.AreEqual(0, pathfinder.DebugBlockedEndpointCount);
            Assert.AreEqual(0, pathfinder.DebugIterationExhaustedCount);
        }

        [Test]
        public void AgentSystem_TripsTheAreaMaskRejection_OnlyForARetainedFootprint()
        {
            // FPNavAgentSystem passes DEFAULT_AREA_MASK, which admits every BAKED area, so on a
            // baked mesh this counter cannot move through the agent path — the open field says so.
            // The one bit the mask excludes is the runtime's: a destination inside a RETAINED
            // building footprint (FPNavMeshAreas.BUILDING_AREA) is refused by FindPath and lands
            // here. Pinning both halves keeps the counter honest in both directions.
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var system = NavAgentTestHelper.CreateSystem(mesh, null, out var pathfinder);

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                NavAgentTestHelper.CellCenter(0, 0), 0, out var entity, out var entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav, NavAgentTestHelper.CellCenter(7, 7));

            system.Update(ref frame, entities, entities.Length, 1, NavAgentTestHelper.DT);

            Assert.AreEqual(0, pathfinder.DebugAreaMaskRejectedCount,
                "a baked area tripped the agent's mask");

            // Same field, one retained building mid-way, destination inside its footprint.
            var retained = NavAgentTestHelper.RebakeWithBuildings(
                mesh, NavAgentTestHelper.Building(8.0, 8.0, retain: true));
            var query = new FPNavMeshQuery(retained, null);
            var system2 = NavAgentTestHelper.CreateSystem(retained, null, out var pathfinder2);

            FPVector3 start = NavAgentTestHelper.CellCenter(0, 0);
            var frame2 = NavAgentTestHelper.CreateFrameWithAgent(
                start, query.FindTriangle(start.ToXZ()), out var entity2, out var entities2);
            ref var nav2 = ref frame2.Get<NavAgentComponent>(entity2);
            NavAgentComponent.SetDestination(ref nav2,
                new FPVector3(FP64.FromDouble(8.0), FP64.Zero, FP64.FromDouble(8.0)));

            system2.Update(ref frame2, entities2, entities2.Length, 1, NavAgentTestHelper.DT);

            Assert.AreEqual(1, pathfinder2.DebugAreaMaskRejectedCount,
                "a destination inside a retained footprint must be refused by the agent's mask");
        }

        [Test]
        public void ASuccessfulPath_LeavesEveryFailureCounterAtZero()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            bool found = pathfinder.FindPath(
                NavAgentTestHelper.CellCenter(0, 0), NavAgentTestHelper.CellCenter(7, 7),
                FPNavAgentSystem.DEFAULT_AREA_MASK, out _, out _);

            Assert.IsTrue(found);
            Assert.AreEqual(0, pathfinder.DebugBlockedEndpointCount);
            Assert.AreEqual(0, pathfinder.DebugAreaMaskRejectedCount);
            Assert.AreEqual(0, pathfinder.DebugIterationExhaustedCount);
            Assert.AreEqual(0, pathfinder.DebugCorridorTruncatedCount);
        }

        #endregion

        #region The partition is a determinism input

        // Runs `ticks` ticks over `count` agents, calling Update once per run of `clusterSize`,
        // and returns the resulting state hash. Everything the rule depends on is frame state or
        // a constant, which is the requirement a real (spatial) rule has to meet too.
        private static ulong SimulateSplit(int count, int clusterSize, int ticks)
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(24);
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            system.SetAvoidance(new FPNavAvoidance());

            var positions = new FPVector3[count];
            var velocities = new FPVector2[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = new FPVector3(
                    FP64.FromDouble(6.0 + (i % 12) * 0.9), FP64.Zero,
                    FP64.FromDouble(6.0 + (i / 12) * 0.9));
                velocities[i] = FPVector2.Zero;
            }

            var frame = NavAgentTestHelper.CreateFrameWithMovingAgents(
                positions, velocities, out var entities, maxEntities: count + 8);

            FPVector3 target = NavAgentTestHelper.CellCenter(22, 22);
            for (int i = 0; i < count; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                NavAgentComponent.SetDestination(ref nav, target);
            }

            var cluster = new EntityRef[clusterSize];
            for (int tick = 1; tick <= ticks; tick++)
            {
                for (int start = 0; start < count; start += clusterSize)
                {
                    int len = System.Math.Min(clusterSize, count - start);
                    System.Array.Copy(entities, start, cluster, 0, len);
                    system.Update(ref frame, cluster, len, tick, NavAgentTestHelper.DT);
                }
            }
            return frame.CalculateHash();
        }

        [Test]
        public void SameSplitRule_ReplaysBitIdentically()
        {
            Assert.AreEqual(SimulateSplit(130, 32, 12), SimulateSplit(130, 32, 12),
                "a split rule that is a pure function of frame state must replay exactly");
        }

        [Test]
        public void ADifferentSplitRule_LandsSomewhereElse()
        {
            // This is the whole point, and the reason a clustering rule may not read wall-clock,
            // local input or hash iteration order: the partition is not a local optimisation, it
            // is simulation input. Two peers that cluster differently diverge — silently, because
            // both results are internally consistent.
            Assert.AreNotEqual(SimulateSplit(130, 32, 12), SimulateSplit(130, 64, 12),
                "ORCA ties break on array index and the correction pass takes the first 64, so " +
                "changing the partition changes the simulation");
        }

        #endregion

        #region Diagnostics are outside the simulation

        [Test]
        public void Counters_DoNotReachTheStateHash()
        {
            var (a, frameA, entitiesA) = MovingAgents(FPNavAgentSystem.MAX_AGENTS + 5);
            var (b, frameB, entitiesB) = MovingAgents(FPNavAgentSystem.MAX_AGENTS + 5);

            a.Update(ref frameA, entitiesA, entitiesA.Length, 1, NavAgentTestHelper.DT);
            // Same tick twice on b — the way a rollback resimulation double-counts.
            b.Update(ref frameB, entitiesB, entitiesB.Length, 1, NavAgentTestHelper.DT);

            Assert.AreNotEqual(0, a.DebugCollisionResolveTruncatedCount);
            Assert.AreEqual(a.DebugCollisionResolveTruncatedCount, b.DebugCollisionResolveTruncatedCount);
            Assert.AreEqual(frameA.CalculateHash(), frameB.CalculateHash(),
                "counters are diagnostic fields — they must not move the hash");
        }

        #endregion
    }
}
