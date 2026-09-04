using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The caller-authored CDT path: <c>BuildSnapshot</c> + <c>TriangulateFromSnapshot</c> +
    /// <c>CdtSnapshot</c> as an opaque handle.
    ///
    /// <para>What these have to prove that the rest of the suite cannot: the resume does not weld
    /// duplicate coordinates, and the check that catches it used to be <c>#if DEBUG</c> on the
    /// grounds that "the production caller cannot break it" and "this entry point is internal".
    /// Making it public makes both false, so the contract now lives on the public entry and has to
    /// hold <b>in a release build</b> — which is exactly where the old scan is compiled out.</para>
    ///
    /// <para>The other half is that the internal rebake path must not pay for it. The public factory
    /// builds a base coordinate map; the core one must not.</para>
    /// </summary>
    [TestFixture]
    public class CdtSnapshotPublicApiTests
    {
        // ── fixture geometry ─────────────────────────────────────────────────
        // A square base with a constrained outer ring, and a small square hole inside it.

        private static long S(double v) => (long)(v * 1000.0);

        private static void Base(out long[] xs, out long[] zs, out int[] cons)
        {
            xs = new[] { S(0), S(10), S(10), S(0) };
            zs = new[] { S(0), S(0), S(10), S(10) };
            cons = new[] { 0, 1, 1, 2, 2, 3, 3, 0 };
        }

        /// <summary>Hole ring over the four appended hole vertices (combined space: base first).</summary>
        private static int[] HoleRing(int realCount)
            => new[] { realCount + 0, realCount + 1,
                       realCount + 1, realCount + 2,
                       realCount + 2, realCount + 3,
                       realCount + 3, realCount + 0 };

        private static void Hole(out long[] hxs, out long[] hzs)
        {
            hxs = new[] { S(3), S(6), S(6), S(3) };
            hzs = new[] { S(3), S(3), S(6), S(6) };
        }

        // ── gate 1 — the contract holds in a release build ───────────────────

        [Test]
        public void HoleVertexDuplicatingABaseCoordinate_Throws()
        {
            // The failure this replaces: the resume does not weld, so a duplicate silently renumbers
            // the caller's positional constraint indices and yields a different mesh. Every peer
            // computes the same different mesh, so no hash comparison would ever report it.
            Base(out long[] xs, out long[] zs, out int[] cons);
            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);

            Hole(out long[] hxs, out long[] hzs);
            hxs[0] = xs[0]; hzs[0] = zs[0];        // collide with base vertex 0

            var ex = Assert.Throws<InvalidOperationException>(
                () => FPConstrainedDelaunay.TriangulateFromSnapshot(
                    snap, hxs, hzs, HoleRing(snap.RealCount)));
            Assert.IsTrue(ex.Message.Contains("duplicates a base coordinate"),
                $"the message must say which contract broke; got: {ex.Message}");
        }

        [Test]
        public void HoleVertexDuplicatingAnEarlierHole_Throws()
        {
            // The base map cannot catch this one — it holds base coordinates only.
            Base(out long[] xs, out long[] zs, out int[] cons);
            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);

            Hole(out long[] hxs, out long[] hzs);
            hxs[2] = hxs[0]; hzs[2] = hzs[0];      // third hole vertex repeats the first

            var ex = Assert.Throws<InvalidOperationException>(
                () => FPConstrainedDelaunay.TriangulateFromSnapshot(
                    snap, hxs, hzs, HoleRing(snap.RealCount)));
            Assert.IsTrue(ex.Message.Contains("duplicates an earlier hole coordinate"),
                $"got: {ex.Message}");
        }

        [Test]
        public void TheContractIsEnforcedBeforeTheDebugScan()
        {
            // In DEBUG the resume's own #if DEBUG scan also throws, so a Debug-only run could pass
            // while the release-active path does nothing. Pinning WHICH message comes out is what
            // makes the Debug run meaningful: ours names the contract and the reason.
            Base(out long[] xs, out long[] zs, out int[] cons);
            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);

            Hole(out long[] hxs, out long[] hzs);
            hxs[0] = xs[1]; hzs[0] = zs[1];

            var ex = Assert.Throws<InvalidOperationException>(
                () => FPConstrainedDelaunay.TriangulateFromSnapshot(
                    snap, hxs, hzs, HoleRing(snap.RealCount)));
            Assert.IsTrue(ex.Message.Contains("does not weld"),
                "the public check must fire first — the DEBUG scan's message says 'snapshot resume "
                + $"contract' instead and does not explain the renumbering. Got: {ex.Message}");
        }

        // ── gate 2 — the internal path pays nothing ─────────────────────────

        [Test]
        public void CoreFactoryAttachesNoMap_PublicFactoryDoes()
        {
            // "Freeze() leaves it null" would NOT be a way to say this: every snapshot goes through
            // Freeze (BuildSnapshot ends in `return cdt.Freeze()`). The distinction has to be the
            // factory, which is why the two have different names.
            Base(out long[] xs, out long[] zs, out int[] cons);

            Assert.IsNull(FPConstrainedDelaunay.BuildSnapshotCore(xs, zs, cons).CoordToIndex,
                "the rebaker's path must not build a second coordinate map — it already carries one");
            Assert.IsNotNull(FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons).CoordToIndex,
                "the public factory pays for the map once so every resume is O(holes)");
        }

        [Test]
        public void TheMapCoversTheBaseVerticesAndNotTheGhosts()
        {
            // Xs/Zs are RealCount + 4: the ghost block trails them and is not caller geometry.
            // A map built over the whole array would reject hole vertices for colliding with a ghost.
            Base(out long[] xs, out long[] zs, out int[] cons);
            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);

            Assert.AreEqual(snap.RealCount, snap.CoordToIndex.Count);
            Assert.AreEqual(snap.RealCount + 4, snap.Xs.Length, "premise: four ghosts trail Xs");
            for (int i = 0; i < snap.RealCount; i++)
                Assert.IsTrue(snap.CoordToIndex.ContainsKey((snap.Xs[i], snap.Zs[i])));
            for (int g = 0; g < 4; g++)
                Assert.IsFalse(
                    snap.CoordToIndex.ContainsKey((snap.Xs[snap.RealCount + g], snap.Zs[snap.RealCount + g])),
                    "a ghost coordinate must not be in the map");
        }

        // ── gate 3 — the round trip a caller actually wants ─────────────────

        [Test]
        public void CallerAuthoredRoundTrip_ProducesAMesh()
        {
            Base(out long[] xs, out long[] zs, out int[] cons);
            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);

            Hole(out long[] hxs, out long[] hzs);
            int[] tris = FPConstrainedDelaunay.TriangulateFromSnapshot(
                snap, hxs, hzs, HoleRing(snap.RealCount));

            Assert.AreEqual(0, tris.Length % 3, "triangle index triplets");
            Assert.Greater(tris.Length, 0);

            // And on into the public pipeline — the point of opening this at all.
            // Unsnap, not FromRaw: the CDT works on the snapped predicate grid and the pipeline
            // wants world units. FromRaw would reinterpret the grid integer as an FP64 raw value and
            // collapse the whole mesh below one cell — it comes back with zero triangles, which reads
            // like "the round trip is broken" rather than "the test converted wrong".
            var verts = new FPVector3[snap.RealCount + hxs.Length];
            for (int i = 0; i < snap.RealCount; i++)
                verts[i] = new FPVector3(FPGeoPredicates.Unsnap(snap.Xs[i]), FP64.Zero,
                                         FPGeoPredicates.Unsnap(snap.Zs[i]));
            for (int i = 0; i < hxs.Length; i++)
                verts[snap.RealCount + i] = new FPVector3(FPGeoPredicates.Unsnap(hxs[i]), FP64.Zero,
                                                         FPGeoPredicates.Unsnap(hzs[i]));

            FPNavMesh mesh = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                verts, tris, new int[tris.Length / 3], FP64.One, null,
                bakeAgentRadius: FP64.Zero, bakeMaxSlopeDeg: FP64.FromInt(45),
                bakeAgentHeight: FP64.FromInt(2), bakeAgentClimb: FP64.Zero);

            Assert.IsNotNull(mesh);
            Assert.Greater(mesh.Triangles.Length, 0, "a caller-authored constraint set reaches a mesh");
        }

        // ── gate 4 — the freeze-consistency contract survives going public ──

        [Test]
        public void BuildSnapshotPlusResume_AgreesWithTriangulate_OnTheSameInput()
        {
            // "BuildSnapshot + Extract == Triangulate" is an existing contract. Publishing the two
            // methods must not have moved it: the same geometry expressed as base+holes and as one
            // flat Triangulate call has to come out the same.
            Base(out long[] xs, out long[] zs, out int[] cons);
            Hole(out long[] hxs, out long[] hzs);

            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);
            int[] resumed = FPConstrainedDelaunay.TriangulateFromSnapshot(
                snap, hxs, hzs, HoleRing(snap.RealCount));

            var allXs = new long[xs.Length + hxs.Length];
            var allZs = new long[zs.Length + hzs.Length];
            Array.Copy(xs, allXs, xs.Length);
            Array.Copy(zs, allZs, zs.Length);
            Array.Copy(hxs, 0, allXs, xs.Length, hxs.Length);
            Array.Copy(hzs, 0, allZs, zs.Length, hzs.Length);

            var allCons = new List<int>(cons);
            allCons.AddRange(HoleRing(xs.Length));

            int[] flat = FPConstrainedDelaunay.Triangulate(allXs, allZs, allCons.ToArray());

            CollectionAssert.AreEqual(flat, resumed,
                "the resume path and the flat path describe one world — if they diverge, the snapshot "
                + "boundary changed the result and every caller-authored mesh is suspect");
        }
    }
}
