using System;
using System.IO;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// V1 — the obstacle extractor's output, pinned byte for byte.
    ///
    /// <para>Recorded BEFORE the allocation work in Plan-ObstacleExtractAlloc, which is the only
    /// order that proves anything: that plan swaps exact-size outputs for reused oversized buffers
    /// and moves the ring-offset list into scratch, and none of that is allowed to move a single
    /// vertex. A golden taken afterwards would only agree with itself.</para>
    ///
    /// <para>What makes this the load-bearing gate rather than one of several: an obstacle set that
    /// came out subtly wrong does not throw, does not move the navmesh fingerprint, and does not
    /// desync — the navmesh is outside the state hash, so every peer computes the same wrong walls
    /// and agents merely steer a little differently on all of them at once.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshObstacleExtractGoldenTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root not found from test base directory");
            return dir.FullName;
        }

        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private static void Fold(ref ulong h, long v)
        {
            for (int b = 0; b < 8; b++)
            {
                h ^= (byte)(v >> (b * 8));
                h *= FnvPrime;
            }
        }

        private static ulong HashVerts(FPVector2[] v, int count)
        {
            ulong h = FnvOffset;
            Fold(ref h, count);
            for (int i = 0; i < count; i++)
            {
                Fold(ref h, v[i].x.RawValue);
                Fold(ref h, v[i].y.RawValue);
            }
            return h;
        }

        private static ulong HashInts(int[] a, int count)
        {
            ulong h = FnvOffset;
            Fold(ref h, count);
            for (int i = 0; i < count; i++)
                Fold(ref h, a[i]);
            return h;
        }

        /// <param name="rel">asset path</param>
        /// <param name="vertCount">boundary ring vertices</param>
        /// <param name="ringCount">rings</param>
        /// <param name="vertsHash">FNV-1a over every vertex's raw fixed-point pair</param>
        /// <param name="offsetsHash">…over the ring offsets</param>
        /// <param name="csrHash">…over triSegStart then triSegList</param>
        [TestCase("Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes",
            15317, 1679, 0x690F69C1A1D8EFAEUL, 0x05551B8F63853AAAUL, 0x991F0ADB78B32D71UL)]
        [TestCase("Samples/Brawler/Assets/Brawler/Data/Stage01.NavMeshData.bytes",
            108, 5, 0xAFA387E323498D57UL, 0x2F721A24407044D0UL, 0xEEBB9BAF3B3762E7UL)]
        [TestCase("Samples/Brawler/Assets/Brawler/Data/Stage02.NavMeshData.bytes",
            60, 1, 0x89D85BD308F50E85UL, 0x392209F14DEA4C24UL, 0x3AA8612AA00D8A0DUL)]
        public void ExtractOutput_IsUnchanged(string rel, int vertCount, int ringCount,
            ulong vertsHash, ulong offsetsHash, ulong csrHash)
        {
            string path = Path.Combine(RepoRoot(), rel);
            if (!File.Exists(path))
                Assert.Ignore($"{rel} missing — nothing to pin");

            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
            FPNavMeshObstacleExtractor.Extract(mesh,
                out FPVector2[] verts, out int[] offsets,
                out int[] triSegStart, out int[] triSegList);

            ulong csr = FnvOffset;
            for (int i = 0; i < triSegStart.Length; i++) Fold(ref csr, triSegStart[i]);
            for (int i = 0; i < triSegList.Length; i++) Fold(ref csr, triSegList[i]);

            Assert.AreEqual(vertCount, verts.Length, $"{rel}: ring vertex count moved");
            Assert.AreEqual(ringCount, offsets.Length, $"{rel}: ring count moved");
            Assert.AreEqual(vertsHash, HashVerts(verts, verts.Length),
                $"{rel}: a boundary vertex moved. Nothing downstream reports this — the navmesh is "
                + "outside the state hash, so every peer would steer around the same wrong wall");
            Assert.AreEqual(offsetsHash, HashInts(offsets, offsets.Length),
                $"{rel}: the ring split moved");
            Assert.AreEqual(csrHash, csr,
                $"{rel}: the triangle->segment CSR moved — graph-local obstacle selection reads it");
        }

        // ── V3 — the array-only overloads still promise exact size ───────────────

        [Test]
        public void ArrayOnlyOverload_StaysExactSize()
        {
            // The editor visualisers derive ring bounds from these lengths — `offsets.Length` for
            // the ring count and `verts.Length` for where the last ring ends. Hand them an
            // oversized array and they draw rings that do not exist and stretch the last one to
            // the end of the buffer. There are four deployed copies of that code, so the failure
            // would be quiet and spread out.
            FPNavMesh mesh = SmallMesh();
            int boundaryEdges = CountBoundaryEdges(mesh);

            FPNavMeshObstacleExtractor.Extract(mesh, out FPVector2[] verts, out int[] offsets);

            Assert.AreEqual(boundaryEdges, verts.Length,
                "the array-only overload must return vertices at exactly the ring length");
            Assert.Greater(offsets.Length, 0);
            Assert.LessOrEqual(offsets[offsets.Length - 1], verts.Length,
                "the last ring must start inside the array it indexes");
        }

        // ── V2 — a reused scratch must not leak its tail ─────────────────────────

        [Test]
        public void ScratchReuse_SmallAfterLarge_DoesNotLeakTheTail()
        {
            // The scratch grows to the largest mesh it has seen and never shrinks. That is the
            // point — but it means a later, smaller mesh gets a buffer with the previous mesh's
            // walls still sitting past the count. Reading through Length instead of the count
            // would load those as obstacles: real geometry, plausible values, silently wrong.
            FPNavMesh large = LargeMesh();
            FPNavMesh small = SmallMesh();

            var scratch = new FPNavMeshObstacleExtractor.ExtractScratch();
            FPNavMeshObstacleExtractor.Extract(large, scratch,
                out _, out int largeVerts, out _, out int largeRings, out _, out _);

            FPNavMeshObstacleExtractor.Extract(small, scratch,
                out FPVector2[] verts, out int vertexCount,
                out int[] offsets, out int polygonCount, out _, out _);

            Assert.Less(vertexCount, largeVerts, "the second mesh must be the smaller one");
            Assert.GreaterOrEqual(verts.Length, largeVerts,
                "the buffer is expected to stay at the larger size — that is what makes this a test");
            Assert.LessOrEqual(polygonCount, largeRings);

            var avoidance = new FPNavAvoidance();
            avoidance.LoadObstacles(verts, vertexCount, offsets, polygonCount);

            Assert.AreEqual(vertexCount, avoidance.DebugObstacleCount,
                "the obstacle count must follow the count, not the buffer length — a mismatch here "
                + "means the previous mesh's walls are still loaded");
        }

        // ── Fixture ──────────────────────────────────────────────────────────────

        private static int CountBoundaryEdges(FPNavMesh mesh)
        {
            int n = 0;
            for (int t = 0; t < mesh.Triangles.Length; t++)
                for (int e = 0; e < 3; e++)
                    if (mesh.Triangles[t].GetNeighbor(e) == -1) n++;
            return n;
        }

        private static FPNavMesh SmallMesh() => BuildGrid(10, 10);
        private static FPNavMesh LargeMesh() => BuildGrid(10, 2);

        private static FPNavMesh BuildGrid(int half, int step)
        {
            var pts = new System.Collections.Generic.List<(int x, int z)>();
            for (int x = -half; x <= half; x += step)
                for (int z = -half; z <= half; z += step)
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
    }
}
