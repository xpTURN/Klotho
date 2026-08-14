using System;
using System.Collections.Generic;

using xpTURN.Klotho.Deterministic;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Axis-aligned building footprint for the runtime rebaker: expansion and validation are
    /// grid-exact, with no rounded intersections.
    /// Y is the caller-supplied placement height for the four corner vertices
    /// (single-level assumption; the rebaker rejects multi-level base meshes).
    ///
    /// <para><b>The four coordinates pair by CORNER, not by axis</b> — <c>(minX, minZ)</c> then
    /// <c>(maxX, maxZ)</c>, so the arguments read as two opposite corners. The other common
    /// convention groups by axis (<c>minX, maxX</c> then <c>minZ, maxZ</c>) and produces a
    /// silently different rectangle from the same four numbers, so it is worth reading the
    /// parameter names once. The footprint is unexpanded: the rebaker adds the base mesh's bake
    /// agent radius itself.</para>
    /// </summary>
    public readonly struct FPBuildingRect
    {
        public readonly FP64 MinX;
        public readonly FP64 MinZ;
        public readonly FP64 MaxX;
        public readonly FP64 MaxZ;
        public readonly FP64 Y;

        /// <param name="minX">West edge — the smaller X. Paired with <paramref name="minZ"/> as one corner.</param>
        /// <param name="minZ">South edge — the smaller Z, NOT the larger X.</param>
        /// <param name="maxX">East edge — the larger X, and the start of the opposite corner.</param>
        /// <param name="maxZ">North edge — the larger Z.</param>
        /// <param name="y">Placement height for all four corners. Single level; the rebaker
        /// refuses a multi-level base mesh, so one Y is the whole footprint.</param>
        public FPBuildingRect(FP64 minX, FP64 minZ, FP64 maxX, FP64 maxZ, FP64 y)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
            Y = y;
        }
    }

    /// <summary>
    /// Which touching placements the game allows. Default is every
    /// flag false = the historical behaviour: a building whose EXPANDED rect so much as grazes
    /// the walkable boundary or another building is rejected.
    ///
    /// For a BUILDING PAIR that rejection was never a requirement — measured, the CDT handles
    /// every touching form (shared edge, shared corner, collinear overlap) and carves them into
    /// one merged obstacle ring; the only thing it refuses is a TRANSVERSAL crossing. Nor is it
    /// geometric: the rect is already expanded by the agent radius, so "touching" means the gap
    /// is exactly zero, which is precisely "an agent cannot fit". So whether to allow it is a
    /// game rule — pack the base wall-to-wall, or don't — and this struct is where the game says.
    ///
    /// There is deliberately NO flag for touching the walkable BOUNDARY. The CDT accepts that too,
    /// but the result is wrong further down: a hole ring sharing an edge with the outer ring is no
    /// hole at all, because even-odd erasure counts the coincident edge once and the notch keeps
    /// odd depth. Measured — such a building is accepted and carves NOTHING. Silently ineffective
    /// is worse than rejected, so it stays rejected.
    ///
    /// DETERMINISM: every peer must use the same value, and so must a replay played back later.
    /// A peer that rejects a placement another accepted ends up with a different BuildingComponent
    /// set (the state hash catches that first, the nav fingerprint after), and a replay recorded
    /// with touching allowed diverges when replayed by a build that forbids it. Source it from
    /// inside the determinism envelope — not from a local config file.
    /// </summary>
    public readonly struct FPBuildingPlacementRules
    {
        /// <summary>
        /// Allow expanded rects to touch each other — shared edge, shared corner, collinear
        /// overlap. Interiors must still be disjoint, so partial overlap AND nesting stay
        /// rejected: nesting is not a near miss but a trap, because even-odd erasure turns the
        /// inner rect back into walkable (measured). Do not "simplify" this into allowing
        /// overlap.
        /// </summary>
        public readonly bool AllowBuildingTouch;

        public FPBuildingPlacementRules(bool allowBuildingTouch)
        {
            AllowBuildingTouch = allowBuildingTouch;
        }
    }

    /// <summary>
    /// Why the map refused a placement. These are the outcomes a PLAYER causes and a player is
    /// meant to see, which is why they come back as a value rather than an exception.
    ///
    /// <para>Malformed requests — a stale shape id, an unquantized centre, a null argument — are
    /// deliberately NOT here. They still throw, because folding them in would let a game report
    /// its own bug to the player as "you cannot build there", identically on every peer and
    /// therefore invisibly. See <see cref="FPNavMeshRebaker.TryRebakePlacements"/>.</para>
    ///
    /// <para><b>Values are ordered, not just named.</b> Nothing here goes on the wire, but a game
    /// that broadcasts a rejection event puts one there, so renumbering breaks that game silently.
    /// Add at the end; never reorder; <c>None</c> stays 0.</para>
    /// </summary>
    public enum FPBuildingRejection
    {
        /// <summary>No rejection — the rebake succeeded.</summary>
        None = 0,

        /// <summary>
        /// Two buildings are not separated after radius expansion.
        ///
        /// <para>Named for the common case, but it covers exact contact too when the rules forbid
        /// touching: the separating-axis test answers one bool and cannot tell a shared edge from
        /// a real overlap. A UI that says "overlapping" will therefore sometimes be describing
        /// "touching" — say "too close" if that matters.</para>
        /// </summary>
        BuildingsOverlap = 1,

        /// <summary>The expanded footprint reaches the edge of the walkable region. Rejected even
        /// though the triangulation accepts it, because a hole ring sharing an edge with the outer
        /// ring carves nothing.</summary>
        TouchesWalkableBoundary = 2,

        /// <summary>The expanded footprint is not on the mesh at all.</summary>
        OutsideWalkableRegion = 3,

        /// <summary>The footprint fully contains a baked hole ring — a pillar, pond or pit — which
        /// would flip that hole's interior back to walkable.</summary>
        SwallowsBakedHole = 4,

        /// <summary>
        /// The rebake left no walkable region.
        ///
        /// <para>Defensive. No placement can reach it: every building is validated to sit strictly
        /// inside the region and clear of its boundary, so a gap always survives and always
        /// triangulates. A base mesh that is already degenerate is the remaining route.</para>
        /// </summary>
        EmptyWalkableRegion = 5,
    }

    /// <summary>
    /// A refusal, with enough context to say which building and — for a swallowed hole — which
    /// hole. Carries no message: the text is assembled only when someone actually throws, so the
    /// rejecting path formats nothing (<see cref="FPNavMeshRebaker.TryRebakePlacements"/>).
    /// </summary>
    public readonly struct FPBuildingRejectionInfo
    {
        public readonly FPBuildingRejection Reason;

        /// <summary>The building at fault, or -1 when the reason names none.</summary>
        public readonly int IndexA;

        /// <summary>The other building for <see cref="FPBuildingRejection.BuildingsOverlap"/>,
        /// otherwise -1.</summary>
        public readonly int IndexB;

        /// <summary>World XZ of the swallowed ring vertex for
        /// <see cref="FPBuildingRejection.SwallowsBakedHole"/>, otherwise default. A building index
        /// alone does not identify WHICH hole was swallowed, and a shipped stage has thousands.
        /// </summary>
        public readonly FPVector2 Site;

        public FPBuildingRejectionInfo(
            FPBuildingRejection reason, int indexA = -1, int indexB = -1, FPVector2 site = default)
        {
            Reason = reason;
            IndexA = indexA;
            IndexB = indexB;
            Site = site;
        }

        public bool IsRejected => Reason != FPBuildingRejection.None;
    }

    /// <summary>
    /// Reusable per-stage rebake snapshot: the base mesh's CDT
    /// state (vertices + boundary-ring constraints inserted) plus the rebaker's validation
    /// and assembly material. Create once at match load (NOT on the tick thread — costs a
    /// full base insertion) via <see cref="FPNavMeshRebaker.CreateSnapshot"/>; immutable and
    /// safely shared read-only across rebakes and worker threads (every rebake clones the
    /// CDT state internally). The base stays fixed: rebakes are always
    /// "original base + full building list" (B' semantics) — never fed back.
    /// </summary>
    public sealed class FPNavMeshRebakeSnapshot
    {
        internal readonly FPNavMesh BaseMesh;
        internal readonly FPConstrainedDelaunay.CdtSnapshot Cdt;
        internal readonly long[] BaseXs;
        internal readonly long[] BaseZs;
        internal readonly FP64[] BaseYs;
        internal readonly Dictionary<(long, long), int> CoordToIndex;   // read-only after creation
        internal readonly List<(int a, int b)> RingEdges;

        /// <summary>
        /// The stage's shape catalog already expanded by this base mesh's bake radius, or null
        /// when the game only places rectangles.
        ///
        /// Derived once here rather than per placement, which is what makes contact authorable
        /// once per (entry, radius) — every instance of an entry shares the same offsets, so
        /// two neighbours meant to sit flush actually do. Derivation validates and throws, so a
        /// shape that cannot be offset conservatively fails at LOAD, next to the base-mesh
        /// checks, instead of surfacing later as a placement that quietly carves the wrong hole.
        /// </summary>
        public FPBuildingShapeExpansion ShapeExpansion { get; }

        internal FPNavMeshRebakeSnapshot(
            FPNavMesh baseMesh, FPConstrainedDelaunay.CdtSnapshot cdt,
            long[] baseXs, long[] baseZs, FP64[] baseYs,
            Dictionary<(long, long), int> coordToIndex, List<(int a, int b)> ringEdges,
            FPBuildingShapeExpansion shapeExpansion = null)
        {
            BaseMesh = baseMesh;
            Cdt = cdt;
            BaseXs = baseXs;
            BaseZs = baseZs;
            BaseYs = baseYs;
            CoordToIndex = coordToIndex;
            RingEdges = ringEdges;
            ShapeExpansion = shapeExpansion;
        }
    }

    /// <summary>
    /// Everything one room needs to rebake: the stage's immutable
    /// <see cref="FPNavMeshRebakeSnapshot"/> plus that room's private work-buffer pool
    /// Create one per room at match load and hold it where
    /// the snapshot used to be held — the pool never appears in game code, and its ownership
    /// rule ("one per room, used serially") is enforced by construction instead of by
    /// convention.
    ///
    /// The two halves are deliberately not merged: a snapshot is a pure function of the base
    /// mesh and is safe to share read-only across rooms and worker threads (the multi-room
    /// case that is reserved for), while a pool is mutable and must never be shared. Two rooms
    /// running the same stage therefore share one snapshot and build one context each.
    /// </summary>
    /// <summary>
    /// Working buffers for the placement-preview calls, owned by the game rather than by a rebake
    /// context.
    ///
    /// <para>Ownership is what makes serial use structural. A preview reads the immutable
    /// snapshot, so this is the only thing two callers could collide on — one scratch per caller,
    /// and a second thread that wants to preview makes a second scratch.</para>
    ///
    /// <para>Keep it for the life of the room. Preview runs as the cursor moves, and building a
    /// fresh scratch per call would put back the allocation this exists to remove.</para>
    /// </summary>
    public sealed class FPBuildingPreviewScratch
    {
        internal readonly FPNavMeshRebakeBufferPool Buffers = new FPNavMeshRebakeBufferPool();
    }

    /// <summary>
    /// The expanded polygons of the last building set a context actually carved, plus one empty
    /// slot at the end for a placement ghost.
    ///
    /// <para>This is what lets the context-form preview answer without being handed a building
    /// list. The list it would have been handed is one the rebaker already accepted — so the
    /// rebaker keeps it instead, and the precondition that the caller cannot get wrong is the one
    /// the caller never states.</para>
    ///
    /// <para><b>A copy, not a reference.</b> The polygons a rebake works on live in pool buffers
    /// that the NEXT rebake overwrites before it validates anything — so a single refused rebake
    /// would destroy the set this is supposed to remember.</para>
    ///
    /// <para><b>Written only where a rebake succeeds</b>, which is once. A rejected placement never
    /// reaches it, and neither does the one rejection decided after validation
    /// (<see cref="FPBuildingRejection.EmptyWalkableRegion"/>, during the carve).</para>
    ///
    /// <para><b>Never read by a rebake.</b> Everything here is downstream of the mesh, so a defect
    /// in it cannot change what gets carved and cannot desync peers — the worst it can do is
    /// throw.</para>
    /// </summary>
    internal sealed class FPBuildingAcceptedSet
    {
        internal long[] PolyX = Array.Empty<long>();
        internal long[] PolyZ = Array.Empty<long>();
        internal int[] PolyStart = new int[1];

        /// <summary>The ghost's AABB, and only the ghost's — see ValidateOneAgainstBase.</summary>
        internal readonly long[] GhostBounds = new long[4];

        /// <summary>Buildings carved. The ghost, when one is written, is at this index.</summary>
        internal int Count;

        /// <summary>Vertices those buildings hold. The ghost's vertices start here.</summary>
        internal int VertCount;

        /// <summary>
        /// The rules the carving rebake ran under, so a preview does not have to be told them
        /// again. Meaningless while <see cref="Count"/> is 0 — with nothing to separate from, the
        /// separation test that reads them never runs.
        /// </summary>
        internal FPBuildingPlacementRules Rules;

        /// <summary>
        /// Takes the accepted polygons, reserving <paramref name="ghostHeadroom"/> vertices past
        /// them.
        ///
        /// <para>The headroom is not optional. Without it the very next preview would have to grow
        /// these arrays, putting an allocation on every rebake-then-preview cycle — which is the
        /// whole cost this API exists to remove.</para>
        /// </summary>
        internal void Capture(
            long[] polyX, long[] polyZ, int[] polyStart, int count, int vertCount,
            int ghostHeadroom, FPBuildingPlacementRules rules)
        {
            EnsureCapacity(vertCount + ghostHeadroom, count + 2);
            Array.Copy(polyX, PolyX, vertCount);
            Array.Copy(polyZ, PolyZ, vertCount);
            Array.Copy(polyStart, PolyStart, count + 1);
            Count = count;
            VertCount = vertCount;
            Rules = rules;
        }

        /// <summary>
        /// Grow-only, and read through the counts above — never through Length.
        ///
        /// <para>Growing PRESERVES what is there. Capture overwrites immediately and would not
        /// notice, but the preview also calls this to make room for a ghost, and dropping the
        /// accepted set there would answer "nothing is built here" with no error at all.</para>
        /// </summary>
        internal void EnsureCapacity(int vertices, int starts)
        {
            if (PolyX.Length < vertices)
            {
                var gx = new long[vertices];
                var gz = new long[vertices];
                Array.Copy(PolyX, gx, VertCount);
                Array.Copy(PolyZ, gz, VertCount);
                PolyX = gx;
                PolyZ = gz;
            }
            if (PolyStart.Length < starts)
            {
                var grown = new int[starts];
                Array.Copy(PolyStart, grown, PolyStart.Length);
                PolyStart = grown;
            }
        }
    }

    public sealed class FPNavMeshRebakeContext
    {
        /// <summary>
        /// The stage snapshot this context carves from. Public so a game that only kept the
        /// context can still reach it — the placement-preview entry points take a snapshot,
        /// because it is the immutable half and therefore safe to read from any thread.
        /// Exposing it opens no capability: every field on it is internal, and its one public
        /// member is already forwarded here as <see cref="ShapeExpansion"/>.
        /// </summary>
        public readonly FPNavMeshRebakeSnapshot Snapshot;
        internal readonly FPNavMeshRebakeBufferPool Pool;

        /// <summary>
        /// What this context last carved, kept so the placement preview can answer without being
        /// handed the building list again. See <see cref="FPBuildingAcceptedSet"/>.
        ///
        /// <para>Held unconditionally rather than behind a switch. A switch would have to be turned
        /// on, and a preview against a cache that was never filled reports an empty map — green
        /// over ground the rebake refuses, with nothing raised. Costing every context a few KB is
        /// the cheaper side of that trade, and a server that screens placement requests before
        /// admitting them as commands wants this anyway.</para>
        /// </summary>
        internal readonly FPBuildingAcceptedSet Accepted = new FPBuildingAcceptedSet();

        // Output recycling bookkeeping. Two invariants live here:
        //  - only a mesh THIS context produced can ever be recycled, so the base mesh — which the
        //    snapshot re-reads on every rebake — is structurally out of reach;
        //  - a mesh is recycled only after a NEWER one has been installed, never while it is live.
        private FPNavMesh _lastProduced;
        private FPNavMesh _installed;

        public FPNavMeshRebakeContext(FPNavMeshRebakeSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentException("FPNavMeshRebakeContext: snapshot is null");
            Snapshot = snapshot;
            Pool = new FPNavMeshRebakeBufferPool();
        }

        /// <summary>
        /// The stage's expanded shape table, or null when the stage has no catalog.
        ///
        /// Exposed because a game that places catalog shapes needs
        /// <see cref="FPBuildingShapeExpansion.TryTilingDelta"/> to author contact: the delta
        /// between two flush neighbours comes from the EXPANDED shape, and no whole world unit
        /// is one — a hexagon's is a multiple of sqrt(3), a turned box's of 1/sqrt(2). A placement
        /// UI that quantizes to whole units therefore cannot express a flush pack at all, however
        /// permissive the rules are.
        /// </summary>
        public FPBuildingShapeExpansion ShapeExpansion => Snapshot.ShapeExpansion;

        internal void NoteProduced(FPNavMesh mesh) => _lastProduced = mesh;

        /// <summary>
        /// Why each rebake through this context did or did not patch the previous mesh instead of
        /// rebuilding it. Exposed so a test can assert the mechanism actually fired — every
        /// correctness gate compares outputs, and output comparison stays green when the
        /// incremental path silently never runs.
        /// </summary>
        public FPNavMeshBuildPipeline.IncrementalOutcome PatchOutcome { get; } =
            new FPNavMeshBuildPipeline.IncrementalOutcome();

        /// <summary>
        /// The mesh a rebake may patch, or null when it must rebuild from scratch.
        ///
        /// <para>Null unless the last mesh this context produced was actually installed. That is
        /// the generation check: a caller that swaps for a while and then stops leaves
        /// <c>_installed</c> pointing at an ever-older mesh, and diffing against it would grow
        /// without bound — correct output, quietly collapsing performance. Requiring the chain to
        /// be caught up costs one reference comparison.</para>
        /// </summary>
        internal FPNavMesh PreviousForPatch => _lastProduced == null ? _installed : null;

        private bool _uncommittedWarned;

        /// <summary>
        /// Says once, per context, that a rebake is starting while the previous one's output was
        /// never installed — which is exactly when <see cref="PreviousForPatch"/> withholds the
        /// mesh and this rebake becomes a full rebuild with nothing recycled.
        ///
        /// <para>Not phrased as a bug, because it is not necessarily one: the design tolerates a
        /// caller that swaps for a while and then stops, and returning null there is the response
        /// to it. A single missed <c>CommitSwap</c> is also self-healing — the next output can be
        /// committed and the chain resumes, so the whole cost is one un-patched rebake.</para>
        ///
        /// <para>What it is really for is the caller who never commits at all. That one pays
        /// double the time and roughly 1,500x the allocation for the life of the room, produces a
        /// byte-identical mesh every time, and has nothing to notice it by — <c>PatchOutcome</c>
        /// cannot help, because its counters are only touched once a previous mesh exists and so
        /// read all-zero exactly as they would on a first rebake. Hence once per context: a
        /// harmless line for the one-off slip, and the only signal there is for the systematic
        /// one.</para>
        /// </summary>
        internal void WarnIfUncommitted(IKLogger logger)
        {
            if (_lastProduced == null || _uncommittedWarned)
                return;
            _uncommittedWarned = true;
            logger?.KWarning(
                $"[FPNavMeshRebakeContext] rebaking while the previous output was never installed — " +
                $"this rebake cannot patch the previous mesh and nothing is recycled, so it costs " +
                $"about twice the time and three orders of magnitude more allocation than a " +
                $"committed one. Call CommitSwap(mesh) after the swap actually happens. Reported " +
                $"once per context; the mesh itself is correct either way, which is why nothing " +
                $"else reports it.");
        }

        /// <summary>
        /// Tells the context that <paramref name="installed"/> — which must be the mesh the last
        /// <see cref="FPNavMeshRebaker.Rebake(FPNavMeshRebakeContext, FPBuildingRect[], IKLogger)"/>
        /// returned — is now the live navmesh. The mesh it replaced is retired and its storage
        /// goes back to the pool for the rebake AFTER the next one.
        ///
        /// The mesh that gets retired must not be read again — its storage becomes another mesh's.
        /// Game code must therefore not cache a navmesh across swaps; read the current one from
        /// the provider (or <c>FPNavAgentSystem.CurrentMesh</c>) each time. In DEBUG a retired
        /// mesh throws on any access, which is how that contract is enforced.
        ///
        /// Call this AFTER the swap has actually happened, never before. Recycling at production
        /// time would hand out the arrays of the mesh that is still live, and the rebake that
        /// validates a building placement can THROW — in that path nothing is installed at all, so
        /// an early recycle would destroy the live mesh with no replacement.
        /// </summary>
        public void CommitSwap(FPNavMesh installed)
        {
            if (installed == null)
                throw new ArgumentException("FPNavMeshRebakeContext.CommitSwap: installed is null");
            if (!ReferenceEquals(installed, _lastProduced))
                throw new InvalidOperationException(
                    "FPNavMeshRebakeContext.CommitSwap: the installed mesh is not this context's most " +
                    "recent output — only meshes produced here may enter the recycling chain.");

            if (_installed != null)
                Pool.RetireOutput(_installed);
            _installed = installed;
            _lastProduced = null;
        }
    }

    /// <summary>
    /// Runtime navmesh full rebake orchestrator: base navmesh + current building set →
    /// new FPNavMesh.
    ///
    /// Pipeline: base boundary rings (FPNavMeshObstacleExtractor, load-time reusable) +
    /// base interior vertices + building hole rings (footprint ⊕ BakeAgentRadius, conservative
    /// grid expansion) → FPConstrainedDelaunay (parity erase removes outer region and hole
    /// interiors — winding-agnostic) → FPNavMeshBuildPipeline (FP64 overload — bake metadata
    /// inherited bit-exactly) → areaMask/costMultiplier inheritance (uniform base only;
    /// per-region reassignment needs region polygon assets, which are not preserved today).
    ///
    /// The rebaker validates strictly and REJECTS rather than repairing: a placement must be
    /// pairwise non-overlapping after radius expansion and strictly inside the walkable region.
    /// Rejection is the defense line — a repaired-but-wrong navmesh is identical on every peer
    /// and therefore invisible to desync checks.
    ///
    /// Caller responsibilities (determinism envelope):
    ///  - Invoke from the deterministic command stream only (same tick, same order on all
    ///    peers; building list order must be deterministic).
    ///  - Swap the returned mesh into the provider at a tick boundary, then re-run
    ///    FPNavAgentSystem.LoadNavMeshObstacles and reseed agents (CurrentTriangleIndex
    ///    re-query + corridor invalidation — full reseed, indices are fully rebuilt).
    ///  - Cross-check <see cref="ComputeFingerprint"/> between peers (the navmesh stays outside
    ///    the state hash).
    ///
    /// Discrete-event scope only — allocates freely (never per-tick).
    /// </summary>
    public static class FPNavMeshRebaker
    {
        /// <summary>
        /// Rebakes the base navmesh with the given buildings carved out as holes.
        /// Throws <see cref="ArgumentException"/> on malformed input,
        /// <see cref="NotSupportedException"/> on multi-level or non-grid base meshes,
        /// <see cref="InvalidOperationException"/> on invalid placement
        /// (touching/overlapping buildings, building touching or leaving the walkable region).
        /// </summary>
        /// <summary>
        /// Builds the per-stage rebake snapshot: validates the base (grid-snapped,
        /// single-level), extracts boundary rings, assembles base constraints and freezes the
        /// CDT base state. Throws on unsupported bases (multi-level / off-grid) — snapshot
        /// failure means the rebake feature itself is unavailable for this stage (surface it
        /// at load time; contrast with the best-effort Prewarm). Call at match load or on a
        /// worker — never on the tick thread (costs a full base insertion, ~30ms at 22k tris).
        /// </summary>
        /// <summary>
        /// Builds a room's rebake context in one step: snapshot (see
        /// <see cref="CreateSnapshot"/> for the cost and the throwing contract) + that room's
        /// buffer pool. This is what a game seam normally calls at match load; rooms that
        /// share one stage snapshot should call <see cref="CreateSnapshot"/> once and
        /// construct an <see cref="FPNavMeshRebakeContext"/> per room instead.
        /// </summary>
        public static FPNavMeshRebakeContext CreateContext(
            FPNavMesh baseMesh, IKLogger logger = null, bool prewarm = true,
            FPBuildingShapeCatalog shapeCatalog = null)
        {
            return new FPNavMeshRebakeContext(CreateSnapshot(baseMesh, logger, prewarm, shapeCatalog));
        }

        public static FPNavMeshRebakeSnapshot CreateSnapshot(
            FPNavMesh baseMesh, IKLogger logger = null, bool prewarm = true,
            FPBuildingShapeCatalog shapeCatalog = null)
        {
            if (baseMesh == null)
                throw new ArgumentException("FPNavMeshRebaker: baseMesh is null");

            // --- Base vertices: must be grid-snapped and single-level (XZ-unique).
            int baseCount = baseMesh.Vertices.Length;
            var xs = new long[baseCount];
            var zs = new long[baseCount];
            var ys = new FP64[baseCount];
            var coordToIndex = new Dictionary<(long, long), int>(baseCount);

            for (int i = 0; i < baseCount; i++)
            {
                FPVector3 v = baseMesh.Vertices[i];
                long sx = FPGeoPredicates.Snap(v.x);
                long sz = FPGeoPredicates.Snap(v.z);
                if (!FPGeoPredicates.IsOnGrid(v.x) || !FPGeoPredicates.IsOnGrid(v.z))
                    throw new NotSupportedException(
                        "FPNavMeshRebaker: base mesh is not predicate-grid snapped — rebake the base asset first");
                if (!coordToIndex.TryAdd((sx, sz), i))
                    throw new NotSupportedException(
                        "FPNavMeshRebaker: base mesh has XZ-duplicate vertices (multi-level) — the rebaker is single-level only");
                xs[i] = sx;
                zs[i] = sz;
                ys[i] = v.y;
            }

            // --- Base boundary rings (existing extractor, winding-aware; parity erase makes
            //     winding irrelevant here). Ring vertices map back to base indices by coordinate.
            FPNavMeshObstacleExtractor.Extract(baseMesh, out FPVector2[] ringVerts, out int[] ringOffsets);
            var constraints = new List<int>(ringVerts.Length * 2);
            var ringEdges = new List<(int a, int b)>(ringVerts.Length);
            for (int p = 0; p < ringOffsets.Length; p++)
            {
                // polygonOffsets = start index per ring; ring p spans [offsets[p], offsets[p+1])
                // or vertices.Length for the last ring (trailing sentinel tolerated).
                int start = ringOffsets[p];
                int end = p + 1 < ringOffsets.Length ? ringOffsets[p + 1] : ringVerts.Length;
                if (start >= end)
                    continue;
                for (int i = start; i < end; i++)
                {
                    int j = i + 1 < end ? i + 1 : start;
                    int ia = RingIndex(ringVerts[i], coordToIndex);
                    int ib = RingIndex(ringVerts[j], coordToIndex);
                    constraints.Add(ia);
                    constraints.Add(ib);
                    ringEdges.Add((ia, ib));
                }
            }

            var cdtSnapshot = FPConstrainedDelaunay.BuildSnapshot(xs, zs, constraints.ToArray(), logger);
            logger?.KInformation($"[FPNavMeshRebaker] snapshot created: {baseCount} base vertices, " +
                $"{ringEdges.Count} ring constraints");
            // Expanding the catalog here, not per placement, is what makes contact authorable
            // It also means a shape that cannot be offset conservatively fails at LOAD,
            // beside the base-mesh checks — the same class of "this stage cannot support the
            // feature" error, surfaced in the same place.
            FPBuildingShapeExpansion expansion = shapeCatalog == null
                ? null
                : new FPBuildingShapeExpansion(shapeCatalog, baseMesh.BakeAgentRadius);
            if (expansion != null)
                logger?.KInformation(
                    $"[FPNavMeshRebaker] shape catalog expanded: {shapeCatalog.EntryCount} entries " +
                    $"at radius {baseMesh.BakeAgentRadius.ToDouble():F3}, hash 0x{shapeCatalog.Hash:X16}");

            var snapshot = new FPNavMeshRebakeSnapshot(
                baseMesh, cdtSnapshot, xs, zs, ys, coordToIndex, ringEdges, expansion);
            if (prewarm)
                Prewarm(snapshot, logger);
            return snapshot;
        }

        /// <summary>
        /// Rebakes from a cached snapshot with the given buildings carved out as holes —
        /// the runtime hot path (clone + incremental hole insertion
        /// instead of a from-scratch base insertion). Throws <see cref="ArgumentException"/> on
        /// malformed input and <see cref="InvalidOperationException"/> on invalid placement
        /// (touching/overlapping buildings, building touching or leaving the walkable region).
        /// </summary>
        /// <param name="buildingCount">
        /// How many entries of <paramref name="buildings"/> are live — the same convention
        /// <see cref="RebakePlacements(FPNavMeshRebakeContext, FPBuildingPlacement[], IKLogger, FPBuildingPlacementRules, int)"/>
        /// uses, so a caller holding a reusable buffer does not have to trim to an exact-size copy
        /// first. -1 (the default) means the array is exactly its content.
        /// </param>
        public static FPNavMesh Rebake(
            FPNavMeshRebakeContext context, FPBuildingRect[] buildings, IKLogger logger = null,
            FPBuildingPlacementRules rules = default, int buildingCount = -1)
        {
            if (!TryRebake(context, buildings, out FPNavMesh mesh, out FPBuildingRejectionInfo rejection,
                    logger, rules, buildingCount))
                throw Rejected(rejection, rules);
            return mesh;
        }

        /// <summary>
        /// Rebakes, reporting a refused PLACEMENT as <c>false</c> instead of an exception.
        ///
        /// <para>A placement the map will not take is a normal outcome — the player caused it and
        /// the player is meant to see it — so it comes back as a value. A malformed REQUEST still
        /// throws: a stale shape id or an unquantized centre is the game's own bug, and folding it
        /// into the same <c>false</c> would let the game report its bug to the player as "you
        /// cannot build there", identically on every peer and therefore invisibly. Catching
        /// <see cref="ArgumentException"/> around this call is still the right thing; it just is
        /// not how a rejection arrives any more.</para>
        ///
        /// <para>Nothing is formatted on the rejecting path — <paramref name="rejection"/> carries
        /// a reason and indices, not text. The message only exists if someone throws, which is
        /// what the <c>Rebake</c> overload above does.</para>
        ///
        /// <para>Offered on the context tier only. The snapshot and base-mesh overloads are
        /// documented as tests-and-one-shot, and a rejection being a frequent normal outcome is a
        /// production concern.</para>
        /// </summary>
        /// <inheritdoc cref="Rebake(FPNavMeshRebakeContext, FPBuildingRect[], IKLogger, FPBuildingPlacementRules, int)" path="/param[@name='buildingCount']"/>
        public static bool TryRebake(
            FPNavMeshRebakeContext context, FPBuildingRect[] buildings,
            out FPNavMesh mesh, out FPBuildingRejectionInfo rejection,
            IKLogger logger = null, FPBuildingPlacementRules rules = default, int buildingCount = -1)
        {
            if (context == null)
                throw new ArgumentException("FPNavMeshRebaker: context is null");
            context.WarnIfUncommitted(logger);
            mesh = Rebake(context.Snapshot, buildings, logger, context.Pool, rules, out rejection,
                context.PreviousForPatch, context.PatchOutcome, buildingCount, context.Accepted);
            if (mesh == null)
                return false;
            // Only on success. Telling the context about a mesh that does not exist would leave
            // its recycling chain pointing at nothing, and the next rebake would quietly stop
            // patching the previous mesh — slower and far heavier, with no visible symptom.
            context.NoteProduced(mesh);
            rejection = default;
            return true;
        }

        /// <summary>
        /// Unpooled snapshot overload — tests and one-shot callers. A room that rebakes
        /// repeatedly should hold an <see cref="FPNavMeshRebakeContext"/> and use the overload
        /// above; the result is bit-identical either way.
        /// </summary>
        /// <inheritdoc cref="Rebake(FPNavMeshRebakeContext, FPBuildingRect[], IKLogger, FPBuildingPlacementRules, int)" path="/param[@name='buildingCount']"/>
        public static FPNavMesh Rebake(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingRect[] buildings, IKLogger logger = null,
            FPBuildingPlacementRules rules = default, int buildingCount = -1)
        {
            FPNavMesh mesh = Rebake(snapshot, buildings, logger, null, rules,
                out FPBuildingRejectionInfo rejection, null, null, buildingCount);
            if (mesh == null)
                throw Rejected(rejection, rules);
            return mesh;
        }
        /// <summary>
        /// Rects to radius-expanded convex polygons, CSR-packed. Shared by the rebake and the
        /// placement preview so both expand a footprint the same way — the preview's whole claim
        /// is that it sees what the rebake sees, and that starts here.
        /// </summary>
        private static void BuildRectPolygons(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingRect[] buildings, int buildingCount,
            FPNavMeshRebakeBufferPool buffers,
            out long[] polyX, out long[] polyZ, out int[] polyStart, out long[] polyBounds,
            FP64[] polyYs = null)
        {
            polyX = buffers.PolyXs(buildingCount * 4);
            polyZ = buffers.PolyZs(buildingCount * 4);
            polyStart = buffers.PolyStarts(buildingCount + 1);
            polyBounds = buffers.PolyBounds(buildingCount * 4);
            int polyVertCount = 0;
            // Everything below indexes [0, buildingCount) — never buildings.Length. Reading the
            // array's own length here is what the count exists to stop: past the live prefix a
            // reusable buffer holds the PREVIOUS call's rects, and those are already known to be
            // mutually legal, so they would be carved without a single check firing.

            for (int b = 0; b < buildingCount; b++)
                WriteRectPolygon(snapshot, buildings[b], b, b,
                    polyX, polyZ, polyStart, polyBounds, polyYs, ref polyVertCount);
            polyStart[buildingCount] = polyVertCount;
        }

        /// <summary>
        /// One rect into slot <paramref name="slot"/> of the CSR arrays, appending its vertices at
        /// <paramref name="vertCount"/>.
        ///
        /// <para>Split out of <see cref="BuildRectPolygons"/> so the placement preview can write a
        /// ghost into a slot the rebake already sized without a second copy of the expansion. The
        /// expansion formula, the vertex order and the domain check all live here, and all three
        /// are load-bearing — a preview that expanded differently from the rebake would answer a
        /// question the rebake never asked.</para>
        ///
        /// <para><paramref name="boundsSlot"/> is separate from <paramref name="slot"/> so a ghost
        /// can be written into a four-long AABB buffer while occupying vertex slot N — the accepted
        /// set keeps no bounds for buildings already placed, because nothing reads them.</para>
        /// </summary>
        private static void WriteRectPolygon(
            FPNavMeshRebakeSnapshot snapshot, in FPBuildingRect rect, int slot, int boundsSlot,
            long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds, FP64[] polyYs,
            ref int vertCount)
        {
            long m = FPGeoPredicates.MAX_SNAPPED_COORD;
            FP64 r = snapshot.BaseMesh.BakeAgentRadius;

            if (rect.MinX.RawValue >= rect.MaxX.RawValue || rect.MinZ.RawValue >= rect.MaxZ.RawValue)
                throw new ArgumentException($"FPNavMeshRebaker: building {slot} is degenerate (min >= max)");

            long minX = SnapFloor(rect.MinX.RawValue - r.RawValue);
            long minZ = SnapFloor(rect.MinZ.RawValue - r.RawValue);
            long maxX = SnapCeil(rect.MaxX.RawValue + r.RawValue);
            long maxZ = SnapCeil(rect.MaxZ.RawValue + r.RawValue);
            if (minX < -m || maxX > m || minZ < -m || maxZ > m)
                throw new ArgumentException($"FPNavMeshRebaker: building {slot} (expanded) outside the snapped domain");

            // CCW from the min corner. Vertex 0 stays (minX, minZ) because that is what the
            // parity probe reads, and keeping it first is what makes the rect path
            // bit-identical to the pre-polygon code.
            polyStart[slot] = vertCount;
            polyX[vertCount] = minX; polyZ[vertCount++] = minZ;
            polyX[vertCount] = maxX; polyZ[vertCount++] = minZ;
            polyX[vertCount] = maxX; polyZ[vertCount++] = maxZ;
            polyX[vertCount] = minX; polyZ[vertCount++] = maxZ;
            polyBounds[boundsSlot * 4] = minX; polyBounds[boundsSlot * 4 + 1] = minZ;
            polyBounds[boundsSlot * 4 + 2] = maxX; polyBounds[boundsSlot * 4 + 3] = maxZ;
            if (polyYs != null) polyYs[slot] = rect.Y;
        }



        private static FPNavMesh Rebake(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingRect[] buildings, IKLogger logger,
            FPNavMeshRebakeBufferPool pool, FPBuildingPlacementRules rules,
            out FPBuildingRejectionInfo rejection,
            FPNavMesh previous = null, FPNavMeshBuildPipeline.IncrementalOutcome outcome = null,
            int requestedCount = -1, FPBuildingAcceptedSet accepted = null)
        {
            if (snapshot == null)
                throw new ArgumentException("FPNavMeshRebaker: snapshot is null");
            buildings = buildings ?? Array.Empty<FPBuildingRect>();
            int buildingCount = ResolveLiveCount(requestedCount, buildings.Length, "buildingCount");

            FPNavMesh baseMesh = snapshot.BaseMesh;
            long[] xs = snapshot.BaseXs;
            long[] zs = snapshot.BaseZs;
            int baseCount = xs.Length;
            List<(int a, int b)> ringEdges = snapshot.RingEdges;

            // --- Buildings: expand by BakeAgentRadius into a CONVEX POLYGON per building
            //     validate, then append its vertices + ring
            //     constraints. An axis-aligned rect is shapeId 0 — four vertices produced by the
            //     same SnapFloor/SnapCeil formula as before, so its result is unchanged.
            var buffers0 = pool ?? FPNavMeshRebakeBufferPool.NonPooling;
            FP64[] polyYs = buffers0.PolyYs(buildingCount == 0 ? 1 : buildingCount);
            BuildRectPolygons(snapshot, buildings, buildingCount, buffers0,
                out long[] polyX, out long[] polyZ, out int[] polyStart, out long[] polyBounds, polyYs);
            int polyVertCount = polyStart[buildingCount];

            return RebakeFromPolygons(
                snapshot, polyX, polyZ, polyStart, polyBounds, polyYs, buildingCount,
                polyVertCount, logger, pool, rules, out rejection, previous, outcome, accepted);
        }

        /// <summary>
        /// Rebakes with catalog shapes: each placement names an entry and where its CENTRE goes.
        ///
        /// A separate NAME rather than an overload of Rebake: overloading on the array type makes
        /// every existing `Rebake(x, null)` call ambiguous, which is a cost paid by consumers for
        /// no gain at the call site.
        ///
        /// The centre must land exactly on the predicate snap grid. That is not a formality —
        /// the catalog's offsets are integers relative to the centre, so an off-grid centre puts
        /// every vertex off-grid, and from there the shared edges that make contact work stop
        /// lining up. It fails loudly for the same reason <see cref="CreateSnapshot"/> refuses an
        /// off-grid base mesh: the alternative is a quiet drift nobody attributes correctly.
        /// </summary>
        /// <param name="placementCount">
        /// How many entries of <paramref name="placements"/> are live. -1 (the default) means the
        /// array is exactly its content, which is the same convention
        /// <see cref="FPNavMeshBuildPipeline.BuildFromConformingTriangulation"/> uses for its
        /// vertex count. Passing it lets a caller hand in a REUSED buffer instead of trimming to
        /// an exact-size copy first — the sample's placement path allocated one array per
        /// placement purely to satisfy the length-is-the-count reading.
        /// </param>
        public static FPNavMesh RebakePlacements(
            FPNavMeshRebakeContext context, FPBuildingPlacement[] placements,
            IKLogger logger = null, FPBuildingPlacementRules rules = default,
            int placementCount = -1)
        {
            if (!TryRebakePlacements(context, placements, out FPNavMesh mesh,
                    out FPBuildingRejectionInfo rejection, logger, rules, placementCount))
                throw Rejected(rejection, rules);
            return mesh;
        }

        /// <inheritdoc cref="TryRebake(FPNavMeshRebakeContext, FPBuildingRect[], out FPNavMesh, out FPBuildingRejectionInfo, IKLogger, FPBuildingPlacementRules, int)"/>
        /// <inheritdoc cref="RebakePlacements(FPNavMeshRebakeContext, FPBuildingPlacement[], IKLogger, FPBuildingPlacementRules, int)" path="/param[@name='placementCount']"/>
        public static bool TryRebakePlacements(
            FPNavMeshRebakeContext context, FPBuildingPlacement[] placements,
            out FPNavMesh mesh, out FPBuildingRejectionInfo rejection,
            IKLogger logger = null, FPBuildingPlacementRules rules = default,
            int placementCount = -1)
        {
            if (context == null)
                throw new ArgumentException("FPNavMeshRebaker: context is null");
            context.WarnIfUncommitted(logger);
            mesh = RebakePlacements(context.Snapshot, placements, logger, context.Pool, rules,
                out rejection, context.PreviousForPatch, context.PatchOutcome, placementCount,
                context.Accepted);
            if (mesh == null)
                return false;
            // Success only — see TryRebake for why a rejection must not reach NoteProduced.
            context.NoteProduced(mesh);
            rejection = default;
            return true;
        }

        /// <inheritdoc cref="RebakePlacements(FPNavMeshRebakeContext, FPBuildingPlacement[], IKLogger, FPBuildingPlacementRules, int)"/>
        public static FPNavMesh RebakePlacements(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingPlacement[] placements,
            IKLogger logger = null, FPBuildingPlacementRules rules = default,
            int placementCount = -1)
        {
            FPNavMesh mesh = RebakePlacements(snapshot, placements, logger, null, rules,
                out FPBuildingRejectionInfo rejection, null, null, placementCount);
            if (mesh == null)
                throw Rejected(rejection, rules);
            return mesh;
        }

        /// <summary>
        /// Turns a refusal back into the exception the throwing overloads have always raised —
        /// the ONLY place a rejection message is built, and only when someone is about to throw.
        ///
        /// <para>Byte-identical to what the checks used to produce inline; the goldens in
        /// <c>FPBuildingRejectionGoldenTests</c> were recorded before the move and pin that.
        /// The overlap wording branches on the rules, which is why they travel with the info
        /// rather than being folded into it.</para>
        ///
        /// <para>The coordinate is formatted with the invariant culture. It used to inherit the
        /// ambient one, so the same swallowed hole read "(-2.000, 0.000)" on one machine and
        /// "(-2,000, 0,000)" on another — harmless for the engine, which never parses it back, but
        /// an odd thing to hand two peers who are supposed to be seeing the same match.</para>
        /// </summary>
        internal static InvalidOperationException Rejected(
            in FPBuildingRejectionInfo info, in FPBuildingPlacementRules rules)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            switch (info.Reason)
            {
                case FPBuildingRejection.BuildingsOverlap:
                    return new InvalidOperationException(
                        $"FPNavMeshRebaker: buildings {info.IndexA} and {info.IndexB} " +
                        (rules.AllowBuildingTouch ? "overlap" : "touch or overlap") +
                        " after radius expansion — placement must be rejected" +
                        (rules.AllowBuildingTouch ? " (interiors must stay disjoint)" : ""));
                case FPBuildingRejection.TouchesWalkableBoundary:
                    return new InvalidOperationException(
                        "FPNavMeshRebaker: building (expanded) touches or crosses the walkable boundary — reject placement");
                case FPBuildingRejection.OutsideWalkableRegion:
                    return new InvalidOperationException(
                        "FPNavMeshRebaker: building (expanded) lies outside the walkable region — reject placement");
                case FPBuildingRejection.SwallowsBakedHole:
                    return new InvalidOperationException(string.Format(ci,
                        "FPNavMeshRebaker: building {0} (expanded) fully contains a baked hole ring at "
                        + "({1:F3}, {2:F3}) — reject placement (swallowing a hole would turn its interior walkable)",
                        info.IndexA, info.Site.x.ToDouble(), info.Site.y.ToDouble()));
                case FPBuildingRejection.EmptyWalkableRegion:
                    return new InvalidOperationException(
                        "FPNavMeshRebaker: rebake produced an empty walkable region");
                default:
                    return new InvalidOperationException(
                        $"FPNavMeshRebaker: rebake failed with no reason recorded ({info.Reason})");
            }
        }
        /// <summary>
        /// Catalog placements to radius-expanded convex polygons, CSR-packed. Shared by the rebake
        /// and the placement preview for the same reason the rect version is: the preview's claim
        /// is that it sees what the rebake sees, and a second copy of this expansion would be the
        /// first place that stopped being true.
        /// </summary>
        private static void BuildPlacementPolygons(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingPlacement[] placements, int count,
            FPNavMeshRebakeBufferPool buffers,
            out long[] polyX, out long[] polyZ, out int[] polyStart, out long[] polyBounds,
            FP64[] polyYs = null)
        {
            FPBuildingShapeExpansion expansion = snapshot.ShapeExpansion;
            if (count > 0 && expansion == null)
                throw new ArgumentException(
                    "FPNavMeshRebaker: this stage's snapshot was built without a shape catalog");

            int total = 0;
            for (int b = 0; b < count; b++)
                total += expansion.Catalog.VertexCount(
                    ResolveEntryOrThrow(expansion, placements[b].ShapeId, placements[b].Orientation, b));

            polyX = buffers.PolyXs(total == 0 ? 1 : total);
            polyZ = buffers.PolyZs(total == 0 ? 1 : total);
            polyStart = buffers.PolyStarts(count + 1);
            polyBounds = buffers.PolyBounds(count == 0 ? 4 : count * 4);

            int n = 0;
            for (int b = 0; b < count; b++)
                WritePlacementPolygon(expansion, placements[b], b, b,
                    polyX, polyZ, polyStart, polyBounds, polyYs, ref n);
            polyStart[count] = n;
        }

        /// <summary>
        /// The catalog entry a placement names, or an exception saying which half is wrong.
        ///
        /// <para>Its own method because the ghost has no sizing pass in front of it to have already
        /// checked the shape id — <see cref="BuildPlacementPolygons"/> could once write with an
        /// unchecked <c>TryResolveEntry</c> precisely because its own first loop had thrown for
        /// anything bad, and a single-polygon writer has no such loop.</para>
        /// </summary>
        private static int ResolveEntryOrThrow(
            FPBuildingShapeExpansion expansion, int shape, int orientation, int slot)
        {
            int e = expansion.Catalog.TryResolveEntry(shape, orientation);
            if (e >= 0)
                return e;
            if ((uint)shape >= (uint)expansion.Catalog.ShapeCount)
                throw new ArgumentException(
                    $"FPNavMeshRebaker: placement {slot} names shape {shape}, and this catalog "
                    + $"holds {expansion.Catalog.ShapeCount}");
            throw new ArgumentException(
                $"FPNavMeshRebaker: placement {slot} names orientation {orientation} of shape "
                + $"{shape}, which turns {expansion.Catalog.DirectionCount(shape)} ways");
        }

        /// <summary>
        /// One catalog placement into slot <paramref name="slot"/>, appending its vertices at
        /// <paramref name="vertCount"/>.
        ///
        /// <para><inheritdoc cref="WriteRectPolygon" path="/summary/para[1]/node()"/></para>
        /// </summary>
        private static void WritePlacementPolygon(
            FPBuildingShapeExpansion expansion, in FPBuildingPlacement p, int slot, int boundsSlot,
            long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds, FP64[] polyYs,
            ref int vertCount)
        {
            long cx = FPGeoPredicates.Snap(p.CentreX);
            long cz = FPGeoPredicates.Snap(p.CentreZ);
            if (!FPGeoPredicates.IsOnGrid(p.CentreX) || !FPGeoPredicates.IsOnGrid(p.CentreZ))
                throw new ArgumentException(
                    $"FPNavMeshRebaker: placement {slot} centre is not on the predicate snap grid — "
                    + "catalog offsets are integers about the centre, so an off-grid centre "
                    + "puts every vertex off-grid and contact stops lining up");

            long[] ex = expansion.ExpandedX;
            long[] ez = expansion.ExpandedZ;
            int e = ResolveEntryOrThrow(expansion, p.ShapeId, p.Orientation, slot);
            int s = expansion.Catalog.EntryStart[e], t = expansion.Catalog.EntryStart[e + 1];
            polyStart[slot] = vertCount;
            long minX = long.MaxValue, minZ = long.MaxValue, maxX = long.MinValue, maxZ = long.MinValue;
            for (int v = s; v < t; v++)
            {
                long vx = cx + ex[v], vz = cz + ez[v];
                polyX[vertCount] = vx; polyZ[vertCount] = vz; vertCount++;
                if (vx < minX) minX = vx;
                if (vx > maxX) maxX = vx;
                if (vz < minZ) minZ = vz;
                if (vz > maxZ) maxZ = vz;
            }
            long m = FPGeoPredicates.MAX_SNAPPED_COORD;
            if (minX < -m || maxX > m || minZ < -m || maxZ > m)
                throw new ArgumentException($"FPNavMeshRebaker: placement {slot} (expanded) outside the snapped domain");
            polyBounds[boundsSlot * 4] = minX; polyBounds[boundsSlot * 4 + 1] = minZ;
            polyBounds[boundsSlot * 4 + 2] = maxX; polyBounds[boundsSlot * 4 + 3] = maxZ;
            if (polyYs != null) polyYs[slot] = p.Y;
        }

        private static FPNavMesh RebakePlacements(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingPlacement[] placements,
            IKLogger logger, FPNavMeshRebakeBufferPool pool, FPBuildingPlacementRules rules,
            out FPBuildingRejectionInfo rejection,
            FPNavMesh previous = null, FPNavMeshBuildPipeline.IncrementalOutcome outcome = null,
            int placementCount = -1, FPBuildingAcceptedSet accepted = null)
        {
            if (snapshot == null)
                throw new ArgumentException("FPNavMeshRebaker: snapshot is null");
            placements = placements ?? Array.Empty<FPBuildingPlacement>();

            int count = ResolveLiveCount(placementCount, placements.Length, "placementCount");

            FPBuildingShapeExpansion expansion = snapshot.ShapeExpansion;
            if (count > 0 && expansion == null)
                throw new ArgumentException(
                    "FPNavMeshRebaker: this stage's snapshot was built without a shape catalog");

            var buffers = pool ?? FPNavMeshRebakeBufferPool.NonPooling;

            FP64[] polyYs = buffers.PolyYs(count == 0 ? 1 : count);
            BuildPlacementPolygons(snapshot, placements, count, buffers,
                out long[] polyX, out long[] polyZ, out int[] polyStart, out long[] polyBounds, polyYs);
            int n = polyStart[count];

            return RebakeFromPolygons(
                snapshot, polyX, polyZ, polyStart, polyBounds, polyYs, count, n, logger, pool, rules,
                out rejection, previous, outcome, accepted);
        }

        /// <summary>
        /// Rebake from FOOTPRINTS — unexpanded snapped convex polygons, CSR-packed. Expands each
        /// by the base mesh's bake radius (miter, see <see cref="FPConvexOffset"/>), proves the
        /// result contains the true offset, and hands the expanded polygons on.
        ///
        /// This is the seam a shape catalog plugs into: the catalog owns the footprints, this
        /// owns turning them into what the CDT carves. The rect overload keeps its own
        /// SnapFloor/SnapCeil expansion instead of coming through here — for an axis-aligned box
        /// that formula is already exactly conservative, and the goldens rest on it being
        /// untouched.
        ///
        /// Expansion failure throws rather than rejecting the placement: a footprint that cannot
        /// be offset is a broken shape definition, not a bad position, and the two should not
        /// arrive at the game as the same kind of error.
        /// </summary>
        internal static FPNavMesh RebakeFromFootprints(
            FPNavMeshRebakeSnapshot snapshot,
            long[] footX, long[] footZ, int[] footStart, FP64[] ys,
            int buildingCount, int footVertCount,
            IKLogger logger, FPNavMeshRebakeBufferPool pool, FPBuildingPlacementRules rules,
            out FPBuildingRejectionInfo rejection,
            FPNavMesh previous = null, FPNavMeshBuildPipeline.IncrementalOutcome outcome = null)
        {
            if (snapshot == null)
                throw new ArgumentException("FPNavMeshRebaker: snapshot is null");

            var buffers = pool ?? FPNavMeshRebakeBufferPool.NonPooling;
            FP64 radius = snapshot.BaseMesh.BakeAgentRadius;
            long[] polyX = buffers.PolyXs(footVertCount == 0 ? 1 : footVertCount);
            long[] polyZ = buffers.PolyZs(footVertCount == 0 ? 1 : footVertCount);
            long[] polyBounds = buffers.PolyBounds(buildingCount == 0 ? 4 : buildingCount * 4);

            for (int b = 0; b < buildingCount; b++)
            {
                int vs = footStart[b], ve = footStart[b + 1];
                if (!FPConvexOffset.Expand(footX, footZ, vs, ve, radius, polyX, polyZ, vs))
                    throw new ArgumentException(
                        $"FPNavMeshRebaker: building {b} footprint could not be offset by the bake "
                        + "radius (degenerate, collinear, or outside the snapped domain)");
                if (!FPConvexOffset.Validate(footX, footZ, vs, ve, polyX, polyZ, vs, radius, out string why))
                    throw new ArgumentException(
                        $"FPNavMeshRebaker: building {b} expansion is not conservative — {why}");

                long minX = long.MaxValue, minZ = long.MaxValue, maxX = long.MinValue, maxZ = long.MinValue;
                for (int v = vs; v < ve; v++)
                {
                    if (polyX[v] < minX) minX = polyX[v];
                    if (polyX[v] > maxX) maxX = polyX[v];
                    if (polyZ[v] < minZ) minZ = polyZ[v];
                    if (polyZ[v] > maxZ) maxZ = polyZ[v];
                }
                polyBounds[b * 4] = minX; polyBounds[b * 4 + 1] = minZ;
                polyBounds[b * 4 + 2] = maxX; polyBounds[b * 4 + 3] = maxZ;
            }

            return RebakeFromPolygons(
                snapshot, polyX, polyZ, footStart, polyBounds, ys,
                buildingCount, footVertCount, logger, pool, rules, out rejection, previous, outcome);
        }
        // ── Placement preview: the same verdict, without carving ────────────────────

        /// <summary>
        /// Answers "can THIS one go here?" — the form a placement ghost wants, because the ghost is
        /// the only thing that moves between frames.
        ///
        /// <para><b>Precondition: <paramref name="existing"/> is a set the rebaker already
        /// accepted.</b> That is what makes checking one building sufficient — with no violation
        /// among the others, every reason left involves the ghost. Pass a set that was never
        /// accepted and the answer can be wrong, which is the price of not re-checking it.</para>
        ///
        /// <para><b>Same verdict, not an approximation.</b> Every reason a player can be refused
        /// is decided before any triangle is cut, so this shares the rebake's validation rather
        /// than reimplementing it — there is no second copy of the rules to keep in sync. The one
        /// reason it cannot report is <see cref="FPBuildingRejection.EmptyWalkableRegion"/>, which
        /// no placement can cause. The reported indices are those of the rebake that would carve
        /// this list with <paramref name="ghost"/> appended: the ghost is
        /// <paramref name="existingCount"/>.</para>
        ///
        /// <para><b>Pass the same <paramref name="rules"/> the rebake gets.</b> The default forbids
        /// contact, so a game that allows touching but omits it here paints red over ground the
        /// player could actually build on — and nothing reports that.</para>
        ///
        /// <para><b>A pass is not a promise.</b> The command runs later against whatever list
        /// exists then — another player may have built in the meantime. Treat green as optimistic
        /// and keep handling the refusal.</para>
        ///
        /// <para><b>It does not replace the rebake.</b> Preview from input, on the local peer; the
        /// rebake still validates from the command stream, which is the only place every peer
        /// agrees.</para>
        ///
        /// <para>About 0.07 ms on a 22k-triangle stage, near enough regardless of how many
        /// buildings are already down — the cost is one walk of the base mesh's edges.</para>
        ///
        /// <para>A game that holds an <see cref="FPNavMeshRebakeContext"/> should prefer the
        /// overload that takes one: it supplies the accepted set and the rules itself, so the two
        /// things this form cannot check for you stop being possible to get wrong.</para>
        /// </summary>
        public static bool TryValidateOne(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingRect[] existing, int existingCount,
            in FPBuildingRect ghost, out FPBuildingRejectionInfo rejection,
            FPBuildingPreviewScratch scratch, FPBuildingPlacementRules rules = default)
        {
            if (snapshot == null) throw new ArgumentException("FPNavMeshRebaker: snapshot is null");
            if (scratch == null) throw new ArgumentException("FPNavMeshRebaker: scratch is null");
            int count = ResolveLiveCount(existingCount, existing?.Length ?? 0, nameof(existingCount));

            var all = scratch.Buffers.PreviewRects(count + 1);
            for (int i = 0; i < count; i++) all[i] = existing[i];
            all[count] = ghost;

            BuildRectPolygons(snapshot, all, count + 1, scratch.Buffers,
                out long[] polyX, out long[] polyZ, out int[] polyStart, out long[] polyBounds);
            return ValidateGhostOnly(snapshot, polyX, polyZ, polyStart, polyBounds, count,
                count, rules, out rejection);
        }

        /// <summary>
        /// <inheritdoc cref="TryValidateOne" path="/summary/node()[1]"/>, from catalog placements.
        /// <para>Everything in <see cref="TryValidateOne"/>'s remarks applies unchanged, including
        /// the precondition that <paramref name="existing"/> was already accepted.</para>
        /// </summary>
        public static bool TryValidateOnePlacement(
            FPNavMeshRebakeSnapshot snapshot, FPBuildingPlacement[] existing, int existingCount,
            in FPBuildingPlacement ghost, out FPBuildingRejectionInfo rejection,
            FPBuildingPreviewScratch scratch, FPBuildingPlacementRules rules = default)
        {
            if (snapshot == null) throw new ArgumentException("FPNavMeshRebaker: snapshot is null");
            if (scratch == null) throw new ArgumentException("FPNavMeshRebaker: scratch is null");
            int count = ResolveLiveCount(existingCount, existing?.Length ?? 0, nameof(existingCount));

            var all = scratch.Buffers.PreviewPlacements(count + 1);
            for (int i = 0; i < count; i++) all[i] = existing[i];
            all[count] = ghost;

            BuildPlacementPolygons(snapshot, all, count + 1, scratch.Buffers,
                out long[] polyX, out long[] polyZ, out int[] polyStart, out long[] polyBounds);
            return ValidateGhostOnly(snapshot, polyX, polyZ, polyStart, polyBounds, count,
                count, rules, out rejection);
        }

        /// <summary>
        /// Answers "can this go here?" for a ghost against what this context last carved — the
        /// form a placement UI wants, because it is the only one that needs no building list at
        /// all.
        ///
        /// <para><b>The set it checks against is the rebaker's own.</b> The list-taking overloads
        /// above require a set the rebaker already accepted and cannot check that they got one; a
        /// caller whose list has drifted — a demolished building still in it, a placement not yet
        /// ticked, a refused rebake left in place — gets a confident wrong answer. Here the set is
        /// whatever the last successful rebake through <paramref name="context"/> carved, so there
        /// is nothing to keep in step.</para>
        ///
        /// <para><b>Rules come from that rebake too.</b> Leave <paramref name="rules"/> null and
        /// the preview uses what the carving rebake ran under, which is what makes the two agree.
        /// Pass a value to override — useful for "what would this look like with contact allowed",
        /// and the one way left to make a preview disagree with a rebake. Before the first rebake
        /// the recorded value is meaningless and also irrelevant: rules only govern separation
        /// BETWEEN buildings, and there are none.</para>
        ///
        /// <para><b>Same thread as the rebake, and one previewer per context.</b> The snapshot-form
        /// overloads are safe on any thread because each caller brings its own scratch. This one
        /// writes the ghost into storage the context owns, so a second previewer — or a rebake
        /// running alongside — corrupts it. Two threads that both need previews need two contexts.</para>
        ///
        /// <para>A malformed ghost still throws, exactly as a malformed rebake request does.
        /// Green is still optimistic: another player may build there before the command lands.</para>
        /// </summary>
        public static bool TryValidateOne(
            FPNavMeshRebakeContext context, in FPBuildingRect ghost,
            out FPBuildingRejectionInfo rejection, FPBuildingPlacementRules? rules = null)
        {
            if (context == null) throw new ArgumentException("FPNavMeshRebaker: context is null");
            FPBuildingAcceptedSet accepted = context.Accepted;
            int ghostIndex = accepted.Count;

            accepted.EnsureCapacity(accepted.VertCount + 4, ghostIndex + 2);
            int vertCount = accepted.VertCount;
            WriteRectPolygon(context.Snapshot, ghost, ghostIndex, 0,
                accepted.PolyX, accepted.PolyZ, accepted.PolyStart, accepted.GhostBounds, null,
                ref vertCount);
            accepted.PolyStart[ghostIndex + 1] = vertCount;

            return ValidateGhostOnly(context.Snapshot,
                accepted.PolyX, accepted.PolyZ, accepted.PolyStart, accepted.GhostBounds, 0,
                ghostIndex, rules ?? accepted.Rules, out rejection);
        }

        /// <summary>
        /// <inheritdoc cref="TryValidateOne(FPNavMeshRebakeContext, in FPBuildingRect, out FPBuildingRejectionInfo, System.Nullable{FPBuildingPlacementRules})" path="/summary/node()[1]"/>, from a catalog placement.
        ///
        /// <para>Everything in the context-form <see cref="TryValidateOne(FPNavMeshRebakeContext, in FPBuildingRect, out FPBuildingRejectionInfo, System.Nullable{FPBuildingPlacementRules})"/>'s
        /// remarks applies unchanged. The ghost's shape need not match what the context carved —
        /// the accepted set holds expanded polygons, not rects or placements, so a hexagon ghost
        /// over a map of rectangles is a normal call.</para>
        /// </summary>
        public static bool TryValidateOnePlacement(
            FPNavMeshRebakeContext context, in FPBuildingPlacement ghost,
            out FPBuildingRejectionInfo rejection, FPBuildingPlacementRules? rules = null)
        {
            if (context == null) throw new ArgumentException("FPNavMeshRebaker: context is null");
            FPBuildingShapeExpansion expansion = context.Snapshot.ShapeExpansion;
            if (expansion == null)
                throw new ArgumentException(
                    "FPNavMeshRebaker: this stage's snapshot was built without a shape catalog");

            FPBuildingAcceptedSet accepted = context.Accepted;
            int ghostIndex = accepted.Count;

            accepted.EnsureCapacity(
                accepted.VertCount + expansion.Catalog.MaxVertexCount, ghostIndex + 2);
            int vertCount = accepted.VertCount;
            WritePlacementPolygon(expansion, ghost, ghostIndex, 0,
                accepted.PolyX, accepted.PolyZ, accepted.PolyStart, accepted.GhostBounds, null,
                ref vertCount);
            accepted.PolyStart[ghostIndex + 1] = vertCount;

            return ValidateGhostOnly(context.Snapshot,
                accepted.PolyX, accepted.PolyZ, accepted.PolyStart, accepted.GhostBounds, 0,
                ghostIndex, rules ?? accepted.Rules, out rejection);
        }

        /// <summary>
        /// The ghost at index <paramref name="ghostIndex"/> against everything else, skipping the
        /// pairs among the others — they were accepted already, so no pair of them can fail.
        ///
        /// <para>Runs the checks in the same order the whole-list form does (separation first,
        /// then the base-mesh tests) so that when several reasons apply, both forms report the
        /// same one.</para>
        /// </summary>
        private static bool ValidateGhostOnly(
            FPNavMeshRebakeSnapshot snapshot,
            long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds, int boundsSlot,
            int ghostIndex, FPBuildingPlacementRules rules,
            out FPBuildingRejectionInfo rejection)
        {
            rejection = default;

            for (int i = 0; i < ghostIndex; i++)
            {
                if (!ConvexPolygonsSeparated(
                        polyX, polyZ, polyStart[i], polyStart[i + 1],
                        polyStart[ghostIndex], polyStart[ghostIndex + 1], rules.AllowBuildingTouch))
                {
                    rejection = new FPBuildingRejectionInfo(
                        FPBuildingRejection.BuildingsOverlap, i, ghostIndex);
                    return false;
                }
            }

            return ValidateOneAgainstBase(snapshot.BaseXs, snapshot.BaseZs, snapshot.RingEdges,
                polyX, polyZ, polyStart, polyBounds, boundsSlot, ghostIndex, out rejection);
        }

        /// <summary>
        /// One building against the base mesh: does it cross the walkable boundary, swallow a
        /// baked hole, or sit outside the region entirely.
        ///
        /// <para>Split out so the whole-list form and the single-ghost form run the SAME checks in
        /// the SAME order rather than two copies that can drift. The order is load-bearing on its
        /// own — the swallow test is deliberately ahead of the parity test.</para>
        ///
        /// <para><paramref name="boundsSlot"/> is separate from <paramref name="b"/> because the
        /// context-form preview holds the accepted set's bounds for the GHOST only: nothing reads
        /// an already-placed building's AABB, so keeping N of them would be storage per room for a
        /// value no caller asks for. Callers with a full bounds array pass the same index twice.</para>
        /// </summary>
        private static bool ValidateOneAgainstBase(
            long[] xs, long[] zs, List<(int a, int b)> ringEdges,
            long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds, int boundsSlot,
            int b, out FPBuildingRejectionInfo rejection)
        {
            rejection = default;
            int vs = polyStart[b], ve = polyStart[b + 1];
            long minX = polyBounds[boundsSlot * 4], minZ = polyBounds[boundsSlot * 4 + 1];
            long maxX = polyBounds[boundsSlot * 4 + 2], maxZ = polyBounds[boundsSlot * 4 + 3];

            // First base ring vertex found strictly inside this building, if any (see below).
            // -1 = none. Recorded rather than thrown so that the crossing test keeps priority.
            int swallowedVertex = -1;

            foreach (var (ea, eb) in ringEdges)
            {
                long rax = xs[ea], raz = zs[ea], rbx = xs[eb], rbz = zs[eb];

                // Cheap reject: a ring edge whose own AABB does not meet the building's
                // cannot cross it, touch it, or have a vertex inside it. Four integer
                // comparisons against the ~15k ring edges that this loop otherwise tests a
                // segment intersection against per building side — measured, that is 95% of
                // the per-building rebake cost.
                //
                // The comparisons are STRICT on purpose. SegmentsIntersectOrTouch below
                // rejects a ring edge that merely TOUCHES the building, so an edge lying
                // exactly on a side must survive this filter — tightening `<` to `<=` here
                // would skip it and silently turn a rejection into an acceptance.
                //
                // The swallow test further down is skipped along with the rest, and that is
                // safe: a ring vertex strictly inside the AABB gives max(rax,rbx) >= rax >
                // minX and min(rax,rbx) <= rax < maxX, so no skip condition can hold.
                if ((rax > rbx ? rax : rbx) < minX || (rax < rbx ? rax : rbx) > maxX ||
                    (raz > rbz ? raz : rbz) < minZ || (raz < rbz ? raz : rbz) > maxZ)
                    continue;

                for (int e = vs; e < ve; e++)
                {
                    int e2 = e + 1 < ve ? e + 1 : vs;
                    // Touching the boundary stays rejected, and NOT because the CDT minds —
                    // it triangulates the shape happily. The problem is downstream: a hole
                    // ring that shares an edge with the outer ring is not a hole any more.
                    // Even-odd erasure counts the coincident edge once, so entering the notch
                    // costs a single crossing and its interior stays at odd depth = WALKABLE.
                    //
                    // Measured: a 1x1 building flush against the wall is accepted, adds two
                    // vertices, and carves NOTHING — agents walk straight through it. That is
                    // worse than a rejection, because the game believes it placed a building.
                    // Allowing this needs the erase to distinguish coincident constraints, or
                    // the hole ring to be inset off the boundary; both are out of scope here.
                    if (SegmentsIntersectOrTouch(
                            polyX[e], polyZ[e], polyX[e2], polyZ[e2], rax, raz, rbx, rbz))
                    {
                        rejection = new FPBuildingRejectionInfo(
                            FPBuildingRejection.TouchesWalkableBoundary, b);
                        return false;
                    }
                }

                // A building that SWALLOWS a base hole ring (pillar, pond, pit) is accepted by
                // every other check and produces a wrong navmesh: even-odd erasure counts one
                // more ring crossing, so the hole's interior flips back to WALKABLE and agents
                // walk into a place they could never reach before. Measured on the shipped
                // Field asset.
                //
                // Testing one vertex per ring suffices, and STRICT interior is deliberate;
                // both rest on the crossing test above.
                // `ea` visits every ring vertex exactly once: ringEdges holds (i, i+1 mod) per
                // ring, so each vertex appears once as the first element.
                if (swallowedVertex < 0
                    && PointStrictlyInsideConvex(polyX, polyZ, vs, ve, rax, raz))
                    swallowedVertex = ea;
            }

            // Checking each building on its own is not a simplification, it is complete:
            // a ring flips only when an ODD number of buildings contain it, and the pairwise
            // test above forces interiors to be disjoint (touching is allowed, overlap and
            // nesting are not), so at most ONE can contain any ring. Odd therefore means
            // exactly one. Allowing building overlap would break that and require a check
            // over the whole set.
            //
            // Thrown BEFORE the parity test: once the probe moves
            // to an interior point, a swallowing placement probes inside the swallowed hole
            // and parity would report "outside the walkable region" — a false diagnosis. The
            // order is fixed now so that the probe can move later without dragging this with
            // it. With vertex-0 probing the two orders agree on every case except a building
            // containing the whole map, which reports "swallows" instead of "outside".
            if (swallowedVertex >= 0)
            {
                rejection = new FPBuildingRejectionInfo(
                    FPBuildingRejection.SwallowsBakedHole, b, -1,
                    new FPVector2(FPGeoPredicates.Unsnap(xs[swallowedVertex]),
                                  FPGeoPredicates.Unsnap(zs[swallowedVertex])));
                return false;
            }

            // Probing one vertex is enough, and it is never ON a ring edge: the check above
            // rejects any building side that touches a ring, so a vertex sitting on one would
            // already have been refused. That is what keeps AllowBuildingTouch from reaching
            // this test — it relaxes building pairs only, never the boundary.
            if (!PointInRingsParity(polyX[vs], polyZ[vs], xs, zs, ringEdges))
            {
                rejection = new FPBuildingRejectionInfo(
                    FPBuildingRejection.OutsideWalkableRegion, b);
                return false;
            }
            return true;
        }


        /// <summary>
        /// Every way a placement can be refused, decided before a single triangle is carved.
        ///
        /// <para>Shared rather than duplicated, and that is the whole point: the preview entry
        /// points answer by running THIS, so there is no second copy of the rules to drift. Split
        /// out of the rebake, which still calls it first and then carves — a rebake that got past
        /// this method is a rebake that will succeed.</para>
        ///
        /// <para><b>Reads only.</b> The snapshot, the caller's polygon buffers and the rules go
        /// in; a rejection comes out. Nothing is written, nothing is pooled, no intermediate the
        /// carve needs is produced — which is what lets a preview call it off the immutable
        /// snapshot from any thread.</para>
        /// </summary>
        /// <returns>true when every building is placeable; false with <paramref name="rejection"/> set.</returns>
        internal static bool ValidatePolygons(
            FPNavMeshRebakeSnapshot snapshot,
            long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds,
            int buildingCount, FPBuildingPlacementRules rules,
            out FPBuildingRejectionInfo rejection)
        {
            // FIRST failure wins, and that is a contract rather than an accident. With throws it
            // came free — the first one out of the method was the one the caller saw. Reporting by
            // value has to reproduce it by RETURNING at each check instead of recording and
            // continuing, because an order flip changes which reason the player is shown while
            // leaving the accept/reject verdict, the mesh and the fingerprint identical. Nothing
            // downstream would notice. The order the checks run in is itself load-bearing: the
            // swallow test is deliberately ahead of the parity test (see below).
            rejection = default;
            long[] xs = snapshot.BaseXs;
            long[] zs = snapshot.BaseZs;
            List<(int a, int b)> ringEdges = snapshot.RingEdges;

            // Pairwise: interiors must stay disjoint. Separating-axis test over both polygons'
            // edge normals — for two axis-aligned rects it reduces to exactly the four
            // comparisons this used to do, so the accept/reject set is unchanged.
            for (int i = 0; i < buildingCount; i++)
            {
                for (int j = i + 1; j < buildingCount; j++)
                {
                    // With AllowBuildingTouch the separating-axis test admits contact (a gap of
                    // exactly zero) instead of demanding a gap. That admits shared edges, shared
                    // corners and collinear overlap — all of which the CDT triangulates correctly
                    // and carves as one merged obstacle ring (measured) — while still rejecting
                    // partial overlap AND nesting, because both have overlapping interiors.
                    //
                    // Do NOT widen this to allow overlap. Nesting is not a near miss: even-odd
                    // erasure counts one more ring crossing, so the inner shape flips back to
                    // WALKABLE and you get a building with a courtyard inside it. That is
                    // measured, it throws no exception, and it only shows up in gameplay.
                    if (!ConvexPolygonsSeparated(
                            polyX, polyZ, polyStart[i], polyStart[i + 1],
                            polyStart[j], polyStart[j + 1], rules.AllowBuildingTouch))
                    {
                        rejection = new FPBuildingRejectionInfo(
                            FPBuildingRejection.BuildingsOverlap, i, j);
                        return false;
                    }
                }
            }
            // Strictly inside walkable: no edge intersection/touch with any base ring edge,
            // no swallowed ring, and vertex 0 strictly inside by ring-crossing parity.
            for (int b = 0; b < buildingCount; b++)
            {
                if (!ValidateOneAgainstBase(xs, zs, ringEdges,
                        polyX, polyZ, polyStart, polyBounds, b, b, out rejection))
                    return false;
            }


            return true;
        }



        /// <summary>
        /// The rebake proper, taking buildings already expanded into snapped convex polygons
        /// (CCW, CSR-packed: building b owns vertices [polyStart[b], polyStart[b+1])).
        ///
        /// Split out from the rect path so that the geometry can be exercised with shapes an
        /// axis-aligned rect cannot express — the 45-degree diamond that proves this path is not
        /// secretly still axis-aligned. Catalog shapes enter here too; the rect overload above is
        /// then just the axis-aligned case.
        ///
        /// The caller owns the buffers and the polygons must already be valid: convex, CCW,
        /// non-degenerate, on the snap grid, and inside the snapped domain.
        /// </summary>
        internal static FPNavMesh RebakeFromPolygons(
            FPNavMeshRebakeSnapshot snapshot,
            long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds, FP64[] polyYs,
            int buildingCount, int polyVertCount,
            IKLogger logger, FPNavMeshRebakeBufferPool pool, FPBuildingPlacementRules rules,
            out FPBuildingRejectionInfo rejection,
            FPNavMesh previous = null, FPNavMeshBuildPipeline.IncrementalOutcome outcome = null,
            FPBuildingAcceptedSet accepted = null)
        {
            if (!ValidatePolygons(snapshot, polyX, polyZ, polyStart, polyBounds,
                    buildingCount, rules, out rejection))
                return null;

            FPNavMesh baseMesh = snapshot.BaseMesh;
            long[] xs = snapshot.BaseXs;
            long[] zs = snapshot.BaseZs;
            int baseCount = xs.Length;
            var buffers0 = pool ?? FPNavMeshRebakeBufferPool.NonPooling;

            // Append building corners + hole ring constraints (combined index space: base
            // 0..baseCount-1, new hole vertices appended after — matches the CDT resume
            // contract). Corners coinciding with base vertices reuse the base index (dedup —
            // the CDT-level duplicate check is the defense line behind this).
            // Arrays + explicit counts rather than List<T>: a reused
            // List would still allocate at the CDT boundary via ToArray(), so the count travels
            // instead and no conversion happens. Every consumer below must use the count, never
            // Length — these are pool buffers whose tail is the previous rebake's holes.
            var buffers = buffers0;
            // Sized by the actual vertex total, not buildings * 4 — a polygon carries as many
            // vertices as it has, and a rect is just the n = 4 case.
            int holeCapacity = polyVertCount;
            long[] holeXs = buffers.HoleXs(holeCapacity);
            long[] holeZs = buffers.HoleZs(holeCapacity);
            FP64[] holeYs = buffers.HoleYs(holeCapacity);
            int[] holeConstraints = buffers.HoleConstraints(polyVertCount * 2);
            int holeCount = 0;
            int holeConstraintCount = 0;

            // The only hash container left in the rebake step. It is looked up and assigned but
            // NEVER enumerated — enumeration order is what would make a reused hash container a
            // determinism hazard, and that surface does not exist here. Adding a foreach over
            // this map would reopen it (pinned by a test).
            var holeMap = new Dictionary<(long, long), int>(holeCapacity);

            int AddHoleVertex(long hx, long hz, FP64 y)
            {
                if (snapshot.CoordToIndex.TryGetValue((hx, hz), out int baseIdx))
                    return baseIdx;
                if (holeMap.TryGetValue((hx, hz), out int holeIdx))
                    return holeIdx;
                int combined = baseCount + holeCount;
                holeMap[(hx, hz)] = combined;
                holeXs[holeCount] = hx;
                holeZs[holeCount] = hz;
                holeYs[holeCount] = y;
                holeCount++;
                return combined;
            }

            for (int b = 0; b < buildingCount; b++)
            {
                int vs = polyStart[b], ve = polyStart[b + 1];
                FP64 y = polyYs[b];
                int first = AddHoleVertex(polyX[vs], polyZ[vs], y);
                int prev = first;
                for (int v = vs + 1; v < ve; v++)
                {
                    int cur = AddHoleVertex(polyX[v], polyZ[v], y);
                    holeConstraints[holeConstraintCount++] = prev;
                    holeConstraints[holeConstraintCount++] = cur;
                    prev = cur;
                }
                holeConstraints[holeConstraintCount++] = prev;
                holeConstraints[holeConstraintCount++] = first;
            }

            // --- CDT resume (clone + ghost rebase + incremental hole insertion) + pipeline.
            // `tris` is a pool buffer valid over [0, indexCount) and only until the next rebake.
            int[] tris = FPConstrainedDelaunay.TriangulateFromSnapshot(
                snapshot.Cdt, holeXs, holeZs, holeCount, holeConstraints, holeConstraintCount,
                out int indexCount, true, logger, pool);
            if (indexCount == 0)
            {
                rejection = new FPBuildingRejectionInfo(FPBuildingRejection.EmptyWalkableRegion);
                return null;
            }

            int vertexCount = baseCount + holeCount;
            var vertices = buffers.RentOutputVertices(vertexCount);
            for (int i = 0; i < baseCount; i++)
                vertices[i] = new FPVector3(FPGeoPredicates.Unsnap(xs[i]), snapshot.BaseYs[i], FPGeoPredicates.Unsnap(zs[i]));
            for (int k = 0; k < holeCount; k++)
                vertices[baseCount + k] = new FPVector3(FPGeoPredicates.Unsnap(holeXs[k]), holeYs[k], FPGeoPredicates.Unsnap(holeZs[k]));

            // Conforming fast path: the CDT output is
            // on-grid, weld-complete, exactly non-degenerate and T-junction-free by
            // construction — skips the pipeline's snap + T-junction scan (its only
            // input-dependent double/epsilon arithmetic) and their allocations.
            FPNavMesh mesh = FPNavMeshBuildPipeline.BuildFromConformingTriangulation(
                vertices, new ReadOnlySpan<int>(tris, 0, indexCount),
                SharedZeroAreas(indexCount / 3), baseMesh.GridCellSize, logger,
                baseMesh.BakeAgentRadius, baseMesh.BakeMaxSlopeDeg,
                baseMesh.BakeAgentHeight, baseMesh.BakeAgentClimb, pool, vertexCount,
                previous, outcome);

            InheritUniformAreaAttributes(baseMesh, mesh, logger);

            // The one place a rebake is known to have produced a mesh, and therefore the only
            // place the preview cache may be told what the current building set is. Deliberately
            // NOT next to ValidatePolygons above: one rejection (EmptyWalkableRegion) is decided
            // during the carve, so "validated" and "carved" are not the same set of placements.
            // The carve only reads the polygons, so they are still what validation saw.
            accepted?.Capture(polyX, polyZ, polyStart, buildingCount, polyVertCount,
                snapshot.ShapeExpansion?.Catalog?.MaxVertexCount ?? 4, rules);

            logger?.KInformation($"[FPNavMeshRebaker] rebaked: {baseMesh.Triangles.Length} → {mesh.Triangles.Length} triangles, " +
                $"{buildingCount} building(s), fingerprint 0x{ComputeFingerprint(mesh):X16}");
            return mesh;
        }

        /// <summary>
        /// Rebakes the base navmesh with the given buildings carved out as holes.
        /// Convenience overload for tests and one-shot use: builds a throwaway snapshot on
        /// every call (full base insertion + clone — the hot path must hold a
        /// <see cref="FPNavMeshRebakeSnapshot"/> from <see cref="CreateSnapshot"/> and call
        /// the snapshot overload instead). Single-path principle: the result is bit-identical
        /// to the snapshot overload. Throws <see cref="ArgumentException"/> on malformed input,
        /// <see cref="NotSupportedException"/> on multi-level or non-grid base meshes,
        /// <see cref="InvalidOperationException"/> on invalid placement.
        /// </summary>
        /// <inheritdoc cref="Rebake(FPNavMeshRebakeContext, FPBuildingRect[], IKLogger, FPBuildingPlacementRules, int)" path="/param[@name='buildingCount']"/>
        public static FPNavMesh Rebake(
            FPNavMesh baseMesh, FPBuildingRect[] buildings, IKLogger logger = null,
            FPBuildingPlacementRules rules = default, int buildingCount = -1)
        {
            return Rebake(CreateSnapshot(baseMesh, logger), buildings, logger, rules, buildingCount);
        }

        /// <summary>
        /// Turns a caller's requested live count into the count the rebake will actually read,
        /// shared by the rect and placement entry points so the two cannot drift.
        ///
        /// <para>-1 means "the array is exactly its content". Any OTHER negative is refused
        /// rather than folded into that sentinel: a caller whose count arithmetic went negative
        /// would otherwise get the whole buffer rebaked, tail included, and a reusable buffer's
        /// tail is the previous rebake's shapes — mutually legal by construction, so every
        /// validation passes and the mesh is simply wrong. That failure is identical on every
        /// peer and therefore invisible to desync checks, which is the class of thing this API
        /// rejects rather than repairs.</para>
        ///
        /// <para>There is deliberately no check that entries past the count are cleared. The
        /// contract is that they are <em>ignored, not cleared</em> — a reusable buffer is
        /// expected to carry stale entries there, and that is the whole point of the parameter.
        /// The one case an omitted count cannot be told from a deliberate full-array one is at
        /// the call site, not here.</para>
        /// </summary>
        private static int ResolveLiveCount(int requested, int arrayLength, string paramName)
        {
            if (requested < -1)
                throw new ArgumentException(
                    $"FPNavMeshRebaker: {paramName} {requested} is negative — only -1 means "
                    + "'the whole array', and folding other negatives into it would silently "
                    + "rebake the buffer's stale tail");
            int count = requested < 0 ? arrayLength : requested;
            if (count > arrayLength)
                throw new ArgumentException(
                    $"FPNavMeshRebaker: {paramName} {count} exceeds the array's {arrayLength}");
            return count;
        }

        /// <summary>
        /// JIT pre-warm for the rebake hot path: runs one
        /// discarded 0-building rebake so the first real rebake does not pay tiered-JIT
        /// tier-0 cost on CoreCLR/Mono. Wired inside <see cref="CreateSnapshot"/> (prewarm
        /// parameter, default on) — cache construction is unconditional on every platform,
        /// only this warming step is a no-op under IL2CPP (AOT — already steady-state).
        /// Best-effort — never throws; the discarded result allocates once at load time.
        /// </summary>
        internal static void Prewarm(FPNavMeshRebakeSnapshot snapshot, IKLogger logger = null)
        {
#if ENABLE_IL2CPP
            // AOT — nothing to warm.
#else
            if (snapshot == null)
                return;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Rebake(snapshot, null);
                sw.Stop();
                logger?.KInformation($"[FPNavMeshRebaker] prewarmed rebake path in {sw.Elapsed.TotalMilliseconds:F1} ms");
            }
            catch (Exception e)
            {
                logger?.KWarning($"[FPNavMeshRebaker] prewarm skipped: {e.Message}");
            }
