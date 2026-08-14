using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Conforming fast path A/B gate, in two stages:
    /// stage 1 pins the premise — the full path performs zero T-junction splits and zero
    /// degenerate removals on rebaked CDT geometry (split count from the captured log; removal
    /// count derived as "split 0 ∧ out==in ⟹ removals 0") — and stage 2 pins bit-identity of
    /// the full path vs <see cref="FPNavMeshBuildPipeline.BuildFromConformingTriangulation"/>.
    /// Derived arrays (adjacency/portals/grid) are produced by the same shared stages 3-6 in
    /// both paths, so verts+tris identity implies their identity; neighbors are still
    /// compared directly as a cheap extra pin. A premise break on ONE geometry is reported as a
    /// divergence warning (fast path is canonical), not a failure.
    ///
    /// <para>Losing every comparison is a different thing and does fail. <c>Assert.Warn</c> makes
    /// NUnit report a test as Skipped and <c>dotnet test</c> exits 0, so "warn, do not fail" on its
    /// own lets this entire A/B switch itself off behind a green run. Each test therefore counts
    /// the comparisons that actually executed and asserts the count is non-zero.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshBuildPipelineFastPathTests
    {
        #region Helpers

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root not found");
            return dir.FullName;
        }

        private static int AreaIndexOf(int areaMask)
        {
            int a = 0;
            while (((areaMask >> a) & 1) == 0)
                a++;
            return a;
        }

        private static void ExtractGeometry(FPNavMesh mesh, out FPVector3[] vertices, out int[] indices, out int[] areas)
        {
            vertices = (FPVector3[])mesh.Vertices.ToArray();
            indices = new int[mesh.Triangles.Length * 3];
            areas = new int[mesh.Triangles.Length];
            for (int t = 0; t < mesh.Triangles.Length; t++)
            {
                indices[t * 3] = mesh.Triangles[t].v0;
                indices[t * 3 + 1] = mesh.Triangles[t].v1;
                indices[t * 3 + 2] = mesh.Triangles[t].v2;
                areas[t] = AreaIndexOf(mesh.Triangles[t].areaMask);
            }
        }

        /// <summary>
        /// Runs the premise check + bit-identity A/B over one conforming geometry.
        /// <returns><c>true</c> if the two build paths were actually compared, <c>false</c> if the
        /// premise broke and the comparison was skipped.</returns>
        /// <para>The return value is the point. A broken premise warns rather than fails, on
        /// purpose — the fast path is canonical, so a full-path split is not evidence of a fast-path
        /// bug. But <c>Assert.Warn</c> makes NUnit report the test as <b>Skipped</b> and
        /// <c>dotnet test</c> still exits 0, so on its own that decision means the only cross-check
        /// between the two build paths can switch itself off and CI stays green. Callers therefore
        /// count the comparisons that really ran and assert the count, which keeps the
        /// "document, do not fail" stance for an individual divergence while refusing to let ALL of
        /// them vanish silently.</para>
        /// </summary>
        private static bool AssertPremiseAndBitIdentity(FPNavMesh source, string label)
        {
            ExtractGeometry(source, out var verts, out var indices, out var areas);
            int inTris = indices.Length / 3;

            var capture = new LogCapture();
            FPNavMesh full = FPNavMeshBuildPipeline.Build(
                (FPVector3[])verts.Clone(), (int[])indices.Clone(), (int[])areas.Clone(),
                source.GridCellSize, capture,
                source.BakeAgentRadius, source.BakeMaxSlopeDeg, source.BakeAgentHeight, source.BakeAgentClimb);

            // Stage 1 — premise: zero splits (captured log) ∧ out==in ⟹ zero removals.
            bool premiseHolds = true;
            foreach (var (_, message) in capture.Entries)
            {
                if (message.Contains("T-Junction split") && !message.Contains("split 0 triangles"))
                    premiseHolds = false;
            }
            if (full.Triangles.Length != inTris)
                premiseHolds = false;

            if (!premiseHolds)
            {
                // Divergence case — the fast path is canonical; document, do not fail.
                Assert.Warn($"{label}: full-path premise broke (splits or removals on conforming input) — " +
                    $"in {inTris} tris, out {full.Triangles.Length}. Divergence case; skipping bit-identity.");
                return false;
            }

            FPNavMesh fast = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                (FPVector3[])verts.Clone(), (int[])indices.Clone(), (int[])areas.Clone(),
                source.GridCellSize, null,
                source.BakeAgentRadius, source.BakeMaxSlopeDeg, source.BakeAgentHeight, source.BakeAgentClimb);

            // Stage 2 — bit-identity.
            Assert.AreEqual(FPNavMeshRebaker.ComputeFingerprint(full), FPNavMeshRebaker.ComputeFingerprint(fast),
                $"{label}: fingerprint must match");
            Assert.AreEqual(full.Vertices.Length, fast.Vertices.Length, label);
            for (int i = 0; i < full.Vertices.Length; i++)
            {
                Assert.AreEqual(full.Vertices[i].x.RawValue, fast.Vertices[i].x.RawValue, $"{label}: vert {i} x");
                Assert.AreEqual(full.Vertices[i].y.RawValue, fast.Vertices[i].y.RawValue, $"{label}: vert {i} y");
                Assert.AreEqual(full.Vertices[i].z.RawValue, fast.Vertices[i].z.RawValue, $"{label}: vert {i} z");
            }
            Assert.AreEqual(full.Triangles.Length, fast.Triangles.Length, label);
            for (int t = 0; t < full.Triangles.Length; t++)
            {
                Assert.AreEqual(full.Triangles[t].v0, fast.Triangles[t].v0, $"{label}: tri {t} v0");
                Assert.AreEqual(full.Triangles[t].v1, fast.Triangles[t].v1, $"{label}: tri {t} v1");
                Assert.AreEqual(full.Triangles[t].v2, fast.Triangles[t].v2, $"{label}: tri {t} v2");
                Assert.AreEqual(full.Triangles[t].neighbor0, fast.Triangles[t].neighbor0, $"{label}: tri {t} n0");
                Assert.AreEqual(full.Triangles[t].neighbor1, fast.Triangles[t].neighbor1, $"{label}: tri {t} n1");
                Assert.AreEqual(full.Triangles[t].neighbor2, fast.Triangles[t].neighbor2, $"{label}: tri {t} n2");
            }
            return true;
        }

        private static FPNavMesh BuildSyntheticBase(int half)
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

        #endregion

        [Test]
        public void FastPath_RealBrawlerAssets_PremiseHolds_BitIdentical()
        {
            string root = RepoRoot();
            string[] assets =
            {
                "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes",
                "Samples/Brawler/Assets/Brawler/Data/Stage01.NavMeshData.bytes",
                "Samples/Brawler/Assets/Brawler/Data/Stage02.NavMeshData.bytes",
            };
            int tested = 0, compared = 0;
            foreach (string rel in assets)
            {
                string path = Path.Combine(root, rel);
                if (!File.Exists(path))
                    continue;
                FPNavMesh baseMesh = FPNavMeshSerializer.Deserialize(path);
                // Rebake runs the fast path (and, in DEBUG, the conforming-contract check).
                FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, null);
                if (AssertPremiseAndBitIdentity(rebaked, Path.GetFileNameWithoutExtension(rel)))
                    compared++;
                tested++;
            }
            Assert.Greater(tested, 0, "no real assets found");

            // Liveness. Each skipped asset is a deliberate divergence case, but if EVERY asset
            // skips, this test compares the two build paths on nothing at all — and it reports that
            // as a skip, which dotnet test summarises as green with exit code 0. Runtime rebaking
            // uses the fast path, so a divergence there is a cross-peer desync at the next mid-join;
            // it must not be possible for the check to retire itself unnoticed.
            Assert.Greater(compared, 0,
                $"every one of the {tested} baked assets broke the full-path premise, so the two "
                + "build paths were not compared at all. That is not necessarily a fast-path bug — "
                + "but it retires the only A/B between them, and that needs a decision rather than "
                + "a silent skip");
        }

        [Test]
        public void FastPath_SyntheticGridWithBuilding_BitIdentical()
        {
            FPNavMesh baseMesh = BuildSyntheticBase(10);
            var building = new[]
            {
                new FPBuildingRect(FP64.FromInt(-1), FP64.FromInt(-1), FP64.FromInt(1), FP64.FromInt(1), FP64.Zero),
            };
            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, building);

            // Same liveness argument, and stronger here: this geometry is built by this file, so a
            // premise break is a change in the pipeline rather than a property of some asset. There
            // is exactly one comparison to lose, so losing it means the test measures nothing.
            Assert.IsTrue(AssertPremiseAndBitIdentity(rebaked, "synthetic 21x21 + building"),
                "the synthetic fixture's premise broke, so this test compared the two build paths on "
                + "nothing. The warning above says which way it diverged; decide on it rather than "
                + "letting the comparison disappear into a skip");
        }

        [Test]
        public void FastPath_RebakeStillConservesAreaExactly()
        {
            // Rebaker now routes through the fast path — re-pin its area-conservation contract.
            FPNavMesh baseMesh = BuildSyntheticBase(10);
            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, null);
            System.Numerics.BigInteger DoubledArea(FPNavMesh m)
            {
                System.Numerics.BigInteger sum = 0;
                foreach (var t in m.Triangles)
                {
                    long ax = FPGeoPredicates.Snap(m.Vertices[t.v0].x), az = FPGeoPredicates.Snap(m.Vertices[t.v0].z);
                    long bx = FPGeoPredicates.Snap(m.Vertices[t.v1].x), bz = FPGeoPredicates.Snap(m.Vertices[t.v1].z);
                    long cx = FPGeoPredicates.Snap(m.Vertices[t.v2].x), cz = FPGeoPredicates.Snap(m.Vertices[t.v2].z);
                    var cross = (System.Numerics.BigInteger)(bx - ax) * (cz - az) - (System.Numerics.BigInteger)(bz - az) * (cx - ax);
                    sum += System.Numerics.BigInteger.Abs(cross);
                }
                return sum;
            }
            Assert.AreEqual(DoubledArea(baseMesh), DoubledArea(rebaked), "area must be conserved through the fast path");
        }
    }

    /// <summary>
    /// T-junction grid-index equivalence tests:
    /// forced index vs forced linear scan must produce bit-identical meshes — the composite
    /// (tParam, vertIdx) sort makes candidate collection order irrelevant, including exact
    /// tParam ties (multi-level stacked vertices). Fixtures cover an actual T-junction split,
    /// tParam ties, the negative quadrant (arithmetic-shift floor bucketing), the small-input
    /// linear fallback, and the diagonal-long-edge query-count bound.
    /// </summary>
    [TestFixture]
    public class FPNavMeshTJunctionIndexTests
    {
        [SetUp]
        public void SetUp() { FPNavMeshBuildPipeline.TJunctionIndexModeForTests = 0; }

        [TearDown]
        public void TearDown() { FPNavMeshBuildPipeline.TJunctionIndexModeForTests = 0; }

        #region Helpers

        private static FPVector3 V(double x, double z, double y = 0)
        {
            return new FPVector3(FP64.FromDouble(x), FP64.FromDouble(y), FP64.FromDouble(z));
        }

        private static FPNavMesh BuildWithMode(int mode, FPVector3[] verts, int[] indices)
        {
            FPNavMeshBuildPipeline.TJunctionIndexModeForTests = mode;
            try
            {
                return FPNavMeshBuildPipeline.Build(
                    (FPVector3[])verts.Clone(), (int[])indices.Clone(), new int[indices.Length / 3], 1.0, null);
            }
            finally
            {
                FPNavMeshBuildPipeline.TJunctionIndexModeForTests = 0;
            }
        }

        private static void AssertModesIdentical(FPVector3[] verts, int[] indices, string label)
        {
            FPNavMesh indexed = BuildWithMode(1, verts, indices);
            FPNavMesh linear = BuildWithMode(2, verts, indices);

            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(linear), FPNavMeshRebaker.ComputeFingerprint(indexed),
                $"{label}: index vs linear must be bit-identical");
            Assert.AreEqual(linear.Triangles.Length, indexed.Triangles.Length, label);
            for (int t = 0; t < linear.Triangles.Length; t++)
            {
                Assert.AreEqual(linear.Triangles[t].v0, indexed.Triangles[t].v0, $"{label}: tri {t} v0");
                Assert.AreEqual(linear.Triangles[t].v1, indexed.Triangles[t].v1, $"{label}: tri {t} v1");
                Assert.AreEqual(linear.Triangles[t].v2, indexed.Triangles[t].v2, $"{label}: tri {t} v2");
            }
        }

        /// <summary>Two triangles sharing edge (A,B) + vertex M exactly on that edge = T-junction.</summary>
        private static (FPVector3[] verts, int[] indices) TJunctionFixture(double ox, double oz)
        {
            var verts = new[]
            {
                V(ox + 0, oz + 0), V(ox + 4, oz + 0), V(ox + 2, oz + 3), V(ox + 2, oz - 3), V(ox + 2, oz + 0),
            };
            var indices = new[] { 0, 1, 2, 1, 0, 3 };
            return (verts, indices);
        }

        #endregion

        [Test]
        public void IndexVsLinear_TJunctionSplit_Identical()
        {
            var (verts, indices) = TJunctionFixture(0, 0);
            AssertModesIdentical(verts, indices, "T-junction");

            // Sanity: the split actually happened (2 tris -> 4).
            FPNavMesh mesh = BuildWithMode(2, verts, indices);
            Assert.AreEqual(4, mesh.Triangles.Length, "both triangles must split at M");
        }

        [Test]
        public void IndexVsLinear_NegativeQuadrant_Identical()
        {
            var (verts, indices) = TJunctionFixture(-50, -50);
            AssertModesIdentical(verts, indices, "negative quadrant");

            FPNavMesh mesh = BuildWithMode(1, verts, indices);
            Assert.AreEqual(4, mesh.Triangles.Length, "split must fire with negative snapped coords");
        }

        [Test]
        public void IndexVsLinear_ExactTParamTies_MultiLevelStacked_Identical()
        {
            // Two stacked mid vertices (same XZ, Y within the height epsilon) on edge (A,B):
            // identical tParam bits — the composite (tParam, vertIdx) sort must keep both
            // collection orders (index scan vs linear scan) output-identical.
            var verts = new[]
            {
                V(0, 0), V(4, 0), V(0, 4), V(2, 0, 0.1), V(2, 0, 0.3),
            };
            var indices = new[] { 0, 1, 2 };
            AssertModesIdentical(verts, indices, "tParam tie");
        }

