using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The baseline the building-shape work is measured against.
    ///
    /// P1 (validation-loop prefilter) and P2 (convex-polygon generalisation) both have "bit
    /// identical" as their completion criterion, and BOTH LOSE THEIR COMPARISON TARGET AT MERGE:
    /// once the prefilter is in there is no "without prefilter" path, and once the polygon path
    /// is in there is
    /// no old AABB path. These constants are captured BEFORE any of that, so the comparison
    /// survives. There was no precedent to lean on — every other ComputeFingerprint assertion in
    /// navigation is A-vs-B inside one run (pooled vs unpooled, called twice, before/after swap);
    /// the only absolute golden was FPConstrainedDelaunayTests' CDT checksum.
    ///
    /// TWO RULES KEEP THIS USEFUL:
    ///
    /// 1. THIS FILE OWNS ITS FIXTURES. The annulus below duplicates one in
    ///    FPNavMeshSwallowedRingTests on purpose. Sharing it would mean that editing that file
    ///    breaks the goldens, and the failure would read as "is the golden wrong or did the
    ///    fixture move?". Nobody edits the builders here without expecting to re-capture.
    ///
    /// 2. THE INPUTS ARE LITERALS. The Field placements were found by a scan (fixed start, fixed
    ///    step, first 32 that validate) and then frozen as the array below. A scan is not a
    ///    reproducible definition — change the start or the step and you get a different set, so
    ///    whoever captured and whoever verifies would be comparing different things.
    ///
    /// WHAT THIS DOES NOT COVER: a fingerprint only exists when a rebake SUCCEEDS. The way the
    /// prefilter goes wrong is a REJECTION turning into an acceptance (a touching ring edge gets
    /// skipped) — that is a fingerprint appearing where there was none, which no golden comparison
    /// can see. Rejection regressions belong to the boundary-contract test and the prefilter
    /// mutation, not here. "Goldens pass" is not "the prefilter is done".
    /// </summary>
    [TestFixture]
    public class FPNavMeshRebakeGoldenTests
    {
        // ── goldens (captured 2026-08-11, before P1) ─────────────────────────

        private const ulong AnnulusEmpty = 0x8EB3F52266B0A5E4;
        private const ulong AnnulusOne = 0xA22DB648E3FD9B50;
        private const ulong AnnulusFour = 0x4A805B43EC569C5C;
        private const ulong SolidEmpty = 0x7B066B26C704CA94;
        private const ulong SolidOne = 0x206A5BABCCF2C7B8;
        private const ulong SolidFour = 0xF7CC5019DC29A664;
        private const ulong FieldEmpty = 0xD9F45E2BC536DFDF;
        private const ulong FieldOne = 0x7AEA62BF0ACF21CE;
        private const ulong FieldEight = 0xD51537F49E67AC1A;
        private const ulong FieldThirtyTwo = 0x7EEE87E9EAC3F89B;

        // ── retain goldens (captured 2026-09-05, before the grid-driven stamp) ─
        //
        // The four above cannot cover the retain stamp AT ALL: FPBuildingRect carries no retain
        // flag, so every one of them rebakes with polyRetain == null and the stamp returns on its
        // first line. FieldThirtyTwo in particular LOOKS like the configuration that matters —
        // the Field asset with 32 buildings — and pins nothing about it.
        //
        // These do, and they are the only automatic net for a stamp that MISSES a triangle: a miss
        // changes areaMask, ComputeFingerprint folds areaMask, but every peer runs the same binary
        // and so misses identically — the cross-peer check passes and only a pinned value objects.
        private const ulong FieldRetainOne = 0x2DDE511EFA5E7F20;
        private const ulong FieldRetainEight = 0x1A094D1532D89CE3;
        private const ulong FieldRetainThirtyTwo = 0xD8A6FE1163A97E93;

        // ── owned fixtures — see rule 1 above ────────────────────────────────

        private const double BakeRadius = 0.5;

        /// <summary>16x16 slab, vertices every 2 units. withPillar carves a [-2,2] hole.</summary>
        private static FPNavMesh BuildSlab(bool withPillar)
        {
            var pts = new List<(int x, int z)>();
            for (int x = -8; x <= 8; x += 2)
                for (int z = -8; z <= 8; z += 2)
                    pts.Add((x, z));

            var vertices = new FPVector3[pts.Count];
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            var index = new Dictionary<(int, int), int>();
            for (int i = 0; i < pts.Count; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
                index[pts[i]] = i;
            }

            var constraints = new List<int>();
            void Ring(params (int x, int z)[] loop)
            {
                for (int i = 0; i < loop.Length; i++)
                {
                    constraints.Add(index[loop[i]]);
                    constraints.Add(index[loop[(i + 1) % loop.Length]]);
                }
            }

            var outer = new List<(int, int)>();
            for (int x = -8; x <= 8; x += 2) outer.Add((x, -8));
            for (int z = -6; z <= 8; z += 2) outer.Add((8, z));
            for (int x = 6; x >= -8; x -= 2) outer.Add((x, 8));
            for (int z = 6; z >= -6; z -= 2) outer.Add((-8, z));
            Ring(outer.ToArray());

            if (withPillar)
                Ring((-2, -2), (0, -2), (2, -2), (2, 0), (2, 2), (0, 2), (-2, 2), (-2, 0));

            int[] tris = FPConstrainedDelaunay.Triangulate(
                xs, zs, constraints.ToArray(), eraseOuterAndHoles: true);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: BakeRadius);
        }

        /// <summary>Slab placements, in canonical (MinX, MinZ) order already.</summary>
        private static readonly (double x, double z)[] SlabCenters =
        {
            (-5.0, -5.0), (-5.0, 5.0), (5.0, -5.0), (5.0, 5.0),
        };

        /// <summary>
        /// Field placements — frozen from a scan (start (-90,-80), step 1.7, footprint 0.6,
        /// first 32 that validate against everything accepted so far). See rule 2 above.
        /// </summary>
        private static readonly (double x, double z)[] FieldCenters =
        {
            (-90.0, -46.0), (-90.0, -5.2), (-90.0, 54.3), (-88.3, -46.0),
            (-88.3, -5.2), (-88.3, 54.3), (-86.6, -46.0), (-86.6, -5.2),
            (-86.6, 54.3), (-84.9, -80.0), (-84.9, -78.3), (-84.9, -76.6),
            (-84.9, -22.2), (-84.9, -20.5), (-84.9, -18.8), (-84.9, -17.1),
            (-84.9, 37.3), (-84.9, 39.0), (-84.9, 40.7), (-84.9, 42.4),
            (-76.4, -22.2), (-76.4, -20.5), (-76.4, -18.8), (-76.4, -17.1),
            (-73.0, -63.0), (-73.0, -61.3), (-73.0, -59.6), (-73.0, -57.9),
            (-73.0, -34.1), (-73.0, -32.4), (-73.0, -30.7), (-73.0, -29.0),
        };

        private const double SlabHalf = 0.5;
        private const double FieldHalf = 0.3;

        private static FPBuildingRect[] Take((double x, double z)[] centers, int count, double half)
        {
            var rects = new FPBuildingRect[count];
            for (int i = 0; i < count; i++)
                rects[i] = new FPBuildingRect(
                    FP64.FromDouble(centers[i].x - half), FP64.FromDouble(centers[i].z - half),
                    FP64.FromDouble(centers[i].x + half), FP64.FromDouble(centers[i].z + half),
                    FP64.Zero);
            Array.Sort(rects, CanonicalOrder.Instance);
            return rects;
        }

        /// <summary>Mirrors the game-side canonical rebake input order.</summary>
        private sealed class CanonicalOrder : IComparer<FPBuildingRect>
        {
            public static readonly CanonicalOrder Instance = new CanonicalOrder();
            public int Compare(FPBuildingRect a, FPBuildingRect b)
            {
                int c = a.MinX.RawValue.CompareTo(b.MinX.RawValue);
                return c != 0 ? c : a.MinZ.RawValue.CompareTo(b.MinZ.RawValue);
            }
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "com.xpturn.klotho")))
                d = d.Parent;
            return d?.FullName ?? ".";
        }

        private static string FieldPath() =>
            Path.Combine(RepoRoot(), "Samples/Brawler/Assets/NavMesh/Data/Field.NavMeshData.bytes");

        private static ulong Fingerprint(FPNavMesh baseMesh, FPBuildingRect[] buildings) =>
            FPNavMeshRebaker.ComputeFingerprint(FPNavMeshRebaker.Rebake(baseMesh, buildings, null));

        // ── the retain fixture — owned here, see rule 1 ──────────────────────

        /// <summary>
        /// One square shape, half 0.25 world units — exact on the snap grid, and SMALLER than the
        /// 0.3 the rect goldens use, so the frozen centres that validated there still validate
        /// here once the expansion is added.
        /// </summary>
        private static FPBuildingShapeCatalog RetainCatalog()
        {
            long h = FPGeoPredicates.SNAP_UNITS_PER_WORLD / 4;
            FPBuildingShapeCatalog.ObbOffsets(h, h, 4, out long[] x, out long[] z, out int[] entryStart);
            return new FPBuildingShapeCatalog(x, z, entryStart);
        }

        /// <summary>
        /// The frozen centres as PLACEMENTS. They have to be quantised first — unlike
        /// <see cref="FPBuildingRect"/>, a placement centre off the predicate grid is refused
        /// outright ("catalog offsets are integers about the centre"), and the scan that froze
        /// these produced plain decimals like -5.2. Quantising is a deterministic function of the
        /// literals, so rule 2 still holds: the input is the array above, not a re-run scan.
        /// </summary>
        private static FPBuildingPlacement[] TakeRetained((double x, double z)[] centres, int count)
        {
            var placements = new FPBuildingPlacement[count];
            for (int i = 0; i < count; i++)
                placements[i] = new FPBuildingPlacement(
                    0,
                    FPGeoPredicates.Quantize(FP64.FromDouble(centres[i].x)),
                    FPGeoPredicates.Quantize(FP64.FromDouble(centres[i].z)),
                    FP64.Zero,
                    retain: true);
            Array.Sort(placements, RetainOrder.Instance);
            return placements;
        }

        private sealed class RetainOrder : IComparer<FPBuildingPlacement>
        {
            public static readonly RetainOrder Instance = new RetainOrder();
            public int Compare(FPBuildingPlacement a, FPBuildingPlacement b)
            {
                int c = a.CentreX.RawValue.CompareTo(b.CentreX.RawValue);
                return c != 0 ? c : a.CentreZ.RawValue.CompareTo(b.CentreZ.RawValue);
            }
        }

        private static ulong RetainFp(int n)
        {
            FPNavMesh field = FPNavMeshSerializer.Deserialize(FieldPath());
            var snapshot = FPNavMeshRebaker.CreateSnapshot(
                field, null, prewarm: false, shapeCatalog: RetainCatalog());
            return FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.RebakePlacements(snapshot, TakeRetained(FieldCenters, n), null));
        }

        // ── the goldens ──────────────────────────────────────────────────────

        [Test]
        public void Annulus_MatchesGolden()
        {
            FPNavMesh baseMesh = BuildSlab(withPillar: true);
            Assert.AreEqual(AnnulusEmpty, Fingerprint(baseMesh, Array.Empty<FPBuildingRect>()), "0 buildings");
            Assert.AreEqual(AnnulusOne, Fingerprint(baseMesh, Take(SlabCenters, 1, SlabHalf)), "1 building");
            Assert.AreEqual(AnnulusFour, Fingerprint(baseMesh, Take(SlabCenters, 4, SlabHalf)), "4 buildings");
        }

        [Test]
        public void Solid_MatchesGolden()
        {
            FPNavMesh baseMesh = BuildSlab(withPillar: false);
            Assert.AreEqual(SolidEmpty, Fingerprint(baseMesh, Array.Empty<FPBuildingRect>()), "0 buildings");
            Assert.AreEqual(SolidOne, Fingerprint(baseMesh, Take(SlabCenters, 1, SlabHalf)), "1 building");
            Assert.AreEqual(SolidFour, Fingerprint(baseMesh, Take(SlabCenters, 4, SlabHalf)), "4 buildings");
        }

        [Test]
        public void FieldAsset_MatchesGolden()
        {
            if (!File.Exists(FieldPath()))
                Assert.Ignore("Field.NavMeshData.bytes not present");

            FPNavMesh field = FPNavMeshSerializer.Deserialize(FieldPath());
            var snapshot = FPNavMeshRebaker.CreateSnapshot(field, null, prewarm: false);

            ulong Fp(int n) => FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(snapshot, Take(FieldCenters, n, FieldHalf), null));

            Assert.AreEqual(FieldEmpty, Fp(0), "0 buildings");
            Assert.AreEqual(FieldOne, Fp(1), "1 building");
            Assert.AreEqual(FieldEight, Fp(8), "8 buildings");
            Assert.AreEqual(FieldThirtyTwo, Fp(32), "32 buildings");
        }

        [Test]
        public void FieldAssetRetain_MatchesGolden()
        {
            if (!File.Exists(FieldPath()))
                Assert.Ignore("Field.NavMeshData.bytes not present");

            Assert.AreEqual(FieldRetainOne, RetainFp(1), "1 retained building");
            Assert.AreEqual(FieldRetainEight, RetainFp(8), "8 retained buildings");
            Assert.AreEqual(FieldRetainThirtyTwo, RetainFp(32), "32 retained buildings");
        }

        /// <summary>
        /// Re-capture helper. Run explicitly, paste the output over the constants above, and say
        /// in the commit WHY the baseline moved — a golden that changes without a stated reason is
        /// the failure this file exists to prevent.
        /// </summary>
        [Test]
        [Explicit("prints the current fingerprints; only run when re-baselining")]
        public void Capture()
        {
            FPNavMesh annulus = BuildSlab(withPillar: true);
            FPNavMesh solid = BuildSlab(withPillar: false);
            void P(string name, ulong v) => TestContext.Out.WriteLine($"        private const ulong {name} = 0x{v:X16};");

            P("AnnulusEmpty", Fingerprint(annulus, Array.Empty<FPBuildingRect>()));
            P("AnnulusOne", Fingerprint(annulus, Take(SlabCenters, 1, SlabHalf)));
            P("AnnulusFour", Fingerprint(annulus, Take(SlabCenters, 4, SlabHalf)));
            P("SolidEmpty", Fingerprint(solid, Array.Empty<FPBuildingRect>()));
            P("SolidOne", Fingerprint(solid, Take(SlabCenters, 1, SlabHalf)));
            P("SolidFour", Fingerprint(solid, Take(SlabCenters, 4, SlabHalf)));

            if (!File.Exists(FieldPath())) { TestContext.Out.WriteLine("        // Field asset absent"); return; }
            FPNavMesh field = FPNavMeshSerializer.Deserialize(FieldPath());
            var snapshot = FPNavMeshRebaker.CreateSnapshot(field, null, prewarm: false);
            ulong Fp(int n) => FPNavMeshRebaker.ComputeFingerprint(
                FPNavMeshRebaker.Rebake(snapshot, Take(FieldCenters, n, FieldHalf), null));
            P("FieldEmpty", Fp(0));
            P("FieldOne", Fp(1));
            P("FieldEight", Fp(8));
            P("FieldThirtyTwo", Fp(32));

            P("FieldRetainOne", RetainFp(1));
            P("FieldRetainEight", RetainFp(8));
            P("FieldRetainThirtyTwo", RetainFp(32));
        }
    }
}
