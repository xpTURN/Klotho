using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// A/B gate for the adjacency pairing: the shipping implementation must be bit-identical to
    /// BOTH earlier ones, element by element (neighbours AND portals), including non-manifold
    /// input where an edge occurs three or more times.
    ///
    /// Three-way rather than pairwise, because the pipeline's pairing order has been restated
    /// twice — Dictionary, then a comparison sort, then a radix sort. Matching only the nearest
    /// predecessor would leave it ambiguous which one is the standard; the Dictionary form shares
    /// no code with either sort, so it is the one that keeps the answer anchored.
    ///
    /// All three builders are pure functions of (triangles, vertices), so the fixtures feed
    /// triangle arrays directly — no pipeline run needed.
    /// </summary>
    [TestFixture]
    public class FPNavMeshAdjacencyPairingTests
    {
        #region Helpers

        internal static FPNavMeshTriangle[] MakeTriangles(int[] indices)
        {
            var tris = new FPNavMeshTriangle[indices.Length / 3];
            for (int i = 0; i < tris.Length; i++)
            {
                tris[i] = new FPNavMeshTriangle
                {
                    v0 = indices[i * 3],
                    v1 = indices[i * 3 + 1],
                    v2 = indices[i * 3 + 2],
                    neighbor0 = -1, neighbor1 = -1, neighbor2 = -1,
                };
            }
            return tris;
        }

        internal static void AssertAdjacencyIdentical(int[] indices, FPVector3[] vertices, string what)
        {
            FPNavMeshTriangle[] radix = MakeTriangles(indices);
            FPNavMeshTriangle[] comparison = MakeTriangles(indices);
            FPNavMeshTriangle[] dictionary = MakeTriangles(indices);

            FPNavMeshBuildPipeline.BuildAdjacency(radix, radix.Length, vertices);
            FPNavMeshBuildPipeline.BuildAdjacencyComparisonReference(comparison, comparison.Length, vertices);
            FPNavMeshBuildPipeline.BuildAdjacencyDictionaryReference(dictionary, vertices);

            AssertPairEqual(dictionary, comparison, $"{what}: comparison vs dictionary");
            AssertPairEqual(comparison, radix, $"{what}: radix vs comparison");
        }

        private static void AssertPairEqual(
            FPNavMeshTriangle[] reference, FPNavMeshTriangle[] got, string what)
        {
            for (int i = 0; i < reference.Length; i++)
            {
                for (int e = 0; e < 3; e++)
                {
                    Assert.AreEqual(reference[i].GetNeighbor(e), got[i].GetNeighbor(e),
                        $"{what}: neighbour mismatch at triangle {i} edge {e}");

                    reference[i].GetPortal(e, out int refLeft, out int refRight);
                    got[i].GetPortal(e, out int gotLeft, out int gotRight);
                    Assert.AreEqual(refLeft, gotLeft, $"{what}: portal left mismatch at triangle {i} edge {e}");
                    Assert.AreEqual(refRight, gotRight, $"{what}: portal right mismatch at triangle {i} edge {e}");
                }
            }
        }

        internal static FPVector3 V(int x, int z)
        {
            return new FPVector3(FP64.FromInt(x), FP64.Zero, FP64.FromInt(z));
        }

        #endregion

        [Test]
        public void SortPairing_MatchesDictionary_ManifoldGrid()
        {
            // 6x6 vertex grid → 50 triangles, every interior edge shared by exactly two.
            const int N = 6;
            var verts = new FPVector3[N * N];
            for (int r = 0; r < N; r++)
                for (int c = 0; c < N; c++)
                    verts[r * N + c] = V(c, r);

            var idx = new List<int>();
            for (int r = 0; r + 1 < N; r++)
            {
                for (int c = 0; c + 1 < N; c++)
                {
                    int a = r * N + c, b = r * N + c + 1;
                    int d = (r + 1) * N + c, e = (r + 1) * N + c + 1;
                    idx.Add(a); idx.Add(b); idx.Add(e);
                    idx.Add(a); idx.Add(e); idx.Add(d);
                }
            }

            AssertAdjacencyIdentical(idx.ToArray(), verts, "manifold grid");
        }

        [Test]
        public void SortPairing_MatchesDictionary_NonManifoldEdge()
        {
            // Edge (0,1) shared by three triangles: add→match→remove pairs occurrences 1-2 and
            // leaves the third unpaired — the sorted grouping must do exactly the same.
            var verts = new[] { V(0, 0), V(2, 0), V(1, 2), V(1, -2), V(3, 1) };
            var idx = new[]
            {
                0, 1, 2,
                1, 0, 3,
                0, 1, 4,
            };

            AssertAdjacencyIdentical(idx, verts, "non-manifold (3 occurrences)");

            // The third occurrence must actually stay a boundary edge — otherwise this fixture
            // would pass vacuously with both builders pairing everything.
            var tris = MakeTriangles(idx);
            FPNavMeshBuildPipeline.BuildAdjacency(tris, tris.Length, verts);
            Assert.AreEqual(1, tris[0].GetNeighbor(0), "occurrences 1-2 must pair");
            Assert.AreEqual(0, tris[1].GetNeighbor(0), "occurrences 1-2 must pair");
            Assert.AreEqual(-1, tris[2].GetNeighbor(0), "the odd occurrence must stay a boundary edge");
        }

        [Test]
        public void SortPairing_MatchesDictionary_FourOccurrences()
        {
            // Four occurrences → two independent pairs (1-2, 3-4) in appearance order.
            var verts = new[] { V(0, 0), V(2, 0), V(1, 2), V(1, -2), V(3, 1), V(-3, 1) };
            var idx = new[]
            {
                0, 1, 2,
                1, 0, 3,
                0, 1, 4,
                1, 0, 5,
            };

            AssertAdjacencyIdentical(idx, verts, "non-manifold (4 occurrences)");

            var tris = MakeTriangles(idx);
            FPNavMeshBuildPipeline.BuildAdjacency(tris, tris.Length, verts);
            Assert.AreEqual(1, tris[0].GetNeighbor(0), "occurrence 1 pairs with 2");
            Assert.AreEqual(3, tris[2].GetNeighbor(0), "occurrence 3 pairs with 4");
        }

        [Test]
        public void SortPairing_MatchesDictionary_RealAsset()
        {
            // Full-scale check on the real triangulation the rebaker actually feeds it.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root not found");

            string path = Path.Combine(dir.FullName, "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");
            if (!File.Exists(path))
                Assert.Ignore("Field asset missing");

            FPNavMesh mesh = FPNavMeshSerializer.Deserialize(path);
            var idx = new int[mesh.Triangles.Length * 3];
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                idx[i * 3] = mesh.Triangles[i].v0;
                idx[i * 3 + 1] = mesh.Triangles[i].v1;
                idx[i * 3 + 2] = mesh.Triangles[i].v2;
            }

            AssertAdjacencyIdentical(idx, mesh.Vertices.ToArray(), "Field asset");
        }
    }
}
