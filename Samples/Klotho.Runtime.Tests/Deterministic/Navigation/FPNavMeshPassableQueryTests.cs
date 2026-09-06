using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The "nearest walkable point" family now knows about passability.
    ///
    /// <para><c>FindPath</c> and <c>MoveAlongSurface</c> have always filtered; these three did not.
    /// Once retain shipped that stopped being theoretical — the rebaker stamps a retained footprint
    /// <c>BUILDING_MASK</c> exclusively, so the footprint is ON-mesh and an unfiltered lookup hands
    /// it back as a usable answer. A bot then commits to a destination its own <c>FindPath</c> will
    /// refuse, and an agent standing inside one is never pushed out.</para>
    ///
    /// <para>The new members are separate names rather than parameters, and the reason is in the
    /// gates below: the UNFILTERED lookup is the right answer for three engine callers, and one of
    /// them (<c>FindPath</c>) attributes its refusals to counters that only work if the endpoints
    /// come back found. The two tests under <i>what this change must NOT touch</i> are the nets for
    /// that, and they are asserted BEFORE anything else because they pin what must not move.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshPassableQueryTests
    {
        private const int Cells = 8;                 // 8x8 cells of 2 units = 16x16 world
        private const double Bx = 8.0, Bz = 8.0;     // footprint centre
        private const int AgentMask = FPNavMeshAreas.DEFAULT_AGENT_MASK;

        private static FPVector2 At(double x, double z)
            => new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z));

        private static FPVector3 At3(double x, double z)
            => new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));

        private static FPNavMesh Field() => NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);

        private static FPNavMesh Retained() => NavAgentTestHelper.RebakeWithBuildings(
            Field(), NavAgentTestHelper.Building(Bx, Bz, retain: true));

        private static bool IsBuilding(FPNavMesh mesh, int tri)
            => tri >= 0 && mesh.Triangles[tri].areaMask == FPNavMeshAreas.BUILDING_MASK;

        #region The unfiltered members are bit-identical to the pre-change code

        /// <summary>
        /// Absolute values captured from the PRE-CHANGE implementation (Release, CoreCLR).
        ///
        /// <para>The <b>zeroed</b> case is the tooth: a triangle whose <c>areaMask</c> is 0 shares
        /// no bit with any mask, so an implementation that made the unfiltered members delegate
        /// with <c>~0</c> would stop finding it. The contract is that they skip the predicate
        /// entirely, and this row is the only input that can tell the two apart.</para>
        /// </summary>
        [TestCase("field", 0, 3.0, 3.0, 18, 12884901888L, 18)]
        [TestCase("field", 0, 8.0, 8.0, 54, 34359738368L, 54)]
        [TestCase("field", 0, -5.0, -5.0, -1, 0L, 0)]
        [TestCase("retained", 1, 8.0, 8.0, 73, 34359738368L, 73)]
        [TestCase("retained", 1, 3.0, 3.0, 4, 12884901888L, 4)]
        [TestCase("zeroed", 2, 3.0, 3.0, 18, 12884901888L, 18)]
        public void UnfilteredMembers_MatchThePreChangeOracle(
            string label, int kind, double x, double z,
            int expectFind, long expectClosestXRaw, int expectClosestTri)
        {
            FPNavMesh mesh;
            if (kind == 0) mesh = Field();
            else if (kind == 1) mesh = Retained();
            else
            {
                mesh = Field();
                var probe = new FPNavMeshQuery(mesh, null);
                int zt = probe.FindTriangle(At(3.0, 3.0));
                mesh.TrianglesMutable[zt].areaMask = 0;
                Assert.AreEqual(0, mesh.Triangles[zt].areaMask, "fixture: the mask was not zeroed");
            }

            var query = new FPNavMeshQuery(mesh, null);
            FPVector2 xz = At(x, z);

            Assert.AreEqual(expectFind, query.FindTriangle(xz), $"{label}: FindTriangle drifted");
            Assert.AreEqual(expectFind, query.FindTriangle(xz, FP64.Zero),
                $"{label}: FindTriangle(agentY) drifted");

            FPVector2 c = query.ClosestPointOnNavMesh(xz, out int ct);
            Assert.AreEqual(expectClosestXRaw, c.x.RawValue, $"{label}: ClosestPoint.x drifted");
            Assert.AreEqual(expectClosestTri, ct, $"{label}: ClosestPoint tri drifted");
        }

        #endregion

        #region What this change must NOT touch

        /// <summary>
        /// <c>FindPath</c>'s attribution survives. A destination on a retained footprint must be
        /// refused as an AREA MASK rejection, not as "off-mesh" — which is what would happen if the
        /// endpoint lookup were filtered, and it would take <c>DebugBlockedEndpointCount</c> with
        /// it. Both counters were shipped by one commit; filtering the lookup reverts that commit.
        /// </summary>
        [Test]
        public void FindPathAttribution_IsUnchanged()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);

            FPVector3 start = At3(3.0, 3.0);
            FPVector3 dest = At3(Bx, Bz);
            Assert.IsTrue(IsBuilding(mesh, query.FindTriangle(dest.ToXZ())),
                "fixture: the destination is not on the retained footprint");

            bool found = pathfinder.FindPath(start, dest, AgentMask, out _, out _);

            Assert.IsFalse(found, "a destination inside the footprint must be refused");
            Assert.AreEqual(1, pathfinder.DebugAreaMaskRejectedCount,
                "the refusal was not attributed to the area mask — the endpoint lookup is filtering");
            Assert.AreEqual(0, pathfinder.DebugBlockedEndpointCount, "wrong attribution");
        }

        /// <summary>
        /// The post-swap reseed keeps giving an agent inside a footprint a VALID triangle. If it
        /// filtered, the agent would get -1, and then the walk's escape clause — the thing that
        /// lets it leave forbidden ground — cannot fire at all, because it needs a current
        /// triangle to be refused FROM.
        /// </summary>
        [Test]
        public void TheLookupUsedByReseed_StillFindsForbiddenGround()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);

            int tri = query.FindTriangle(At(Bx, Bz), FP64.Zero);

            Assert.GreaterOrEqual(tri, 0, "the reseed lookup lost the agent's triangle");
            Assert.IsTrue(IsBuilding(mesh, tri), "fixture: that triangle is not the footprint");
        }

        #endregion

        #region The destination-snap pattern refuses the building

        /// <summary>
        /// The headline, expressed as the two calls a destination snap makes: the filtered lookup
        /// refuses the footprint, and the filtered projection hands back somewhere the agent's own
        /// <c>FindPath</c> accepts. Checking both halves in one test is the point — the promise is
        /// that the two judgements agree.
        /// </summary>
        [Test]
        public void DestinationSnap_RefusesTheFootprint_AndFindPathAgrees()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            FPVector2 desired = At(Bx, Bz);

            Assert.AreEqual(-1, query.FindPassableTriangle(desired, AgentMask),
                "the filtered lookup accepted a point inside the footprint");

            FPVector2 snapped = query.ProjectToPassable(
                desired, FP64.FromDouble(4.0), AgentMask, out int tri);

            Assert.GreaterOrEqual(tri, 0, "the projection found nothing within reach");
            Assert.IsFalse(IsBuilding(mesh, tri), "the projection landed on the footprint");
            Assert.IsTrue(snapped.x.RawValue != desired.x.RawValue
                          || snapped.y.RawValue != desired.y.RawValue,
                "the projection returned the desired point unchanged");
            Assert.GreaterOrEqual(query.FindPassableTriangle(snapped, AgentMask), 0,
                "the point the projection chose is not itself passable");

            bool found = pathfinder.FindPath(
                At3(3.0, 3.0), new FPVector3(snapped.x, FP64.Zero, snapped.y),
                AgentMask, out _, out _);
            Assert.IsTrue(found, "FindPath refused the point the snap chose — the two disagree");
        }

        #endregion

        #region The re-snap pattern

        /// <summary>
        /// The asymmetry, assembled by the caller: ask the UNFILTERED lookup where you are, and
        /// only when that says nothing ask the filtered projection where to go. Standing inside a
        /// footprint the first question answers, so the position is left alone — which is what lets
        /// the walk's escape clause carry the agent out on its own instead of teleporting it.
        /// </summary>
        [Test]
        public void ReSnapPattern_LeavesAnAgentInsideAFootprintWhereItIs()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            FPVector2 inside = At(Bx, Bz);

            int tri = query.FindTriangle(inside);          // unfiltered: where am I
            FPVector2 result = tri >= 0
                ? inside
                : query.ClosestPassablePoint(inside, AgentMask, out tri);

            Assert.IsTrue(IsBuilding(mesh, tri), "the pattern did not report the footprint");
            Assert.AreEqual(inside.x.RawValue, result.x.RawValue, "the agent was moved");
            Assert.AreEqual(inside.y.RawValue, result.y.RawValue, "the agent was moved");
        }

        /// <summary>
        /// And the other direction: the fallback scan — the loop that actually lands a snap on
        /// something — must never choose forbidden ground. Filtering only the lookup and leaving
        /// this loop open is the half that would keep pulling callers in.
        /// </summary>
        [Test]
        public void TheFallbackScan_NeverLandsOnForbiddenGround()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);

            // Every point inside the footprint reaches the fallback (the filtered lookup says -1).
            for (double dx = -1.5; dx <= 1.5; dx += 0.5)
            {
                for (double dz = -1.5; dz <= 1.5; dz += 0.5)
                {
                    FPVector2 p = At(Bx + dx, Bz + dz);
                    if (!IsBuilding(mesh, query.FindTriangle(p)))
                        continue;   // not inside the footprint at this offset

                    query.ClosestPassablePoint(p, AgentMask, out int tri);
                    Assert.GreaterOrEqual(tri, 0, $"({dx},{dz}): no passable point found");
                    Assert.IsFalse(IsBuilding(mesh, tri),
                        $"({dx},{dz}): the fallback snapped onto the footprint");
                }
            }
        }

        /// <summary>
        /// The invariant that rejects "filter both steps": the point and the triangle a snap
        /// reports must belong together. A fully filtered re-snap would project the position out
        /// while the caller's triangle index still named the footprint, and the next move step
        /// feeds that mismatched pair straight into the walk.
        /// </summary>
        [Test]
        public void ThePointAndTheTriangle_BelongTogether()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);

            int checked_ = 0, ambiguous = 0;
            for (double dx = -half - 0.2; dx <= half + 0.2; dx += 0.17)
            {
                for (double dz = -half - 0.2; dz <= half + 0.2; dz += 0.17)
                {
                    FPVector2 p = At(Bx + dx, Bz + dz);
                    int tri = query.FindTriangle(p);
                    bool onMesh = tri >= 0;
                    FPVector2 result = onMesh ? p : query.ClosestPassablePoint(p, AgentMask, out tri);

                    Assert.GreaterOrEqual(tri, 0, "the pattern produced no triangle");
                    checked_++;
                    if (CountContaining(mesh, result) > 1)
                        ambiguous++;

                    // Asked with the SAME question that produced the triangle. The two branches
                    // answer different questions, so one comparator cannot serve both: the
                    // unfiltered branch reports where the point IS (the footprint, when it is on
                    // one), while the projection reports where the query MAY GO.
                    //
                    // The distinction is not pedantic here. A projected point lands on a triangle
                    // EDGE, and a footprint boundary edge belongs to both neighbours at the same
                    // interpolated height — so asking the unfiltered lookup about a PROJECTED point
                    // answers whichever neighbour has the lower index, which is the footprint about
                    // half the time. This test used to do exactly that on three hand-picked points
                    // and passed on all three by luck of numbering; the sweep below would have
                    // failed, and did.
                    int reported = onMesh
                        ? query.FindTriangle(result)
                        : query.FindPassableTriangle(result, AgentMask);
                    Assert.AreEqual(tri, reported,
                        "the reported triangle does not contain the reported point");
                }
            }

            TestContext.Out.WriteLine($"checked={checked_} ambiguous={ambiguous}");
            Assert.Greater(checked_, 100, "fixture: the sweep must actually run");
            Assert.Greater(ambiguous, 0,
                "fixture: some results must be ambiguous, or the sweep is not exercising the case "
                + "this pairing is about (three hand-picked points passed here by luck of numbering)");
        }

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

        #endregion

        #region Outside maxDist is reported as failure, not as a wrong point

        /// <summary>
        /// The new failure mode, pinned so it is a contract and not a surprise. With a reach
        /// shorter than the footprint's own half extent, the nearest PASSABLE point is out of range
        /// while impassable ground is underfoot — the filtered projection says "nothing", where the
        /// unfiltered one returned a point the caller could not use.
        /// </summary>
        [Test]
        public void WhenTheNearestPassablePointIsTooFar_ItReportsFailure()
        {
            var mesh = Retained();
            var query = new FPNavMeshQuery(mesh, null);
            FPVector2 centre = At(Bx, Bz);

            FPVector2 unfiltered = query.ProjectToNavMesh(centre, FP64.FromDouble(0.25), out int ut);
            Assert.GreaterOrEqual(ut, 0, "fixture: the unfiltered projection should succeed here");
            Assert.IsTrue(IsBuilding(mesh, ut), "fixture: and it should return the footprint");
            Assert.AreEqual(centre.x.RawValue, unfiltered.x.RawValue, "fixture: unchanged point");

            query.ProjectToPassable(centre, FP64.FromDouble(0.25), AgentMask, out int ft);
            Assert.AreEqual(-1, ft,
                "the filtered projection returned a point although none is passable within reach");
        }

        #endregion
    }
}
