using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Correctness checks for the buffer pool, which exist for a specific reason: pool-reuse
    /// corruption is DETERMINISTIC. Both peers run the same code with the same pool history, so
    /// they compute the same wrong mesh and agree about it — the nav fingerprint and the state
    /// hash both stay silent. Same argument as
    /// <see cref="FPNavMeshOversizedBackingTests"/>, one layer earlier: these cover the buffers
    /// that feed the mesh rather than the mesh's own storage.
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakeZeroAllocContractTests
    {
        #region Fixtures

        private static FPNavMesh BuildBase(int half = 8)
        {
            var pts = new List<(int x, int z)>();
            for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++)
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

        private static FPBuildingRect[] OneBuilding() => new[]
        {
            new FPBuildingRect(FP64.FromInt(-1), FP64.FromInt(-1), FP64.FromInt(1), FP64.FromInt(1), FP64.Zero),
        };

        private static (long[] xs, long[] zs, int[] cons) SquareRing()
        {
            // A closed square boundary — enough for a resume that actually inserts constraints.
            long u = 1024;
            var xs = new long[] { -4 * u, 4 * u, 4 * u, -4 * u, 0, 2 * u, -2 * u };
            var zs = new long[] { -4 * u, -4 * u, 4 * u, 4 * u, 0, 2 * u, -2 * u };
            var cons = new int[] { 0, 1, 1, 2, 2, 3, 3, 0 };
            return (xs, zs, cons);
        }

        #endregion

        [Test]
        public void HoleArrays_OversizedWithPoisonedTail_MatchExactSize()
        {
            // The Cdt constructor used to take the hole count from holeXs.Length. With
            // pooled hole buffers the tail is the PREVIOUS rebake's holes, so a surviving Length
            // read pulls extra vertices — and the ghost block — into the hole range. The tail is
            // filled with in-domain coordinates on purpose: zeros would trip the duplicate-hole
            // guard and mask the real failure with an exception that looks like a caught bug.
            var (xs, zs, cons) = SquareRing();
            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);

            var holeXs = new long[] { 512, -512 };
            var holeZs = new long[] { 512, -512 };
            int[] exact = FPConstrainedDelaunay.TriangulateFromSnapshot(
                snap, holeXs, holeZs, 2, Array.Empty<int>(), 0, out int exactCount,
                eraseOuterAndHoles: false);

            var paddedXs = new long[8];
            var paddedZs = new long[8];
            Array.Copy(holeXs, paddedXs, 2);
            Array.Copy(holeZs, paddedZs, 2);
            for (int i = 2; i < paddedXs.Length; i++)
            {
                // Valid, distinct, in-domain — indistinguishable from real holes if ever read.
                paddedXs[i] = 1024 + i * 256;
                paddedZs[i] = -1024 - i * 256;
            }

            int[] padded = FPConstrainedDelaunay.TriangulateFromSnapshot(
                snap, paddedXs, paddedZs, 2, Array.Empty<int>(), 0, out int paddedCount,
                eraseOuterAndHoles: false);

            Assert.AreEqual(exactCount, paddedCount, "the hole count must come from the count, not Length");
            for (int i = 0; i < exactCount; i++)
                Assert.AreEqual(exact[i], padded[i], $"index {i} must not change");
        }

        [Test]
        public void HoleConstraints_OversizedWithPoisonedTail_MatchExactSize()
        {
            // Same failure mode one argument over: InsertConstraints derived its loop bound from
            // constraintPairs.Length, so a pooled buffer's tail would be inserted as real edges.
            var (xs, zs, cons) = SquareRing();
            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);

            var holeXs = new long[] { 512, -512, 512, -512 };
            var holeZs = new long[] { 512, -512, -512, 512 };
            int baseCount = snap.RealCount;
            var exactCons = new int[] { baseCount, baseCount + 1 };

            int[] exact = FPConstrainedDelaunay.TriangulateFromSnapshot(
                snap, holeXs, holeZs, 4, exactCons, 2, out int exactCount);

            var paddedCons = new int[8];
            Array.Copy(exactCons, paddedCons, 2);
            paddedCons[2] = baseCount + 2; paddedCons[3] = baseCount + 3;
            paddedCons[4] = baseCount + 1; paddedCons[5] = baseCount + 2;
            paddedCons[6] = 0; paddedCons[7] = 1;

            int[] padded = FPConstrainedDelaunay.TriangulateFromSnapshot(
                snap, holeXs, holeZs, 4, paddedCons, 2, out int paddedCount);

            Assert.AreEqual(exactCount, paddedCount, "constraint count must come from the count, not Length");
            for (int i = 0; i < exactCount; i++)
                Assert.AreEqual(exact[i], padded[i], $"index {i} must not change");
        }

        [Test]
        public void HoleCount_OutsideArray_Throws()
        {
            var (xs, zs, cons) = SquareRing();
            var snap = FPConstrainedDelaunay.BuildSnapshot(xs, zs, cons);
            var holeXs = new long[] { 512 };
            var holeZs = new long[] { 512 };

            Assert.Throws<ArgumentException>(() => FPConstrainedDelaunay.TriangulateFromSnapshot(
                snap, holeXs, holeZs, 2, Array.Empty<int>(), 0, out _));
            Assert.Throws<ArgumentException>(() => FPConstrainedDelaunay.TriangulateFromSnapshot(
                snap, holeXs, holeZs, -1, Array.Empty<int>(), 0, out _));
        }

        [Test]
        public void SharedZeroAreas_NeverShrinksBelowRequest()
        {
            // The ONLY guarantee the lock-free sharing needs. A racing grow is harmless
            // because every element is 0 and an older array is still large enough for the caller
            // that took it — but only if a request is never answered with something smaller.
            int[] big = FPNavMeshRebaker.SharedZeroAreas(4096);
            Assert.GreaterOrEqual(big.Length, 4096);
            int[] small = FPNavMeshRebaker.SharedZeroAreas(16);
            Assert.GreaterOrEqual(small.Length, 16, "a smaller request must not shrink the shared array");

            foreach (int v in big)
                Assert.AreEqual(0, v, "the shared array is read-only zeros — anything else means someone wrote to it");
        }

        [Test]
        public void SharedZeroAreas_ProducesSameMeshAsDedicatedZeroArray()
        {
            // Sharing one zero array rests entirely on "the content of `areas` cannot affect the
            // result". This is
            // the direct check of that sentence — same geometry, one run fed the process-shared
            // (and therefore oversized) zero array, one fed an exact-size dedicated one.
            FPNavMesh baseMesh = BuildBase();
            var snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);
            FPNavMesh viaShared = FPNavMeshRebaker.Rebake(snapshot, OneBuilding());

            // Rebuild the same mesh through the pipeline with a dedicated exact-size zero array.
            var (xs, zs) = (new long[baseMesh.VertexCount], new long[baseMesh.VertexCount]);
            for (int i = 0; i < baseMesh.VertexCount; i++)
            {
                xs[i] = FPGeoPredicates.Snap(baseMesh.Vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(baseMesh.Vertices[i].z);
            }

            var dedicated = new int[viaShared.TriangleCount];
            var vertices = new FPVector3[viaShared.VertexCount];
            viaShared.Vertices.CopyTo(vertices);
            var indices = new int[viaShared.TriangleCount * 3];
            for (int t = 0; t < viaShared.TriangleCount; t++)
            {
                indices[t * 3] = viaShared.Triangles[t].v0;
                indices[t * 3 + 1] = viaShared.Triangles[t].v1;
                indices[t * 3 + 2] = viaShared.Triangles[t].v2;
            }

            FPNavMesh viaDedicated = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                vertices, indices, dedicated, baseMesh.GridCellSize, null,
                baseMesh.BakeAgentRadius, baseMesh.BakeMaxSlopeDeg,
                baseMesh.BakeAgentHeight, baseMesh.BakeAgentClimb);

            Assert.AreEqual(viaShared.TriangleCount, viaDedicated.TriangleCount);
            for (int t = 0; t < viaShared.TriangleCount; t++)
            {
                // areaMask is the only field `areas` can reach, and it is folded into the nav
                // fingerprint — so equality here is exactly the claim being made.
                Assert.AreEqual(viaShared.Triangles[t].areaMask, viaDedicated.Triangles[t].areaMask,
                    $"triangle {t} areaMask must not depend on which zero array was passed");
            }
        }

        [Test]
        public void NonUniformBase_KeepsAreaMaskOne_UnderSharedZeroAreas()
        {
            // The one path where `areas` survives into the output: when the base is non-uniform,
            // InheritUniformAreaAttributes early-returns and whatever BuildCore computed stays.
            // With an all-zero areas array that value is `1 << 0 == 1` — the same value a
            // dedicated zero array would leave. Without this fixture, the shared array is only ever exercised on
            // the path that overwrites areaMask anyway, and the claim would be untested.
            FPNavMesh baseMesh = BuildBase();
            var mutable = baseMesh.TrianglesMutable;
            mutable[0].areaMask = 1 << 3;   // make the base non-uniform

            var snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);
            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(snapshot, OneBuilding());

            for (int t = 0; t < rebaked.TriangleCount; t++)
                Assert.AreEqual(1, rebaked.Triangles[t].areaMask,
                    $"triangle {t}: a non-uniform base leaves `1 << 0` from the zero areas array");
        }

        [Test]
        public void PooledRebake_IsBitIdenticalToUnpooled_AndIndependentOfPoolHistory()
        {
            // A/B for the pooled buffers, plus the reuse-history axis: a context that has already rebaked
            // several different building sets must produce exactly what a fresh one does. That
            // covers every stale-tail and stale-container hazard the pooling introduced at once,
            // including any future decision to pool the hole map (which today is allocated fresh
            // per rebake and therefore has no reuse order to get wrong).
            FPNavMesh baseMesh = BuildBase();
            var snapshot = FPNavMeshRebaker.CreateSnapshot(baseMesh);
            FPBuildingRect[] target = OneBuilding();

            FPNavMesh unpooled = FPNavMeshRebaker.Rebake(snapshot, target);

            var ctx = new FPNavMeshRebakeContext(snapshot);
            var others = new[]
            {
                new FPBuildingRect(FP64.FromInt(3), FP64.FromInt(3), FP64.FromInt(5), FP64.FromInt(5), FP64.Zero),
            };
            for (int i = 0; i < 3; i++)
            {
                FPNavMesh warm = FPNavMeshRebaker.Rebake(ctx, i % 2 == 0 ? others : target);
                ctx.CommitSwap(warm);
            }

            FPNavMesh pooled = FPNavMeshRebaker.Rebake(ctx, target);

            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(unpooled),
                FPNavMeshRebaker.ComputeFingerprint(pooled),
                "a pooled rebake must be bit-identical to an unpooled one regardless of pool history");
            Assert.AreEqual(unpooled.TriangleCount, pooled.TriangleCount);
            for (int t = 0; t < unpooled.TriangleCount; t++)
            {
                Assert.AreEqual(unpooled.Triangles[t].v0, pooled.Triangles[t].v0, $"tri {t}.v0");
                Assert.AreEqual(unpooled.Triangles[t].neighbor0, pooled.Triangles[t].neighbor0, $"tri {t}.n0");
                Assert.AreEqual(unpooled.Triangles[t].areaMask, pooled.Triangles[t].areaMask, $"tri {t}.areaMask");
            }
        }
    }
}
