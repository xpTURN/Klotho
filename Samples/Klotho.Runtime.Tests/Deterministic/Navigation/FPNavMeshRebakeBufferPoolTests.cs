using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The work-buffer pool gate: a pooled rebake must be bit-identical to an
    /// unpooled one, and must stay identical when the same pool is reused — including across
    /// *different* building sets. The cross-input A→B→A case is the one that matters: only a
    /// shrinking hole count leaves a stale tail in a rented buffer, so repeating one input
    /// cannot expose a misread. DEBUG builds poison every rented buffer, which is what turns
    /// such a misread into a divergence instead of a silent pass — run this suite in Debug.
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakeBufferPoolTests
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

        // Three buildings ("A") and a strict subset ("B") — going A→B→A shrinks the hole count
        // and then grows it back, which is what walks over a stale buffer tail.
        private static FPBuildingRect[] SetA()
        {
            return new[]
            {
                Rect(-6, -6, -4, -4),
                Rect(1, 1, 3, 3),
                Rect(-6, 3, -4, 5),
            };
        }

        private static FPBuildingRect[] SetB()
        {
            return new[] { Rect(1, 1, 3, 3) };
        }

        private static void AssertMeshIdentical(FPNavMesh expected, FPNavMesh actual, string what)
        {
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(expected),
                FPNavMeshRebaker.ComputeFingerprint(actual),
                $"{what}: fingerprint diverged");

            Assert.AreEqual(expected.Triangles.Length, actual.Triangles.Length, $"{what}: triangle count");
            Assert.AreEqual(expected.Vertices.Length, actual.Vertices.Length, $"{what}: vertex count");

            for (int i = 0; i < expected.Vertices.Length; i++)
            {
                Assert.AreEqual(expected.Vertices[i].x.RawValue, actual.Vertices[i].x.RawValue, $"{what}: vertex {i}.x");
                Assert.AreEqual(expected.Vertices[i].y.RawValue, actual.Vertices[i].y.RawValue, $"{what}: vertex {i}.y");
                Assert.AreEqual(expected.Vertices[i].z.RawValue, actual.Vertices[i].z.RawValue, $"{what}: vertex {i}.z");
            }

            for (int i = 0; i < expected.Triangles.Length; i++)
            {
                FPNavMeshTriangle e = expected.Triangles[i];
                FPNavMeshTriangle a = actual.Triangles[i];
                Assert.AreEqual(e.v0, a.v0, $"{what}: triangle {i}.v0");
                Assert.AreEqual(e.v1, a.v1, $"{what}: triangle {i}.v1");
                Assert.AreEqual(e.v2, a.v2, $"{what}: triangle {i}.v2");
                for (int k = 0; k < 3; k++)
                {
                    Assert.AreEqual(e.GetNeighbor(k), a.GetNeighbor(k), $"{what}: triangle {i} neighbour {k}");
                    e.GetPortal(k, out int el, out int er);
                    a.GetPortal(k, out int al, out int ar);
                    Assert.AreEqual(el, al, $"{what}: triangle {i} portal {k} left");
                    Assert.AreEqual(er, ar, $"{what}: triangle {i} portal {k} right");
                }
                Assert.AreEqual(e.area.RawValue, a.area.RawValue, $"{what}: triangle {i} area");
                Assert.AreEqual(e.centerXZ.x.RawValue, a.centerXZ.x.RawValue, $"{what}: triangle {i} centre.x");
                Assert.AreEqual(e.centerXZ.y.RawValue, a.centerXZ.y.RawValue, $"{what}: triangle {i} centre.y");
            }

            Assert.AreEqual(expected.GridWidth, actual.GridWidth, $"{what}: grid width");
            Assert.AreEqual(expected.GridHeight, actual.GridHeight, $"{what}: grid height");
            // Element-wise over the LOGICAL range: the mesh exposes spans now, and a whole-array
            // comparison would either not compile or compare a recycled buffer's stale tail.
            Assert.AreEqual(expected.GridCells.Length, actual.GridCells.Length, $"{what}: grid cell count");
            for (int i = 0; i < expected.GridCells.Length; i++)
                Assert.AreEqual(expected.GridCells[i], actual.GridCells[i], $"{what}: grid cell {i}");

            Assert.AreEqual(expected.GridTriangleCount, actual.GridTriangleCount, $"{what}: grid triangle count");
            for (int i = 0; i < expected.GridTriangles.Length; i++)
                Assert.AreEqual(expected.GridTriangles[i], actual.GridTriangles[i], $"{what}: grid triangle {i}");
        }

        #endregion

        [Test]
        public void PooledRebake_IsBitIdenticalToUnpooled()
        {
            FPNavMesh baseMesh = BuildBase();
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);
            var ctx = new FPNavMeshRebakeContext(snapshot);

            FPNavMesh unpooled = FPNavMeshRebaker.Rebake(snapshot, SetA());
            FPNavMesh pooled = FPNavMeshRebaker.Rebake(ctx, SetA());

            AssertMeshIdentical(unpooled, pooled, "pool vs NonPooling");
        }

        [Test]
        public void PooledRebake_ZeroBuildings_IsBitIdenticalToUnpooled()
        {
            FPNavMesh baseMesh = BuildBase();
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);
            var ctx = new FPNavMeshRebakeContext(snapshot);

            FPNavMesh unpooled = FPNavMeshRebaker.Rebake(snapshot, null);
            FPNavMesh pooled = FPNavMeshRebaker.Rebake(ctx, null);

            AssertMeshIdentical(unpooled, pooled, "pool vs NonPooling (0 buildings)");
        }

        [Test]
        public void SamePool_TenSuccessiveRebakes_AllIdentical()
        {
            FPNavMesh baseMesh = BuildBase();
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);
            var ctx = new FPNavMeshRebakeContext(snapshot);

            FPNavMesh first = FPNavMeshRebaker.Rebake(ctx, SetA());
            for (int i = 2; i <= 10; i++)
            {
                FPNavMesh again = FPNavMeshRebaker.Rebake(ctx, SetA());
                AssertMeshIdentical(first, again, $"rebake #{i} on the same pool");
            }
        }

        [Test]
        public void SamePool_CrossInputReuse_AThenBThenA()
        {
            // The stale-tail stress: B has fewer holes than A, so the second A must not read
            // anything left over from A's own first run or from B's shorter run.
            FPNavMesh baseMesh = BuildBase();
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);

            FPNavMesh expectedA = FPNavMeshRebaker.Rebake(snapshot, SetA());
            FPNavMesh expectedB = FPNavMeshRebaker.Rebake(snapshot, SetB());
            Assert.AreNotEqual(
                FPNavMeshRebaker.ComputeFingerprint(expectedA),
                FPNavMeshRebaker.ComputeFingerprint(expectedB),
                "fixture is degenerate: A and B must differ for this test to have any power");

            var ctx = new FPNavMeshRebakeContext(snapshot);
            AssertMeshIdentical(expectedA, FPNavMeshRebaker.Rebake(ctx, SetA()), "A (first)");
            AssertMeshIdentical(expectedB, FPNavMeshRebaker.Rebake(ctx, SetB()), "B (shrinking)");
            AssertMeshIdentical(expectedA, FPNavMeshRebaker.Rebake(ctx, SetA()), "A (growing back)");
            AssertMeshIdentical(expectedB, FPNavMeshRebaker.Rebake(ctx, SetB()), "B (shrinking again)");
        }

        [Test]
        public void SamePool_SurvivesRejectedPlacement()
        {
            // A rejected placement throws out of the middle of a rebake. The pool resets at the
            // start of every rebake rather than returning buffers, so there is nothing to leak
            // and the next rebake must be unaffected.
            FPNavMesh baseMesh = BuildBase();
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);
            var ctx = new FPNavMeshRebakeContext(snapshot);

            FPNavMesh expected = FPNavMeshRebaker.Rebake(snapshot, SetA());
            AssertMeshIdentical(expected, FPNavMeshRebaker.Rebake(ctx, SetA()), "before rejection");

            Assert.Throws<System.InvalidOperationException>(
                () => FPNavMeshRebaker.Rebake(ctx, new[] { Rect(9, 9, 12, 12) }),
                "placement outside the walkable region must be rejected");

            AssertMeshIdentical(expected, FPNavMeshRebaker.Rebake(ctx, SetA()), "after rejection");
        }

        [Test]
        public void PoolIsNotSharedWithSnapshot_CreateSnapshotStaysUnpooled()
        {
            // The snapshot takes ownership of its CDT arrays, so it must never hold pooled
            // storage. Rebaking through a pool must leave the snapshot able to produce the same
            // result unpooled afterwards — if the snapshot had been aliased, this diverges.
            FPNavMesh baseMesh = BuildBase();
            FPNavMeshRebakeSnapshot snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);
            var ctx = new FPNavMeshRebakeContext(snapshot);

            FPNavMesh before = FPNavMeshRebaker.Rebake(snapshot, SetA());
            FPNavMeshRebaker.Rebake(ctx, SetA());
            FPNavMeshRebaker.Rebake(ctx, SetB());
            FPNavMesh after = FPNavMeshRebaker.Rebake(snapshot, SetA());

            AssertMeshIdentical(before, after, "snapshot immutability across pooled rebakes");
        }

        [Test]
        public void NonPoolingPreviewRents_HandBackAFreshArrayEveryTime()
        {
            // NonPooling is a process-global static, so a rent that returned an instance field
            // from it would be one array shared by every room in the process — the exact thing
            // the class note ("Never share one pool between rooms") forbids. The preview rents
            // used to do that: they had no `if (!_reuse)` branch at all, so the reuse path ran
            // on the non-reuse instance.
            //
            // Unreachable when it was written — the only callers take a required
            // FPBuildingPreviewScratch, which always carries a reuse=true pool of its own — which
            // is why nothing caught it. One optional-scratch overload would have been enough.
            var pool = FPNavMeshRebakeBufferPool.NonPooling;

            Assert.AreNotSame(pool.PreviewRects(8), pool.PreviewRects(8),
                "NonPooling.PreviewRects handed back the same array twice — that array is now shared "
                + "by every caller in the process");
            Assert.AreNotSame(pool.PreviewPlacements(8), pool.PreviewPlacements(8),
                "NonPooling.PreviewPlacements handed back the same array twice");
        }

        [Test]
        public void PreviewRents_GoThroughTheAllocationCounter()
        {
            // AllocatedArrayCount is the counter gate's only input, and Counted's own note calls
            // itself "the only way to produce an array here". The preview rents allocated around
            // it, so the counter read zero no matter how often they grew — a preview alloc gate
            // written against this number would have passed while measuring nothing.
            var pool = new FPNavMeshRebakeBufferPool();

            pool.PreviewRects(4);
            pool.PreviewPlacements(4);

            pool.ResetAllocatedArrayCount();
            pool.PreviewRects(4096);
            pool.PreviewPlacements(4096);
            Assert.AreEqual(2, pool.AllocatedArrayCount,
                "a preview rent that outgrows its slot must allocate THROUGH Counted, once each");

            // The other half, so the assertion above cannot be satisfied by counting every call:
            // at a size the pool already covers, nothing is allocated at all.
            pool.ResetAllocatedArrayCount();
            pool.PreviewRects(4096);
            pool.PreviewPlacements(4096);
            Assert.AreEqual(0, pool.AllocatedArrayCount,
                "a repeat rent at a covered size must reuse the slot");
        }