#endif
        }

        /// <summary>
        /// FNV-1a fingerprint over the full navigation-relevant state (vertices, triangles,
        /// area/cost/isBlocked, bake metadata). Cross-peer check material: the navmesh is
        /// outside the state hash, so peers compare this on FullState resync — mirrors
        /// FPPhysicsWorld.GetStaticFingerprint.
        /// </summary>
        public static ulong ComputeFingerprint(FPNavMesh mesh)
        {
            // FNV-1a, TWO rounds per value: the low 64 bits, then the high 32 again. The extra
            // round is not decoration — most of what goes in below is a small int (a vertex index,
            // an areaMask, a bool), and folding those 64 bits at a time leaves the top half of the
            // word constant across the whole mesh.
            //
            // This is deliberately NOT FPHash.Hash, which folds once. The two produce different
            // values, and THIS value is pinned: Goldens/NavMeshPortalGolden.txt records it per
            // shipped asset against the pre-compaction layout (a baseline that cannot be
            // regenerated), and the FullState resync compares it across peers. Switching the fold
            // to share code would rewrite every one of those numbers for no behavioural gain. The
            // shared thing is the CONSTANTS, which are values rather than an algorithm.
            ulong h = FPHash.FNV_OFFSET;
            void Mix(long v)
            {
                unchecked
                {
                    h = (h ^ (ulong)v) * FPHash.FNV_PRIME;
                    h = (h ^ ((ulong)v >> 32)) * FPHash.FNV_PRIME;
                }
            }

            Mix(mesh.Vertices.Length);
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                Mix(mesh.Vertices[i].x.RawValue);
                Mix(mesh.Vertices[i].y.RawValue);
                Mix(mesh.Vertices[i].z.RawValue);
            }
            Mix(mesh.Triangles.Length);
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                ref readonly FPNavMeshTriangle t = ref mesh.Triangles[i];
                Mix(t.v0); Mix(t.v1); Mix(t.v2);
                Mix(t.areaMask);
                Mix(t.costMultiplier.RawValue);
                Mix(t.isBlocked ? 1 : 0);
            }
            Mix(mesh.BakeAgentRadius.RawValue);
            Mix(mesh.BakeMaxSlopeDeg.RawValue);
            Mix(mesh.BakeAgentHeight.RawValue);
            Mix(mesh.BakeAgentClimb.RawValue);
            return h;
        }

        #region Helpers

        private static long SnapFloor(long raw)
        {
            return raw >> FPGeoPredicates.SNAP_SHIFT;
        }

        private static long SnapCeil(long raw)
        {
            return -((-raw) >> FPGeoPredicates.SNAP_SHIFT);
        }

        private static int RingIndex(FPVector2 pos, Dictionary<(long, long), int> coordToIndex)
        {
            long sx = FPGeoPredicates.Snap(pos.x);
            long sz = FPGeoPredicates.Snap(pos.y);
            if (!coordToIndex.TryGetValue((sx, sz), out int idx))
                throw new InvalidOperationException(
                    "FPNavMeshRebaker: boundary ring vertex not found among base vertices (corrupt base mesh?)");
            return idx;
        }

        /// <summary>
        /// Separating-axis test for two convex polygons given as CCW vertex ranges of a shared
        /// CSR buffer. Returns true when the interiors do not meet.
        ///
        /// The projections use the raw edge normal — the edge delta rotated, NEVER normalised.
        /// Dividing would introduce rounding and the whole point of this test is that the
        /// touching/overlapping boundary is exact. With <paramref name="allowTouch"/> the axis
        /// separates on a gap of exactly zero as well, which is what admits shared edges and
        /// shared corners while still refusing overlapping interiors.
        ///
        /// For two axis-aligned rectangles the four normals are the coordinate axes and this
        /// reduces exactly to the four min/max comparisons it replaced.
        ///
        /// Overflow: |coordinate| &lt;= MAX_SNAPPED_COORD (about 4.7e7) and a normal component is
        /// bounded by the shape's extent in snap units, so a projection stays far below 2^63.
        /// </summary>
        private static bool ConvexPolygonsSeparated(
            long[] px, long[] pz, int aStart, int aEnd, int bStart, int bEnd, bool allowTouch)
        {
            return HasSeparatingAxis(px, pz, aStart, aEnd, bStart, bEnd, allowTouch)
                || HasSeparatingAxis(px, pz, bStart, bEnd, aStart, aEnd, allowTouch);
        }

        /// <summary>Tests the edge normals of the first polygon only; the caller does both sides.</summary>
        private static bool HasSeparatingAxis(
            long[] px, long[] pz, int aStart, int aEnd, int bStart, int bEnd, bool allowTouch)
        {
            for (int e = aStart; e < aEnd; e++)
            {
                int e2 = e + 1 < aEnd ? e + 1 : aStart;
                // Outward normal of a CCW edge (dx, dz) is (dz, -dx).
                long nx = pz[e2] - pz[e];
                long nz = -(px[e2] - px[e]);

                long aMin = long.MaxValue, aMax = long.MinValue;
                for (int v = aStart; v < aEnd; v++)
                {
                    long d = nx * px[v] + nz * pz[v];
                    if (d < aMin) aMin = d;
                    if (d > aMax) aMax = d;
                }
                long bMin = long.MaxValue, bMax = long.MinValue;
                for (int v = bStart; v < bEnd; v++)
                {
                    long d = nx * px[v] + nz * pz[v];
                    if (d < bMin) bMin = d;
                    if (d > bMax) bMax = d;
                }

                if (allowTouch ? (aMax <= bMin || bMax <= aMin) : (aMax < bMin || bMax < aMin))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when the point is strictly inside the CCW convex polygon — on an edge does not
        /// count. Strict is deliberate: a ring vertex lying on a building side has already been
        /// refused by the boundary test, and once boundary contact is ever allowed a ring merely
        /// touching from outside must NOT read as swallowed.
        /// </summary>
        private static bool PointStrictlyInsideConvex(
            long[] px, long[] pz, int start, int end, long qx, long qz)
        {
            for (int e = start; e < end; e++)
            {
                int e2 = e + 1 < end ? e + 1 : start;
                if (FPGeoPredicates.Orient2D(px[e], pz[e], px[e2], pz[e2], qx, qz) <= 0)
                    return false;
            }
            return true;
        }

        /// <summary>Exact segment intersection including touching and collinear overlap.</summary>
        private static bool SegmentsIntersectOrTouch(
            long ax, long az, long bx, long bz,
            long cx, long cz, long dx, long dz)
        {
            int o1 = FPGeoPredicates.Orient2D(ax, az, bx, bz, cx, cz);
            int o2 = FPGeoPredicates.Orient2D(ax, az, bx, bz, dx, dz);
            int o3 = FPGeoPredicates.Orient2D(cx, cz, dx, dz, ax, az);
            int o4 = FPGeoPredicates.Orient2D(cx, cz, dx, dz, bx, bz);

            if (o1 != o2 && o3 != o4 && o1 != 0 && o2 != 0 && o3 != 0 && o4 != 0)
                return true; // proper crossing
            if (o1 == 0 && OnSegmentBounds(ax, az, bx, bz, cx, cz)) return true;
            if (o2 == 0 && OnSegmentBounds(ax, az, bx, bz, dx, dz)) return true;
            if (o3 == 0 && OnSegmentBounds(cx, cz, dx, dz, ax, az)) return true;
            if (o4 == 0 && OnSegmentBounds(cx, cz, dx, dz, bx, bz)) return true;
            return false;
        }

        private static bool OnSegmentBounds(long ax, long az, long bx, long bz, long px, long pz)
        {
            long minX = ax < bx ? ax : bx, maxX = ax < bx ? bx : ax;
            long minZ = az < bz ? az : bz, maxZ = az < bz ? bz : az;
            return px >= minX && px <= maxX && pz >= minZ && pz <= maxZ;
        }

        /// <summary>
        /// Crossing-parity point-in-walkable over all boundary rings (outer + holes combined:
        /// odd = walkable). Caller guarantees the point is not on any ring edge.
        /// </summary>
        private static bool PointInRingsParity(
            long px, long pz, long[] xs, long[] zs, List<(int a, int b)> ringEdges)
        {
            bool inside = false;
            foreach (var (ia, ib) in ringEdges)
            {
                long ax = xs[ia], az = zs[ia];
                long bx = xs[ib], bz = zs[ib];
                if ((az > pz) != (bz > pz))
                {
                    // px < x-intersection of the edge with the horizontal through pz (exact).
                    long lhs = (px - ax) * (bz - az);
                    long rhs = (bx - ax) * (pz - az);
                    if (bz - az > 0 ? lhs < rhs : lhs > rhs)
                        inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>
        /// The <c>areas</c> argument for a rebake, which is always all-zero.
        /// <see cref="FPNavMeshBuildPipeline.BuildCore"/> only turns it into
        /// <c>areaMask = 1 &lt;&lt; areas[i]</c>, and the rebake path overwrites every areaMask
        /// right after via <see cref="InheritUniformAreaAttributes"/>; when the base is
        /// non-uniform that inherit early-returns, and then <c>1 &lt;&lt; 0 == 1</c> is the value
        /// that stays — the same value a dedicated zero array would have produced. So the content
        /// is irrelevant and one process-wide array serves every rebake, allocating nothing.
        ///
        /// Deliberately NOT a pool slot: the pool is room-local and would pay this per room, and
        /// a zero array has no per-room state to keep. Deliberately not "allow null" either —
        /// that would make the same parameter nullable on the fast path and not on the full path
        /// (SplitTJunctions reads areas[t]), which is a trap a person walks into.
        ///
        /// Thread safety without a lock: the array is read-only to callers and every element is
        /// 0, so a racing grow is harmless — a caller that already took the older array took one
        /// that is large enough for ITS request, which is the only guarantee anyone needs. The
        /// local-then-publish order below is what makes that true; do not "simplify" it into a
        /// field read after the assignment.
        /// </summary>
        internal static int[] SharedZeroAreas(int minSize)
        {
            int[] current = _sharedZeroAreas;
            if (current.Length < minSize)
            {
                current = new int[minSize];
                _sharedZeroAreas = current;
            }
            return current;
        }

        private static int[] _sharedZeroAreas = Array.Empty<int>();

        private static void InheritUniformAreaAttributes(FPNavMesh baseMesh, FPNavMesh mesh, IKLogger logger)
        {
            if (baseMesh.Triangles.Length == 0)
                return;
            int areaMask = baseMesh.Triangles[0].areaMask;
            long cost = baseMesh.Triangles[0].costMultiplier.RawValue;
            for (int i = 1; i < baseMesh.Triangles.Length; i++)
            {
                if (baseMesh.Triangles[i].areaMask != areaMask
                    || baseMesh.Triangles[i].costMultiplier.RawValue != cost)
                {
                    // Per-region reassignment needs area region polygons preserved as assets —
                    // not available yet.
                    logger?.KError($"[FPNavMeshRebaker] base mesh has non-uniform areaMask/costMultiplier — " +
                        $"re-triangulation loses per-triangle areas; region-based reassignment is a follow-up. " +
                        $"Rebaked mesh keeps pipeline defaults.");
                    return;
                }
            }
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                mesh.TrianglesMutable[i].areaMask = areaMask;
                mesh.TrianglesMutable[i].costMultiplier = FP64.FromRaw(cost);
            }
        }

        #endregion
    }
}
