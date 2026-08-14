using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The exact text of every placement rejection, pinned by equality.
    ///
    /// <para>The eleven assertions that already existed for these messages are all
    /// <c>StringAssert.Contains</c>, and one of them is the single word "overlap" — a fragment
    /// that appears in both wordings the overlap check can produce. They cannot tell a preserved
    /// message from a rewritten one, which is exactly what has to be gated while the rejection
    /// reason moves from an exception's text to a returned value: the text is supposed to come
    /// out the other side byte for byte.</para>
    ///
    /// <para>These strings were captured from the code BEFORE that change. Read them as a
    /// recording, not as a specification — if a message is deliberately improved, re-record and
    /// say so in the commit; if one changes without anyone meaning it to, this is the only thing
    /// that notices.</para>
    /// </summary>
    [TestFixture]
    public class FPBuildingRejectionGoldenTests
    {
        #region Fixtures

        private const double R = 0.5;

        /// <summary>20x20 slab, no agent radius — a rect is its own expansion, so the numbers in
        /// the message are the numbers in the test.</summary>
        private static FPNavMesh BuildSlab()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -10; x <= 10; x += 2)
                for (int z = -10; z <= 10; z += 2)
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
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.0);
        }

        /// <summary>16x16 slab with a [-2,2] pillar — the only fixture that can produce the
        /// swallowed-ring rejection, and the only message carrying a coordinate.</summary>
        private static FPNavMesh BuildAnnulus()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -8; x <= 8; x += 2)
                for (int z = -8; z <= 8; z += 2)
                    pts.Add((x, z));

            var vertices = new FPVector3[pts.Count];
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            var index = new Dictionary<(int, int), int>();
            for (int i = 0; i < pts.Count; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
                index[pts[i]] = i;
            }

            var constraints = new List<int>();
            void Ring(params (int x, int z)[] loop)
            {
                for (int i = 0; i < loop.Length; i++)
                {
                    constraints.Add(index[loop[i]]);
                    constraints.Add(index[loop[(i + 1) % loop.Length]]);
                }
            }

            var outer = new List<(int, int)>();
            for (int x = -8; x <= 8; x += 2) outer.Add((x, -8));
            for (int z = -6; z <= 8; z += 2) outer.Add((8, z));
            for (int x = 6; x >= -8; x -= 2) outer.Add((x, 8));
            for (int z = 6; z >= -6; z -= 2) outer.Add((-8, z));
            Ring(outer.ToArray());
            Ring((-2, -2), (0, -2), (2, -2), (2, 0), (2, 2), (0, 2), (-2, 2), (-2, 0));

            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs, zs, constraints.ToArray(), eraseOuterAndHoles: true);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: R);
        }

        private static FPBuildingRect Rect(double x0, double z0, double x1, double z1) =>
            new FPBuildingRect(FP64.FromDouble(x0), FP64.FromDouble(z0),
                               FP64.FromDouble(x1), FP64.FromDouble(z1), FP64.Zero);

        /// <summary>The rejection message, or null when the rebake succeeded.</summary>
        private static string Reject(
            FPNavMesh baseMesh, FPBuildingRect[] buildings, FPBuildingPlacementRules rules = default)
        {
            try
            {
                FPNavMeshRebaker.Rebake(baseMesh, buildings, null, rules);
                return null;
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
        }

        #endregion

        [Test]
        public void Golden_BuildingsOverlap()
        {
            Assert.AreEqual(
                "FPNavMeshRebaker: buildings 0 and 1 touch or overlap after radius expansion — "
                + "placement must be rejected",
                Reject(BuildSlab(), new[] { Rect(-4, -4, 0, 0), Rect(-2, -2, 2, 2) }));
        }

        [Test]
        public void Golden_BuildingsOverlap_WithTouchAllowed()
        {
            // The wording branches on the rules value, so the payload alone does not determine the
            // string — whatever assembles it needs the rules too.
            Assert.AreEqual(
                "FPNavMeshRebaker: buildings 0 and 1 overlap after radius expansion — "
                + "placement must be rejected (interiors must stay disjoint)",
                Reject(BuildSlab(), new[] { Rect(-4, -4, 0, 0), Rect(-2, -2, 2, 2) },
                       new FPBuildingPlacementRules(allowBuildingTouch: true)));
        }

        [Test]
        public void Golden_TouchesWalkableBoundary()
        {
            Assert.AreEqual(
                "FPNavMeshRebaker: building (expanded) touches or crosses the walkable boundary — "
                + "reject placement",
                Reject(BuildSlab(), new[] { Rect(-10, -2, -8, 2) }));
        }

        [Test]
        public void Golden_OutsideWalkableRegion()
        {
            Assert.AreEqual(
                "FPNavMeshRebaker: building (expanded) lies outside the walkable region — "
                + "reject placement",
                Reject(BuildSlab(), new[] { Rect(20, 20, 22, 22) }));
        }

        [Test]
        public void Golden_SwallowsBakedHole()
        {
            // The one message with a runtime coordinate — and the one that a bare building index
            // could not replace, because a real stage has ~1,700 pillars.
            //
            // The coordinate is (-2, 0), not the (-2, -2) one might guess from the pillar's
            // corner: the reported vertex is whichever ring vertex the boundary-edge walk visits
            // first, which follows the extractor's ring order rather than the order the fixture
            // declares. Worth pinning precisely for that reason — the pre-existing
            // StringAssert.Contains("-2.000") in FPNavMeshSwallowedRingTests passes for BOTH,
            // which is the concrete demonstration of why substring assertions are not a gate here.
            Assert.AreEqual(
                "FPNavMeshRebaker: building 0 (expanded) fully contains a baked hole ring at "
                + "(-2.000, 0.000) — reject placement (swallowing a hole would turn its interior walkable)",
                Reject(BuildAnnulus(), new[] { Rect(-4, -4, 4, 4) }));
        }

        [Test]
        public void SwallowsBakedHole_CarriesTheSiteAsAValue()
        {
            // The value counterpart of the golden above. The message pins the coordinate as
            // rendered text; this pins it as the FPVector2 a game actually branches on, and the
            // two are the same number by construction only as long as nobody rewires the payload.
            //
            // Worth its own test because Site is the one payload field with no other coverage:
            // the contract suite asserts IndexA/IndexB for overlap and outside, but never gets as
            // far as a swallowed hole. Anything that reorders or drops the swallowed-vertex
            // bookkeeping would leave every other assertion in the suite green.
            var snapshot = FPNavMeshRebaker.CreateSnapshot(BuildAnnulus(), null, prewarm: false);
            var context = new FPNavMeshRebakeContext(snapshot);

            bool ok = FPNavMeshRebaker.TryRebake(
                context, new[] { Rect(-4, -4, 4, 4) }, out _, out FPBuildingRejectionInfo rejection);

            Assert.IsFalse(ok, "a building that contains the pillar ring must be refused");
            Assert.AreEqual(FPBuildingRejection.SwallowsBakedHole, rejection.Reason);
            Assert.AreEqual(0, rejection.IndexA, "the building at fault");
            Assert.AreEqual(-1, rejection.IndexB, "a swallow names one building, not a pair");
            Assert.AreEqual(FP64.FromInt(-2), rejection.Site.x, "Site.x — which hole was swallowed");
            Assert.AreEqual(FP64.Zero, rejection.Site.y, "Site.y");
        }

        [Test]
        public void EmptyWalkableRegion_HasNoPlacementThatReachesIt()
        {
            // Recorded as a finding, not as a golden — nothing in the repository covers this
            // rejection, and checking why turned up the reason: no placement can reach it.
            //
            // Every building is validated to sit strictly inside the walkable region and not to
            // touch its boundary, so a gap to the boundary always survives and always
            // triangulates. A placement cannot erase the whole region. The check stays as a
            // defence (a degenerate base mesh is a different route) and the enum keeps a value
            // for it, but there is no message to pin because nothing produces one.
            //
            // A building large enough to try is refused earlier — and by the SWALLOW check, not
            // the boundary one, because containing the whole map means containing the outer ring.
            // FPNavMeshRebaker's own comment records that ordering: "with vertex-0 probing the two
            // orders agree on every case except a building containing the whole map, which reports
            // 'swallows' instead of 'outside'."
            StringAssert.StartsWith(
                "FPNavMeshRebaker: building 0 (expanded) fully contains a baked hole ring at",
                Reject(BuildSlab(), new[] { Rect(-12, -12, 12, 12) }),
                "a building big enough to empty the region is refused before it can");
        }
    }
}
