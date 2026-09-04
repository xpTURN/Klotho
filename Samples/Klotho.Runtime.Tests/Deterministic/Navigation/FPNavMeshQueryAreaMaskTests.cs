using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The area filter now reaches the surface walk, not just A*.
    ///
    /// <para>Before this, <c>areaMask</c> stopped at <see cref="FPNavMeshPathfinder.FindPath"/>:
    /// the walk that actually moves an agent treated a masked-out triangle exactly like any other,
    /// so a corridor follow could slide a unit across ground its own path refused. Nothing observed
    /// it, because the engine's only caller passes <c>~0</c> — which is why these gates are the
    /// point of the change rather than a side effect of it.</para>
    ///
    /// <para>What each gate is for:
    /// <list type="bullet">
    /// <item>the unfiltered call is bit-identical to the pre-change walk (the oracle below),</item>
    /// <item>a masked-out neighbour is a wall, and <c>FindPath</c> agrees about the same triangle,</item>
    /// <item>the mask never gates the STARTING triangle — the asymmetry with <c>FindPath</c> that
    /// keeps a narrowed mask from freezing agents where they stand,</item>
    /// <item>the visited chain the public overload hands back respects the filter,</item>
    /// <item>the agent path still passes <c>~0</c> — the tripwire for the day it does not.</item>
    /// </list></para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshQueryAreaMaskTests
    {
        private const int NoFilter = ~0;
        private const int LeftArea = 1 << 0;    // build areas[] == 0
        private const int RightArea = 1 << 3;   // build areas[] == 3 — 1 is FPNavMeshAreas.BUILDING_AREA, reserved

        private static FP64 Threshold => FP64.FromDouble(2.0);

        /// <summary>
        /// A world point, chosen rather than derived — because a lattice CELL is two triangles
        /// split along its rising diagonal, and <c>CellCenter</c> lands exactly ON that diagonal.
        /// A point on the split is degenerately inside both halves, so <c>FindTriangle</c> and the
        /// walk's own containment test may name different triangles for it, and only one of the two
        /// abuts the neighbouring cell. Every seam test below therefore names a point that is
        /// unambiguously in one triangle: with cells 2 units wide, cell (gx, gz) spans
        /// <c>x ∈ [2gx, 2gx+2]</c> and its diagonal runs from <c>(2gx, 2gz)</c> to
        /// <c>(2gx+2, 2gz+2)</c>, so <c>z - 2gz &gt; x - 2gx</c> picks the upper-LEFT triangle
        /// (the one carrying the cell's left and top edges) and the reverse picks the lower-right.
        /// </summary>
        private static FPVector3 At(double x, double z)
            => new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));

        #region V-1 — the unfiltered call is the pre-change walk, bit for bit

        /// <summary>
        /// Absolute values captured from the PRE-CHANGE implementation (Release, CoreCLR) and
        /// pinned here, which is the only way "adding the parameter changed nothing" can be
        /// checked at all — comparing the new code against itself proves nothing.
        ///
        /// <para>Positions are raw FP64 so a fixed-point drift of one ULP fails rather than
        /// rounds away, and the visited SEQUENCE is pinned rather than just its length: the order
        /// is the BFS parent chain, and a reordering is exactly the kind of change that keeps the
        /// count and breaks determinism.</para>
        ///
        /// <para>Four cases, and the pair matters. <b>Reach</b> ends inside a triangle, so it
        /// returns through the endpoint short circuit and never examines a neighbour — on its own
        /// it would leave the wall fallthrough, the branch this change actually touches, untested.
        /// <b>Clamp</b>/<b>Clamp2</b>/<b>Long</b> all end on a wall: the corner degenerate, a
        /// lateral boundary edge with non-zero coordinates, and a diagonal long enough to exhaust
        /// <c>MOVE_MAX_QUEUE</c> before reaching its target.</para>
        /// </summary>
        // Targets are WORLD coordinates: CellCenter(gx, gz) == ((gx + 0.5) * 2, 0, (gz + 0.5) * 2),
        // so cell (2,2) is (5, 5) and cell (7,7) is (15, 15). The clamp cases point off-mesh.
        [TestCase("reach", 1, 1, 5.0, 5.0, 21474836480L, 21474836480L, 36, new[] { 18, 21, 36 })]
        [TestCase("clamp", 0, 0, -10.0, -10.0, 0L, 0L, 0, new[] { 0 })]
        [TestCase("clamp2", 3, 3, -10.0, 7.0, 8589934592L, 25769803776L, 50, new[] { 54, 55, 52, 53, 50 })]
        [TestCase("long", 0, 0, 15.0, 15.0, 42949672960L, 34359738368L, 57, new[] { 0, 3, 18, 21, 36, 39, 54, 57 })]
        public void UnfilteredWalk_MatchesThePreChangeOracle(
            string label, int fromGx, int fromGz, double toX, double toZ,
            long expectPosXRaw, long expectPosZRaw, int expectTri, int[] expectVisited)
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var query = new FPNavMeshQuery(mesh, null);

            FPVector3 from = NavAgentTestHelper.CellCenter(fromGx, fromGz);
            FPVector3 to = new FPVector3(FP64.FromDouble(toX), FP64.Zero, FP64.FromDouble(toZ));

            int startTri = query.FindTriangle(from.ToXZ());
            var visited = new int[64];

            var (pos, tri) = query.MoveAlongSurfaceWithVisited(
                from, to, startTri, NoFilter, Threshold, visited, out int count);

            Assert.AreEqual(expectPosXRaw, pos.x.RawValue, $"{label}: resultPos.x drifted");
            Assert.AreEqual(expectPosZRaw, pos.z.RawValue, $"{label}: resultPos.z drifted");
            Assert.AreEqual(expectTri, tri, $"{label}: resultTri changed");
            Assert.AreEqual(expectVisited.Length, count, $"{label}: visitedCount changed");
            for (int i = 0; i < expectVisited.Length; i++)
                Assert.AreEqual(expectVisited[i], visited[i], $"{label}: visited[{i}] changed");
        }

        #endregion

        #region V-2 — a masked-out neighbour is a wall, and FindPath says the same

        /// <summary>
        /// The headline claim, checked as one statement rather than two: the walk refuses the
        /// triangle AND the path refuses it, on the same mesh with the same mask. Checking only
        /// the walk would leave the promise — "a unit no longer slides across ground its own
        /// <c>FindPath</c> refused" — resting on an assumption about the other half.
        /// </summary>
        [Test]
        public void MaskedOutNeighbour_IsAWall_AndFindPathAgrees()
        {
            var mesh = NavAgentTestHelper.CreateTwoAreaFieldNavMesh(8, 4);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            // The two triangles that SHARE the seam edge at x = 8: the lower-right half of cell
            // (3,1) carries that cell's right edge, and the upper-left half of cell (4,1) carries
            // its left edge. One hop, so the queue cap plays no part.
            FPVector3 start = At(7.5, 2.5);    // cell (3,1), lower-right half — area 0
            FPVector3 target = At(8.5, 3.5);   // cell (4,1), upper-left half — area 3
            int startTri = query.FindTriangle(start.ToXZ());

            Assert.AreEqual(LeftArea, mesh.Triangles[startTri].areaMask, "fixture: start is not area 0");

            // Unfiltered, the two halves are one connected surface: the walk gets there. Asserted
            // on the AREA rather than a triangle index, which is the property under test and does
            // not depend on which half of the target cell the containment test picks.
            var (openPos, openTri) = query.MoveAlongSurface(
                start, target, startTri, NoFilter, Threshold);
            Assert.AreEqual(RightArea, mesh.Triangles[openTri].areaMask,
                "fixture: the halves are not edge-adjacent, so this proves nothing about the mask");
            Assert.AreEqual(target.x.RawValue, openPos.x.RawValue, "unfiltered walk did not reach the target");

            // Filtered to the left area, the seam is a wall.
            var (pos, tri) = query.MoveAlongSurface(start, target, startTri, LeftArea, Threshold);

            Assert.AreEqual(LeftArea, mesh.Triangles[tri].areaMask,
                "the walk ended on a triangle the mask forbids");
            Assert.Less(pos.x.RawValue, target.x.RawValue,
                "the walk was not clamped short of the masked-out half");

            // And the path refuses the same target under the same mask, for the same reason.
            bool found = pathfinder.FindPath(start, target, LeftArea, out _, out _);
            Assert.IsFalse(found, "FindPath accepted a target the walk treats as unreachable");
            Assert.AreEqual(1, pathfinder.DebugAreaMaskRejectedCount,
                "the refusal was not attributed to the area mask");
        }

        #endregion

        #region V-3 — the mask never gates the starting triangle

        /// <summary>
        /// The asymmetry with <c>FindPath</c>, and the reason for it: refusing a masked-out start
        /// would return <c>startPos</c> with nothing re-acquiring the triangle, which is the freeze
        /// <c>FPNavAgentOffMeshFreezeTests</c> already records for <c>startTri &lt; 0</c>. A game
        /// that narrows the mask under an agent's feet must get an agent that can walk out.
        /// </summary>
        [Test]
        public void StandingOnAMaskedOutTriangle_CanStillWalkOut()
        {
            var mesh = NavAgentTestHelper.CreateTwoAreaFieldNavMesh(8, 4);
            var query = new FPNavMeshQuery(mesh, null);

            // The start must ABUT the allowed half: "can walk out" needs an allowed neighbour to
            // walk out through. Deeper inside the forbidden half every neighbour is forbidden too,
            // and staying put is then the correct answer rather than a freeze — which is also why
            // this is the upper-LEFT half of cell (4,1), the only one of its two triangles that
            // carries the seam edge.
            FPVector3 start = At(8.5, 3.5);    // cell (4,1), upper-left half — forbidden, at the seam
            FPVector3 target = At(7.5, 2.5);   // cell (3,1), lower-right half — allowed
            int startTri = query.FindTriangle(start.ToXZ());
            Assert.AreEqual(RightArea, mesh.Triangles[startTri].areaMask, "fixture: start is not area 3");

            var (pos, tri) = query.MoveAlongSurface(start, target, startTri, LeftArea, Threshold);

            Assert.AreNotEqual(startTri, tri, "the agent froze on a masked-out starting triangle");
            Assert.Less(pos.x.RawValue, start.x.RawValue, "the agent did not move toward the allowed area");
            Assert.AreEqual(LeftArea, mesh.Triangles[tri].areaMask, "the walk did not end in the allowed area");
        }

        /// <summary>
        /// The other half of that contract: movement WITHIN a masked-out starting triangle is
        /// unfiltered, because the endpoint-inside-current-triangle short circuit runs before any
        /// neighbour is examined. Documented so it does not read as a leak.
        /// </summary>
        [Test]
        public void MovingInsideAMaskedOutStartingTriangle_IsUnfiltered()
        {
            var mesh = NavAgentTestHelper.CreateTwoAreaFieldNavMesh(8, 4);
            var query = new FPNavMeshQuery(mesh, null);

            FPVector3 start = At(8.5, 3.5);    // cell (4,1), upper-left half — forbidden
            int startTri = query.FindTriangle(start.ToXZ());
            Assert.AreEqual(RightArea, mesh.Triangles[startTri].areaMask, "fixture: start is not area 3");

            // A tiny step that stays inside the same triangle, moving further from the diagonal
            // so it cannot cross into the cell's other half.
            FPVector3 nudge = At(8.4, 3.6);
            Assert.AreEqual(startTri, query.FindTriangle(nudge.ToXZ()), "fixture: the nudge left the triangle");

            var (pos, tri) = query.MoveAlongSurface(start, nudge, startTri, LeftArea, Threshold);

            Assert.AreEqual(startTri, tri, "an intra-triangle step changed triangle");
            Assert.AreEqual(nudge.x.RawValue, pos.x.RawValue, "an intra-triangle step was clamped");
            Assert.AreEqual(nudge.z.RawValue, pos.z.RawValue, "an intra-triangle step was clamped");
        }

        #endregion

        #region V-4 — the visited chain respects the filter

        /// <summary>
        /// <c>outVisited</c> is a public output of a public overload, and it is the BFS parent
        /// chain — so it is the direct witness that the expansion guard held, one entry per
        /// triangle the walk actually accepted. The starting triangle is the documented exception,
        /// since the mask never gates it (V-3) and the chain always begins there.
        /// </summary>
        [Test]
        public void VisitedChain_ContainsNoForbiddenTriangle_ExceptTheStart()
        {
            var mesh = NavAgentTestHelper.CreateTwoAreaFieldNavMesh(8, 4);
            var query = new FPNavMeshQuery(mesh, null);

            FPVector3 start = At(8.5, 3.5);    // cell (4,1), upper-left half — forbidden, at the seam
            FPVector3 target = At(2.5, 3.5);   // cell (1,1), upper-left half — inside the allowed half
            int startTri = query.FindTriangle(start.ToXZ());
            var visited = new int[64];

            query.MoveAlongSurfaceWithVisited(
                start, target, startTri, LeftArea, Threshold, visited, out int count);

            Assert.Greater(count, 1, "the walk did not leave the starting triangle, so nothing is witnessed");
            Assert.AreEqual(startTri, visited[0], "the chain does not begin at the starting triangle");
            for (int i = 1; i < count; i++)
            {
                Assert.AreEqual(LeftArea, mesh.Triangles[visited[i]].areaMask,
                    $"visited[{i}] = triangle {visited[i]} is outside the requested mask");
            }
        }

        #endregion

        #region V-6 — areaMask == 0 is a wall even to an unfiltered call

        /// <summary>
        /// The one case where adding the parameter is NOT byte-neutral, pinned as documented
        /// behaviour rather than left to be discovered. No bit can intersect zero, so such a
        /// triangle is unreachable even at <c>~0</c>. The build pipeline never emits one (it
        /// writes <c>1 &lt;&lt; areaIndex</c>); the only producer is a caller writing through
        /// <c>TrianglesMutable</c>, which is what this test uses.
        /// </summary>
        [Test]
        public void ZeroAreaMaskTriangle_IsAWall_EvenUnfiltered()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(8);
            var query = new FPNavMeshQuery(mesh, null);

            FPVector3 start = At(2.5, 3.5);    // cell (1,1)
            FPVector3 target = At(4.5, 5.5);   // cell (2,2), upper-left half — the cell that gets zeroed
            int startTri = query.FindTriangle(start.ToXZ());
            int targetTri = query.FindTriangle(target.ToXZ());

            // Baseline: reachable while every triangle carries a non-zero mask.
            var (_, openTri) = query.MoveAlongSurface(start, target, startTri, NoFilter, Threshold);
            Assert.AreEqual(targetTri, openTri, "fixture: the target was not reachable to begin with");

            // Zero the whole target cell (both triangles) so there is no diagonal way in.
            for (int i = 0; i < mesh.TriangleCount; i++)
            {
                if (mesh.Triangles[i].centerXZ.x > FP64.FromDouble(4.0)
                    && mesh.Triangles[i].centerXZ.x < FP64.FromDouble(6.0)
                    && mesh.Triangles[i].centerXZ.y > FP64.FromDouble(4.0)
                    && mesh.Triangles[i].centerXZ.y < FP64.FromDouble(6.0))
                {
                    mesh.TrianglesMutable[i].areaMask = 0;
                }
            }
            Assert.AreEqual(0, mesh.Triangles[targetTri].areaMask, "fixture: the target mask was not zeroed");

            var (_, tri) = query.MoveAlongSurface(start, target, startTri, NoFilter, Threshold);
            Assert.AreNotEqual(0, mesh.Triangles[tri].areaMask,
                "a zero-areaMask triangle was entered by an unfiltered call");
        }

        #endregion

        #region V-8 — the agent path pins its constant

        /// <summary>
        /// The callsite gate, and it needs its own fixture: every other mesh helper builds one
        /// area, so <c>areaMask</c> is 1 everywhere and a callsite that passed a literal <c>1</c>
        /// instead of <see cref="FPNavAgentSystem.DEFAULT_AREA_MASK"/> would satisfy the query-level
        /// oracle, the <c>FindPath</c> tripwire and the whole regression suite — and then filter
        /// silently on a real multi-area mesh. On a two-area mesh the two constants disagree, so
        /// this is the one place that distinguishes them.
        ///
        /// <para>It pins the default: <see cref="FPNavAgentSystem.DEFAULT_AREA_MASK"/> admits every
        /// BAKED area, so the agent crosses the seam. The one area it excludes is the runtime's —
        /// <see cref="FPNavMeshAreas.BUILDING_AREA"/>, stamped onto retained building footprints —
        /// and that half is pinned from the other side: <c>FPNavMeshBuildingAreaTests</c> for the
        /// walk and <c>FPNavCrowdDiagnosticsTests.AgentSystem_TripsTheAreaMaskRejection_
        /// OnlyForARetainedFootprint</c> for <c>FindPath</c>.</para>
        /// </summary>
        [Test]
        public void AgentSystem_PassesTheDefaultMaskToTheWalk_WhichAdmitsEveryBakedArea()
        {
            var mesh = NavAgentTestHelper.CreateTwoAreaFieldNavMesh(8, 4);
            var query = new FPNavMeshQuery(mesh, null);
            var system = NavAgentTestHelper.CreateSystem(mesh, null);

            FPVector3 start = At(7.5, 2.5);    // cell (3,1), lower-right half — left of the seam
            int startTri = query.FindTriangle(start.ToXZ());
            Assert.AreEqual(LeftArea, mesh.Triangles[startTri].areaMask, "fixture: start is not area 0");

            var frame = NavAgentTestHelper.CreateFrameWithAgent(
                start, startTri, out EntityRef entity, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav, NavAgentTestHelper.CellCenter(6, 1));

            for (int tick = 1; tick <= 240; tick++)
            {
                system.Update(ref frame, entities, entities.Length, tick, NavAgentTestHelper.DT);
                ref var cur = ref frame.Get<NavAgentComponent>(entity);
                if (mesh.Triangles[cur.CurrentTriangleIndex].areaMask == RightArea)
                    Assert.Pass("the agent crossed into the other baked area, so the default mask admits it");
            }

            ref var final = ref frame.Get<NavAgentComponent>(entity);
            Assert.Fail("the agent never left area 0 — the move callsites filter a baked area. " +
                $"status={(FPNavAgentStatus)final.Status} tri={final.CurrentTriangleIndex} " +
                $"mask={mesh.Triangles[final.CurrentTriangleIndex].areaMask} pos={final.Position}");
        }

        #endregion
    }
}
