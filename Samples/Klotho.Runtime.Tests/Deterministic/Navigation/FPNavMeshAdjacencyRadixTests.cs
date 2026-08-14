using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The two gates the radix adjacency sort needs and the fixed fixtures cannot give.
    ///
    /// <para><b>Why fixed fixtures are not enough.</b> The radix sort is only correct because it
    /// is STABLE — ties must keep their original order. Break that and nothing visible happens on
    /// any valid triangulation: when an edge appears exactly twice, the two occurrences become
    /// each other's neighbour, so swapping them writes the same slots with the same values. The
    /// difference appears only at three or more occurrences, where it changes WHICH one is left
    /// unpaired. CDT output never contains such an edge, so the goldens, the real-asset fixture
    /// and the whole rebake suite would pass a broken sort.</para>
    ///
    /// <para>An authored NavMesh export, on the other hand, can absolutely be non-manifold, and
    /// the offline baker has no gate that rejects it — so the bake path is where this class of
    /// bug would actually ship. That is what the randomised differential covers.</para>
    ///
    /// <para>The second gate covers the other half: the radix sort is the first thing in
    /// <c>BuildAdjacency</c> to rent buffers whose CONTENT must be cleared per call. Every
    /// existing correctness check runs unpooled, so a stale-histogram bug would have had no
    /// detector at all.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshAdjacencyRadixTests
    {
        // ── randomised differential ──────────────────────────────────────────

        private const int Seeds = 240;

        /// <summary>
        /// Deterministic 32-bit generator. Not System.Random: the seed→sequence mapping has to be
        /// stable across runtimes and framework versions, or a reported failing seed is useless.
        /// </summary>
        private struct Rng
        {
            private uint _state;
            internal Rng(int seed) { _state = (uint)seed * 2654435761u + 1u; }
            internal int Next(int bound)
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return (int)(_state % (uint)bound);
            }
        }

        [Test]
        public void RadixMatchesBothReferences_AcrossRandomTriangleSoups()
        {
            // Coverage is asserted, not assumed: the point of the sweep is to reach edges that
            // occur 3+ times, and a generator that quietly stopped producing them would leave
            // this test green and empty.
            var multiplicitySeen = new HashSet<int>();

            for (int seed = 0; seed < Seeds; seed++)
            {
                var rng = new Rng(seed);

                // Log sweep on the vertex count: it sizes the radix buckets, and a small V with
                // many triangles is what forces edges to repeat.
                int vExp = seed % 7;                       // 0..6
                int vertexCount = 3 + (1 << vExp) * (1 + rng.Next(3));   // 4 .. ~1000
                int triangleCount = 1 + rng.Next(seed % 3 == 0 ? 2000 : 60);

                var vertices = new FPVector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    // Integer lattice: ComputePortalLeftRight reads the coordinates, so they are
                    // part of the output being compared, not decoration.
                    vertices[i] = FPNavMeshAdjacencyPairingTests.V(rng.Next(41) - 20, rng.Next(41) - 20);
                }

                var indices = new int[triangleCount * 3];
                for (int t = 0; t < triangleCount; t++)
                {
                    // Degenerate triangles (repeated or collinear vertices) are deliberately NOT
                    // filtered — adjacency and portals are defined on them too, and the baker's
                    // degenerate removal runs before this stage only on the full path.
                    indices[t * 3] = rng.Next(vertexCount);
                    indices[t * 3 + 1] = rng.Next(vertexCount);
                    indices[t * 3 + 2] = rng.Next(vertexCount);
                }

                multiplicitySeen.Add(MaxEdgeMultiplicity(indices));

                try
                {
                    FPNavMeshAdjacencyPairingTests.AssertAdjacencyIdentical(
                        indices, vertices, $"seed {seed} (V={vertexCount}, T={triangleCount})");
                }
                catch (AssertionException e)
                {
                    Assert.Fail($"seed {seed} (V={vertexCount}, T={triangleCount}) diverged:\n{e.Message}");
                }
            }

            foreach (int m in new[] { 1, 2, 3, 4 })
            {
                Assert.IsTrue(multiplicitySeen.Contains(m),
                    $"the sweep never produced a mesh whose busiest edge occurs {m} time(s) — "
                    + "the generator stopped covering what this test exists for");
            }
            Assert.IsTrue(HasAtLeast(multiplicitySeen, 5),
                "the sweep never produced an edge occurring 5+ times");
        }

        private static int MaxEdgeMultiplicity(int[] indices)
        {
            var counts = new Dictionary<long, int>();
            int max = 0;
            for (int t = 0; t * 3 < indices.Length; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    int va = indices[t * 3 + e];
                    int vb = indices[t * 3 + (e + 1) % 3];
                    long key = va < vb ? ((long)va << 32) | (uint)vb : ((long)vb << 32) | (uint)va;
                    counts.TryGetValue(key, out int c);
                    counts[key] = ++c;
                    if (c > max)
                        max = c;
                }
            }
            return max;
        }

        private static bool HasAtLeast(HashSet<int> set, int threshold)
        {
            foreach (int v in set)
            {
                if (v >= threshold)
                    return true;
            }
            return false;
        }

        // ── pooled vs unpooled ───────────────────────────────────────────────

        private static FPNavMesh BuildBase(int half)
        {
            int side = half * 2 + 1;
            var pts = new (int x, int z)[side * side];
            int n = 0;
            for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++)
                    pts[n++] = (x, z);

            var vertices = new FPVector3[pts.Length];
            var xs = new long[pts.Length];
            var zs = new long[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        /// <summary>A grid of separated 1x1 footprints, so 32 of them still fit the base.</summary>
        private static FPBuildingRect[] Buildings(int count)
        {
            var result = new FPBuildingRect[count];
            for (int i = 0; i < count; i++)
            {
                FP64 x = FP64.FromInt(-18 + (i % 8) * 5);
                FP64 z = FP64.FromInt(-8 + (i / 8) * 5);
                result[i] = new FPBuildingRect(x, z, x + FP64.One, z + FP64.One, FP64.Zero);
            }
            return result;
        }

        [Test]
        public void PooledRebake_MatchesUnpooled_AsBucketCountGrowsAndShrinks()
        {
            FPNavMesh baseMesh = BuildBase(half: 20);
            var ctx = new FPNavMeshRebakeContext(FPNavMeshRebaker.CreateSnapshot(baseMesh));
            FPNavMeshRebakeSnapshot fresh = FPNavMeshRebaker.CreateSnapshot(baseMesh);

            // Up and then back DOWN. The radix histograms are rented once and reused, and their
            // length is derived from the largest vertex index in the mesh — which grows with the
            // building count. Only the descending half can catch a stale tail: on the way up the
            // buffer is cleared over a range that covers everything written before.
            foreach (int n in new[] { 0, 1, 8, 32, 8, 1, 0 })
            {
                FPBuildingRect[] buildings = Buildings(n);

                FPNavMesh pooled = FPNavMeshRebaker.Rebake(ctx, buildings);
                ulong pooledFp = FPNavMeshRebaker.ComputeFingerprint(pooled);
                ctx.CommitSwap(pooled);

                ulong unpooledFp = FPNavMeshRebaker.ComputeFingerprint(
                    FPNavMeshRebaker.Rebake(fresh, buildings));

                Assert.AreEqual(unpooledFp, pooledFp,
                    $"{n} buildings: the pooled rebake must produce the same mesh as the unpooled "
                    + "one — a difference here is a reused buffer that kept content it should not");
            }
        }
    }
}
