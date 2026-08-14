using System;
using System.Collections.Generic;
using System.Numerics;
using SysRandom = System.Random;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// FPConstrainedDelaunay tests. Pins: golden checksum on a fixed
    /// map-with-hole input, the empty-circumcircle property against the exact predicates,
    /// input-order independence (canonical output), constraint edge presence, parity
    /// erasure of outer region and hole interiors, collinear-vertex constraint splitting,
    /// crossing-constraint rejection, duplicate welding, and cocircular grid stability.
    /// </summary>
    [TestFixture]
    public class FPConstrainedDelaunayTests
    {
        #region Helpers

        private static int[] RingEdges(int[] ring)
        {
            var edges = new int[ring.Length * 2];
            for (int i = 0; i < ring.Length; i++)
            {
                edges[i * 2] = ring[i];
                edges[i * 2 + 1] = ring[(i + 1) % ring.Length];
            }
            return edges;
        }

        private static HashSet<(int, int)> EdgeSet(int[] tris)
        {
            var set = new HashSet<(int, int)>();
            for (int i = 0; i < tris.Length; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = tris[i + k];
                    int b = tris[i + (k + 1) % 3];
                    set.Add(a < b ? (a, b) : (b, a));
                }
            }
            return set;
        }

        private static void AssertAllCcw(long[] xs, long[] zs, int[] tris)
        {
            for (int i = 0; i < tris.Length; i += 3)
            {
                Assert.AreEqual(1, FPGeoPredicates.Orient2D(
                    xs[tris[i]], zs[tris[i]],
                    xs[tris[i + 1]], zs[tris[i + 1]],
                    xs[tris[i + 2]], zs[tris[i + 2]]), $"triangle {i / 3} must be CCW");
            }
        }

        /// <summary>Point-in-polygon (ray cast, exact integers; boundary treated as inside).</summary>
        private static bool PointInRing(long px, long pz, long[] xs, long[] zs, int[] ring)
        {
            bool inside = false;
            for (int i = 0; i < ring.Length; i++)
            {
                long ax = xs[ring[i]], az = zs[ring[i]];
                long bx = xs[ring[(i + 1) % ring.Length]], bz = zs[ring[(i + 1) % ring.Length]];
                if ((az > pz) != (bz > pz))
                {
                    // px < ax + (bx-ax)*(pz-az)/(bz-az) — cross-multiplied, sign-aware.
                    BigInteger lhs = (BigInteger)(px - ax) * (bz - az);
                    BigInteger rhs = (BigInteger)(bx - ax) * (pz - az);
                    if (bz - az > 0 ? lhs < rhs : lhs > rhs)
                        inside = !inside;
                }
            }
            return inside;
        }

        private static ulong Fnv1a(int[] values)
        {
            ulong h = 14695981039346656037UL;
            foreach (int v in values)
            {
                unchecked
                {
                    h = (h ^ (uint)v) * 1099511628211UL;
                    h = (h ^ ((uint)v >> 16)) * 1099511628211UL;
                }
            }
            return h;
        }

        /// <summary>
        /// Input-order-independent geometry hash: each triangle is rotated so its
        /// lexicographically smallest coordinate comes first (cyclic — winding preserved),
        /// triangles are sorted by coordinates, then hashed. Vertex indices never enter.
        /// </summary>
        private static ulong GeometryHash(long[] xs, long[] zs, int[] tris)
        {
            var rows = new List<long[]>(tris.Length / 3);
            for (int i = 0; i < tris.Length; i += 3)
            {
                var p = new (long x, long z)[3];
                for (int k = 0; k < 3; k++)
                    p[k] = (xs[tris[i + k]], zs[tris[i + k]]);

                int min = 0;
                for (int k = 1; k < 3; k++)
                {
                    if (p[k].x < p[min].x || (p[k].x == p[min].x && p[k].z < p[min].z))
                        min = k;
                }
                rows.Add(new[]
                {
                    p[min].x, p[min].z,
                    p[(min + 1) % 3].x, p[(min + 1) % 3].z,
                    p[(min + 2) % 3].x, p[(min + 2) % 3].z,
                });
            }
            rows.Sort((a, b) =>
            {
                for (int k = 0; k < 6; k++)
                {
                    int c = a[k].CompareTo(b[k]);
                    if (c != 0)
                        return c;
                }
                return 0;
            });

            ulong h = 14695981039346656037UL;
            foreach (var row in rows)
            {
                foreach (long c in row)
                {
                    unchecked
                    {
                        h = (h ^ (ulong)c) * 1099511628211UL;
                        h = (h ^ ((ulong)c >> 32)) * 1099511628211UL;
                    }
                }
            }
            return h;
        }

        // Fixed golden fixture: 12x12 outer square, interior grid points, 4x4 building hole.
        private static (long[] xs, long[] zs, int[] constraints, int[] outerRing, int[] holeRing) GoldenFixture()
        {
            var xs = new List<long>();
            var zs = new List<long>();

            // 0..3: outer ring corners (0,0)-(12,0)-(12,12)-(0,12), scaled by 64 grid units.
            long s = 64;
            xs.Add(0); zs.Add(0);
            xs.Add(12 * s); zs.Add(0);
            xs.Add(12 * s); zs.Add(12 * s);
            xs.Add(0); zs.Add(12 * s);

            // 4..7: hole ring corners (4,4)-(8,4)-(8,8)-(4,8).
            xs.Add(4 * s); zs.Add(4 * s);
            xs.Add(8 * s); zs.Add(4 * s);
            xs.Add(8 * s); zs.Add(8 * s);
            xs.Add(4 * s); zs.Add(8 * s);

            // 8..: interior sprinkle (deterministic positions, off the rings).
            long[,] pts =
            {
                { 2, 2 }, { 10, 2 }, { 2, 10 }, { 10, 10 },
                { 6, 2 }, { 6, 10 }, { 2, 6 }, { 10, 6 },
                { 3, 9 }, { 9, 3 },
            };
            for (int i = 0; i < pts.GetLength(0); i++)
            {
                xs.Add(pts[i, 0] * s);
                zs.Add(pts[i, 1] * s);
            }

            var outer = new[] { 0, 1, 2, 3 };
            var hole = new[] { 4, 5, 6, 7 };
            var constraints = new List<int>();
            constraints.AddRange(RingEdges(outer));
            constraints.AddRange(RingEdges(hole));
            return (xs.ToArray(), zs.ToArray(), constraints.ToArray(), outer, hole);
        }

        #endregion

        [Test]
        public void Golden_MapWithHole_ChecksumStable()
        {
            var (xs, zs, constraints, _, _) = GoldenFixture();
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, constraints);

            Assert.Greater(tris.Length, 0);
            AssertAllCcw(xs, zs, tris);

            // Golden checksum — deterministic across runs and runtimes (pure integer path).
            ulong checksum = Fnv1a(tris);
            Assert.AreEqual(checksum, Fnv1a(FPConstrainedDelaunay.Triangulate(xs, zs, constraints)),
                "same input twice must be bit-identical");
            TestContext.Out.WriteLine($"golden checksum = 0x{checksum:X16}, triangles = {tris.Length / 3}");
            Assert.AreEqual(0x8930B3FF5300ABEDUL, checksum,
                "golden checksum drifted — determinism spec (T3) or algorithm changed");
        }

        [Test]
        public void DelaunayProperty_RandomPoints_EmptyCircumcircle()
        {
            var rng = new SysRandom(96020);
            var xs = new long[80];
            var zs = new long[80];
            var seen = new HashSet<(long, long)>();
            for (int i = 0; i < xs.Length; i++)
            {
                long x, z;
                do
                {
                    x = rng.NextInt64(-40000, 40001);
                    z = rng.NextInt64(-40000, 40001);
                } while (!seen.Add((x, z)));
                xs[i] = x;
                zs[i] = z;
            }

            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            Assert.Greater(tris.Length, 0);
            AssertAllCcw(xs, zs, tris);

            // Empty circumcircle: no vertex strictly inside any triangle's circumcircle.
            for (int i = 0; i < tris.Length; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                for (int v = 0; v < xs.Length; v++)
                {
                    if (v == a || v == b || v == c)
                        continue;
                    int s = FPGeoPredicates.InCircle(
                        xs[a], zs[a], xs[b], zs[b], xs[c], zs[c], xs[v], zs[v]);
                    Assert.LessOrEqual(s, 0,
                        $"vertex {v} strictly inside circumcircle of triangle ({a},{b},{c})");
                }
            }
        }

        [Test]
        public void Determinism_ShuffledInput_SameGeometry()
        {
            var (xs, zs, constraints, _, _) = GoldenFixture();
            ulong reference = GeometryHash(xs, zs, FPConstrainedDelaunay.Triangulate(xs, zs, constraints));

            var rng = new SysRandom(96021);
            for (int trial = 0; trial < 3; trial++)
            {
                // Shuffle vertex order; remap constraint indices accordingly.
                int n = xs.Length;
                var perm = new int[n];
                for (int i = 0; i < n; i++) perm[i] = i;
                for (int i = n - 1; i > 0; i--)
                {
                    int j = (int)rng.NextInt64(0, i + 1);
                    (perm[i], perm[j]) = (perm[j], perm[i]);
                }

                var sx = new long[n];
                var sz = new long[n];
                var inv = new int[n];
                for (int i = 0; i < n; i++)
                {
                    sx[perm[i]] = xs[i];
                    sz[perm[i]] = zs[i];
                    inv[i] = perm[i];
                }
                var sc = new int[constraints.Length];
                for (int i = 0; i < constraints.Length; i++)
                    sc[i] = inv[constraints[i]];

                ulong shuffled = GeometryHash(sx, sz, FPConstrainedDelaunay.Triangulate(sx, sz, sc));
                Assert.AreEqual(reference, shuffled, $"trial {trial}: input order must not affect geometry");
            }
        }

        [Test]
        public void Constraints_AllEdgesPresent_HoleAndOuterErased()
        {
            var (xs, zs, constraints, outer, hole) = GoldenFixture();
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, constraints);
            var edges = EdgeSet(tris);

            for (int i = 0; i < constraints.Length; i += 2)
            {
                int a = constraints[i], b = constraints[i + 1];
                Assert.IsTrue(edges.Contains(a < b ? (a, b) : (b, a)),
                    $"constraint edge ({a},{b}) missing from output");
            }

            // Every kept triangle: centroid*3 inside outer ring, outside hole ring.
            for (int i = 0; i < tris.Length; i += 3)
            {
                long cx = xs[tris[i]] + xs[tris[i + 1]] + xs[tris[i + 2]];
                long cz = zs[tris[i]] + zs[tris[i + 1]] + zs[tris[i + 2]];
                var xs3 = new long[xs.Length];
                var zs3 = new long[zs.Length];
                for (int v = 0; v < xs.Length; v++)
                {
                    xs3[v] = xs[v] * 3;
                    zs3[v] = zs[v] * 3;
                }
                Assert.IsTrue(PointInRing(cx, cz, xs3, zs3, outer), $"triangle {i / 3} escaped the outer ring");
                Assert.IsFalse(PointInRing(cx, cz, xs3, zs3, hole), $"triangle {i / 3} inside the hole");
            }
        }

        [Test]
        public void Constraint_CollinearVertexOnSegment_SplitsAndSucceeds()
        {
            // Square with a midpoint vertex sitting exactly on the bottom edge: the ring
            // constraint (0,1) passes exactly through vertex 4 -> must split into (0,4)+(4,1).
            var xs = new long[] { 0, 1000, 1000, 0, 500, 500 };
            var zs = new long[] { 0, 0, 1000, 1000, 0, 400 };
            var constraints = RingEdges(new[] { 0, 1, 2, 3 });

            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, constraints);
            var edges = EdgeSet(tris);

            Assert.IsTrue(edges.Contains((0, 4)), "split sub-edge (0,4) missing");
            Assert.IsTrue(edges.Contains((1, 4)), "split sub-edge (4,1) missing");
            Assert.IsFalse(edges.Contains((0, 1)), "unsplit edge (0,1) must not exist (vertex 4 lies on it)");
            AssertAllCcw(xs, zs, tris);
        }

        [Test]
        public void Constraint_CrossingConstraints_Throws()
        {
            // An X: two segments crossing strictly between grid vertices.
            var xs = new long[] { 0, 1000, 0, 1000, -500, -500, 1500, 1500 };
            var zs = new long[] { 0, 1000, 1000, 0, -500, 1500, -500, 1500 };
            var constraints = new[] { 0, 1, 2, 3 }; // (0->1) crosses (2->3) at (500,500) off-grid

            Assert.Throws<InvalidOperationException>(
                () => FPConstrainedDelaunay.Triangulate(xs, zs, constraints, eraseOuterAndHoles: false));
        }

        [Test]
        public void DuplicateVertices_WeldToFirstOccurrence()
        {
            var xs = new long[] { 0, 1000, 1000, 0, 1000, 0 };  // 4 dup of 1, 5 dup of 0
            var zs = new long[] { 0, 0, 1000, 1000, 0, 0 };
            var constraints = RingEdges(new[] { 5, 4, 2, 3 }); // ring via duplicate indices

            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, constraints);
            Assert.Greater(tris.Length, 0);
            for (int i = 0; i < tris.Length; i++)
                Assert.IsTrue(tris[i] != 4 && tris[i] != 5, "duplicates must weld to first occurrence");
            AssertAllCcw(xs, zs, tris);
        }

        [Test]
        public void CocircularGrid_ValidAndDeterministic()
        {
            // 5x5 integer grid: every unit square's corners are exactly cocircular —
            // the tie-break (no flip on inCircle == 0) must stay stable and valid.
            int n = 5;
            var xs = new long[n * n];
            var zs = new long[n * n];
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    xs[r * n + c] = c * 100;
                    zs[r * n + c] = r * 100;
                }
            }

            int[] first = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            int[] second = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);

            // Euler: triangles = 2n_pts - hull - 2 = 50 - 16 - 2 = 32.
            Assert.AreEqual(32, first.Length / 3);
            CollectionAssert.AreEqual(first, second, "cocircular grid must triangulate deterministically");
            AssertAllCcw(xs, zs, first);
        }

        [Test]
        public void Constraint_MultiPointChannel_UnitGridWithHalfIntegerHole()
        {
            // Regression: a carve channel with 2+ points on BOTH polylines. Unit grid with a
            // hole ring at half-integer coordinates (= diagonal midpoints of unit quads, so the
            // corners insert via the on-edge split path): each hole edge carves a channel with
            // 3 left + 3 right points. The right polyline is collected in a→stop walk order but
            // its pseudo-polygon base is (stop, a) — without reversing the list the Anglada
            // recursion mis-splits and corrupts topology (seen as a bogus T1 crossing throw,
            // or an unterminated channel walk in Release).
            const long S = 1024;
            int n = 5;
            var xs = new long[n * n + 4];
            var zs = new long[n * n + 4];
            for (int x = 0; x < n; x++)
            {
                for (int z = 0; z < n; z++)
                {
                    xs[x * n + z] = (x - 2) * S;
                    zs[x * n + z] = (z - 2) * S;
                }
            }
            long h = 3 * S / 2; // ±1.5 world
            int c0 = n * n;
            xs[c0] = -h; zs[c0] = -h;
            xs[c0 + 1] = h; zs[c0 + 1] = -h;
            xs[c0 + 2] = h; zs[c0 + 2] = h;
            xs[c0 + 3] = -h; zs[c0 + 3] = h;

            int V(int x, int z) => (x + 2) * n + (z + 2);
            var outer = new List<int>();
            for (int x = -2; x <= 2; x++) outer.Add(V(x, -2));
            for (int z = -1; z <= 2; z++) outer.Add(V(2, z));
            for (int x = 1; x >= -2; x--) outer.Add(V(x, 2));
            for (int z = 1; z >= -1; z--) outer.Add(V(-2, z));
            var constraints = new List<int>();
            constraints.AddRange(RingEdges(outer.ToArray()));
            constraints.AddRange(RingEdges(new[] { c0, c0 + 1, c0 + 2, c0 + 3 }));

            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, constraints.ToArray());
            int[] again = FPConstrainedDelaunay.Triangulate(xs, zs, constraints.ToArray());

            AssertAllCcw(xs, zs, tris);
            CollectionAssert.AreEqual(tris, again, "must be deterministic");

            // Exact area: 4x4 square minus 3x3 hole = 7 world² → doubled Σ|cross| = 14·S².
            BigInteger doubled = 0;
            for (int i = 0; i < tris.Length; i += 3)
            {
                BigInteger cross =
                    (BigInteger)(xs[tris[i + 1]] - xs[tris[i]]) * (zs[tris[i + 2]] - zs[tris[i]]) -
                    (BigInteger)(zs[tris[i + 1]] - zs[tris[i]]) * (xs[tris[i + 2]] - xs[tris[i]]);
                doubled += BigInteger.Abs(cross);
            }
            Assert.AreEqual((BigInteger)14 * S * S, doubled,
                "walkable area must be exactly the square minus the hole");

            // No triangle may sit inside the hole (centroid test, x3 to stay integer).
            for (int i = 0; i < tris.Length; i += 3)
            {
                long cx = xs[tris[i]] + xs[tris[i + 1]] + xs[tris[i + 2]];
                long cz = zs[tris[i]] + zs[tris[i + 1]] + zs[tris[i + 2]];
                Assert.IsFalse(cx > -3 * h && cx < 3 * h && cz > -3 * h && cz < 3 * h,
                    "no triangle centroid may fall inside the hole");
            }
        }

        [Test]
        public void OutOfDomain_Throws()
        {
            long m = FPGeoPredicates.MAX_SNAPPED_COORD;
            var xs = new long[] { 0, m + 1, 0 };
            var zs = new long[] { 0, 0, 100 };
            Assert.Throws<ArgumentException>(
                () => FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false));
        }

        [Test]
        public void EndToEnd_CdtOutputFeedsBuildPipeline()
        {
            // Contract smoke: CDT output triangulates into a valid FPNavMesh
            // through the existing pipeline (adjacency forms, all vertices on-grid).
            var (xs, zs, constraints, _, _) = GoldenFixture();
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, constraints);

            var vertices = new FPVector3[xs.Length];
            for (int i = 0; i < xs.Length; i++)
                vertices[i] = new FPVector3(FPGeoPredicates.Unsnap(xs[i]), FP64.Zero, FPGeoPredicates.Unsnap(zs[i]));
            var areas = new int[tris.Length / 3];

            FPNavMesh mesh = FPNavMeshBuildPipeline.Build(vertices, tris, areas, 1.0);

            Assert.AreEqual(tris.Length / 3, mesh.Triangles.Length,
                "pipeline must keep every CDT triangle (no degenerates in CDT output)");
            int linked = 0;
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                for (int e = 0; e < 3; e++)
                {
                    if (mesh.Triangles[i].GetNeighbor(e) >= 0)
                        linked++;
                }
            }
            Assert.Greater(linked, 0, "adjacency must form across CDT output");
        }
    }
}
