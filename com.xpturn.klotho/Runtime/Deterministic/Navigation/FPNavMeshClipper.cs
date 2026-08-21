using System;
using System.Collections.Generic;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Clip stage for <see cref="FPBoundaryPlacementPolicy.ClipOverlap"/>: turns overhanging
    /// building footprints into clip rings C = ∂(footprint ∩ walkable), with the exact
    /// (rational) footprint×wall intersection points replaced by nearby lattice points (X′)
    /// and the wall portions welded onto EXISTING base ring vertices — the CDT never sees a
    /// coordinate off the snap grid, and wall runs coincide with base edges exactly (parity 0,
    /// the coincident-constraint contract). Docs/IMP/IMP96/Plan-BoundaryOverlapClip.md is the
    /// design record; section tags below (ⓑ, ⓒ, HB, JA, …) point into it.
    ///
    /// <para>Vertex principle (ⓑ, as amended by X′): a clip ring's vertices are (1) building
    /// corners inside walkable, (2) base ring vertices, and (3) exactly one new lattice point
    /// X′ per transition. No intersection is ever rounded onto the grid — X′ is CHOSEN by an
    /// exact predicate ("strictly inside the footprint and on the walkable side"), so there is
    /// no snap-rounding topology hazard.</para>
    ///
    /// <para>Overlapping wall runs MERGE (§0 L2): transitions sharing a wall interval emit one
    /// ring per union-find group — interval co-membership is exactly run sharing, and grouping
    /// is its transitive closure (measured, ⓪-⑶). Every degenerate this walker cannot handle
    /// is a NAMED deterministic rejection, never a silent wrong mesh.</para>
    ///
    /// <para>Dependency: the swallow check must run BEFORE this stage (HG order). The
    /// overshoot rule leans on it — a footprint containing a ring vertex without transitions
    /// is impossible after swallow, which is what lets the interval extremes retreat to the
    /// immediately adjacent vertex without scanning.</para>
    ///
    /// <para>V1 allocation note: this stage allocates transient lists per call (like the Begin
    /// phase's holeMap). The default and Touch policies never enter it, so the existing
    /// zero-allocation gates are unaffected; a pooled rewrite is recorded follow-up work.</para>
    /// </summary>
    internal static class FPNavMeshClipper
    {
        internal struct Transition
        {
            public int Building;
            public int Side;                // polygon vertex index (side = Side → next)
            public int RingEdge;            // index into snapshot.RingEdges
            public long TNumEdge, TDenEdge; // param along ring edge a→b, in (0,1), TDen > 0
            public long TNumSide, TDenSide; // param along building side p→q, in (0,1), TDen > 0
            public bool Exit;               // ∂P (CCW) leaves walkable here
            public long XpX, XpZ;           // chosen X′ lattice point
            public int Partner;             // interval pairing partner (index into list)
            public bool OuterPair;          // partner via overshoot path (extremes) vs gap chord
            public int IntervalPos;         // position within the interval's cyclic walk order —
                                            // the seam-safe "which way is the partner" source
            public bool Visited;
        }

        /// <summary>
        /// The emitted clip rings and the per-building verdicts.
        ///
        /// <para><b>Nothing here is copied out, and that has a lifetime.</b> The ring channels are
        /// the working lists themselves — every consumer reads them by index inside the call that
        /// produced them, and materialising five arrays for that cost 8,520 B per preview at 32
        /// buildings. When the call was given an <see cref="FPClipScratch"/> those lists (and
        /// <see cref="IdentityBuilding"/>) are ITS buffers, so <b>this value is valid only until the
        /// next clip through the same scratch.</b> Hold nothing from it across two calls; copy what
        /// you need. The clipper tests do exactly that, deliberately — see the copy note there.
        /// </para>
        /// </summary>
        internal struct Result
        {
            public List<long> Xs;
            public List<long> Zs;
            public List<FP64> Ys;
            public List<int> Starts;        // CSR: ring r owns [Starts[r], Starts[r+1])
            /// <summary><b>Null when the call passed no <c>polyYs</c>.</b> The Y channel is the
            /// rebake's — it builds hole rings from it — and the preview has no use for it, so it
            /// is not produced there. Reading it unconditionally is the one way to break on a
            /// preview-shaped call.</summary>
            public int RingCount;
            /// <summary>Transition-free buildings: carve the footprint verbatim (identity —
            /// the caller runs the flush-semantics validation on them afterwards).</summary>
            public bool[] IdentityBuilding;
            /// <summary>Per emitted ring: the union-find root building index (for pair-layer
            /// rejection reporting).</summary>
            public List<int> GroupRoot;

            /// <summary>
            /// Per BUILDING: the resolved union-find root, so a caller can ask which emitted rings
            /// belong to a given building's group — <c>GroupRoot[r] == Root[b]</c>. That is the
            /// question the ghost preview localises on, and it cannot be answered from
            /// <see cref="GroupRoot"/> alone: when a ghost merges with an existing building the
            /// smaller index wins the root, so the ghost's own index never appears there.
            /// </summary>
            public int[] Root;

            /// <summary>
            /// The per-building transitions, with senses and X′ already resolved, so an accepted
            /// set can keep them and a later call skip the four per-building scans. Null unless
            /// the caller asked for them — the frame-path caller does not, and filling it would put
            /// two arrays on every ghost move.
            ///
            /// <para><c>TransitionStart</c> is a CSR over the building index, length
            /// <c>buildingCount + 1</c>. Pairing state is NOT meaningful here: it is set after
            /// these are produced and is reset when they are read back.</para>
            /// </summary>
            public Transition[] Transitions;
            public int[] TransitionStart;

            /// <summary>
            /// Transitions resolved for every building EXCEPT the last, always set. The
            /// snapshot-form preview has no accepted set to size its transition list from, so it
            /// carries this forward on its scratch — and it is this rather than the total because
            /// the last building is the ghost, the one part that moves between two previews. Sizing
            /// from the total would count the ghost twice and the two API forms would then reserve
            /// different amounts for the same placement. Costs nothing to produce.
            /// </summary>
            public int ExistingTransitionCount;
        }

        /// <summary>Slots the transition list reserves for the ghost on top of the cached count.
        /// See the sizing note at the allocation — above the 16 floor this value is not sensitive.
        /// </summary>
        private const int GhostTransitionHeadroom = 8;

        /// <summary>k = 1 is the 2×2 cell corners, 4 the 8×8 block. Measured (⓪-⑷): k ≤ 4
        /// succeeds for 99.44–100% of transitions and k = 5 finds nothing more.</summary>
        private const int X_PRIME_MAX_RING = 4;

        /// <param name="cachedTransitions">
        /// Transitions already resolved for buildings <c>[0, cachedCount)</c>, from a previous call
        /// that exported them. Those buildings were ACCEPTED, which is what makes the skip safe:
        /// their transitions, senses and X′ are functions of that building and the base rings
        /// alone (no other building enters them), and their per-vertex incidence and ring-granular
        /// swallow already came back clean. Nothing a later building does can change either.
        /// </param>
        /// <param name="exportTransitions">
        /// Fill <see cref="Result.Transitions"/> so the caller can cache them. Off by default: the
        /// per-frame preview reads a cache and never writes one.
        /// </param>
        /// <param name="scratch">
        /// Reused working buffers, for the per-frame preview. Null — the rebake's case — allocates
        /// them fresh, which is what a once-per-placement call should do. When it is supplied,
        /// <see cref="Result.IdentityBuilding"/> is one of ITS buffers and is therefore only valid
        /// until the next call through the same scratch.
        /// </param>
        internal static bool TryBuildClipRings(
            FPNavMeshRebakeSnapshot snapshot,
            long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds, FP64[] polyYs,
            int buildingCount,
            out FPBuildingRejectionInfo rejection, out Result result,
            Transition[] cachedTransitions = null, int[] cachedStart = null, int cachedCount = 0,
            bool exportTransitions = false, FPClipScratch scratch = null)
        {
            rejection = default;
            result = default;
            long[] xs = snapshot.BaseXs;
            long[] zs = snapshot.BaseZs;
            List<(int a, int b)> ringEdges = snapshot.RingEdges;

            // ① The ring decomposition is the base mesh's, so the snapshot owns it — this used to
            // be a 15,317-entry array allocated and filled on every call. ①′ the per-ring AABBs
            // come from the same place and drive the three prunes below.
            int[] ringOfEdge = snapshot.RingOfEdge;
            int[] ringStartEdge = snapshot.RingStart;
            long[] ringBounds = snapshot.RingBounds;
            int ringTotal = snapshot.RingCount;

            // ── 1) transitions + exact-incidence guard per building ───────────────────────
            if (cachedTransitions == null) cachedCount = 0;
            // The cache knows the existing buildings' transition count exactly, so the list can be
            // sized instead of doubled into place — 16·32·64·128·256 slots were allocated to hold
            // 156, which was 49% of a preview's whole allocation.
            //
            // The snapshot form has no cache, so it passes the EXISTING count the last call
            // resolved (transitionHint) — right from the second frame on, because a cursor moves the
            // ghost and not the accepted set. Both forms therefore feed the same quantity into the
            // same formula and reserve the same amount; sizing the snapshot form from the previous
            // TOTAL instead counted the ghost twice and the two forms diverged by 528 B, which the
            // allocation gate's cross-form equality caught on its first run.
            //
            // A hint proportional to buildingCount would be stateless but wrong in the common
            // direction: buildings that do not clip produce no transitions, and it would reserve
            // 23 KB where 1.5 KB is needed.
            //
            // Math.Max is load-bearing, not defensive: presizing to a SMALL exact count is worse
            // than starting at 16, because the list then doubles several times from that small
            // value. Measured — hinting `cached + K` alone made N = 1 go 5,132 → 5,836 B, and
            // K = 8/4/0 all landed on the same 5,836. Above the floor the constant barely matters
            // (8 and 32 measure identically), so it only has to cover a ghost's own crossings.
            int reserve = System.Math.Max(
                16,
                (cachedStart != null ? cachedStart[cachedCount] : scratch?.TransitionHint ?? 0)
                    + GhostTransitionHeadroom);    // both arms are an EXISTING count
            var transitions = scratch != null
                ? scratch.RentTransitions(reserve)
                : new List<Transition>(reserve);
            var transitionCountOf = scratch != null
                ? scratch.RentTransitionCountOf(buildingCount)
                : new int[buildingCount];
            if (!CollectTransitions(snapshot, polyX, polyZ, polyStart, polyBounds, buildingCount,
                    transitions, transitionCountOf, ringStartEdge, ringBounds, ringTotal,
                    cachedTransitions, cachedStart, cachedCount, out rejection))
                return false;
            // Where the fresh transitions begin. The cached ones are a PREFIX in building order,
            // which is the order a full scan produces them in — so the list is the same list, and
            // the walk (whose start is "first entry in list order") sees the same thing.
            int cachedTotal = transitions.Count - CountFrom(transitionCountOf, cachedCount, buildingCount);

            // ── 1b) swallow, ring-granular (HH + the straddle finding) ─────────────────────
            // The per-vertex swallow the Reject/Touch path runs would reject every straddle
            // placement (wall vertices inside the footprint are NORMAL under clipping — the
            // run passes through them), so here the rule is per RING: a footprint containing
            // EVERY vertex of a boundary ring flips that ring's interior — hole or island
            // alike — and is rejected. Transition-free buildings still get the per-vertex
            // check via the flush-semantics validation the caller runs on them afterwards.
            // Runs BEFORE the walk: the overshoot rule and the probe both lean on it.
            for (int b = cachedCount; b < buildingCount; b++)
            {
                for (int r = 0; r < ringTotal; r++)
                {
                    // ①′ "every vertex of this ring is strictly inside the footprint" needs the
                    // ring's box inside the footprint's box first. Necessary, so skipping on it
                    // cannot hide a swallow.
                    if (ringBounds[r * 4] < polyBounds[b * 4] || ringBounds[r * 4 + 2] > polyBounds[b * 4 + 2]
                        || ringBounds[r * 4 + 1] < polyBounds[b * 4 + 1] || ringBounds[r * 4 + 3] > polyBounds[b * 4 + 3])
                        continue;
                    int lo = ringStartEdge[r], hi = ringStartEdge[r + 1];
                    bool all = true;
                    for (int e = lo; e < hi && all; e++)
                    {
                        int vtx = ringEdges[e].a;
                        all = StrictlyInsideConvex(polyX, polyZ, polyStart[b], polyStart[b + 1],
                            xs[vtx], zs[vtx]);
                    }
                    if (all && hi > lo)
                    {
                        int site = ringEdges[lo].a;
                        rejection = new FPBuildingRejectionInfo(
                            FPBuildingRejection.SwallowsBakedHole, b, -1,
                            new FPVector2(FPGeoPredicates.Unsnap(xs[site]),
                                          FPGeoPredicates.Unsnap(zs[site])));
                        return false;
                    }
                }
            }

            // ── 2) senses: alternation along ∂P anchored at vertex 0's parity ──────────────
            SortTransitionsPerBuilding(transitions, buildingCount, transitionCountOf,
                scratch, out int[] order, out int[] orderStart);
            for (int b = cachedCount; b < buildingCount; b++)
            {
                if (transitionCountOf[b] == 0)
                    continue;
                if (transitionCountOf[b] % 2 != 0)
                {
                    // ∂P is closed — crossings pair up. Odd = a missed degenerate contact.
                    rejection = new FPBuildingRejectionInfo(
                        FPBuildingRejection.ExactBoundaryContact, b);
                    return false;
                }
                bool inside = FPNavMeshRebaker.PointInRingsParityPruned(
                    polyX[polyStart[b]], polyZ[polyStart[b]], xs, zs, ringEdges,
                    ringStartEdge, ringBounds, ringTotal);
                for (int oi = orderStart[b]; oi < orderStart[b + 1]; oi++)
                {
                    int ti = order[oi];
                    var t = transitions[ti];
                    t.Exit = inside;        // the stretch before this crossing is inside ⇒ exit
                    inside = !inside;
                    transitions[ti] = t;
                }
            }

            // ── 3) per-ring intervals → pairing + union-find groups ───────────────────────
            int[] uf = scratch != null ? scratch.RentUf(buildingCount) : new int[buildingCount];
            for (int i = 0; i < buildingCount; i++) uf[i] = i;
            if (!PairTransitions(transitions, xs, zs, ringEdges, ringOfEdge, ringStartEdge,
                    ringTotal, polyX, polyZ, polyStart, polyBounds, buildingCount, uf, scratch,
                    out rejection))
                return false;

            // ── 4) X′ per transition ───────────────────────────────────────────────────────
            for (int i = cachedTotal; i < transitions.Count; i++)
            {
                var t = transitions[i];
                if (!TryFindXPrime(snapshot, polyX, polyZ, polyStart, in t, out t.XpX, out t.XpZ))
                {
                    rejection = new FPBuildingRejectionInfo(
                        FPBuildingRejection.ClipCandidateMissing, t.Building);
                    return false;
                }
                transitions[i] = t;
            }

            // ── 5) one walk per group ──────────────────────────────────────────────────────
            var clipXs = scratch != null ? scratch.RentXs() : new List<long>(64);
            var clipZs = scratch != null ? scratch.RentZs() : new List<long>(64);
            // No polyYs means nobody reads the Y channel — the preview passes null and validation
            // never looks at Result.Ys. Filling a list with zeros and then copying it out cost
            // 11,920 B per preview at 32 buildings (7,808 list + 4,112 for the array).
            var clipYs = polyYs == null ? null
                : scratch != null ? scratch.RentYs() : new List<FP64>(64);
            var starts = scratch != null ? scratch.RentStarts() : new List<int>(8) { 0 };
            var roots = scratch != null ? scratch.RentRoots() : new List<int>(8);
            var identity = scratch != null
                ? scratch.RentIdentity(buildingCount) : new bool[buildingCount];
            var emittedRoot = scratch != null
                ? scratch.RentEmittedRoot(buildingCount) : new bool[buildingCount];

            for (int b = 0; b < buildingCount; b++)
            {
                if (transitionCountOf[b] == 0)
                {
                    identity[b] = true;
                    continue;
                }
                int root = Find(uf, b);
                if (emittedRoot[root])
                    continue;
                emittedRoot[root] = true;
                if (!WalkGroup(transitions, order, orderStart, polyX, polyZ, polyYs, polyStart,
                        xs, zs, ringEdges, ringOfEdge, ringStartEdge, uf, root,
                        clipXs, clipZs, clipYs, out rejection))
                    return false;
                starts.Add(clipXs.Count);
                roots.Add(root);
            }

            // a loop that closed while group transitions remain unvisited = the clip separates
            // into several regions (or a pinched merge). V1 rejects (PC's rule) rather than
            // guessing a second ring.
            for (int i = 0; i < transitions.Count; i++)
            {
                if (!transitions[i].Visited)
                {
                    rejection = new FPBuildingRejectionInfo(
                        FPBuildingRejection.ClipSplitsWalkableRegion, transitions[i].Building);
                    return false;
                }
            }

            // Flatten before handing it out — Find is path-compressing but not every entry has
            // been asked, and the consumer indexes it directly.
            for (int b = 0; b < buildingCount; b++) uf[b] = Find(uf, b);

            result = new Result
            {
                Xs = clipXs, Zs = clipZs, Ys = clipYs,
                Starts = starts, RingCount = starts.Count - 1,
                IdentityBuilding = identity, GroupRoot = roots,
                Root = uf,
                ExistingTransitionCount = buildingCount > 0
                    ? transitions.Count - transitionCountOf[buildingCount - 1]
                    : 0,
            };
            if (exportTransitions)
            {
                // transitionCountOf is in building order and so is the list, so the CSR is the
                // prefix sum — no bucketing pass.
                var start = new int[buildingCount + 1];
                for (int b = 0; b < buildingCount; b++) start[b + 1] = start[b] + transitionCountOf[b];
                result.Transitions = transitions.ToArray();
                result.TransitionStart = start;
            }
            return true;
        }

        // ═════════════════════════ collection ════════════════════════════════════════════

        private static bool CollectTransitions(
            FPNavMeshRebakeSnapshot snapshot,
            long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds, int buildingCount,
            List<Transition> transitions, int[] transitionCountOf,
            int[] ringStartEdge, long[] ringBounds, int ringTotal,
            Transition[] cachedTransitions, int[] cachedStart, int cachedCount,
            out FPBuildingRejectionInfo rejection)
        {
            rejection = default;
            long[] xs = snapshot.BaseXs;
            long[] zs = snapshot.BaseZs;
            List<(int a, int b)> ringEdges = snapshot.RingEdges;

            for (int b = 0; b < cachedCount; b++)
            {
                // Replayed verbatim, with the pairing fields cleared — those are assigned after
                // the export and mean nothing on the way back in.
                for (int i = cachedStart[b]; i < cachedStart[b + 1]; i++)
                {
                    Transition t = cachedTransitions[i];
                    t.Partner = -1;
                    t.OuterPair = false;
                    t.IntervalPos = 0;
                    t.Visited = false;
                    transitions.Add(t);
                    transitionCountOf[b]++;
                }
            }

            for (int b = cachedCount; b < buildingCount; b++)
            {
                int vs = polyStart[b], ve = polyStart[b + 1];
                long minX = polyBounds[b * 4], minZ = polyBounds[b * 4 + 1];
                long maxX = polyBounds[b * 4 + 2], maxZ = polyBounds[b * 4 + 3];
                bool incidence = false;

                for (int r = 0; r < ringTotal; r++)
                {
                // ①′ A ring whose box does not reach this footprint's box holds no edge that can
                // properly cross one of its sides — the per-edge filter below would reject every
                // one of them, one at a time. Hoisting it is a filter, not a rule change.
                if (RingBoxDisjoint(ringBounds, r, minX, minZ, maxX, maxZ))
                    continue;
                for (int e = ringStartEdge[r]; e < ringStartEdge[r + 1]; e++)
                {
                    var (ea, eb) = ringEdges[e];
                    long rax = xs[ea], raz = zs[ea], rbx = xs[eb], rbz = zs[eb];
                    // strict AABB prefilter — same rationale as the validation loop: an edge
                    // exactly on a side must survive (incidence detection reads it).
                    if ((rax > rbx ? rax : rbx) < minX || (rax < rbx ? rax : rbx) > maxX ||
                        (raz > rbz ? raz : rbz) < minZ || (raz < rbz ? raz : rbz) > maxZ)
                        continue;

                    for (int v = vs; v < ve; v++)
                    {
                        int v2 = v + 1 < ve ? v + 1 : vs;
                        long px = polyX[v], pz = polyZ[v], qx = polyX[v2], qz = polyZ[v2];

                        // Exact incidences (corner on wall edge / wall endpoint on side) are
                        // legal only for transition-free placements — flush and tangent, the
                        // Touch-shaped cases, where C = P verbatim. Mixed with proper
                        // crossings the walk turns degenerate (a crossing AT a lattice point
                        // produces no proper crossing and breaks alternation), so the mix is
                        // REJECTED (V1 rule for HF's partial-collinear gap). Nudge a snap.
                        if (FPNavMeshRebaker.PointOnSegment(px, pz, rax, raz, rbx, rbz)
                            || FPNavMeshRebaker.PointOnSegment(rax, raz, px, pz, qx, qz)
                            || FPNavMeshRebaker.PointOnSegment(rbx, rbz, px, pz, qx, qz))
                            incidence = true;

                        if (!FPNavMeshRebaker.SegmentsProperlyCross(px, pz, qx, qz, rax, raz, rbx, rbz))
                            continue;

                        long sdx = qx - px, sdz = qz - pz;
                        long edx = rbx - rax, edz = rbz - raz;
                        long den = sdx * edz - sdz * edx;                     // ≠ 0
                        long tSideNum = (rax - px) * edz - (raz - pz) * edx;
                        long tEdgeNum = (px - rax) * sdz - (pz - raz) * sdx;
                        long tEdgeDen = -den;
                        long tSideDen = den;
                        if (tSideDen < 0) { tSideDen = -tSideDen; tSideNum = -tSideNum; }
                        if (tEdgeDen < 0) { tEdgeDen = -tEdgeDen; tEdgeNum = -tEdgeNum; }

                        transitions.Add(new Transition
                        {
                            Building = b, Side = v, RingEdge = e,
                            TNumSide = tSideNum, TDenSide = tSideDen,
                            TNumEdge = tEdgeNum, TDenEdge = tEdgeDen,
                            Partner = -1,
                        });
                        transitionCountOf[b]++;
                    }
                }
                }

                if (incidence && transitionCountOf[b] > 0)
                {
                    rejection = new FPBuildingRejectionInfo(
                        FPBuildingRejection.ExactBoundaryContact, b);
                    return false;
                }
            }
            return true;
        }

        // ═════════════════════════ ring topology / ordering ══════════════════════════════

        /// <summary>
        /// Transition indices grouped by building and sorted along ∂P, as a CSR pair rather than a
        /// list per building — the same replacement PairTransitions made on the ring axis, which
        /// this was the last copy of. 67 objects (the array, 33 lists, their 33 inner arrays) become
        /// 2.
        ///
        /// <para><b>Every building, cached ones included.</b> The walk reads this for any building in
        /// the ghost's merged group, so covering only the uncached ones is wrong — that was tried and
        /// it dereferenced a null slot. The CSR form makes that particular failure impossible (there
        /// are no slots to be null), and the reason still stands: the per-building sort is not a cost
        /// the transition cache exists to remove — k is one footprint's crossings. What the cache
        /// removes is the ring-parity walk in senses.</para>
        ///
        /// <para><b>No bucketing pass is needed.</b> CollectTransitions appends in ascending
        /// building order, so the list is already grouped and the prefix sums of
        /// <paramref name="transitionCountOf"/> ARE each building's index range —
        /// <c>order[i] = i</c> is the whole fill. (Bucketing would need a second
        /// <c>int[buildingCount]</c> write cursor for nothing.)</para>
        ///
        /// <para>Insertion sort per range, not <c>Array.Sort</c> with a comparison: k is one
        /// footprint's crossings (≤ 8 in practice) and a comparison delegate allocates per call,
        /// which is what this whole change is removing.</para>
        /// </summary>
        private static void SortTransitionsPerBuilding(
            List<Transition> transitions, int buildingCount, int[] transitionCountOf,
            FPClipScratch scratch, out int[] order, out int[] orderStart)
        {
            orderStart = scratch != null
                ? scratch.RentSortOrderStart(buildingCount + 1) : new int[buildingCount + 1];
            orderStart[0] = 0;      // explicit: a rented buffer is not zeroed
            for (int b = 0; b < buildingCount; b++)
                orderStart[b + 1] = orderStart[b] + transitionCountOf[b];

            int total = transitions.Count;
            order = scratch != null ? scratch.RentSortOrder(total) : new int[total];
            for (int i = 0; i < total; i++) order[i] = i;

            for (int b = 0; b < buildingCount; b++)
            {
                int lo = orderStart[b], hi = orderStart[b + 1];
                for (int i = lo + 1; i < hi; i++)
                {
                    int v = order[i], j = i - 1;
                    while (j >= lo && CompareAlongSide(transitions[order[j]], transitions[v]) > 0)
                    {
                        order[j + 1] = order[j];
                        j--;
                    }
                    order[j + 1] = v;
                }
            }
        }

        /// <summary>
        /// "Ring <paramref name="ring"/>'s box does not meet the query box" — the necessary
        /// condition both edge-level scans prune on (transition collection and the post-walk
        /// boundary recheck).
        ///
        /// <para>A function rather than the expression twice, and not for tidiness: an over-eager
        /// version of this test is the one change in this stage that produces a WRONG mesh while
        /// every check still passes, and it was verified that widening it by a single snap unit
        /// left the whole suite green. One callable definition is what let a test pin the
        /// implication it has to satisfy — see the clipper tests' pure-filter case.</para>
        /// </summary>
        internal static bool RingBoxDisjoint(
            long[] ringBounds, int ring, long minX, long minZ, long maxX, long maxZ)
            => ringBounds[ring * 4 + 2] < minX || ringBounds[ring * 4] > maxX
               || ringBounds[ring * 4 + 3] < minZ || ringBounds[ring * 4 + 1] > maxZ;

        private static int CountFrom(int[] counts, int from, int to)
        {
            int n = 0;
            for (int i = from; i < to; i++) n += counts[i];
            return n;
        }

        /// <summary>Order along ∂P: side index, then the exact rational side-param (the
        /// 128-bit comparison — same-side multi-crossings order by it, ⓑ).</summary>
        private static int CompareAlongSide(in Transition x, in Transition y)
        {
            if (x.Side != y.Side) return x.Side.CompareTo(y.Side);
            return FPInt128.Sub(
                FPInt128.Mul64(x.TNumSide, y.TDenSide),
                FPInt128.Mul64(y.TNumSide, x.TDenSide)).Sign();
        }

        /// <summary>Order along a ring: edge index (edges are stored in ring order), then the
        /// exact rational edge-param.</summary>
        private static int CompareAlongRing(in Transition x, in Transition y)
        {
            if (x.RingEdge != y.RingEdge) return x.RingEdge.CompareTo(y.RingEdge);
            return FPInt128.Sub(
                FPInt128.Mul64(x.TNumEdge, y.TDenEdge),
                FPInt128.Mul64(y.TNumEdge, x.TDenEdge)).Sign();
        }

        private static int Find(int[] uf, int i)
        {
            while (uf[i] != i) { uf[i] = uf[uf[i]]; i = uf[i]; }
            return i;
        }

        private static void Union(int[] uf, int a, int b)
        {
            int ra = Find(uf, a), rb = Find(uf, b);
            if (ra != rb) uf[ra < rb ? rb : ra] = ra < rb ? ra : rb;  // smaller root wins
        }

        // ═════════════════════════ intervals + pairing ═══════════════════════════════════

        /// <summary>
        /// Per ring: sort all transitions along the ring; split (cyclically) into intervals
        /// wherever a ring vertex outside EVERY footprint separates neighbours — an interval
        /// is exactly one maximal shared-run stretch (⓪-⑶'s structure). Pair extremes through
        /// the overshoot path, remaining adjacent middles through gap chords; co-members merge.
        /// </summary>
        private static bool PairTransitions(
            List<Transition> transitions,
            long[] xs, long[] zs, List<(int a, int b)> ringEdges, int[] ringOfEdge,
            int[] ringStartEdge, int ringTotal,
            long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds, int buildingCount,
            int[] uf, FPClipScratch scratch, out FPBuildingRejectionInfo rejection)
        {
            rejection = default;
            int total = transitions.Count;

            // ③′ ONE ordering for every ring, instead of a list per ring.
            //
            // The previous shape was `new List<int>[ringTotal]` plus a `List<int>` for each ring,
            // built on every call. On a boundary that decomposes into 1,679 rings — the real
            // 22k-triangle asset does — that is 1,680 allocations to group a handful of
            // transitions, and a placement touches two or three of those rings. Sorting the
            // transitions ONCE by (ring, edge, param) puts each ring's own in a contiguous run, so
            // the grouping is a walk over runs and the containers are gone.
            //
            // The order the walk sees is unchanged: rings ascending (the old loop went
            // 0..ringTotal), and within a ring by (edge, param) — which is exactly what the
            // per-ring Sort produced. Ties remain impossible, so the ordering is unique: two
            // transitions comparing equal would be the same point on the same edge, and that is an
            // exact lattice incidence, which CollectTransitions already refused.
            int[] order = scratch != null ? scratch.RentPairOrder(total) : new int[total];
            for (int i = 0; i < total; i++) order[i] = i;
            for (int i = 1; i < total; i++)
            {
                int v = order[i], j = i - 1;
                // Insertion sort on purpose: `total` is the transition count of one placement (a
                // handful), and Array.Sort with a comparison allocates a delegate and a closure
                // per call — which is the cost this whole change is removing.
                while (j >= 0 && CompareByRingThenAlong(
                           transitions[order[j]], transitions[v], ringOfEdge) > 0)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = v;
            }

            List<int> interval = scratch != null ? scratch.RentInterval() : new List<int>(8);
            bool[] breakBefore = scratch != null
                ? scratch.RentBreakBefore(total) : new bool[total];
            for (int runStart = 0; runStart < total; )
            {
                int ring = ringOfEdge[transitions[order[runStart]].RingEdge];
                int runEnd = runStart + 1;
                while (runEnd < total
                       && ringOfEdge[transitions[order[runEnd]].RingEdge] == ring)
                    runEnd++;
                int n = runEnd - runStart;

                Array.Clear(breakBefore, 0, n);
                int breaks = 0;
                for (int k = 0; k < n; k++)
                {
                    var prev = transitions[order[runStart + (k - 1 + n) % n]];
                    var cur = transitions[order[runStart + k]];
                    if (HasOutsideVertexBetween(in prev, in cur, k == 0,
                            xs, zs, ringEdges, ringOfEdge, ringStartEdge,
                            polyX, polyZ, polyStart, polyBounds, buildingCount))
                    {
                        breakBefore[k] = true;
                        breaks++;
                    }
                }
                if (breaks == 0)
                {
                    // every vertex of the ring sits inside some footprint: a building chain
                    // enclosing the whole boundary ring. No overshoot anchor exists — reject.
                    rejection = new FPBuildingRejectionInfo(
                        FPBuildingRejection.ClipSplitsWalkableRegion,
                        transitions[order[runStart]].Building);
                    return false;
                }

                int firstBreak = Array.IndexOf(breakBefore, true, 0, n);
                interval.Clear();
                for (int step = 0; step < n; step++)
                {
                    int k = (firstBreak + step) % n;
                    if (step > 0 && breakBefore[k])
                    {
                        if (!PairInterval(transitions, interval, uf, out rejection))
                            return false;
                        interval.Clear();
                    }
                    interval.Add(order[runStart + k]);
                }
                if (interval.Count > 0 && !PairInterval(transitions, interval, uf, out rejection))
                    return false;
                interval.Clear();
                runStart = runEnd;
            }
            return true;
        }

        /// <summary>Ring index first, then the along-ring order — the key the run walk needs.</summary>
        private static int CompareByRingThenAlong(in Transition x, in Transition y, int[] ringOfEdge)
        {
            int rx = ringOfEdge[x.RingEdge], ry = ringOfEdge[y.RingEdge];
            if (rx != ry) return rx.CompareTo(ry);
            return CompareAlongRing(in x, in y);
        }

        private static bool PairInterval(
            List<Transition> transitions, List<int> interval, int[] uf,
            out FPBuildingRejectionInfo rejection)
        {
            rejection = default;
            int m = interval.Count;
            // senses alternate within one shared-run stretch; anything else is interleaved
            // geometry this V1 does not walk (the same-edge in-out-in family) — reject loudly.
            if (m % 2 != 0)
            {
                rejection = new FPBuildingRejectionInfo(
                    FPBuildingRejection.ClipRunsInterleave, transitions[interval[0]].Building);
                return false;
            }
            for (int k = 1; k < m; k++)
            {
                if (transitions[interval[k]].Exit == transitions[interval[k - 1]].Exit)
                {
                    rejection = new FPBuildingRejectionInfo(
                        FPBuildingRejection.ClipRunsInterleave, transitions[interval[k]].Building);
                    return false;
                }
            }

            for (int k = 0; k < m; k++)
            {
                var t = transitions[interval[k]];
                t.IntervalPos = k;          // NOT the ring ordinal: an interval may straddle the
                transitions[interval[k]] = t; // ring list's seam, where ordinals wrap
            }
            SetPartners(transitions, interval[0], interval[m - 1], outer: true);
            for (int k = 1; k + 1 < m; k += 2)
                SetPartners(transitions, interval[k], interval[k + 1], outer: false);

            for (int k = 1; k < m; k++)
                Union(uf, transitions[interval[0]].Building, transitions[interval[k]].Building);
            return true;
        }

        private static void SetPartners(List<Transition> transitions, int i, int j, bool outer)
        {
            var ti = transitions[i]; ti.Partner = j; ti.OuterPair = outer; transitions[i] = ti;
            var tj = transitions[j]; tj.Partner = i; tj.OuterPair = outer; transitions[j] = tj;
        }

        /// <summary>Ring vertices strictly between two consecutive-sorted transitions: the
        /// start vertices of every edge after prev's, up to and including cur's edge's start.
        /// True if any of them is outside every footprint.</summary>
        private static bool HasOutsideVertexBetween(
            in Transition prev, in Transition cur, bool wrap,
            long[] xs, long[] zs, List<(int a, int b)> ringEdges, int[] ringOfEdge,
            int[] ringStartEdge, long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds,
            int buildingCount)
        {
            if (prev.RingEdge == cur.RingEdge && !wrap)
                return false;
            int ring = ringOfEdge[prev.RingEdge];
            int lo = ringStartEdge[ring], hi = ringStartEdge[ring + 1];
            int e = prev.RingEdge;
            int guard = hi - lo + 1;
            while (guard-- > 0)
            {
                e = e + 1 < hi ? e + 1 : lo;
                int vertex = ringEdges[e].a;
                if (VertexOutsideAllFootprints(xs[vertex], zs[vertex],
                        polyX, polyZ, polyStart, polyBounds, buildingCount))
                    return true;
                if (e == cur.RingEdge)
                    return false;
            }
            return true;    // defensive — a broken ring walks as "separated"
        }

        private static bool VertexOutsideAllFootprints(long vx, long vz,
            long[] polyX, long[] polyZ, int[] polyStart, long[] polyBounds, int buildingCount)
        {
            for (int b = 0; b < buildingCount; b++)
            {
                if (vx <= polyBounds[b * 4] || vz <= polyBounds[b * 4 + 1]
                    || vx >= polyBounds[b * 4 + 2] || vz >= polyBounds[b * 4 + 3])
                    continue;
                if (StrictlyInsideConvex(polyX, polyZ, polyStart[b], polyStart[b + 1], vx, vz))
                    return false;
            }
            return true;
        }

        private static bool StrictlyInsideConvex(
            long[] polyX, long[] polyZ, int vs, int ve, long px, long pz)
        {
            for (int v = vs; v < ve; v++)
            {
                int v2 = v + 1 < ve ? v + 1 : vs;
                if (FPGeoPredicates.Orient2D(polyX[v], polyZ[v], polyX[v2], polyZ[v2], px, pz) <= 0)
                    return false;
            }
            return true;
        }

        // ═════════════════════════ X′ ════════════════════════════════════════════════════

        /// <summary>
        /// X′ (HB): a lattice point near the exact crossing X, strictly inside the footprint
        /// and strictly on the walkable side of the crossed wall edge, not coinciding with a
        /// base vertex (that would defeat the weld and re-create the exact-T-junction the
        /// conforming contract now asserts against). Qualification is LOCAL — a candidate that
        /// is locally fine but globally inside some other boundary's pocket is caught by the C
        /// boundary recheck downstream: conservative rejection, never a wrong mesh (MC).
        /// Enumeration: Chebyshev rings k = 1..4 around X's cell (k = 1 = the 4 cell corners),
        /// lexicographic within a ring — deterministic; the only division in the rebaker
        /// (DivRem ×2 per transition, MA) locates the cell.
        /// </summary>
        private static bool TryFindXPrime(
            FPNavMeshRebakeSnapshot snapshot, long[] polyX, long[] polyZ, int[] polyStart,
            in Transition t, out long gx, out long gz)
        {
            var (ea, eb) = snapshot.RingEdges[t.RingEdge];
            long ax = snapshot.BaseXs[ea], az = snapshot.BaseZs[ea];
            long bx = snapshot.BaseXs[eb], bz = snapshot.BaseZs[eb];

            long cellX = ax + FloorDiv(t.TNumEdge, bx - ax, t.TDenEdge);
            long cellZ = az + FloorDiv(t.TNumEdge, bz - az, t.TDenEdge);

            // Walkable side of the wall at this crossing, winding-free: the ∂P stretch just
            // before an exit (just after an entry) is inside walkable, and that stretch lies
            // entirely on the crossed side's p-side (q-side) of the wall line — the side
            // crosses the wall exactly once, at X (§2-ⓒ derivation).
            int vs = polyStart[t.Building], ve = polyStart[t.Building + 1];
            int sv2 = t.Side + 1 < ve ? t.Side + 1 : vs;
            long wx = t.Exit ? polyX[t.Side] : polyX[sv2];
            long wz = t.Exit ? polyZ[t.Side] : polyZ[sv2];
            int walkSide = FPGeoPredicates.Orient2D(ax, az, bx, bz, wx, wz);

            for (int k = 1; k <= X_PRIME_MAX_RING; k++)
            {
                for (long dx = -k + 1; dx <= k; dx++)
                {
                    for (long dz = -k + 1; dz <= k; dz++)
                    {
                        if (dx != -k + 1 && dx != k && dz != -k + 1 && dz != k)
                            continue;                       // interior of the ring — visited earlier
                        long cx = cellX + dx, cz = cellZ + dz;
                        if (FPGeoPredicates.Orient2D(ax, az, bx, bz, cx, cz) != walkSide)
                            continue;
                        if (!StrictlyInsideConvex(polyX, polyZ, vs, ve, cx, cz))
                            continue;
                        if (snapshot.CoordToIndex.ContainsKey((cx, cz)))
                            continue;
                        gx = cx; gz = cz;
                        return true;
                    }
                }
            }
            gx = 0; gz = 0;
            return false;
        }

        private static long FloorDiv(long tNum, long delta, long tDen)
        {
            long q = FPInt128.DivRem(FPInt128.Mul64(tNum, delta), tDen, out long r);
            return r < 0 ? q - 1 : q;                      // tDen > 0 by construction
        }

        // ═════════════════════════ the walk ══════════════════════════════════════════════

        /// <summary>
        /// Emit one clip ring for a group: alternate building arcs (X′ → inside footprint
        /// vertices → X′) and wall paths (overshoot span or gap chord), starting from the
        /// group's first entry transition. Deterministic: list order is deterministic, the
        /// start choice is "first entry in list order", and every step follows fixed rules.
        /// </summary>
        private static bool WalkGroup(
            List<Transition> transitions, int[] order, int[] orderStart,
            long[] polyX, long[] polyZ, FP64[] polyYs, int[] polyStart,
            long[] xs, long[] zs, List<(int a, int b)> ringEdges, int[] ringOfEdge,
            int[] ringStartEdge, int[] uf, int root,
            List<long> outXs, List<long> outZs, List<FP64> outYs,
            out FPBuildingRejectionInfo rejection)
        {
            rejection = default;
            int start = -1;
            for (int i = 0; i < transitions.Count; i++)
            {
                if (!transitions[i].Exit && Find(uf, transitions[i].Building) == root)
                {
                    start = i;
                    break;
                }
            }
            if (start < 0)
            {
                rejection = new FPBuildingRejectionInfo(
                    FPBuildingRejection.ClipRunsInterleave, root);
                return false;
            }

            int ringBase = outXs.Count;
            int cur = start;
            int guard = transitions.Count + 1;
            while (guard-- > 0)
            {
                // arc: entry → the same building's next transition along ∂P (must be an exit)
                var entry = transitions[cur];
                MarkVisited(transitions, cur);
                Emit(outXs, outZs, outYs, ringBase, entry.XpX, entry.XpZ, YOf(polyYs, entry.Building));

                int ob = entry.Building, oLo = orderStart[ob], oHi = orderStart[ob + 1];
                int at = Array.IndexOf(order, cur, oLo, oHi - oLo);
                int exitIdx = order[at + 1 < oHi ? at + 1 : oLo];
                var exit = transitions[exitIdx];
                MarkVisited(transitions, exitIdx);
                if (!exit.Exit)
                {
                    rejection = new FPBuildingRejectionInfo(
                        FPBuildingRejection.ClipRunsInterleave, entry.Building);
                    return false;
                }

                // footprint vertices strictly between the two crossings along ∂P (forward);
                // none when both crossings share a side with the entry first.
                int vs = polyStart[entry.Building], ve = polyStart[entry.Building + 1];
                if (entry.Side != exit.Side || CompareAlongSide(entry, exit) > 0)
                {
                    int v = entry.Side;
                    int stepGuard = ve - vs + 1;
                    do
                    {
                        v = v + 1 < ve ? v + 1 : vs;
                        Emit(outXs, outZs, outYs, ringBase, polyX[v], polyZ[v], YOf(polyYs, entry.Building));
                    } while (v != exit.Side && stepGuard-- > 0);
                }
                Emit(outXs, outZs, outYs, ringBase, exit.XpX, exit.XpZ, YOf(polyYs, exit.Building));

                // wall: exit → its interval partner
                int partnerIdx = exit.Partner;
                if (partnerIdx < 0)
                {
                    rejection = new FPBuildingRejectionInfo(
                        FPBuildingRejection.ClipRunsInterleave, exit.Building);
                    return false;
                }
                EmitWallPath(transitions, exitIdx, partnerIdx, xs, zs, ringEdges, ringOfEdge,
                    ringStartEdge, YOf(polyYs, exit.Building), ringBase, outXs, outZs, outYs);

                if (partnerIdx == start)
                    break;
                cur = partnerIdx;
                if (transitions[cur].Exit)
                {
                    rejection = new FPBuildingRejectionInfo(
                        FPBuildingRejection.ClipRunsInterleave, transitions[cur].Building);
                    return false;
                }
            }
            if (guard <= 0)
            {
                rejection = new FPBuildingRejectionInfo(
                    FPBuildingRejection.ClipRunsInterleave, root);
                return false;
            }

            // the closing X′ equals the first emitted vertex only implicitly (the ring is
            // closed by the constraint emitter); collapse an explicit duplicate if the walk
            // produced one, then require a real polygon.
            if (outXs.Count - ringBase >= 2
                && outXs[outXs.Count - 1] == outXs[ringBase]
                && outZs[outZs.Count - 1] == outZs[ringBase])
            {
                outXs.RemoveAt(outXs.Count - 1);
                outZs.RemoveAt(outZs.Count - 1);
                outYs?.RemoveAt(outYs.Count - 1);
            }
            if (outXs.Count - ringBase < 3)
            {
                rejection = new FPBuildingRejectionInfo(
                    FPBuildingRejection.ClipSplitsWalkableRegion, root);
                return false;
            }
            return true;
        }

        /// <summary>The preview path validates geometry without emission data — ys may be null.</summary>
        private static FP64 YOf(FP64[] polyYs, int building)
            => polyYs != null ? polyYs[building] : FP64.Zero;

        private static void MarkVisited(List<Transition> transitions, int i)
        {
            var t = transitions[i]; t.Visited = true; transitions[i] = t;
        }

        /// <summary>
        /// Appends one ring vertex, collapsing a zero-length step against the previous vertex OF
        /// THE SAME RING.
        ///
        /// <para><c>ringBase</c> is what makes it same-ring, and it is load-bearing rather than
        /// decorative: <c>outXs</c> is the shared CSR buffer for EVERY ring, so comparing against
        /// its global last element would silently drop a new ring's first vertex whenever it
        /// coincided with the PREVIOUS ring's last — and that coordinate belongs to the previous
        /// ring's span, so the vertex is not lost but handed to the wrong polygon. The
        /// three-vertex minimum in <see cref="WalkGroup"/> passes anything with three left, and
        /// <see cref="ValidateClipRings"/> only looks for proper crossings, so nothing downstream
        /// is guaranteed to catch it.</para>
        ///
        /// <para>Unreachable today, and that is exactly why the bound is written here instead of
        /// being argued elsewhere: a ring's first vertex is always an X′, and X′ is strictly inside
        /// its own footprint and never a base vertex, while footprint interiors are pairwise
        /// disjoint — so it cannot equal a base ring vertex nor another group's X′. That is three
        /// separate layers away from this method. Measured before the bound was added: over 68,773
        /// clips including 28,828 multi-ring results the global form never once fired across a ring
        /// boundary, so this changes no output — it moves an invariant from "holds by accident" to
        /// "holds locally".</para>
        ///
        /// <para>The collapse itself is defensive rather than hot: instrumented over 103,689 clips
        /// (single-building and two-building multi-ring sweeps alike) it fired ZERO times, so no
        /// fixture pins its behaviour either way. That is a reason to keep the bound tight, not a
        /// reason to delete the check — a zero-length step is exactly what a future walk rule would
        /// produce, and the CDT must not receive one.</para>
        /// </summary>
        private static void Emit(List<long> outXs, List<long> outZs, List<FP64> outYs,
            int ringBase, long x, long z, FP64 y)
        {
            int n = outXs.Count;
            if (n > ringBase && outXs[n - 1] == x && outZs[n - 1] == z)
                return;                                     // collapse zero-length steps
            outXs.Add(x); outZs.Add(z); outYs?.Add(y);
        }

        // ═════════════════════════ C validation ══════════════════════════════════════════

        /// <summary>
        /// Post-walk checks on the emitted rings (HG order ③④). All conservative — anything
        /// suspicious is a rejection, never a silent wrong mesh.
        /// (③) boundary recheck: no C segment may PROPERLY cross a base ring edge. Wall runs
        ///     coincide with base edges (collinear ⇒ not a proper crossing) and X′ chords touch
        ///     rings only at welded endpoints, so a hit means the MC band premise failed or an
        ///     X′ chord escaped — the designed backstop for the "locally qualified" X′ rule.
        /// (③) self-simplicity: no two non-adjacent segments of one ring properly cross.
        /// (④) pair layer: no two segments of DIFFERENT rings properly cross — run sharing
        ///     merges upstream, so a surviving crossing is a real arc collision. Containment of
        ///     one ring by another cannot occur (footprint interiors are SAT-disjoint and the
        ///     band is snaps-thin), so crossing is the whole check.
        /// </summary>
        /// <param name="ghostIndex">
        /// Localise to one building's group, and report refusals against it (D-4/D-5). Pass -1 —
        /// the rebake's case — to check every emitted ring against every other.
        ///
        /// <para><b>Why localising is sound.</b> A group that does not contain the ghost is made of
        /// the same buildings it was made of when those buildings were accepted, and a group's walk
        /// reads only its own buildings and the base rings — so its rings are the same rings, and
        /// (③) already passed on them. The pair layer between two such groups likewise. What the
        /// ghost changes is its OWN group (which may have swallowed neighbours' runs), so that is
        /// what is rechecked — against every other ring, not just the higher-indexed ones, because
        /// restricting <c>r</c> to the ghost's rings would otherwise drop its pairs with every ring
        /// numbered below it.</para>
        ///
        /// <para>And a transition-free ghost cannot have merged with anything: merging needs a
        /// shared run, a shared run needs transitions. So there is nothing to recheck at all, which
        /// falls out of this rather than being a separate case — no ring carries its root.</para>
        ///
        /// <para><b>Why refusals are reported against the ghost.</b> Union keeps the smaller index
        /// as root and the ghost is always the highest index, so a merged group's root is ALWAYS an
        /// existing building. Reporting the raw root would name an innocent neighbour for a refusal
        /// the ghost caused — for the first ring of a pair, the caller wants the ghost. The second
        /// index is left alone: that one is a genuine neighbour.</para>
        /// </param>
        internal static bool ValidateClipRings(
            FPNavMeshRebakeSnapshot snapshot, in Result result,
            out FPBuildingRejectionInfo rejection, int ghostIndex = -1)
        {
            rejection = default;
            int ghostRoot = ghostIndex >= 0 ? result.Root[ghostIndex] : -1;
            int reportAs = ghostIndex >= 0 ? ghostIndex : -1;
            long[] xs = snapshot.BaseXs;
            long[] zs = snapshot.BaseZs;
            List<(int a, int b)> ringEdges = snapshot.RingEdges;
            int[] ringStart = snapshot.RingStart;
            long[] ringBounds = snapshot.RingBounds;
            int ringTotal = snapshot.RingCount;

            for (int r = 0; r < result.RingCount; r++)
            {
                if (ghostRoot >= 0 && result.GroupRoot[r] != ghostRoot)
                    continue;
                int owner = reportAs >= 0 ? reportAs : result.GroupRoot[r];
                int lo = result.Starts[r], hi = result.Starts[r + 1];
                for (int i = lo; i < hi; i++)
                {
                    int i2 = i + 1 < hi ? i + 1 : lo;
                    long ax = result.Xs[i], az = result.Zs[i];
                    long bx = result.Xs[i2], bz = result.Zs[i2];
                    long minX = ax < bx ? ax : bx, maxX = ax < bx ? bx : ax;
                    long minZ = az < bz ? az : bz, maxZ = az < bz ? bz : az;

                    // (③) against every base ring edge (AABB prefilter, strict — collinear
                    // coincidence must survive to be tested and correctly report "no").
                    for (int rr = 0; rr < ringTotal; rr++)
                    {
                        // Ring level first, then edge level — the same necessary condition the
                        // clip stage prunes on, and the same reason it cannot hide a crossing: a
                        // segment that misses a ring's box misses every edge in it. This loop was
                        // the flat "every base ring edge" scan, and on a stage whose boundary is
                        // one outer ring plus 1,678 small holes that made the POST-walk validation
                        // the dominant cost of a preview — 5.87 of 6.04 ms with one building down,
                        // against 0.17 ms for the walk itself. Pruning it here costs 0.94 ms.
                        if (RingBoxDisjoint(ringBounds, rr, minX, minZ, maxX, maxZ))
                            continue;
                        for (int e = ringStart[rr]; e < ringStart[rr + 1]; e++)
                        {
                            var (ea, eb) = ringEdges[e];
                            long rax = xs[ea], raz = zs[ea], rbx = xs[eb], rbz = zs[eb];
                            if ((rax > rbx ? rax : rbx) < minX || (rax < rbx ? rax : rbx) > maxX ||
                                (raz > rbz ? raz : rbz) < minZ || (raz < rbz ? raz : rbz) > maxZ)
                                continue;
                            if (FPNavMeshRebaker.SegmentsProperlyCross(ax, az, bx, bz, rax, raz, rbx, rbz))
                            {
                                rejection = new FPBuildingRejectionInfo(
                                    FPBuildingRejection.TouchesWalkableBoundary, owner);
                                return false;
                            }
                        }
                    }

                    // (③) self-simplicity: later non-adjacent segments of the same ring.
                    for (int j = i + 2; j < hi; j++)
                    {
                        if (i == lo && j == hi - 1)
                            continue;                       // ring-closing neighbour
                        int j2 = j + 1 < hi ? j + 1 : lo;
                        if (FPNavMeshRebaker.SegmentsProperlyCross(ax, az, bx, bz,
                                result.Xs[j], result.Zs[j], result.Xs[j2], result.Zs[j2]))
                        {
                            rejection = new FPBuildingRejectionInfo(
                                FPBuildingRejection.ClipSplitsWalkableRegion, owner);
                            return false;
                        }
                    }

                    // (④) other rings' segments. Localised, this must reach BELOW r as well —
                    // the r2 = r+1 form is only complete because the unlocalised loop eventually
                    // visits every ring as r.
                    for (int r2 = ghostRoot >= 0 ? 0 : r + 1; r2 < result.RingCount; r2++)
                    {
                        if (ghostRoot >= 0
                            && (r2 == r || (result.GroupRoot[r2] == ghostRoot && r2 < r)))
                            continue;       // self, and ghost-ring pairs already taken the other way
                        int lo2 = result.Starts[r2], hi2 = result.Starts[r2 + 1];
                        for (int j = lo2; j < hi2; j++)
                        {
                            int j2 = j + 1 < hi2 ? j + 1 : lo2;
                            if (FPNavMeshRebaker.SegmentsProperlyCross(ax, az, bx, bz,
                                    result.Xs[j], result.Zs[j], result.Xs[j2], result.Zs[j2]))
                            {
                                rejection = new FPBuildingRejectionInfo(
                                    FPBuildingRejection.BuildingsOverlap,
                                    owner, result.GroupRoot[r2]);
                                return false;
                            }
                        }
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Wall vertices between an exit and its partner, all EXISTING ring vertices (welded at
        /// emission — the run coincides with base edges exactly). Outer pair: overshoot one
        /// vertex outward of the exit (extremity + the pre-clip swallow check guarantee the
        /// immediate neighbour is outside every footprint), then the whole covered span through
        /// to the overshoot beyond the partner. Gap pair: the plain vertices between the two
        /// crossings (possibly none).
        /// </summary>
        private static void EmitWallPath(
            List<Transition> transitions, int exitIdx, int partnerIdx,
            long[] xs, long[] zs, List<(int a, int b)> ringEdges, int[] ringOfEdge,
            int[] ringStartEdge, FP64 y, int ringBase,
            List<long> outXs, List<long> outZs, List<FP64> outYs)
        {
            var exit = transitions[exitIdx];
            var partner = transitions[partnerIdx];
            int ring = ringOfEdge[exit.RingEdge];
            int lo = ringStartEdge[ring], hi = ringStartEdge[ring + 1];
            // seam-safe direction: interval positions are assigned along the cyclic walk,
            // so "partner ahead" survives an interval that straddles the ring list's seam
            // (where raw edge ordinals wrap and a plain comparison would flip).
            bool forward = exit.IntervalPos < partner.IntervalPos;

            int e = exit.RingEdge;
            if (exit.OuterPair)
            {
                // outward overshoot vertex (single step — see summary):
                int vOut = forward ? ringEdges[e].a : ringEdges[e].b;
                Emit(outXs, outZs, outYs, ringBase, xs[vOut], zs[vOut], y);
                // covered span from that vertex through to the overshoot beyond the partner:
                int stopVertex = forward ? ringEdges[partner.RingEdge].b : ringEdges[partner.RingEdge].a;
                int guard = 2 * (hi - lo) + 2;
                while (guard-- > 0)
                {
                    int v;
                    if (forward) { v = ringEdges[e].b; e = e + 1 < hi ? e + 1 : lo; }
                    else { v = ringEdges[e].a; e = e - 1 >= lo ? e - 1 : hi - 1; }
                    Emit(outXs, outZs, outYs, ringBase, xs[v], zs[v], y);
                    if (v == stopVertex)
                        break;
                }
            }
            else
            {
                // gap chord: vertices strictly between the crossings.
                int target = partner.RingEdge;
                if (e == target)
                    return;
                int guard = hi - lo + 1;
                while (guard-- > 0)
                {
                    int v;
                    if (forward) { v = ringEdges[e].b; e = e + 1 < hi ? e + 1 : lo; }
                    else { v = ringEdges[e].a; e = e - 1 >= lo ? e - 1 : hi - 1; }
                    Emit(outXs, outZs, outYs, ringBase, xs[v], zs[v], y);
                    if (e == target)
                        return;
                }
            }
        }
    }
}
