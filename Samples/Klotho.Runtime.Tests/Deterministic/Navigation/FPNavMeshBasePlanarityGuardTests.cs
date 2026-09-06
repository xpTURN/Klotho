using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The rebaker is single-level only, and its old guard only caught XZ-DUPLICATE VERTICES —
    /// which stacked levels that share no exact coordinate slip straight past. Two families get
    /// through, and they fail differently:
    ///
    /// <para><b>Crossing</b> — the levels' boundary rings cross in XZ. The triangulator refuses
    /// that exactly, so the only question was which exception, and when: it used to escape as
    /// the triangulator's own <c>InvalidOperationException</c> from wherever the snapshot happened
    /// to be built (for the placement probe, the first click).</para>
    ///
    /// <para><b>Nested</b> — a platform sits wholly inside the floor. Nothing crosses, the
    /// triangulation accepts it, and the parity erase then reads the inner ring as a hole: the
    /// platform vanishes, the floor beneath it is carved away, and the hole's rim keeps the
    /// platform's height so the surviving floor tilts. No refusal at all, identical on every
    /// peer, invisible to any fingerprint comparison. That is what the zero-building rebake
    /// check catches.</para>
    /// </summary>
    [TestFixture]
    public class FPNavMeshBasePlanarityGuardTests
    {
        #region Fixture

        /// <summary>A flat 20x20 walkable square at y = 0, on-grid, single level.</summary>
        private static void Floor(List<FPVector3> verts, List<int> tris)
        {
            var xs = new List<long>();
            var zs = new List<long>();
            for (int x = -10; x <= 10; x += 5)
            {
                for (int z = -10; z <= 10; z += 5)
                {
                    verts.Add(new FPVector3(FP64.FromInt(x), FP64.Zero, FP64.FromInt(z)));
                    xs.Add(FPGeoPredicates.Snap(FP64.FromInt(x)));
                    zs.Add(FPGeoPredicates.Snap(FP64.FromInt(z)));
                }
            }
            tris.AddRange(FPConstrainedDelaunay.Triangulate(
                xs.ToArray(), zs.ToArray(), null, eraseOuterAndHoles: false));
        }

        /// <summary>
        /// The floor plus one quad at y = 4 sharing no vertex with it, so the two levels are
        /// separate components whose XZ projections overlap. <paramref name="minX"/> decides the
        /// family: inside the floor = nested rings (nothing crosses), straddling the floor edge
        /// at x = 10 = crossing rings.
        /// </summary>
        private static FPNavMesh Stacked(int minX, int maxX)
        {
            var verts = new List<FPVector3>();
            var tris = new List<int>();
            Floor(verts, tris);

            int b = verts.Count;
            int[] px = { minX, maxX, maxX, minX };
            int[] pz = { -1, -1, 3, 3 };
            for (int i = 0; i < 4; i++)
                verts.Add(new FPVector3(FP64.FromInt(px[i]), FP64.FromInt(4), FP64.FromInt(pz[i])));
            tris.AddRange(new[] { b + 0, b + 1, b + 2, b + 0, b + 2, b + 3 });

            return FPNavMeshBuildPipeline.Build(
                verts.ToArray(), tris.ToArray(), new int[tris.Count / 3], 1.0, null,
                bakeAgentRadius: 0.5);
        }

        private static FPNavMesh NestedPlatform() => Stacked(-2, 2);      // wholly inside
        private static FPNavMesh StraddlingPlatform() => Stacked(8, 14);  // crosses x = 10

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "com.xpturn.klotho")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "repo root not found from test base directory");
            return dir.FullName;
        }

        private static FPNavMesh LoadAsset(string relPath)
        {
            string path = Path.Combine(RepoRoot(), relPath);
            Assert.IsTrue(File.Exists(path), $"asset is not where the test expects it: {relPath}");
            return FPNavMeshSerializer.Deserialize(File.ReadAllBytes(path));
        }

        private static BigInteger DoubledArea(FPNavMesh mesh)
        {
            BigInteger sum = 0;
            foreach (var t in mesh.Triangles)
            {
                long ax = FPGeoPredicates.Snap(mesh.Vertices[t.v0].x), az = FPGeoPredicates.Snap(mesh.Vertices[t.v0].z);
                long bx = FPGeoPredicates.Snap(mesh.Vertices[t.v1].x), bz = FPGeoPredicates.Snap(mesh.Vertices[t.v1].z);
                long cx = FPGeoPredicates.Snap(mesh.Vertices[t.v2].x), cz = FPGeoPredicates.Snap(mesh.Vertices[t.v2].z);
                sum += BigInteger.Abs((BigInteger)(bx - ax) * (cz - az) - (BigInteger)(bz - az) * (cx - ax));
            }
            return sum;
        }

        #endregion

        #region The guard fires

        [Test]
        public void CrossingRings_AreRefusedAsAnUnsupportedStage()
        {
            FPNavMesh stacked = StraddlingPlatform();

            // Non-vacuity: the OLD guard passes this mesh. If a later change made the levels share
            // an XZ coordinate, the duplicate check would refuse it first and this test would be
            // measuring that instead.
            var seen = new HashSet<(long, long)>();
            foreach (var v in stacked.Vertices)
                Assert.IsTrue(seen.Add((FPGeoPredicates.Snap(v.x), FPGeoPredicates.Snap(v.z))),
                    "fixture must have XZ-unique vertices — otherwise the old duplicate guard is what refuses it");

            var ex = Assert.Throws<NotSupportedException>(
                () => FPNavMeshRebaker.CreateSnapshot(stacked, null, prewarm: false));

            Assert.That(ex.Message, Does.Contain("boundary rings cross in XZ"));
            // The coordinates travel: an asset owner has to be able to find the overlap.
            Assert.That(ex.Message, Does.Contain("crosses"));
            Assert.IsInstanceOf<FPConstraintCrossingException>(ex.InnerException,
                "the triangulator's own refusal must be preserved as the inner exception");
        }

        [Test]
        public void NestedRings_AreRefusedByTheZeroBuildingRebake()
        {
            FPNavMesh stacked = NestedPlatform();

            // Nothing crosses here — this family is invisible to the crossing refusal, which is
            // the whole reason the area check exists.
            FPNavMeshObstacleExtractor.Extract(stacked, out FPVector2[] rings, out int[] offsets);
            Assert.AreEqual(2, offsets.Length, "fixture must produce two rings (floor + platform)");
            Assert.AreEqual(400 + 16, (int)(DoubledArea(stacked) / 2 / (1024 * 1024)),
                "fixture area must be floor + platform, i.e. the platform is a second sheet");

            var ex = Assert.Throws<NotSupportedException>(
                () => FPNavMeshRebaker.CreateSnapshot(stacked, null, prewarm: false));

            // The numbers must be IN the message. After the guard lands the rebaked mesh cannot be
            // observed at all (the refusal comes first), so asserting only the exception type would
            // pass for an unrelated failure just as happily.
            Assert.That(ex.Message, Does.Contain("did not reproduce the base walkable region"));
            Assert.That(ex.Message, Does.Contain("416.00 -> 384.00"),
                "the message must name the area it lost: 400 + 16 down to 400 - 16");
        }

        [Test]
        public void ThePolySampleAsset_IsRefused()
        {
            // Secondary fixture only: the sample's asset is a live file that may be re-exported
            // (it is a stacked Godot NavigationRegion3D bake today). The synthetic cases above are
            // what pin the behaviour.
            FPNavMesh poly = LoadAsset("Samples/GodotPolySample/NavigationRegion3D.NavMeshData.bytes");

            var ex = Assert.Throws<NotSupportedException>(
                () => FPNavMeshRebaker.CreateSnapshot(poly, null, prewarm: false));
            Assert.That(ex.Message, Does.Contain("single-level"));
        }

        #endregion

        #region The guard stays out of the way

        [TestCase("Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes", 22321, "29102050607")]
        [TestCase("Samples/Brawler/Assets/Brawler/Data/Stage01.NavMeshData.bytes", 116, "1374772853")]
        [TestCase("Samples/Brawler/Assets/Brawler/Data/Stage02.NavMeshData.bytes", 60, "1417224650")]
        public void ShippedSingleLevelAssets_PassAndAreReproducedExactly(
            string relPath, int expectedTris, string expectedDoubledArea)
        {
            FPNavMesh mesh = LoadAsset(relPath);
            Assert.AreEqual(expectedTris, mesh.Triangles.Length, "asset drifted from the pinned baseline");
            Assert.AreEqual(BigInteger.Parse(expectedDoubledArea), DoubledArea(mesh),
                "asset drifted from the pinned baseline");

            FPNavMeshRebakeSnapshot snapshot = null;
            Assert.DoesNotThrow(
                () => snapshot = FPNavMeshRebaker.CreateSnapshot(mesh, null, prewarm: false),
                "a single-level shipped asset must not be refused");

            // The validation's own claim, restated from outside: the zero-building rebake is the
            // base region, exactly. If this ever drifts the guard would start refusing the asset,
            // so pinning it here says WHY the guard is safe rather than just that it passed.
            FPNavMesh zero = FPNavMeshRebaker.Rebake(snapshot, null);
            Assert.AreEqual(expectedTris, zero.Triangles.Length);
            Assert.AreEqual(BigInteger.Parse(expectedDoubledArea), DoubledArea(zero));
        }

        [Test]
        public void TheValidationRunsWhetherOrNotTheCallerAsksForAWarmUp()
        {
            // prewarm: false used to skip the rebake entirely, and under ENABLE_IL2CPP the warm-up
            // is a no-op in every configuration — so hanging the validation off the warm-up would
            // have left the guard absent exactly where the wrong navmesh ships.
            FPNavMesh stacked = NestedPlatform();
            Assert.Throws<NotSupportedException>(
                () => FPNavMeshRebaker.CreateSnapshot(stacked, null, prewarm: false));
            Assert.Throws<NotSupportedException>(
                () => FPNavMeshRebaker.CreateSnapshot(stacked, null, prewarm: true));
        }

        #endregion

        #region The probe

        [Test]
        public void Probe_TryPrepare_ReportsAnUnsupportedStageAsAValue()
        {
            var probe = new FPNavMeshPlacementProbe(
                NestedPlatform(), FPNavMeshPlacementProbe.ToolCatalog);

            Assert.IsFalse(probe.TryPrepare(out string reason), "an unsupported stage must come back as false");
            Assert.That(reason, Does.Contain("single-level"), "the reason has to be showable");

            // And a supported one prepares, so the query is not simply always-false.
            var ok = new FPNavMeshPlacementProbe(GoodBase(), FPNavMeshPlacementProbe.ToolCatalog);
            Assert.IsTrue(ok.TryPrepare(out string none));
            Assert.IsNull(none);
        }

        /// <summary>
        /// The class contract is that the list is always a set that bakes: on refusal the append is
        /// undone, and nothing accumulates across attempts.
        ///
        /// <para><b>The mechanism changed, the invariant did not.</b> An unsupported stage used to
        /// leave <c>TryPlace</c> as a <c>NotSupportedException</c>, and the gate here pinned that
        /// the try/finally undid the append anyway. It is now reported the way every other refusal
        /// is — <c>false</c> plus <see cref="FPBuildingRejection.BaseMeshUnsupported"/> — which is
        /// what <c>TryPlace</c>'s own doc always promised (<i>on refusal the append is UNDONE and
        /// the reason comes back</i>). What still has to hold, and is what this asserts, is that
        /// the list is empty after each attempt.</para>
        /// </summary>
        [Test]
        public void Probe_ARefusedRebake_LeavesThePlacementListUntouched()
        {
            var probe = new FPNavMeshPlacementProbe(
                NestedPlatform(), FPNavMeshPlacementProbe.ToolCatalog);

            Assert.IsFalse(probe.TryPlace(
                FPNavMeshPlacementProbe.ToolBoxShape, 0, FP64.Zero, FP64.Zero, FP64.Zero,
                retain: false, out _, out FPBuildingRejectionInfo first));
            Assert.AreEqual(FPBuildingRejection.BaseMeshUnsupported, first.Reason,
                "the stage is refused, and it says so as a value rather than an exception");
            Assert.AreEqual(0, probe.Count, "a refusal must not leave the placement behind");

            Assert.IsFalse(probe.TryPlace(
                FPNavMeshPlacementProbe.ToolBoxShape, 0, FP64.Zero, FP64.Zero, FP64.Zero,
                retain: false, out _, out _));
            Assert.AreEqual(0, probe.Count, "and it must not accumulate across attempts");
        }

        private static FPNavMesh GoodBase()
        {
            var verts = new List<FPVector3>();
            var tris = new List<int>();
            Floor(verts, tris);
            return FPNavMeshBuildPipeline.Build(
                verts.ToArray(), tris.ToArray(), new int[tris.Count / 3], 1.0, null,
                bakeAgentRadius: 0.5);
        }

        #endregion
    }
}
