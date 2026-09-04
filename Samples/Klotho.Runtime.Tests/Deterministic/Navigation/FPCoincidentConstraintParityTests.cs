using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Even-odd erasure distinguishes coincident constraints.
    ///
    /// The change splits one boolean into two bits per edge: <b>Constrained</b> (OR — an edge
    /// marked at least once is a wall and never flips) and <b>CrossParity</b> (XOR — does crossing
    /// here change walkability?). For an edge two rings share the answers differ, and a single
    /// boolean could not say so.
    ///
    /// <para><b>What this file has to prove that the rest of the suite cannot.</b> The 10 rebake
    /// goldens and the CDT checksum fixture are all NON-coincident placements, so they only prove
    /// "nothing changes where nothing overlaps" — the parity path is never taken there. These
    /// tests are the ones that actually drive it, which makes them the primary detector for a
    /// propagation site that dropped a bit symmetrically.</para>
    ///
    /// <para>Two of them assert at the CDT layer rather than through the rebaker, and that is not
    /// a shortcut: the rebaker REFUSES both configurations (boundary contact is rejected outright,
    /// a swallowed ring is rejected by the placement check), so the erase behaviour they describe
    /// is unreachable from above. Fixing erasure does not reopen either policy.</para>
    /// </summary>
    [TestFixture]
    public class FPCoincidentConstraintParityTests
    {
        #region Fixture

        private static readonly FPBuildingPlacementRules TouchOk =
            new FPBuildingPlacementRules(allowBuildingTouch: true);

        /// <summary>Solid 16x16 slab, vertices every 2 units, bake radius 0.5.</summary>
        private static FPNavMesh BuildBase()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -8; x <= 8; x += 2)
                for (int z = -8; z <= 8; z += 2)
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

        private static FPBuildingRect R(double x0, double z0, double x1, double z1) =>
            new FPBuildingRect(FP64.FromDouble(x0), FP64.FromDouble(z0),
                               FP64.FromDouble(x1), FP64.FromDouble(z1), FP64.Zero);

        private static bool Walkable(FPNavMesh mesh, double x, double z) =>
            new FPNavMeshQuery(mesh, null).FindTriangle(
                new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z))) >= 0;

        private static long S(double v) => FPGeoPredicates.Snap(FP64.FromDouble(v));

        /// <summary>Closed ring over consecutive indices [start, start + n): the shape every
        /// producer in the engine emits, and the shape the erase pass requires.</summary>
        private static void Ring(List<int> into, int start, int n)
        {
            for (int i = 0; i < n; i++)
            {
                into.Add(start + i);
                into.Add(start + (i + 1) % n);
            }
        }

        /// <summary>Is the triangle containing (x, z) present in a raw CDT index list?</summary>
        private static bool Covered(int[] tris, long[] xs, long[] zs, double x, double z)
        {
            long px = S(x), pz = S(z);
            for (int i = 0; i < tris.Length; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                long o0 = FPGeoPredicates.Orient2D(xs[a], zs[a], xs[b], zs[b], px, pz);
                long o1 = FPGeoPredicates.Orient2D(xs[b], zs[b], xs[c], zs[c], px, pz);
                long o2 = FPGeoPredicates.Orient2D(xs[c], zs[c], xs[a], zs[a], px, pz);
                if (o0 >= 0 && o1 >= 0 && o2 >= 0)
                    return true;
            }
            return false;
        }

        #endregion

        // ── the defect this change exists to close ───────────────────────────

        [Test]
        public void FiveRectsInACross_LeaveNoWalkableCourtyard()
        {
            // The rectangle twin of the hexagon ring, and the configuration that made this a
            // SHIPPING defect rather than a hexagon-only one: five 1x1 buildings in a cross, each
            // expanded to 2x2 by the bake radius, centres 2 apart. The middle one shares all four
            // of its edges, and used to come out walkable — a courtyard in the middle of a base
            // that an agent could stand in but never reach.
            //
            // The hexagon test alone would not have caught a fix that only worked for even vertex
            // counts, and this is the arrangement a player actually builds.
            FPNavMesh baseMesh = BuildBase();
            var cross = new[]
            {
                R(-0.5, -0.5, 0.5, 0.5),      // centre
                R(-0.5,  1.5, 0.5, 2.5),      // +z
                R(-0.5, -2.5, 0.5, -1.5),     // -z
                R( 1.5, -0.5, 2.5, 0.5),      // +x
                R(-2.5, -0.5, -1.5, 0.5),     // -x
            };

            FPNavMesh mesh = FPNavMeshRebaker.Rebake(baseMesh, cross, null, TouchOk);

            Assert.IsFalse(Walkable(mesh, 0, 0),
                "the enclosed building is solid — this is the defect the parity split closes");
            Assert.IsFalse(Walkable(mesh, 0, 2), "and its neighbours with it");
            Assert.IsFalse(Walkable(mesh, 0, -2));
            Assert.IsFalse(Walkable(mesh, 2, 0));
            Assert.IsFalse(Walkable(mesh, -2, 0));

            // The seams: with the old boolean these were the crossings that miscounted.
            Assert.IsFalse(Walkable(mesh, 0, 1), "seam between centre and +z");
            Assert.IsFalse(Walkable(mesh, 1, 0), "seam between centre and +x");

            // And the fix must not have carved away the world: the diagonal gaps of the cross are
            // outside every building and stay walkable.
            Assert.IsTrue(Walkable(mesh, 2, 2), "the diagonal notch is not part of any building");
            Assert.IsTrue(Walkable(mesh, -6, -6), "far corner untouched");
        }

        [Test]
        public void MixedSizeFlushPair_BlocksTheSharedRunAndKeepsTheRest()
        {
            // Coincidence does not only arrive as whole matching edges. Contact is allowed when a
            // SMALL building sits flush against PART of a big one's wall, and then the overlap is
            // a sub-segment: the big building emits (p,q), the small one (r,s) with r,s strictly
            // inside it. Nothing welds to anything.
            //
            // Two mechanisms already handle it — the constraint walk stops at a vertex lying on
            // the segment and marks only the part-edge, and a later vertex insertion splits a
            // constrained edge with both halves inheriting the mark. This pins that the RESULT is
            // right either way: the shared run stops changing walkability, the rest of the big
            // wall still blocks.
            //
            // Equal-sized tiles never reach either path, so without this test both would be
            // unexercised.
            FPNavMesh baseMesh = BuildBase();
            var pair = new[]
            {
                R(-3, -3, 0, 3),        // big:   expanded x in [-3.5, 0.5], z in [-3.5, 3.5]
                R(1, -1, 3, 1),         // small: expanded x in [ 0.5, 3.5], z in [-1.5, 1.5]
            };

            FPNavMesh mesh = FPNavMeshRebaker.Rebake(baseMesh, pair, null, TouchOk);

            Assert.IsFalse(Walkable(mesh, -1.5, 0), "big building solid");
            Assert.IsFalse(Walkable(mesh, 2, 0), "small building solid");
            Assert.IsFalse(Walkable(mesh, 0.5, 0),
                "the shared run — the sub-segment where the two walls coincide");

            // The parts of the big wall the small building does NOT cover are still wall: the
            // parity toggled only along the shared run, so crossing above or below it must still
            // flip walkability.
            Assert.IsTrue(Walkable(mesh, 0.5, 3),
                "above the small building the big wall ends and the world resumes");
            Assert.IsTrue(Walkable(mesh, 0.5, -3), "and below it");
        }

        // ── CDT layer: reachable only from here ──────────────────────────────

        [Test]
        public void HoleRingSharingAnEdgeWithTheOuterRing_IsNowErased()
        {
            // This behaviour is why boundary contact is REJECTED by placement policy: a
            // hole flush against the outer ring shares an edge, even-odd counted it once, and the
            // notch stayed walkable — the building was accepted and carved nothing.
            //
            // The rebaker still rejects that placement, so this asserts at the CDT layer. It is an
            // equivalence check rather than a reproduction of that measurement: it came through
            // the rebaker and this does not, so only the VERDICT is comparable.
            //
            // What it means for the policy: the reason for that rejection is gone. Reopening it is
            // a separate decision and deliberately not part of this change.
            var xs = new List<long>();
            var zs = new List<long>();
            var cons = new List<int>();

            // Outer ring: (0,0) (10,0) (10,10) (0,10) — CCW.
            foreach (var (x, z) in new[] { (0, 0), (10, 0), (10, 10), (0, 10) })
            {
                xs.Add(S(x)); zs.Add(S(z));
            }
            Ring(cons, 0, 4);

            // Notch sharing the whole bottom-left run of the outer edge z = 0: (2,0) (6,0) (6,3)
            // (2,3). Its bottom side lies ON the outer ring's bottom side.
            foreach (var (x, z) in new[] { (2, 0), (6, 0), (6, 3), (2, 3) })
            {
                xs.Add(S(x)); zs.Add(S(z));
            }
            Ring(cons, 4, 4);

            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs.ToArray(), zs.ToArray(), cons.ToArray(), eraseOuterAndHoles: true);

            Assert.IsFalse(Covered(tris, xs.ToArray(), zs.ToArray(), 4, 1.5),
                "the notch interior is erased — a hole flush against the boundary is still a hole");
            Assert.IsTrue(Covered(tris, xs.ToArray(), zs.ToArray(), 8, 8),
                "and the rest of the region survives");
        }

        [Test]
        public void SwallowedRing_IsUnchanged_AndStillNeedsItsOwnRejection()
        {
            // This change does NOT close the swallowed-hole defect, and that claim needs its own
            // test. A building containing a baked hole is NESTING, not coincidence — the rings sit
            // on different
            // edges, every multiplicity is 1, and the parity is untouched.
            //
            // FPNavMeshSwallowedRingTests passing does not verify that claim, because those tests
            // exercise the REJECTION path: the placement throws before erasure ever runs. So the
            // claim has to be checked where it is actually about — here, at the CDT layer, by
            // pinning that the old behaviour is still exactly the old behaviour.
            //
            // If this ever starts failing, the swallowed-ring rejection is no longer load-bearing
            // and should be revisited on purpose rather than by accident.
            var xs = new List<long>();
            var zs = new List<long>();
            var cons = new List<int>();

            foreach (var (x, z) in new[] { (0, 0), (12, 0), (12, 12), (0, 12) })   // outer
            {
                xs.Add(S(x)); zs.Add(S(z));
            }
            Ring(cons, 0, 4);

            foreach (var (x, z) in new[] { (2, 2), (10, 2), (10, 10), (2, 10) })   // building
            {
                xs.Add(S(x)); zs.Add(S(z));
            }
            Ring(cons, 4, 4);

            foreach (var (x, z) in new[] { (5, 5), (7, 5), (7, 7), (5, 7) })       // pillar inside it
            {
                xs.Add(S(x)); zs.Add(S(z));
            }
            Ring(cons, 8, 4);

            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs.ToArray(), zs.ToArray(), cons.ToArray(), eraseOuterAndHoles: true);

            Assert.IsTrue(Covered(tris, xs.ToArray(), zs.ToArray(), 6, 6),
                "the pillar inside the building is STILL walkable — nesting alternates by "
                + "definition, and this change does not touch it");
            Assert.IsFalse(Covered(tris, xs.ToArray(), zs.ToArray(), 3, 3),
                "the building itself is still carved");
            Assert.IsTrue(Covered(tris, xs.ToArray(), zs.ToArray(), 1, 1),
                "and the ring around it survives");
        }

        // ── the two bits really do diverge ───────────────────────────────────

        [Test]
        public void TwiceMarkedEdge_StaysAWall_AndStillRefusesToBeCrossed()
        {
            // The hardest property here to observe from outside, and the one whose failure
            // is silent: if Constrained were XOR'd like the parity, an edge marked twice would
            // stop being a wall. Not just for the erase pass — a later constraint would be allowed
            // to carve straight THROUGH it, and the channel walk would demolish a real wall
            // without a word.
            //
            // Finding an observation point for it took two attempts, and the reason is worth
            // recording. The obvious one — "a free edge gets flipped away by legalisation" — is
            // NOT reachable through Triangulate: every vertex is inserted (and legalised) before
            // any constraint is marked, so nothing re-legalises afterwards and the flip never
            // happens either way. A test written that way passes under the mutation. The crossing
            // check is the role that IS exercised, so that is what this asserts.
            //
            // The kite makes the geometry unambiguous: 1-3 and 0-2 genuinely cross.
            var xs = new List<long>();
            var zs = new List<long>();
            foreach (var (x, z) in new[] { (0.0, 0.0), (1.0, -3.0), (2.0, 0.0), (1.0, 3.0) })
            {
                xs.Add(S(x)); zs.Add(S(z));
            }
            var hull = new List<int> { 0, 1, 1, 2, 2, 3, 3, 0 };

            // Marked once, then crossed: T1 NotAllowed. This is the established behaviour.
            var onceThenCross = new List<int>(hull) { 1, 3, 0, 2 };
            Assert.Throws<InvalidOperationException>(
                () => FPConstrainedDelaunay.Triangulate(
                    xs.ToArray(), zs.ToArray(), onceThenCross.ToArray(), eraseOuterAndHoles: false),
                "sanity: a constraint may not cross another constraint");

            // Marked TWICE, then crossed: parity is back to 0, but the wall bit is not, so this
            // must refuse identically. Under a XOR'd Constrained it would be accepted and the
            // channel would carve through the shared wall.
            var twiceThenCross = new List<int>(hull) { 1, 3, 3, 1, 0, 2 };
            Assert.Throws<InvalidOperationException>(
                () => FPConstrainedDelaunay.Triangulate(
                    xs.ToArray(), zs.ToArray(), twiceThenCross.ToArray(), eraseOuterAndHoles: false),
                "a twice-marked edge is STILL a wall — an edge two buildings share must not "
                + "become a hole a third constraint can drive through");
        }

        // ── the doubled-segment rule, as a contract ──────────────────────────

        [Test]
        public void DoubledOpenSeam_CarvesNothing_AndIsStillAWall()
        {
            // The half of the doubled-segment rule that nothing pinned. TwiceMarkedEdge above proves
            // Constrained is OR'd, but it runs with eraseOuterAndHoles:false — the erase pass is off,
            // so "parity is back to 0" is stated in its comment and asserted nowhere. This is the
            // shape a caller actually uses: an OPEN seam (a wall footing, a cliff join) sent twice so
            // it stays geometrically present without carving the region beside it.
            //
            // Why an open chain is legal here at all: InsertConstraints requires the marked set to be
            // a sum of CLOSED curves, because the erase pass reads a min-depth parity that is only
            // path-independent then. Doubling makes every edge of the chain even, so the odd set is
            // EMPTY — and the empty set is trivially such a sum. The rule is not a lucky side effect;
            // it is what makes an open seam expressible.
            var xs = new List<long>();
            var zs = new List<long>();

            // Outer ring 0..3, then an open seam 4..6 running through the interior.
            foreach (var (x, z) in new[] { (0, 0), (10, 0), (10, 10), (0, 10) })
            {
                xs.Add(S(x)); zs.Add(S(z));
            }
            foreach (var (x, z) in new[] { (2, 5), (5, 5), (8, 5) })
            {
                xs.Add(S(x)); zs.Add(S(z));
            }

            var cons = new List<int>();
            Ring(cons, 0, 4);
            // The seam, twice. Same segments, same direction — multiplicity is the whole point.
            for (int rep = 0; rep < 2; rep++)
            {
                cons.Add(4); cons.Add(5);
                cons.Add(5); cons.Add(6);
            }

            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs.ToArray(), zs.ToArray(), cons.ToArray(), eraseOuterAndHoles: true);

            Assert.IsTrue(Covered(tris, xs.ToArray(), zs.ToArray(), 5, 3),
                "below a doubled seam must stay walkable — an even edge does not change depth");
            Assert.IsTrue(Covered(tris, xs.ToArray(), zs.ToArray(), 5, 7),
                "and so must above it: the seam is geometry, not a boundary");

            // Still a wall. Observed through the crossing check for the reason TwiceMarkedEdge
            // records: a free edge being flipped away by legalisation is unreachable through
            // Triangulate, so the crossing check is the role that actually exercises the OR bit.
            // x = 3.5, i.e. strictly between seam vertices 4 (x=2) and 5 (x=5): the crosser has to
            // meet segment 4-5 in its INTERIOR. Running it through x=5 instead would pass exactly
            // through vertex 5 and be split there rather than rejected — the same trap the kite in
            // TwiceMarkedEdge avoids by making 1-3 and 0-2 genuinely cross.
            var crossing = new List<int>(cons) { 7, 8 };
            xs.Add(S(3.5)); zs.Add(S(3));
            xs.Add(S(3.5)); zs.Add(S(7));
            Assert.Throws<InvalidOperationException>(
                () => FPConstrainedDelaunay.Triangulate(
                    xs.ToArray(), zs.ToArray(), crossing.ToArray(), eraseOuterAndHoles: true),
                "a twice-marked seam is STILL a wall — parity-neutral is not the same as absent");
        }

        [Test]
        public void DoubledRing_ThroughASnapshotResume_CarvesNothing_AndIsStillAWall()
        {
            // The same rule through the OTHER public entry point. W08 made BuildSnapshot and
            // TriangulateFromSnapshot public, so the multiplicity contract now covers a constraint
            // set the caller authors as HOLE pairs — and nothing exercised doubling on that path.
            // It runs the same InsertConstraints, which is why the gap was easy to miss: the claim
            // was true and untested on the surface that turned it into a contract.
            //
            // A CLOSED ring, and that is the whole reason this test is shaped differently from
            // DoubledOpenSeam above. The first version of this test doubled an open seam and passed
            // with the doubling REMOVED — an odd open chain is undefined, and in that arrangement it
            // happens to carve nothing either, so the assertion had no teeth. A ring is defined in
            // both directions: odd carves (the control below), even does not.
            var baseXs = new long[] { S(0), S(10), S(10), S(0) };
            var baseZs = new long[] { S(0), S(0), S(10), S(10) };
            var baseCons = new List<int>();
            Ring(baseCons, 0, 4);

            // Interior ring 4..7, appended as hole vertices (base 0..3 first, then holes).
            var holeXs = new long[] { S(2), S(8), S(8), S(2) };
            var holeZs = new long[] { S(4), S(4), S(6), S(6) };

            var xs = new long[8];
            var zs = new long[8];
            Array.Copy(baseXs, xs, 4);
            Array.Copy(baseZs, zs, 4);
            Array.Copy(holeXs, 0, xs, 4, 4);
            Array.Copy(holeZs, 0, zs, 4, 4);

            int[] once = ResumeWithRing(baseXs, baseZs, baseCons, holeXs, holeZs, repeats: 1);
            int[] twice = ResumeWithRing(baseXs, baseZs, baseCons, holeXs, holeZs, repeats: 2);

            Assert.IsFalse(Covered(once, xs, zs, 5, 5),
                "control: one ring at odd depth is erased on the resume path as well");
            Assert.IsTrue(Covered(twice, xs, zs, 5, 5),
                "the same ring sent twice must carve nothing — an even edge does not change depth");
            Assert.IsTrue(Covered(twice, xs, zs, 5, 1), "and the rest of the slab survives");

            // Still a wall, observed the way the OR bit is actually reachable: a crossing constraint
            // that meets a ring side in its INTERIOR (x = 5 crosses side 4-5 at z = 4 between
            // x = 2 and x = 8, so it is refused rather than split at a vertex).
            var crossXs = new long[] { holeXs[0], holeXs[1], holeXs[2], holeXs[3], S(5), S(5) };
            var crossZs = new long[] { holeZs[0], holeZs[1], holeZs[2], holeZs[3], S(1), S(9) };
            var crossing = new List<int>();
            for (int rep = 0; rep < 2; rep++)
                Ring(crossing, 4, 4);
            crossing.Add(8); crossing.Add(9);

            Assert.Throws<InvalidOperationException>(
                () => FPConstrainedDelaunay.TriangulateFromSnapshot(
                    FPConstrainedDelaunay.BuildSnapshot(baseXs, baseZs, baseCons.ToArray()),
                    crossXs, crossZs, crossing.ToArray(), eraseOuterAndHoles: true),
                "a twice-marked ring is STILL a wall when the pairs arrive as hole constraints");
        }

        /// <summary>
        /// One resume with the interior ring submitted <paramref name="repeats"/> times. A fresh
        /// snapshot per call on purpose: a snapshot is immutable and shared, and reusing one across
        /// the two arms would make the test depend on that being true rather than assert it.
        /// </summary>
        private static int[] ResumeWithRing(long[] baseXs, long[] baseZs, List<int> baseCons,
            long[] holeXs, long[] holeZs, int repeats)
        {
            var cons = new List<int>();
            for (int rep = 0; rep < repeats; rep++)
                Ring(cons, 4, 4);

            return FPConstrainedDelaunay.TriangulateFromSnapshot(
                FPConstrainedDelaunay.BuildSnapshot(baseXs, baseZs, baseCons.ToArray()),
                holeXs, holeZs, cons.ToArray(), eraseOuterAndHoles: true);
        }

        [Test]
        public void ClosedRingOverTheSameArea_DoesCarve()
        {
            // The control, and deliberately NOT "the same open seam sent once". A single open chain
            // is the case InsertConstraints calls out as path-dependent — its erase result depends on
            // which way the BFS arrived — so pinning it would turn undefined behaviour into a golden.
            // A closed ring is defined, and it shows the same machinery erasing when parity is odd,
            // which is what makes the doubled case above mean something.
            var xs = new List<long>();
            var zs = new List<long>();
            foreach (var (x, z) in new[] { (0, 0), (10, 0), (10, 10), (0, 10) })
            {
                xs.Add(S(x)); zs.Add(S(z));
            }
            foreach (var (x, z) in new[] { (2, 4), (8, 4), (8, 6), (2, 6) })
            {
                xs.Add(S(x)); zs.Add(S(z));
            }

            var cons = new List<int>();
            Ring(cons, 0, 4);
            Ring(cons, 4, 4);

            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs.ToArray(), zs.ToArray(), cons.ToArray(), eraseOuterAndHoles: true);

            Assert.IsFalse(Covered(tris, xs.ToArray(), zs.ToArray(), 5, 5),
                "a closed ring at odd depth is erased — parity does move the erase pass");
            Assert.IsTrue(Covered(tris, xs.ToArray(), zs.ToArray(), 5, 1),
                "and the rest of the slab survives");
        }

        // ── goldens for the configurations this change MAKES correct ─────────

        // Captured 2026-08-11, after the fix. The existing rebake goldens pin what must NOT move,
        // and every one of them is a non-coincident placement — so nothing pins what just BECAME
        // correct, and a revert would leave them all silent. These do that job.
        //
        // Measured justification, not a precaution: mutating the split-inheritance site to drop
        // the parity bit fired the SelfCheck assertion ZERO times (the loss is symmetric) and left
        // all ten rebake goldens green. One pre-existing test caught it. That is how thin the net
        // is over the parity path.
        //
        // Same rules as FPNavMeshRebakeGoldenTests: this file owns its fixtures, and the inputs
        // are literals. Re-capture with Capture below and say in the commit why the baseline moved.
        private const ulong TouchingPairGolden = 0x0058F3BAA4B169E6;
        private const ulong EnclosedCrossGolden = 0xF7C5132D81400F28;
        private const ulong MixedSizeFlushGolden = 0x6D25443623799438;

        private static FPBuildingRect[] TouchingPair() =>
            new[] { R(-2, -0.5, -1, 0.5), R(0, -0.5, 1, 0.5) };

        private static FPBuildingRect[] EnclosedCross() =>
            new[]
            {
                R(-0.5, -0.5, 0.5, 0.5),
                R(-0.5, 1.5, 0.5, 2.5),
                R(-0.5, -2.5, 0.5, -1.5),
                R(1.5, -0.5, 2.5, 0.5),
                R(-2.5, -0.5, -1.5, 0.5),
            };

        private static FPBuildingRect[] MixedSizeFlush() =>
            new[] { R(-3, -3, 0, 3), R(1, -1, 3, 1) };

        private static ulong Fingerprint(FPNavMesh baseMesh, FPBuildingRect[] buildings) =>
            FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(baseMesh, buildings, null, TouchOk));

        [Test]
        public void CoincidentConfigurations_MatchGolden()
        {
            FPNavMesh baseMesh = BuildBase();
            Assert.AreEqual(TouchingPairGolden, Fingerprint(baseMesh, TouchingPair()), "touching pair");
            Assert.AreEqual(EnclosedCrossGolden, Fingerprint(baseMesh, EnclosedCross()), "enclosed cross");
            Assert.AreEqual(MixedSizeFlushGolden, Fingerprint(baseMesh, MixedSizeFlush()), "mixed-size flush");
        }

        [Test]
        [Explicit("prints the current fingerprints; only run when re-baselining")]
        public void Capture()
        {
            FPNavMesh baseMesh = BuildBase();
            void P(string n, ulong v) => TestContext.Out.WriteLine($"        private const ulong {n} = 0x{v:X16};");
            P("TouchingPairGolden", Fingerprint(baseMesh, TouchingPair()));
            P("EnclosedCrossGolden", Fingerprint(baseMesh, EnclosedCross()));
            P("MixedSizeFlushGolden", Fingerprint(baseMesh, MixedSizeFlush()));
        }

        // ── the invariant that moved up a layer ──────────────────────────────

        [Test]
        public void RebakerEmitsEachBuildingEdgeExactlyOnce_UnlessThePlacementAsksToRetain()
        {
            // Multiplicity is now MEANINGFUL, so "the same logical wall twice" erases that wall
            // from the erase pass. There is deliberately no duplicate check inside the CDT: at
            // that layer a repeat is indistinguishable from the legitimate case, because two rings
            // sharing an edge emit (p,q) and (q,p), which normalise to the same pair. The
            // invariant therefore lives here, in the producer.
            //
            // ⚠ CONDITIONAL SINCE RETAIN MODE, and the condition is the whole design: a placement
            // with `FPBuildingPlacement.Retain` emits its ring TWICE on purpose, which is what
            // makes the footprint a wall to the triangulator and parity-neutral to the erase pass
            // (FPNavMeshRetainPlacementTests). So the invariant is "exactly once per CARVE" —
            // never "the producer cannot double-emit". Deleting the qualifier and tightening this
            // back to an unconditional claim would not fail here; it would make retain mode
            // unimplementable while this test stayed green, which is why the reason is written
            // down rather than left to the diff.
            //
            // Counted, not scanned, for exactly that reason — a scan would fire on the coincident
            // pair below, which is the configuration this whole plan exists to support.
            FPNavMesh baseMesh = BuildBase();

            // A touching pair: 8 edges from 2 rects, and the shared run must not make it 7 or 9.
            var touching = new[] { R(-2, -0.5, -1, 0.5), R(0, -0.5, 1, 0.5) };
            FPNavMesh mesh = FPNavMeshRebaker.Rebake(baseMesh, touching, null, TouchOk);
            Assert.IsNotNull(mesh, "the touching pair is accepted");

            // The structural reason the producer cannot double-emit, pinned as an assertion about
            // the emitted ring rather than about the CDT: a simple ring visits n distinct vertices
            // and emits n edges, so the same unordered pair cannot repeat within one building, and
            // two DIFFERENT buildings can only coincide (which is legal) because overlapping
            // interiors are refused by the pairwise test.
            var overlapping = new[] { R(-2, -2, 1, 1), R(-1, -1, 2, 2) };
            Assert.Throws<InvalidOperationException>(
                () => FPNavMeshRebaker.Rebake(baseMesh, overlapping, null, TouchOk),
                "overlapping interiors stay refused — that is what keeps 'coincident' from "
                + "widening into 'the same wall twice'");
        }
    }
}
