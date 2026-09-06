using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// A snapped destination and the triangle <c>FindPath</c> resolves it to must be the same
    /// triangle.
    ///
    /// <para>The filtered projection returns the closest point on a triangle EDGE, and a footprint
    /// boundary edge belongs to both the footprint triangle and the walkable one — with
    /// <c>PIT_EPSILON</c> tolerance, and with bit-identical interpolated heights, because the
    /// surface is continuous across an edge. So the point is genuinely ambiguous, and
    /// <c>FindPath</c> re-resolved it from scratch: <c>FindTriangle(xz, y)</c> updates only on a
    /// STRICTLY smaller height distance, so on a tie the first candidate in cell order wins, which
    /// is the lower triangle index. Where that was the footprint, the endpoint was refused by mask
    /// — silently, since that path only bumps a counter.</para>
    ///
    /// <para>Measured before the fix on the fixture below: of 729 clicks inside a retained
    /// footprint, 351 (48%) produced a destination the projection certified as walkable and
    /// <c>FindPath</c> then refused. Ambiguity itself was 100% — every projected point lay in two
    /// or more triangles.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshEndpointOwnershipTests
    {
        private const int Cells = 8;                 // 8x8 lattice cells of 2 units = 16x16 world
        private const double Bx = 8.0, Bz = 8.0;     // footprint centre
        private const int AgentMask = FPNavMeshAreas.DEFAULT_AGENT_MASK;

        private static FPVector2 At(double x, double z)
            => new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z));

        private static FPVector3 At3(double x, double z)
            => new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));

        private static FPNavMesh Retained() => NavAgentTestHelper.RebakeWithBuildings(
            NavAgentTestHelper.CreateOpenFieldNavMesh(Cells),
            NavAgentTestHelper.Building(Bx, Bz, retain: true));

        private static bool IsBuilding(FPNavMesh mesh, int tri)
            => tri >= 0 && mesh.Triangles[tri].areaMask == FPNavMeshAreas.BUILDING_MASK;

        #region The sweep: if the projection succeeds, FindPath must succeed

        /// <summary>
        /// The gate that had to exist. The three-point net it replaces passed on all three by luck
        /// — the walkable triangle happened to come first in cell order there. A sweep is the
        /// only shape that catches it, because whether a given boundary point
        /// fails is fixed by triangle index order and therefore deterministic PER POSITION.
        /// </summary>
        [Test]
        public void EverySnappedDestinationIsOneFindPathAccepts()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);
            FPVector3 start = At3(3.0, 3.0);

            int snapped = 0, ambiguous = 0, refused = 0, toothed = 0;

            for (double dx = -half - 0.2; dx <= half + 0.2; dx += 0.13)
            {
                for (double dz = -half - 0.2; dz <= half + 0.2; dz += 0.13)
                {
                    FPVector2 click = At(Bx + dx, Bz + dz);
                    if (query.FindPassableTriangle(click, AgentMask) >= 0)
                        continue;                       // not on the footprint — nothing to snap

                    FPVector2 point = query.ProjectToPassable(
                        click, FP64.FromInt(4), AgentMask, out int certified);
                    if (certified < 0)
                        // Out of reach: the projection reports failure there, which its own test
                        // covers.
                        continue;
                    snapped++;

                    if (CountContaining(mesh, point) > 1)
                        ambiguous++;

                    FP64 y = query.SampleHeight(point, certified);
                    var dest = new FPVector3(point.x, y, point.y);

                    // The teeth. This is a property of the FIXTURE, not of the fix: the unfiltered
                    // lookup is deliberately unchanged, so this count is the same before and after.
                    // Without it the sweep could pass on a mesh whose numbering happens to favour
                    // the walkable side everywhere — exactly how the three-point net passed.
                    if (IsBuilding(mesh, query.FindTriangle(dest.ToXZ(), dest.y)))
                        toothed++;

                    if (!pathfinder.FindPath(start, dest, AgentMask, out _, out int len) || len <= 0)
                        refused++;
                }
            }

            TestContext.Out.WriteLine(
                $"snapped={snapped} ambiguous={ambiguous} toothed={toothed} refused={refused} "
                + $"areaMaskRejected={pathfinder.DebugAreaMaskRejectedCount} "
                + $"blockedEndpoint={pathfinder.DebugBlockedEndpointCount}");

            Assert.Greater(snapped, 100, "fixture: the sweep must actually produce snapped points");
            Assert.Greater(toothed, 0,
                "fixture: at least one snapped point must resolve to the FOOTPRINT under the "
                + "unfiltered lookup, or this sweep cannot fail and proves nothing");
            Assert.AreEqual(snapped, ambiguous,
                "fixture check: EVERY projected point should lie in two or more triangles — that is "
                + "what makes the endpoint ambiguous in the first place");
            Assert.AreEqual(0, refused,
                "DEFECT: the projection certified these points as walkable and FindPath refused them");
        }

        /// <summary>
        /// The same sweep on a surface that is not flat. Recorded because the first draft of this
        /// plan claimed the tie needs equal heights and so could not happen on a slope — the
        /// opposite is true: the surface is continuous across a shared edge, so the interpolated
        /// heights are bit-identical there whatever the slope.
        /// </summary>
        [Test]
        public void EverySnappedDestinationIsOneFindPathAccepts_OnASlopedSurface()
        {
            // One quad, y = x/2, split into two triangles sharing the diagonal. Area 3 rather than
            // BUILDING_AREA because the build pipeline refuses area 1 as authored input.
            //
            // The FORBIDDEN half is listed FIRST on purpose. Whether an ambiguous edge point
            // resolves to the walkable or the forbidden neighbour is decided by triangle index
            // order, so a fixture that puts the forbidden half second cannot fail at all — the
            // walkable triangle wins the tie by luck. The teeth assertion below refuses to let this
            // gate go vacuous if canonicalisation ever renumbers it.
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(8), FP64.FromInt(4), FP64.Zero),
                new FPVector3(FP64.FromInt(8), FP64.FromInt(4), FP64.FromInt(8)),
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(8)),
            };
            const int ForbiddenArea = 3;
            const int GroundOnly = ~(1 << ForbiddenArea);
            var mesh = FPNavMeshBuildPipeline.Build(
                vertices, new[] { 0, 1, 2, 0, 2, 3 },
                new[] { ForbiddenArea, 0 }, 16.0, null, bakeAgentRadius: 0.5);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            bool IsForbidden(int tri)
                => tri >= 0 && mesh.Triangles[tri].areaMask == (1 << ForbiddenArea);

            // The forbidden half is the z < x side (triangle v0-v1-v2); walk from the other one.
            var start = new FPVector3(FP64.FromDouble(1.0), FP64.FromDouble(0.5), FP64.FromDouble(6.0));
            Assert.IsFalse(IsForbidden(query.FindTriangle(start.ToXZ(), start.y)),
                "fixture: the start must be on the walkable half");

            int snapped = 0, toothed = 0, refused = 0;
            for (double t = 0.4; t < 7.2; t += 0.11)
            {
                FPVector2 click = At(t + 0.6, t);           // z < x → forbidden half
                if (query.FindPassableTriangle(click, GroundOnly) >= 0)
                    continue;

                FPVector2 point = query.ProjectToPassable(
                    click, FP64.FromInt(4), GroundOnly, out int certified);
                if (certified < 0)
                    continue;
                snapped++;

                FP64 y = query.SampleHeight(point, certified);
                var dest = new FPVector3(point.x, y, point.y);
                if (IsForbidden(query.FindTriangle(dest.ToXZ(), dest.y)))
                    toothed++;

                if (!pathfinder.FindPath(start, dest, GroundOnly, out _, out int len) || len <= 0)
                    refused++;
            }

            TestContext.Out.WriteLine($"slope: snapped={snapped} toothed={toothed} refused={refused}");
            Assert.Greater(snapped, 20, "fixture: the sweep must produce snapped points on the slope");
            Assert.Greater(toothed, 0,
                "fixture: the unfiltered lookup must resolve at least one snapped point to the "
                + "forbidden half, or this gate cannot fail");
            Assert.AreEqual(0, refused, "DEFECT: same ambiguity, on a sloped surface");
        }

        #endregion

        #region The Y disambiguation must survive

        /// <summary>
        /// The gate that separates the fix from two wrong ones. On stacked floors a destination on
        /// the forbidden UPPER floor must still be refused: there is no walkable interpretation AT
        /// THAT HEIGHT, and the walkable floor below is a different place, not a reinterpretation.
        ///
        /// <para>Both naive rules fail here. Reusing <c>FindPassableTriangle</c> ignores y outright
        /// and returns the lower floor; "nearest passable" also returns the lower floor because it
        /// is the only passable candidate. The rule that works restricts to the candidates that
        /// MINIMISE the height distance first, and only breaks ties inside that set.</para>
        /// </summary>
        [Test]
        public void ADestinationOnTheForbiddenUpperFloorIsNotReroutedDownstairs()
        {
            // Area 3 upstairs, and a mask that omits it. Not BUILDING_AREA: the build pipeline
            // refuses area 1 as authored input, so forbidden ground straight out of the bake needs
            // another index (the same trick CreateTwoAreaFieldNavMesh uses).
            const int UpperArea = 3;
            const int GroundOnly = ~(1 << UpperArea);
            var mesh = NavAgentTestHelper.CreateStackedFloorsNavMesh(
                Cells, floorGap: 5.0, upperArea: UpperArea);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            FPVector2 xz = At(5.0, 5.0);
            var upstairs = new FPVector3(xz.x, FP64.FromInt(5), xz.y);
            var downstairs = new FPVector3(xz.x, FP64.Zero, xz.y);

            int upper = query.FindTriangle(xz, FP64.FromInt(5));
            int lower = query.FindTriangle(xz, FP64.Zero);
            Assert.AreNotEqual(upper, lower, "fixture: the two floors must be distinct triangles");
            Assert.AreEqual(1 << UpperArea, mesh.Triangles[upper].areaMask,
                "fixture: the upper floor must be the forbidden area");
            Assert.AreEqual(1, mesh.Triangles[lower].areaMask,
                "fixture: the lower floor must be ordinary ground (area 0)");

            // What the naive rules would have answered, pinned so the gate says WHY it exists.
            Assert.AreEqual(lower, query.FindPassableTriangle(xz, GroundOnly),
                "fixture: a y-blind passable lookup answers the LOWER floor — that is the wrong answer "
                + "for a destination upstairs, and it is the answer both rejected rules give");

            Assert.IsFalse(
                pathfinder.FindPath(downstairs, upstairs, GroundOnly, out _, out _),
                "a destination on the forbidden upper floor must be refused, not rerouted to the "
                + "walkable floor below it");
            Assert.AreEqual(1, pathfinder.DebugAreaMaskRejectedCount,
                "and the refusal must still be attributed to the area mask");
        }

        /// <summary>
        /// The predicate a TOOL must use to decide "is this click usable as clicked". Both editor
        /// simulators asked <c>FindPassableTriangle</c>, which is y-blind, and then handed the
        /// click's own y straight through — so on this fixture they accepted a destination on the
        /// forbidden upper floor because walkable ground happened to sit beneath it, and the
        /// snap that method exists to perform was skipped entirely. FindPath then refused it by
        /// mask, producing the "it has a destination and will not move" symptom.
        ///
        /// <para>This is the disagreement itself, pinned. The tool code lives in editor assemblies
        /// no test project compiles, so this is what dotnet can hold: the two predicates give
        /// OPPOSITE answers for the same point, and only one of them agrees with the pathfinder.</para>
        /// </summary>
        [Test]
        public void TheEndpointPassabilityPredicate_DisagreesWithTheYBlindOne_Upstairs()
        {
            const int UpperArea = 3;
            const int GroundOnly = ~(1 << UpperArea);
            var mesh = NavAgentTestHelper.CreateStackedFloorsNavMesh(
                Cells, floorGap: 5.0, upperArea: UpperArea);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            FPVector2 xz = At(5.0, 5.0);
            FP64 upstairsY = FP64.FromInt(5);

            // The y-blind lookup says yes — it found the walkable floor BELOW the click.
            Assert.GreaterOrEqual(query.FindPassableTriangle(xz, GroundOnly), 0,
                "fixture: the y-blind lookup must accept this point, otherwise the two predicates "
                + "are not being made to disagree and this gate proves nothing");

            // The endpoint predicate says no — the triangle AT that height is forbidden.
            Assert.AreEqual(-1, query.FindPassableTriangleForEndpoint(xz, upstairsY, GroundOnly),
                "the endpoint the engine would pick at this height is forbidden, so a tool must "
                + "not accept the click as clicked");

            // And the pathfinder sides with the endpoint predicate, which is the whole point.
            var downstairs = new FPVector3(xz.x, FP64.Zero, xz.y);
            var upstairs = new FPVector3(xz.x, upstairsY, xz.y);
            Assert.IsFalse(pathfinder.FindPath(downstairs, upstairs, GroundOnly, out _, out _),
                "the pathfinder refuses it, so the predicate that accepted it was the wrong one");

            // Downstairs the two agree — the fix must not reject ordinary ground.
            Assert.GreaterOrEqual(query.FindPassableTriangleForEndpoint(xz, FP64.Zero, GroundOnly), 0,
                "a destination on the walkable floor stays acceptable");
        }

        /// <summary>
        /// Revision 2's behaviour, pinned to a FIXED COORDINATE rather than to a scenario.
        ///
        /// <para><b>Why a coordinate.</b> Every other nav hash test in this suite compares A to B
        /// inside one process, which cannot see a change that moves both sides equally — exactly
        /// what a build-to-build behaviour change does. A literal point with a literal expected
        /// answer is the only shape that survives that: if a future edit moves it, this fails, and
        /// <c>NAV_BEHAVIOUR_REVISION</c> must move with it.</para>
        ///
        /// <para>The point sits ON a retained footprint boundary — the walkable and the stamped
        /// triangle share that edge at a bit-identical interpolated height, so both contain it and
        /// the plain lookup answers whichever has the lower index. Revision 1 could therefore
        /// answer the forbidden one; revision 2 answers the usable one.</para>
        /// </summary>
        [Test]
        public void OnAStampBoundary_TheEndpointResolvesToTheWalkableSide()
        {
            FPNavMesh retained = Retained();
            var query = new FPNavMeshQuery(retained, null);
            int mask = FPNavAgentSystem.DEFAULT_AREA_MASK;

            // The footprint's west edge, at a fixed point on it.
            double half = NavAgentTestHelper.ExpandedBuildingHalf(retained);
            FPVector2 onEdge = At(Bx - half, Bz + 0.5);

            int endTri = query.FindTriangleForEndpoint(onEdge, FP64.Zero, mask);
            Assert.GreaterOrEqual(endTri, 0, "fixture: the boundary point must be on the mesh");
            Assert.AreNotEqual(0, retained.Triangles[endTri].areaMask & mask,
                "a point on a stamp boundary belongs to both sides at the same height — the endpoint "
                + "rule must answer the side the agent can actually use. If this fails after a "
                + "navigation change, bump FPNavAgentSystem.NAV_BEHAVIOUR_REVISION with it");
        }

        /// <summary>
        /// The tie-break must never move the answer ACROSS floors — it exists for two triangles on
        /// the SAME surface (a stamped footprint boundary), where the two candidates are the same
        /// place seen twice.
        ///
        /// <para><b>Why a midpoint.</b> <c>dist</c> is <c>|surfaceY - agentY|</c> and constrains
        /// nothing about <c>surfaceY</c>, so two floors tie whenever the agent's y is the exact
        /// midpoint between them. At that height the plain lookup answers the first candidate in
        /// cell order; a tie-break that only checks passability then hands back the OTHER FLOOR,
        /// which is a different place, not a reinterpretation.</para>
        ///
        /// <para>The mask omits area 0 (ordinary ground) so the plain answer is the forbidden one
        /// and the tie-break has something to move — the mirror of
        /// <c>ADestinationOnTheForbiddenUpperFloorIsNotReroutedDownstairs</c>, which forbids
        /// upstairs. The assertion is equality with the plain lookup rather than a named triangle,
        /// so it does not depend on which floor cell order happens to list first.</para>
        /// </summary>
        [Test]
        public void TheTieBreakDoesNotCrossFloorsAtAnEquidistantHeight()
        {
            const int UpperArea = 3;
            const int GroundOnly = ~1;               // omit area 0 — the LOWER floor is forbidden here
            const double FloorGap = 5.0;
            var mesh = NavAgentTestHelper.CreateStackedFloorsNavMesh(
                Cells, floorGap: FloorGap, upperArea: UpperArea);
            var query = new FPNavMeshQuery(mesh, null);

            FPVector2 xz = At(5.0, 5.0);
            FP64 midpoint = FP64.FromDouble(FloorGap / 2.0);

            int upper = query.FindTriangle(xz, FP64.FromDouble(FloorGap));
            int lower = query.FindTriangle(xz, FP64.Zero);
            Assert.AreNotEqual(upper, lower, "fixture: the two floors must be distinct triangles");

            int plain = query.FindTriangle(xz, midpoint);
            Assert.AreEqual(0, mesh.Triangles[plain].areaMask & GroundOnly,
                "fixture: the plain answer at the midpoint must be the FORBIDDEN floor, or the "
                + "tie-break has nothing to move and this gate is vacuous");

            Assert.AreEqual(plain, query.FindTriangleForEndpoint(xz, midpoint, GroundOnly),
                "an equidistant height is not a tie on ONE surface — the two candidates are "
                + "different floors, and swapping to the passable one silently relocates the "
                + "destination a storey away from where it was asked for");
        }

        #endregion

        #region Where the frame hash can move, stated as an invariant

        /// <summary>
        /// The scope of the behaviour change, pinned directly instead of inferred from scenarios.
        ///
        /// <para>The endpoint rule only ever differs from the plain lookup when the tied candidates
        /// disagree about passability. Where every triangle containing the point is usable — all of
        /// ordinary ground, every interior edge on it included — "first passable" is "first", so the
        /// answer is bit-identical and nothing downstream can move. That is what bounds the hash
        /// movement to meshes carrying forbidden ground, and to destinations on its boundary.</para>
        /// </summary>
        [Test]
        public void OnGroundWithNothingForbidden_TheEndpointRuleIsTheOldLookup()
        {
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var query = new FPNavMeshQuery(mesh, null);

            int compared = 0, onEdge = 0;
            for (double x = 0.25; x < Cells * 2.0; x += 0.25)
            {
                for (double z = 0.25; z < Cells * 2.0; z += 0.25)
                {
                    FPVector2 p = At(x, z);
                    int plain = query.FindTriangle(p, FP64.Zero);
                    int endpoint = query.FindTriangleForEndpoint(p, FP64.Zero, AgentMask);
                    compared++;
                    if (CountContaining(mesh, p) > 1)
                        onEdge++;

                    Assert.AreEqual(plain, endpoint,
                        $"the endpoint rule changed the answer at ({x:F2},{z:F2}) on ground where "
                        + "nothing is forbidden — the hash-movement bound does not hold");
                }
            }

            TestContext.Out.WriteLine($"compared={compared} onSharedEdge={onEdge}");
            Assert.Greater(onEdge, 0,
                "fixture: the sweep must hit shared edges, or it is not testing the tie at all");
        }

        /// <summary>
        /// And the other half of the bound: on a mesh that DOES carry forbidden ground the two
        /// answers differ — and only where the tie genuinely holds both kinds.
        ///
        /// <para>Swept over PROJECTED points rather than a lattice, because that is the only way to
        /// land on a boundary edge: the ambiguity band is the edge plus/minus <c>PIT_EPSILON</c>
        /// (1e-4), which a lattice of any practical step steps straight over. A first attempt on a
        /// 0.25 grid found zero differences across 3969 points — the case exists, the grid just
        /// cannot see it.</para>
        /// </summary>
        [Test]
        public void OnARetainedMesh_TheTwoLookupsDifferOnlyWhereTheTieHoldsBothKinds()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);

            int examined = 0, differed = 0;
            for (double dx = -half - 0.2; dx <= half + 0.2; dx += 0.13)
            {
                for (double dz = -half - 0.2; dz <= half + 0.2; dz += 0.13)
                {
                    FPVector2 click = At(Bx + dx, Bz + dz);
                    if (query.FindPassableTriangle(click, AgentMask) >= 0)
                        continue;

                    FPVector2 p = query.ProjectToPassable(
                        click, FP64.FromInt(4), AgentMask, out int certified);
                    if (certified < 0)
                        continue;
                    examined++;

                    FP64 y = query.SampleHeight(p, certified);
                    int plain = query.FindTriangle(p, y);
                    int endpoint = query.FindTriangleForEndpoint(p, y, AgentMask);
                    if (plain == endpoint)
                    {
                        Assert.IsFalse(IsBuilding(mesh, endpoint),
                            "the answers agree on forbidden ground — the tie should have been broken");
                        continue;
                    }
                    differed++;

                    // A difference means the tied set held both kinds. The old answer must be the
                    // unusable one and the new answer a usable one — never the other way round.
                    Assert.IsTrue(IsBuilding(mesh, plain),
                        "the answers differ but the OLD one was already usable ground");
                    Assert.IsFalse(IsBuilding(mesh, endpoint),
                        "the answers differ but the NEW one is forbidden ground");
                    Assert.Greater(CountContaining(mesh, p), 1,
                        "the answer changed at a point only one triangle contains — that cannot be a tie");
                }
            }

            TestContext.Out.WriteLine($"examined={examined} differed={differed}");
            Assert.Greater(differed, 0, "fixture: the retained mesh must produce differences");
        }

        #endregion

        #region The endpoint only; the start keeps its own rules

        /// <summary>
        /// The change is deliberately asymmetric. The start is exempt from the mask check on
        /// purpose (a unit with a building dropped on it must still be given a route out) and that
        /// exemption is observable through <c>DebugMaskedStartCount</c>; resolving an ambiguous
        /// start toward walkable ground would silence it at boundary positions.
        ///
        /// <para><c>isBlocked</c> is the same story with a harder edge — for the start it is not an
        /// exemption but an outright refusal, so an agent standing on blocked ground gets no path at
        /// all. Recovering an agent from blocked ground is a separate concern, not this one.</para>
        /// </summary>
        [Test]
        public void TheStartIsUntouched_AndItsCounterStillFires()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);

            // A start ON the footprint boundary that the UNFILTERED lookup resolves to the
            // footprint — searched for rather than assumed, because which side of an ambiguous edge
            // wins is fixed by triangle index order and so depends on the mesh's numbering.
            FPVector3 from = default;
            bool found = false;
            for (double d = 0.05; d < half && !found; d += 0.05)
            {
                foreach (var probe in new[]
                {
                    At(Bx, Bz - half + d), At(Bx, Bz + half - d),
                    At(Bx - half + d, Bz), At(Bx + half - d, Bz),
                })
                {
                    FPVector2 edge = query.ProjectToPassable(
                        probe, FP64.FromInt(4), AgentMask, out int certified);
                    if (certified < 0 || CountContaining(mesh, edge) <= 1)
                        continue;
                    int standingOn = query.FindTriangle(edge, FP64.Zero);
                    if (!IsBuilding(mesh, standingOn))
                        continue;                       // this edge resolves to walkable ground
                    from = new FPVector3(edge.x, query.SampleHeight(edge, standingOn), edge.y);
                    found = true;
                    break;
                }
            }
            Assert.IsTrue(found,
                "fixture: no ambiguous boundary position resolved to the footprint — the start-side "
                + "case this gate is about does not exist on this mesh");

            Assert.IsTrue(pathfinder.FindPath(from, At3(3.0, 3.0), AgentMask, out _, out int len),
                "the start is exempt, so a route out must still be produced");
            Assert.Greater(len, 0, "and it must be a real corridor");
            Assert.AreEqual(1, pathfinder.DebugMaskedStartCount,
                "the exemption must stay OBSERVABLE — resolving the start toward walkable ground "
                + "would silence this counter at exactly the positions it exists to report");
        }

        #endregion

        #region The walk actually reaches a boundary destination

        /// <summary>
        /// A corridor is not movement. The destination sits ON the footprint's edge, so the walk has
        /// to arrive at a point it may not step past — checking only that <c>FindPath</c> succeeded
        /// would miss a walk that stalls at the last portal or slides into the footprint.
        /// </summary>
        [Test]
        public unsafe void AnAgentWalksToTheBoundaryDestinationAndStops()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);

            FPVector2 point = query.ProjectToPassable(
                At(Bx, Bz - half + 0.2), FP64.FromInt(4), AgentMask, out int certified);
            Assert.GreaterOrEqual(certified, 0, "fixture: the projection should reach the boundary");
            var dest = new FPVector3(point.x, query.SampleHeight(point, certified), point.y);

            var start = new FPVector3(FP64.FromDouble(3.0), FP64.Zero, FP64.FromDouble(3.0));
            FPNavAgentSystem system = NavAgentTestHelper.CreateSystem(mesh, null);
            Frame frame = NavAgentTestHelper.CreateFrameWithAgent(
                start, query.FindTriangle(start.ToXZ(), start.y),
                out EntityRef entity, out EntityRef[] entities);

            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav, dest);

            for (int tick = 1; tick <= 600; tick++)
                system.Update(ref frame, entities, 1, tick, NavAgentTestHelper.DT);

            ref var after = ref frame.Get<NavAgentComponent>(entity);
            TestContext.Out.WriteLine(
                $"status={(FPNavAgentStatus)after.Status} pos={after.Position} dest={dest} "
                + $"tri={after.CurrentTriangleIndex}");

            Assert.AreEqual((byte)FPNavAgentStatus.Arrived, after.Status,
                "the agent must actually arrive at a destination on the footprint's edge");
            Assert.IsFalse(IsBuilding(mesh, after.CurrentTriangleIndex),
                "and it must not have walked INTO the footprint to get there");
        }

        #endregion

        #region The bot's pattern, reproduced

        /// <summary>
        /// The runtime half of the defect. <c>BotFSMHelper.SnapDestination</c> cannot be called from
        /// here — it lives in a Brawler Unity assembly this project does not reference — so this
        /// reproduces its shape instead: project with the plan mask, resample y at the snapped xz,
        /// hand the result to <c>FindPath</c> with the same mask.
        /// </summary>
        [Test]
        public void TheBotsSnapPatternProducesDestinationsFindPathAccepts()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);
            FPVector3 self = At3(2.0, 2.0);

            int tried = 0;
            foreach (double d in new[] { 0.0, 0.4, 0.8, 1.2, 1.6 })
            {
                foreach (var desired in new[]
                {
                    At3(Bx + d, Bz), At3(Bx - d, Bz), At3(Bx, Bz + d), At3(Bx, Bz - d),
                })
                {
                    // SnapDestination's shape, verbatim in structure.
                    FPVector2 desiredXZ = new FPVector2(desired.x, desired.z);
                    if (query.FindPassableTriangle(desiredXZ, AgentMask) >= 0)
                        continue;
                    FPVector2 snappedXZ = query.ProjectToPassable(
                        desiredXZ, FP64.FromDouble(1.5), AgentMask, out int triIdx);
                    if (triIdx < 0)
                        continue;
                    var destination = new FPVector3(
                        snappedXZ.x, query.SampleHeight(snappedXZ, triIdx), snappedXZ.y);
                    tried++;

                    Assert.IsTrue(
                        pathfinder.FindPath(self, destination, AgentMask, out _, out int len) && len > 0,
                        $"the bot committed to a destination its own FindPath refuses (desired "
                        + $"{desired.x.ToDouble():F2},{desired.z.ToDouble():F2})");
                }
            }

            Assert.Greater(tried, 5, "fixture: the loop must actually exercise snapped destinations");
        }

        #endregion

        private static int CountContaining(FPNavMesh mesh, FPVector2 p)
        {
            int n = 0;
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                ref readonly var tri = ref mesh.Triangles[i];
                if (FPNavMeshQuery.PointInTriangle2D(
                        p,
                        mesh.Vertices[tri.v0].ToXZ(),
                        mesh.Vertices[tri.v1].ToXZ(),
                        mesh.Vertices[tri.v2].ToXZ()))
                    n++;
            }
            return n;
        }
    }
}
