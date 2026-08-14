using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Pins a KNOWN DEFECT, not desired behaviour: an agent that a rebake leaves off-mesh never
    /// recovers and never moves again.
    ///
    /// `ReseedAgents` sets CurrentTriangleIndex = -1 when the agent's position is no longer on the
    /// mesh (a building was placed on top of it), and nothing in the engine ever sets it back:
    /// `MoveAlongSurface` returns early on `startTri &lt; 0` and hands the -1 straight back, so the
    /// only write that could restore it is the NEXT rebake's reseed. Until then the agent is
    /// frozen — and, because the corridor can never advance, it also repaths forever while
    /// reporting Moving.
    ///
    /// **These tests passing is the bug, not the fix.** The day recovery is implemented they must
    /// be inverted — that inversion is the success signal for any recovery work.
    ///
    /// Why the defect is invisible today: Brawler masks it in its own layer (BotFSMSystem Pass 2
    /// re-snaps nav.Position from the transform every tick, and a PathFailed fallback drives the
    /// character straight at the destination). These tests deliberately use FPNavAgentSystem
    /// ALONE, which is what an engine-only consumer would get.
    ///
    /// Re-review conditions (a recovery path is deferred, not rejected): revisit when either
    ///   (a) a consumer appears that rebakes WITHOUT Brawler's two devices, or
    ///   (b) those two devices are removed from Brawler — which makes Brawler itself such a
    ///       consumer the moment it happens, so that removal must be atomic with engine recovery.
    /// Neither condition can be detected automatically, which is why it is written here: this
    /// comment is the trail back to the analysis, not an alarm.
    /// </summary>
    [TestFixture]
    public class FPNavAgentOffMeshFreezeTests
    {
        #region Fixture

        private static FPNavMesh BuildBase()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -10; x <= 10; x += 5)
                for (int z = -10; z <= 10; z += 5)
                    pts.Add((x, z));

            var vertices = new FPVector3[pts.Count];
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        /// <summary>2x2 at the origin; the 0.5 bake radius expands the carved hole to exactly 3x3.</summary>
        private static FPBuildingRect CenterBuilding() => new FPBuildingRect(
            FP64.FromInt(-1), FP64.FromInt(-1), FP64.FromInt(1), FP64.FromInt(1), FP64.Zero);

        private static (FPNavAgentSystem system, FPNavMeshQuery query) CreateSystem(FPNavMesh mesh)
        {
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            var funnel = new FPNavMeshFunnel(mesh, query, null);
            var system = new FPNavAgentSystem(mesh, query, pathfinder, funnel, null);
            system.SetAvoidance(new FPNavAvoidance());
            system.LoadNavMeshObstacles();
            return (system, query);
        }

        private static FPNavMeshQuery Swap(FPNavAgentSystem system, FPNavMesh newMesh)
        {
            var q = new FPNavMeshQuery(newMesh, null);
            system.SwapNavMesh(newMesh, q, new FPNavMeshPathfinder(newMesh, q, null),
                new FPNavMeshFunnel(newMesh, q, null));
            return q;
        }

        /// <summary>
        /// Places an agent at <paramref name="start"/> heading east, carves the centre building,
        /// swaps, and reseeds — i.e. exactly what a building placed on top of an agent does.
        /// </summary>
        private static (FPNavAgentSystem system, FPNavMeshQuery newQuery, Frame frame, EntityRef[] entities)
            BuryAgent(FPVector3 start)
        {
            FPNavMesh baseMesh = BuildBase();
            var (system, query) = CreateSystem(baseMesh);

            Frame frame = NavAgentTestHelper.CreateFrameWithAgent(
                start, query.FindTriangle(start.ToXZ(), start.y), out EntityRef entity, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav, new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero));
            Assert.GreaterOrEqual(nav.CurrentTriangleIndex, 0, "agent must start on the base mesh");

            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });
            FPNavMeshQuery newQuery = Swap(system, rebaked);
            system.ReseedAgents(ref frame, entities, 1);

            return (system, newQuery, frame, entities);
        }

        #endregion

        [Test]
        public unsafe void BuriedAgent_NeverMovesAgain()
        {
            // The defect in its plainest form. The agent had a destination and was on the mesh;
            // after a building is dropped on it, it is off-mesh and stays exactly where it was
            // for as long as the simulation runs.
            //
            // And it is NOT stuck for want of somewhere to go: the projection finds walkable
            // ground even from the hole's centre at this scale (measured — see
            // ProjectionReach_IsBoundedToTheNeighbouringCells). So the engine is not failing to
            // find an escape; it never looks for one.
            var start = new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero);
            var (system, newQuery, frame, entities) = BuryAgent(start);

            ref var nav = ref frame.Get<NavAgentComponent>(entities[0]);
            Assert.AreEqual(-1, nav.CurrentTriangleIndex, "reseed marks the buried agent off-mesh");

            newQuery.ClosestPointOnNavMesh(nav.Position.ToXZ(), out int reachable);
            Assert.GreaterOrEqual(reachable, 0,
                "an escape exists — which is what makes the freeze below a missing re-acquisition "
                + "rather than an impossible situation");

            FPVector3 afterReseed = nav.Position;
            for (int tick = 1; tick <= 120; tick++)
                system.Update(ref frame, entities, 1, tick, NavAgentTestHelper.DT);

            ref var after = ref frame.Get<NavAgentComponent>(entities[0]);
            Assert.AreEqual(-1, after.CurrentTriangleIndex,
                "DEFECT: nothing in the engine re-acquires the triangle — MoveAlongSurface returns "
                + "early on startTri < 0 and hands the -1 back, so only the NEXT rebake's reseed could");
            Assert.AreEqual(afterReseed.x.RawValue, after.Position.x.RawValue,
                "DEFECT: the agent is frozen — 120 ticks with a destination and it has not moved (x)");
            Assert.AreEqual(afterReseed.z.RawValue, after.Position.z.RawValue,
                "DEFECT: the agent is frozen — 120 ticks with a destination and it has not moved (z)");
        }

        [Test]
        public unsafe void BuriedAgent_AtHoleEdge_StaysFrozen_EvenThoughWalkableIsOneStepAway()
        {
            // Second position, near the hole's edge. Same outcome, and it rules out any reading
            // where the centre case depended on the agent sitting exactly on a symmetry point:
            // wherever inside the hole it stands, the index stays -1 and the agent stays put.
            var start = new FPVector3(FP64.FromDouble(1.4), FP64.Zero, FP64.Zero);
            var (system, newQuery, frame, entities) = BuryAgent(start);

            ref var nav = ref frame.Get<NavAgentComponent>(entities[0]);
            Assert.AreEqual(-1, nav.CurrentTriangleIndex, "agent just inside the hole edge is off-mesh");

            // Recovery IS possible from here — the projection finds walkable ground. Nothing calls it.
            newQuery.ClosestPointOnNavMesh(nav.Position.ToXZ(), out int reachable);
            Assert.GreaterOrEqual(reachable, 0,
                "fixture check: walkable ground is within the projection's 3x3-cell reach here too");

            FPVector3 afterReseed = nav.Position;
            for (int tick = 1; tick <= 120; tick++)
                system.Update(ref frame, entities, 1, tick, NavAgentTestHelper.DT);

            ref var after = ref frame.Get<NavAgentComponent>(entities[0]);
            Assert.AreEqual(-1, after.CurrentTriangleIndex,
                "DEFECT: a walkable triangle is one projection away and the engine never asks");
            Assert.AreEqual(afterReseed.x.RawValue, after.Position.x.RawValue,
                "DEFECT: frozen despite an escape being available (x)");
            Assert.AreEqual(afterReseed.z.RawValue, after.Position.z.RawValue,
                "DEFECT: frozen despite an escape being available (z)");
        }

        [Test]
        public void ProjectionReach_IsBoundedToTheNeighbouringCells()
        {
            // Records the limit any recovery design would inherit: the projection
            // only searches the agent's cell plus the 8 around it, so the failure condition is not
            // "the building is large" but "there is no walkable triangle within one cell ring".
            //
            // Measured at this scale (cell 1.0, hole 3x3): the projection succeeds from EVERY
            // point inside the hole, centre included. So the reach limit does not bite here — the
            // freeze the other two tests pin is purely a missing re-acquisition, with nothing to
            // do with how far the search can see. A larger hole relative to the cell size is what
            // would make the limit matter, and that is the case the recovery plan must measure
            // before choosing a search width.
            FPNavMesh baseMesh = BuildBase();
            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });
            var query = new FPNavMeshQuery(rebaked, null);

            TestContext.Out.WriteLine(
                $"grid cell size {rebaked.GridCellSize.ToDouble():F2}, hole 3x3 world units");

            for (double x = 0.0; x <= 1.4; x += 0.2)
            {
                var p = new FPVector2(FP64.FromDouble(x), FP64.Zero);
                Assert.AreEqual(-1, query.FindTriangle(p),
                    $"fixture check: x={x:F1} must be inside the carved hole");
                query.ClosestPointOnNavMesh(p, out int tri);
                TestContext.Out.WriteLine($"  x={x:F1}: projection {(tri >= 0 ? "found tri " + tri : "FAILED")}");
            }
        }
    }
}