#if DEBUG
        [Test]
        public void AutoMode_SmallInput_FallsBackToLinear()
        {
            var (verts, indices) = TJunctionFixture(0, 0);
            FPNavMeshBuildPipeline.TJunctionIndexModeForTests = 0;
            FPNavMeshBuildPipeline.Build((FPVector3[])verts.Clone(), (int[])indices.Clone(), new int[2], 1.0, null);
            Assert.IsFalse(FPNavMeshBuildPipeline.LastTJunctionUsedIndex,
                "tiny input must fall back to the linear scan (index build is pure overhead)");
        }

        [Test]
        public void DiagonalLongEdge_IndexCutsCandidateChecks()
        {
            // One long-diagonal skinny triangle + 150 scattered vertices far from every edge.
            // The supercover walk must visit only edge-adjacent cells — a naive AABB traversal
            // or a broken index would approach the linear scan's check count.
            var verts = new List<FPVector3> { V(0, 0), V(100, 100), V(0, 1) };
            for (int i = 0; i < 150; i++)
                verts.Add(V(i * 0.7, -20));
            var indices = new[] { 0, 1, 2 };

            long Run(int mode)
            {
                FPNavMeshBuildPipeline.TJunctionCandidateChecks = 0;
                BuildWithMode(mode, verts.ToArray(), indices);
                return FPNavMeshBuildPipeline.TJunctionCandidateChecks;
            }

            long linear = Run(2);
            long indexed = Run(1);
            Assert.Greater(linear, 0);
            Assert.Less(indexed * 4, linear,
                $"index must cut candidate checks by >4x (index {indexed} vs linear {linear})");

            AssertModesIdentical(verts.ToArray(), indices, "diagonal long edge");
        }
#endif
    }
}