#if DEBUG
        [Test]
        public void EveryRent_ClearsStaleData_NotJustTheOnesThatRememberedTo()
        {
            // The class contract says every rent poisons in DEBUG, and this suite's own summary
            // leans on it: the A→B→A gate only catches a misread because the stale tail has been
            // made obviously wrong. Three rents did not honour it — PolyYs, PatchEdges and
            // PatchBoundaryEdges — and the pattern was mechanical rather than considered: those
            // three were written out by hand because their element type had no shared helper,
            // while every buffer that went through RentLongs/RentInts got the fill for free.
            //
            // Asserted as "the values I wrote are gone" rather than "the tail equals POISON_x",
            // because the contract is about stale data surviving, and the poison constant is a
            // private implementation detail. A rent that cleared to zero would also pass, and
            // should — zero is not last rebake's answer.
            var pool = new FPNavMeshRebakeBufferPool();

            // Large rent, filled with a marker; then a smaller rent of the same slot. The pool
            // hands back the same (oversized) array, so the marker is exactly the stale data the
            // caller must never be able to read.
            const long Marker = 0x5A5A5A5A5A5A5A5AL;

            FP64[] polyYs = pool.PolyYs(64);
            for (int i = 0; i < polyYs.Length; i++) polyYs[i] = FP64.FromRaw(Marker);
            FP64[] polyYs2 = pool.PolyYs(8);
            Assert.AreSame(polyYs, polyYs2, "premise: the pool must be reusing the same array");
            for (int i = 0; i < polyYs2.Length; i++)
                Assert.AreNotEqual(Marker, polyYs2[i].RawValue, $"PolyYs[{i}] still holds stale data");

            AssertEdgeRecordRentIsCleared(pool.PatchEdges, "PatchEdges");
            AssertEdgeRecordRentIsCleared(pool.PatchBoundaryEdges, "PatchBoundaryEdges");

            // Two more of the same kind, found later: the preview rents. Their callers pass an
            // explicit count so the tail is not read today, but that is the caller's discipline,
            // not the buffer's — and this buffer is the one whose length the caller varies by one
            // per frame, so a count that drifts finds a full tail of last frame's shapes.
            FPBuildingRect[] rects = pool.PreviewRects(64);
            for (int i = 0; i < rects.Length; i++)
                rects[i] = Rect(1, 1, 2, 2);
            FPBuildingRect[] rects2 = pool.PreviewRects(8);
            Assert.AreSame(rects, rects2, "premise: PreviewRects must be reusing the same array");
            for (int i = 0; i < rects2.Length; i++)
                Assert.AreNotEqual(Rect(1, 1, 2, 2).MinX.RawValue, rects2[i].MinX.RawValue,
                    $"PreviewRects[{i}] still holds stale data");

            FPBuildingPlacement[] places = pool.PreviewPlacements(64);
            for (int i = 0; i < places.Length; i++)
                places[i] = new FPBuildingPlacement(0x5A5A, 0, FP64.Zero, FP64.Zero, FP64.Zero);
            FPBuildingPlacement[] places2 = pool.PreviewPlacements(8);
            Assert.AreSame(places, places2, "premise: PreviewPlacements must be reusing the same array");
            for (int i = 0; i < places2.Length; i++)
                Assert.AreNotEqual(0x5A5A, places2[i].ShapeId,
                    $"PreviewPlacements[{i}] still holds a stale shape id");

            // Controls: two that always honoured it, so a change that broke the fill everywhere
            // could not pass this test by making the assertions vacuous.
            AssertEdgeRecordRentIsCleared(pool.BuildEdges, "BuildEdges");
            FP64[] holeYs = pool.HoleYs(64);
            for (int i = 0; i < holeYs.Length; i++) holeYs[i] = FP64.FromRaw(Marker);
            FP64[] holeYs2 = pool.HoleYs(8);
            for (int i = 0; i < holeYs2.Length; i++)
                Assert.AreNotEqual(Marker, holeYs2[i].RawValue, $"HoleYs[{i}] still holds stale data");
        }

        /// <summary>
        /// Rents large, marks every slot, rents smaller, and asserts the marker is unreadable.
        ///
        /// <para>Takes the rent as a delegate rather than two arrays on purpose: passing
        /// <c>pool.X(64), pool.X(8)</c> would evaluate BOTH rents before the marker is written, so
        /// the assertion would compare an array against itself and fail even on a correct pool. The
        /// order rent → mark → rent again is the thing being tested.</para>
        /// </summary>
        private static void AssertEdgeRecordRentIsCleared(
            Func<int, FPNavMeshBuildPipeline.EdgeRecord[]> rent, string label)
        {
            const long Marker = 0x5A5A5A5A5A5A5A5AL;
            FPNavMeshBuildPipeline.EdgeRecord[] first = rent(64);
            for (int i = 0; i < first.Length; i++)
                first[i] = new FPNavMeshBuildPipeline.EdgeRecord(Marker, 0x5A5A);

            FPNavMeshBuildPipeline.EdgeRecord[] second = rent(8);
            Assert.AreSame(first, second, $"premise: {label} must be reusing the same array");
            for (int i = 0; i < second.Length; i++)
                Assert.AreNotEqual(Marker, second[i].Key, $"{label}[{i}] still holds stale data");
        }
#endif
    }
}
