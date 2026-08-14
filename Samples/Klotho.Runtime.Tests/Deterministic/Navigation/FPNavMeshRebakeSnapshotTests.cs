using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// CDT base snapshot cache tests: freeze consistency,
    /// ghost-rebase isolation (h>0 clone before insertion), snapshot immutability across
    /// reuse (same instance N times + A→B→A reproduction), the duplicate-vertex contract,
    /// and the two dead-code-activation paths the snapshot flow enables for the first time
    /// (legalize meeting constrained edges, SplitOnEdge splitting a constraint).
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakeSnapshotTests
    {
        #region Fixture

        private static FPNavMesh BuildBase(int half = 10)
        {
            var pts = new List<(int x, int z)>();
            for (int x = -half; x <= half; x++)
            {
                for (int z = -half; z <= half; z++)
                    pts.Add((x, z));
            }
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

        private static FPBuildingRect Rect(double minX, double minZ, double maxX, double maxZ)
        {
            return new FPBuildingRect(
                FP64.FromDouble(minX), FP64.FromDouble(minZ),
                FP64.FromDouble(maxX), FP64.FromDouble(maxZ), FP64.Zero);
        }

        /// <summary>Simple snapped fixture: 5x5 unit grid + outer ring constraints.</summary>
        private static (long[] xs, long[] zs, int[] cons) CdtFixture()
        {
            const long S = 1024;
            int n = 5;
            var xs = new long[n * n];
            var zs = new long[n * n];
            for (int x = 0; x < n; x++)
            {
                for (int z = 0; z < n; z++)
                {
                    xs[x * n + z] = (x - 2) * S;
                    zs[x * n + z] = (z - 2) * S;
                }
            }
            int V(int x, int z) => (x + 2) * n + (z + 2);
            var ring = new List<int>();
            for (int x = -2; x <= 2; x++) ring.Add(V(x, -2));
            for (int z = -1; z <= 2; z++) ring.Add(V(2, z));
            for (int x = 1; x >= -2; x--) ring.Add(V(x, 2));
            for (int z = 1; z >= -1; z--) ring.Add(V(-2, z));
            var cons = new List<int>();
            for (int i = 0; i < ring.Count; i++)
            {
                cons.Add(ring[i]);
                cons.Add(ring[(i + 1) % ring.Count]);
            }
            return (xs, zs, cons.ToArray());
        }

        #endregion

        /// <summary>

        /// Resume with the counts the production path now threads,

        /// and pin the contract the array-shaped assertions below depend on: with no pool the

        /// extract buffer is exact-size, so Length still equals the index count. If someone gives

        /// NonPooling headroom, this fails here rather than silently in every caller that still

        /// reads Length.

        /// </summary>

        private static int[] ResumeExact(

            FPConstrainedDelaunay.CdtSnapshot snap, long[] holeXs, long[] holeZs, int[] constraints)

        {

            int[] result = FPConstrainedDelaunay.TriangulateFromSnapshot(

                snap, holeXs, holeZs, holeXs.Length, constraints, constraints.Length, out int indexCount);

            Assert.AreEqual(result.Length, indexCount,

                "NonPooling must return an exact-size extract buffer");

            return result;

        }


        [Test]
        public void FreezeConsistency_SnapshotResumeZeroHoles_EqualsOneShot()
        {
            var (xs, zs, cons) = CdtFixture();
            int[] oneShot = FPConstrainedDelaunay.Triangulate(xs, zs, cons);

            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);
            int[] resumed = ResumeExact(snap, Array.Empty<long>(), Array.Empty<long>(), Array.Empty<int>());

            CollectionAssert.AreEqual(oneShot, resumed, "freeze + zero-hole resume must equal the one-shot result");
        }

        [Test]
        public void GhostRebase_CloneBeforeInsertion_PreservesGeometry()
        {
            // h>0 exercises the rebase (+h offset); comparing the clone's extraction
            // BEFORE hole insertion against the one-shot result isolates the rebase itself
            // (ghost index shifts never appear in output — ghost-adjacent tris are dropped).
            var (xs, zs, cons) = CdtFixture();
            int[] oneShot = FPConstrainedDelaunay.Triangulate(xs, zs, cons, eraseOuterAndHoles: false);

            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);
            var holeXs = new long[] { 512, -512 };   // off-grid, no duplicates
            var holeZs = new long[] { 512, -512 };
            var cdt = new FPConstrainedDelaunay.Cdt(snap, holeXs, holeZs, holeXs.Length, null);
            cdt.SelfCheck();
            int[] cloned = cdt.Extract(false, out int clonedCount);
            Assert.AreEqual(cloned.Length, clonedCount,
                "NonPooling must return an exact-size extract buffer");

            CollectionAssert.AreEqual(oneShot, cloned, "clone + ghost rebase must be geometry-neutral");
        }

        [Test]
        public void SnapshotImmutability_ReuseAndABAReproduction()
        {
            FPNavMesh baseMesh = BuildBase();
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);

            var a = new[] { Rect(-1, -1, 1, 1) };
            var b = new[] { Rect(3, 3, 5, 5) };

            ulong fpA1 = FPNavMeshRebaker.ComputeFingerprint(FPNavMeshRebaker.Rebake(snapshot, a));
            ulong fpB = FPNavMeshRebaker.ComputeFingerprint(FPNavMeshRebaker.Rebake(snapshot, b));
            ulong fpA2 = FPNavMeshRebaker.ComputeFingerprint(FPNavMeshRebaker.Rebake(snapshot, a));
            ulong fpA3 = FPNavMeshRebaker.ComputeFingerprint(FPNavMeshRebaker.Rebake(snapshot, a));

            Assert.AreNotEqual(fpA1, fpB, "different building sets must differ");
            Assert.AreEqual(fpA1, fpA2, "A→B→A must reproduce A bit-identically (no state leak into the snapshot)");
            Assert.AreEqual(fpA1, fpA3, "repeated reuse of the same snapshot instance must stay identical");

            // Cached snapshot vs throwaway-snapshot overload: single-path bit identity.
            ulong fpOneShot = FPNavMeshRebaker.ComputeFingerprint(FPNavMeshRebaker.Rebake(baseMesh, a));
            Assert.AreEqual(fpA1, fpOneShot, "baseMesh overload (throwaway snapshot) must match the cached snapshot path");
        }

        [Test]
        public void HoleVertexDuplicatesBase_IsADebugOnlyContractCheck()
        {
            // The resume contract says hole vertices repeat neither a base coordinate nor each
            // other, and the constructor used to scan for it on every rebake. The base half is
            // O(holes x base vertices): on Field's 17,142 base vertices the clone constructor cost
            // 1.395 ms at 128 holes with the scans and 0.126 ms without — 91% of the entire
            // hole-proportional cost, ~9.4 us per hole, which is one pass over the base
            // coordinates each time.
            //
            // The production caller cannot break the contract. FPNavMeshRebaker.AddHoleVertex looks
            // each corner up in the snapshot's base coordinate map first and reuses the base index
            // on a hit, so a coinciding corner never becomes a hole vertex at all; a second map
            // does the same hole-to-hole. So the scans are compiled out of release builds.
            //
            // They are kept for DEBUG rather than deleted because THIS entry point is reachable
            // from tests with hand-built arrays — which is what this test is — and there they are
            // the only net. Both configurations are asserted so neither half can drift unnoticed.
            var (xs, zs, cons) = CdtFixture();
            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);

