using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Logging;

namespace Brawler
{
    /// <summary>
    /// The building footprints this sample can place.
    ///
    /// A quantized oriented box: one 2x1 footprint in <see cref="Directions"/> orientations, so a
    /// placement carries an orientation instead of a min/max pair. That is the point of the sample
    /// wiring — an axis-aligned rect needs no orientation at all, and a 90-degree-only "rotation"
    /// would still be axis-aligned.
    ///
    /// <para><b>This table is inside the determinism envelope.</b> An orientation index is a
    /// REFERENCE into it, so two builds that disagree about the table would carve different
    /// navmeshes from identical commands, and nothing would fail loudly until the nav
    /// fingerprint. The catalog exposes <see cref="FPBuildingShapeCatalog.Hash"/> for exactly this;
    /// a real product should fold it into the match config or StaticFingerprint so the mismatch
    /// surfaces at load. The sample does not, because it ships one build — a note rather than an
    /// omission, but a note worth having when this gets copied.</para>
    ///
    /// <para>Non-square on purpose. With halfWidth == halfDepth a 90-degree turn reproduces the
    /// shape, so only M/4 of the orientations would look different and the wiring would appear to
    /// work while exercising a quarter of the table.</para>
    ///
    /// <para><b>The second shape is a hexagon</b> (P3), and it turns exactly one way rather than
    /// sixteen. That is not a shortcut: no integer hexagon is symmetric under 60 degrees — a vertex
    /// at (2a, 0) turned 60 degrees needs b = a*sqrt(3), which is irrational — so the orientations a
    /// rotate button would offer do not exist in the table. 180 degrees is exact, and that maps the
    /// shape onto itself, so there is nothing to choose. The hexagon's own reason for existing is
    /// that it TILES, which the box shares only in its axis-aligned orientations.</para>
    /// </summary>
    public static class BrawlerBuildingShapes
    {
        /// <summary>Orientations offered for the BOX. Multiple of 4 so four turns return exactly (P4).</summary>
        public const int Directions = 16;

        /// <summary>
        /// Feature key for the deterministic draw that picks a building's orientation.
        ///
        /// <para>Lives here, with the table it draws against, because it is a DETERMINISM input:
        /// two call sites feed it to <c>DeterministicRandom.FromSeed</c>, and if they ever
        /// disagreed the two paths would walk different streams from the same match seed — a
        /// desync that nothing checks, because "both sides used their own copy of the constant"
        /// leaves no trace. It used to be declared twice, once per assembly.</para>
        ///
        /// <para>The value spells "BLDGORIN" in ASCII; only its stability matters, not the text.</para>
        /// </summary>
        public const ulong OrientationFeatureKey = 0x424C44474F52494EUL;

        private const long Unit = FPGeoPredicates.SNAP_UNITS_PER_WORLD;
        private const long HalfWidth = Unit;        // 2 x 1 box footprint...
        private const long HalfDepth = Unit / 2;    // ...before the bake-radius expansion
        private const long HexCircumradius = Unit;  // 2 wide across the points x ~1.73 across the flats

        public static readonly FPBuildingShapeCatalog Catalog;

        /// <summary>The oriented box. Turns <see cref="Directions"/> ways.</summary>
        public static readonly int BoxShape;

        /// <summary>The hexagon. One orientation, not a range — see the class remarks.</summary>
        public static readonly int HexShape;

        static BrawlerBuildingShapes()
        {
            // Order defines the shape ids, and a stored building names one by number — so this is
            // straight-line code on purpose. Reordering it would silently repoint every stored
            // placement (the catalog Hash is what would catch that).
            var b = new FPBuildingShapeCatalogBuilder();
            BoxShape = b.AddObb(HalfWidth, HalfDepth, Directions);
            HexShape = b.AddHexagon(HexCircumradius);
            Catalog = b.Build();
        }

        /// <summary>
        /// Builds the rebake context for a stage, WITH this catalog. The only way any peer should
        /// create one.
        ///
        /// <para>Every peer has to agree about the shape table, because a placement is a reference
        /// into it. This exists because they once did not: the client path passed the catalog and
        /// the dedicated server's did not, so a placement the clients accepted and carved was
        /// rejected by the server on the same tick — the two ends then held different entity sets
        /// and the match spent the rest of its life in desync verdicts and FullState resyncs. The
        /// engine's "snapshot was built without a shape catalog" refusal fired exactly as designed;
        /// what was missing was a single place to pass it.</para>
        ///
        /// <para>Worth knowing about the failure that is NOT covered here: two peers passing
        /// DIFFERENT catalogs would carve different navmeshes without any refusal at all. The
        /// expansion logs its <see cref="FPBuildingShapeCatalog.Hash"/> on every peer, so a
        /// mismatch is at least visible by comparing logs; a product should fold that hash into the
        /// match config or StaticFingerprint so it fails at load instead.</para>
        /// </summary>
        public static FPNavMeshRebakeContext CreateContext(FPNavMesh baseMesh, IKLogger logger)
        {
            return FPNavMeshRebaker.CreateContext(baseMesh, logger, prewarm: true, shapeCatalog: Catalog);
        }

        /// <summary>
        /// True when the pair names a footprint this catalog actually holds. Commands arrive from
        /// the network, so both indices are untrusted — an out-of-range pair would otherwise be
        /// refused inside the trial rebake and reported as a placement rejection, which points at
        /// the wrong thing.
        ///
        /// <para>The pair, not each half: a shape that exists says nothing about whether it turns
        /// that many ways, and that is exactly the combination a flat index used to accept.</para>
        /// </summary>
        public static bool IsValidPlacement(int shapeId, int orientation)
        {
            return Catalog.TryResolveEntry(shapeId, orientation) >= 0;
        }

        /// <summary>
        /// Snaps a desired position onto the hexagon's tiling lattice, so consecutive placements
        /// land flush instead of merely near each other. Falls back to the plain quantised
        /// position when the stage has no catalog.
        /// </summary>
        public static void SnapHexPlacement(
            FPBuildingShapeExpansion expansion, FP64 x, FP64 z, out FP64 snappedX, out FP64 snappedZ)
        {
            if (expansion != null
                && expansion.TrySnapToLattice(HexShape, 0, x, z, out snappedX, out snappedZ))
                return;
            snappedX = FPGeoPredicates.Quantize(x);
            snappedZ = FPGeoPredicates.Quantize(z);
        }

    }
}
