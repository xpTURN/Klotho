using System;
using System.Collections.Generic;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// A base navmesh plus an ordered list of building placements, rebaked on demand — the data
    /// layer behind the NavMesh visualizer's placement tool.
    ///
    /// <para><b>Why this is here and not in the editor.</b> It exists to be TESTED. The visualizer's
    /// own types are <c>internal</c> to an editor assembly that no test assembly references, so
    /// logic living there cannot be gated at all — not by the .NET suite and not by Unity's EditMode
    /// suite either. Sitting in the runtime puts it where the .NET suite can reach it, so the gates
    /// run without an editor at all.</para>
    ///
    /// <para><b>Why public.</b> It started <c>internal</c>, which was enough for Unity's editor
    /// assembly. It is not enough for Godot's: those adapters ship as SOURCE and compile into the
    /// consuming project's own assembly, so no friend declaration in this package can reach them.
    /// Leaving it internal would have meant a second, DIFFERENT implementation on that side — the
    /// non-throwing bake and the query rebind are both internal, so Godot would have had to drive
    /// the context form and build a fresh query trio, losing the one-trio invariant the Unity side
    /// keeps and leaving half the tool ungated. One public type buys one code path.</para>
    ///
    /// <para>A game does not need this: it builds placements from frame state and drives
    /// <see cref="FPNavMeshRebaker"/> directly, as Brawler does. What this adds is the bookkeeping
    /// an authoring tool wants — a base to return to, an ordered list to edit, and a bake that
    /// reports a refusal instead of throwing it.</para>
    ///
    /// <para><b>The list is the state, and every rebake starts from the base.</b> That mirrors the
    /// runtime model — the rebaker is never incremental over its own output, it always takes the
    /// base plus the current set — and it makes removal free: drop an entry and bake again. The
    /// snapshot is a function of the base alone, so it is built once and reused.</para>
    ///
    /// <para><b>No patching, deliberately.</b> This drives the snapshot form, which passes no
    /// previous mesh, so every rebake is a full build. The tool is for looking at the RESULT of a
    /// placement; the patch path has its own gates one layer down, and a tool that silently
    /// exercised only one of the two paths would be a bad place to look for a patch regression.</para>
    ///
    /// <para><b>Centres are quantised on the way in.</b> A mouse click produces an arbitrary float
    /// and an off-grid centre is a MALFORMED request the rebaker throws on — even through the
    /// non-throwing entry points, because catalog offsets are integers about the centre. Quantising
    /// here makes that throw unreachable from a UI, which is the difference between "you cannot
    /// build there" and a stack trace in the console.</para>
    ///
    /// <para><b>And snapped to the shape's tiling lattice, by default.</b> Quantising is the
    /// minimum a placement needs; it is NOT enough to make two buildings meet. Flush contact
    /// happens at multiples of the footprint's tiling delta, and those deltas are not round
    /// numbers — the expansion pads outward by two snap units per side to absorb the vertex snap,
    /// so a 2x1 box at radius 0.5 meets its neighbour at 2.003906 rather than 2, and a hexagon at
    /// 2.371094. Place at the number that looks right and about four millimetres of walkable
    /// ground is left between the two footprints, which the engine cannot tell from a door. See
    /// <see cref="SnapToTilingLattice"/>.</para>
    /// </summary>
    public sealed class FPNavMeshPlacementProbe
    {
        #region The tool's own shape table

        /// <summary>Turns the box offers. A multiple of 4, so four turns return exactly.</summary>
        public const int ToolBoxDirections = 16;

        /// <summary>
        /// One box and one hexagon — a shape table for an authoring tool that has no game behind
        /// it. It belongs to the TOOL: acceptance here says nothing about a game whose catalog
        /// differs, and a tool that let the user believe otherwise would be worse than one with no
        /// shapes at all. Shared by both editors so there is one definition rather than a copy per
        /// host that can drift.
        /// </summary>
        public static FPBuildingShapeCatalog ToolCatalog { get; private set; }

        public static int ToolBoxShape { get; private set; }
        public static int ToolHexShape { get; private set; }

        static FPNavMeshPlacementProbe()
        {
            const long unit = FPGeoPredicates.SNAP_UNITS_PER_WORLD;
            var b = new FPBuildingShapeCatalogBuilder();
            ToolBoxShape = b.AddObb(unit, unit / 2, ToolBoxDirections);   // 2 x 1 before expansion
            ToolHexShape = b.AddHexagon(unit);
            ToolCatalog = b.Build();
        }

        /// <summary>
        /// Rebinds a caller's query, pathfinder and funnel onto a rebaked mesh, keeping their
        /// IDENTITY. Exists because an editor tool holds its own trio and cannot reach the rebind
        /// itself, and because building a fresh trio instead is the subtle wrong answer: an agent
        /// system constructed from those instances would keep the old ones and the two halves of
        /// the tool would answer on different meshes.
        ///
        /// <para><b>Not a swap.</b> This rebinds queries and nothing else. An agent system on the
        /// same mesh still needs its own swap — which re-extracts the ORCA obstacles and reseeds
        /// the agents, neither of which this does.</para>
        /// </summary>
        public static void RebindQueries(
            FPNavMesh mesh, FPNavMeshQuery query, FPNavMeshPathfinder pathfinder,
            FPNavMeshFunnel funnel)
        {
            if (mesh == null)
                throw new ArgumentException("FPNavMeshPlacementProbe.RebindQueries: mesh is null");

            query?.Rebind(mesh);
            pathfinder?.Rebind(mesh);
            funnel?.Rebind(mesh);
        }

        #endregion

        private readonly FPNavMesh _baseMesh;
        private readonly FPBuildingShapeCatalog _catalog;
        private readonly IKLogger _logger;
        private readonly List<FPBuildingPlacement> _placements = new List<FPBuildingPlacement>();

        /// <summary>Built on first use and reused — see the class remarks for why one is enough.</summary>
        private FPNavMeshRebakeSnapshot _snapshot;

        /// <summary>Reused so a rebake per keystroke does not allocate a fresh array each time.</summary>
        private FPBuildingPlacement[] _scratch = Array.Empty<FPBuildingPlacement>();

        /// <summary>
        /// Built lazily for the lattice snap, from the base mesh's own bake radius — the same
        /// radius <c>CreateSnapshot</c> uses, so the lattice this snaps to is the lattice the
        /// rebaker will actually carve against. Deriving it from anything else would snap to a
        /// grid the footprints do not sit on, which is worse than not snapping.
        /// </summary>
        private FPBuildingShapeExpansion _expansion;

        public FPNavMeshPlacementProbe(
            FPNavMesh baseMesh, FPBuildingShapeCatalog catalog, IKLogger logger = null)
        {
            if (baseMesh == null)
                throw new ArgumentException("FPNavMeshPlacementProbe: baseMesh is null");
            if (catalog == null)
                throw new ArgumentException(
                    "FPNavMeshPlacementProbe: catalog is null — retain mode is a placement field and "
                    + "the rect forms cannot carry it, so a shape catalog is required rather than optional");

            _baseMesh = baseMesh;
            _catalog = catalog;
            _logger = logger;
        }

        /// <summary>The mesh every rebake starts from. Reverting hands this back unchanged.</summary>
        public FPNavMesh BaseMesh => _baseMesh;

        public FPBuildingShapeCatalog Catalog => _catalog;

        public int Count => _placements.Count;

        public FPBuildingPlacement PlacementAt(int index) => _placements[index];

        /// <summary>
        /// Boundary policy and contact rule for every rebake. Defaults to
        /// <see cref="FPBoundaryPlacementPolicy.Touch"/> with contact allowed rather than to the
        /// policy a particular game ships: under <c>ClipOverlap</c> a RETAINED footprint that
        /// crosses the walkable boundary is refused (a carve would be clipped instead), so a tool
        /// defaulting there would refuse retained buildings near an edge for a reason nobody
        /// looking at the screen could guess. Set it to see that rule.
        /// </summary>
        public FPBuildingPlacementRules Rules { get; set; } =
            new FPBuildingPlacementRules(allowBuildingTouch: true, FPBoundaryPlacementPolicy.Touch);

        /// <summary>
        /// Move a requested centre to the nearest point of the shape's own tiling lattice, so
        /// neighbours meet with no sliver between them. On by default: "build these wall to wall"
        /// is what a placement tool is normally for, and the spacing that achieves it is not a
        /// number anyone would guess (see the class remarks).
        ///
        /// <para>Turn it off to place freely — a tool also has to be able to show what a
        /// near-miss does, and a shape that cannot tile at all (neither a parallelogram nor a
        /// centrally symmetric hexagon) falls back to plain quantisation regardless.</para>
        /// </summary>
        public bool SnapToTilingLattice { get; set; } = true;

        /// <summary>
        /// Appends a placement and rebakes. On refusal the append is UNDONE and the reason comes
        /// back, so the list is always a set that bakes — which is what lets removal and a rules
        /// change rebake without re-validating anything.
        ///
        /// <para><paramref name="cx"/> and <paramref name="cz"/> are quantised onto the predicate
        /// grid (see the class remarks); <paramref name="y"/> is not, since it never enters a
        /// predicate.</para>
        /// </summary>
        public bool TryPlace(
            int shapeId, int orientation, FP64 cx, FP64 cz, FP64 y, bool retain,
            out FPNavMesh mesh, out FPBuildingRejectionInfo rejection)
        {
            ResolveCentre(shapeId, orientation, cx, cz, out FP64 px, out FP64 pz);
            var placement = new FPBuildingPlacement(shapeId, orientation, px, pz, y, retain);

            _placements.Add(placement);
            if (TryRebake(out mesh, out rejection))
                return true;

            _placements.RemoveAt(_placements.Count - 1);
            return false;
        }

        /// <summary>
        /// Where a requested centre actually goes: the tiling lattice when
        /// <see cref="SnapToTilingLattice"/> is on and the shape can tile, otherwise the predicate
        /// grid. Both land on the grid — the lattice is built from integer deltas — so the
        /// malformed-request throw stays unreachable either way.
        /// </summary>
        public void ResolveCentre(
            int shapeId, int orientation, FP64 cx, FP64 cz, out FP64 x, out FP64 z)
        {
            if (SnapToTilingLattice)
            {
                _expansion = _expansion
                    ?? new FPBuildingShapeExpansion(_catalog, _baseMesh.BakeAgentRadius);
                if (_expansion.TrySnapToLattice(shapeId, orientation, cx, cz, out x, out z))
                    return;
            }

            x = FPGeoPredicates.Quantize(cx);
            z = FPGeoPredicates.Quantize(cz);
        }

        /// <summary>
        /// Drops one placement and rebakes. Cannot be refused — the remaining set is a subset of
        /// one that was already accepted — but the reason travels anyway so a caller has one shape
        /// of call to handle.
        /// </summary>
        public bool TryRemoveAt(int index, out FPNavMesh mesh, out FPBuildingRejectionInfo rejection)
        {
            _placements.RemoveAt(index);
            return TryRebake(out mesh, out rejection);
        }

        /// <summary>
        /// Reverts to the base mesh and returns it — the SAME instance that was handed to the
        /// constructor, not a zero-placement rebake. Those two are not obviously the same mesh
        /// (a rebake re-extracts and re-triangulates) and "revert" means the former.
        /// </summary>
        public FPNavMesh Revert()
        {
            _placements.Clear();
            return _baseMesh;
        }

        /// <summary>
        /// Rebakes the current list. An empty list returns the base mesh (see <see cref="Revert"/>).
        /// </summary>
        public bool TryRebake(out FPNavMesh mesh, out FPBuildingRejectionInfo rejection)
        {
            rejection = default;
            if (_placements.Count == 0)
            {
                mesh = _baseMesh;
                return true;
            }

            if (_snapshot == null)
            {
                _snapshot = FPNavMeshRebaker.CreateSnapshot(
                    _baseMesh, _logger, prewarm: false, shapeCatalog: _catalog);
            }

            if (_scratch.Length < _placements.Count)
                _scratch = new FPBuildingPlacement[System.Math.Max(4, _placements.Count * 2)];
            for (int i = 0; i < _placements.Count; i++)
                _scratch[i] = _placements[i];

            // previous: null — the snapshot form never patches, which is the decision, not an
            // oversight. See the class remarks.
            mesh = FPNavMeshRebaker.RebakePlacements(
                _snapshot, _scratch, _logger, null, Rules, out rejection,
                placementCount: _placements.Count);
            return mesh != null;
        }
    }
}