#if DEBUG
            Assert.Throws<InvalidOperationException>(() =>
                ResumeExact(snap, new long[] { xs[0] }, new long[] { zs[0] }, Array.Empty<int>()),
                "hole vertex duplicating a base coordinate must violate the resume contract");
            Assert.Throws<InvalidOperationException>(() =>
                ResumeExact(snap, new long[] { 512, 512 }, new long[] { 512, 512 }, Array.Empty<int>()),
                "duplicate hole vertices must violate the resume contract");
#else
            // Release pins the removal. Reaching the triangulation at all is the assertion; what
            // comes out is unspecified, because the input violates the contract on purpose.
            Assert.DoesNotThrow(() =>
                ResumeExact(snap, new long[] { xs[0] }, new long[] { zs[0] }, Array.Empty<int>()),
                "the O(holes x base) scan must not be present in a release build");
#endif
        }

        [Test]
        public void DeadCodeActivation_LegalizeMeetsConstraint_NearBoundaryBuilding()
        {
            // (a) hole corners land one cell from the constrained outer ring — legalize
            // around the inserted vertices reaches constrained edges (skip branch fires,
            // unreachable in the from-scratch flow). Valid output = exact area conservation.
            FPNavMesh baseMesh = BuildBase();
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);

            // Expanded hole = (-9.5,-9.5)..(-7.5,-7.5): corners 0.5 world from the ring at -10.
            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(snapshot, new[] { Rect(-9, -9, -8, -8) });

            System.Numerics.BigInteger DoubledArea(FPNavMesh mm)
            {
                System.Numerics.BigInteger sum = 0;
                foreach (var t in mm.Triangles)
                {
                    long ax = FPGeoPredicates.Snap(mm.Vertices[t.v0].x), az = FPGeoPredicates.Snap(mm.Vertices[t.v0].z);
                    long bx = FPGeoPredicates.Snap(mm.Vertices[t.v1].x), bz = FPGeoPredicates.Snap(mm.Vertices[t.v1].z);
                    long cx = FPGeoPredicates.Snap(mm.Vertices[t.v2].x), cz = FPGeoPredicates.Snap(mm.Vertices[t.v2].z);
                    sum += System.Numerics.BigInteger.Abs(
                        (System.Numerics.BigInteger)(bx - ax) * (cz - az) - (System.Numerics.BigInteger)(bz - az) * (cx - ax));
                }
                return sum;
            }
            var hole = (System.Numerics.BigInteger)2 * (2 * 1024) * (2 * 1024);
            Assert.AreEqual(DoubledArea(baseMesh) - hole, DoubledArea(rebaked),
                "area must be conserved exactly with a near-boundary hole");
        }

        [Test]
        public void DeadCodeActivation_VertexOnConstraint_SplitsWithInheritance()
        {
            // (b) a resume vertex placed exactly on a constrained edge — SplitOnEdge's
            // cSplit inheritance fires (unreachable in the from-scratch flow; the rebaker
            // pre-blocks this, so this is the API-level "defined behavior" evidence).
            var (xs, zs, cons) = CdtFixture();
            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);

            // Midpoint of the bottom boundary ring segment between (-2,-2)*1024 and (-1,-2)*1024.
            long mx = -3 * 512;
            long mz = -2048;
            int[] tris = ResumeExact(snap, new[] { mx }, new[] { mz }, Array.Empty<int>());

            int midIdx = xs.Length; // combined index of the single hole vertex
            bool referenced = false;
            foreach (int v in tris)
            {
                if (v == midIdx)
                    referenced = true;
            }
            Assert.IsTrue(referenced, "the on-constraint vertex must be split into the triangulation");
            Assert.Greater(tris.Length, 0, "erase parity must survive the constraint split (both halves stay constrained)");
        }

    }
}
