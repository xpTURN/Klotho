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
    /// fingerprint. The catalog exposes <see cref="FPBuildingShapeCatalog.Hash"/> for exactly this,
    /// and <c>BotFSMSystem.GetGameFingerprint</c> folds it into the static environment fingerprint —
    /// so a mismatch surfaces wherever a FullState is exchanged.
    ///
    /// <para>Two limits on that, both worth knowing when this gets copied. It is not a LOAD-time
    /// check: peers that start together and never resync never compare it — for those, folding the
    /// hash into the match config is still the right instrument. And nothing requires the fold at
    /// all: <c>IGameFingerprintSource</c> is optional and the engine folds 0 when no system
    /// implements it, so a game that copies this wiring without that interface loses the net
    /// silently.</para>
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
        /// <para>Worth knowing about the failure that is NOT refused here: two peers passing
        /// DIFFERENT catalogs would carve different navmeshes, and this call would accept both. What
        /// catches it is the fold in <c>BotFSMSystem.GetGameFingerprint</c> (see the class remarks),
        /// plus the hash the expansion logs on every peer; neither is a load-time check.</para>
        /// </summary>
        public static FPNavMeshRebakeContext CreateContext(FPNavMesh baseMesh, IKLogger logger)
        {
            return FPNavMeshRebaker.CreateContext(baseMesh, logger, prewarm: true, shapeCatalog: Catalog);
        }

        /// <summary>
        /// The snapshot half of <see cref="CreateContext"/>, for a host that serves several
        /// simulations off one stage — build this ONCE at load and give each of them its own
        /// <c>new FPNavMeshRebakeContext(snapshot)</c>. The snapshot is immutable and safe to share;
        /// the context is not (it owns work buffers).
        ///
        /// <para>Why it matters where this runs: a snapshot costs a full base insertion, and the
        /// first rebake in a process additionally pays the JIT (<c>prewarm: true</c> absorbs it
        /// here). The dedicated server builds rooms on its main loop's receive stage, so doing this
        /// per room puts that cost between the poll and the room dispatch — every OTHER room's tick
        /// budget shrinks by exactly that much. See <c>Docs/IMP/IMP96/Report-MultiRoomThreading.md</c>
        /// §H-1.</para>
        ///
        /// <para>Same catalog rule as <see cref="CreateContext"/>, for the same reason.</para>
        /// </summary>
        public static FPNavMeshRebakeSnapshot CreateSnapshot(FPNavMesh baseMesh, IKLogger logger)
        {
            return FPNavMeshRebaker.CreateSnapshot(baseMesh, logger, prewarm: true, shapeCatalog: Catalog);
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
