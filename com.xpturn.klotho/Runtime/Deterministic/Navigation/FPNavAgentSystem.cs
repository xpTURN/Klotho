using System;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Per-tick agent update system.
    /// Handles path requests, steering, movement, and NavMesh constraints.
    /// </summary>
    public class FPNavAgentSystem : INavFingerprintSource
    {
        // Non-readonly: SwapNavMesh rebinds these to a rebaked mesh at runtime.
        private FPNavMesh _navMesh;
        private FPNavMeshQuery _query;
        private FPNavMeshPathfinder _pathfinder;
        private FPNavMeshFunnel _funnel;
        private readonly IKLogger _logger;

        private FPNavAvoidance _avoidance;

        /// <summary>
        /// The navmesh this system is currently running on — the base mesh until the first
        /// <see cref="SwapNavMesh"/>, a rebaked mesh afterwards. This is the single source of
        /// truth for "current": the swap rebinds the fields these read, so an
        /// <c>INavMeshProvider</c> that delegates here cannot go stale, including when
        /// <see cref="SwapNavMesh"/> is called directly rather than through a game system.
        /// Diagnostics only — the simulation reads the fields.
        /// </summary>
        public FPNavMesh CurrentMesh => _navMesh;

        /// <summary>Query bound to <see cref="CurrentMesh"/>. See that property for the contract.</summary>
        public FPNavMeshQuery CurrentQuery => _query;

        private const int VISITED_BUFFER_SIZE = 48;
        private readonly int[] _visitedBuffer = new int[VISITED_BUFFER_SIZE];
        private readonly int[] _corridorBuffer = new int[NavAgentComponent.MAX_CORRIDOR];

        // --- Graph-local obstacle query (BFS here, reusing the navmesh topology directly) ---
        // Forward triangle->segment CSR built alongside LoadObstacles (segment index aligns with
        // FPNavAvoidance._obstacles). null until LoadNavMeshObstacles runs on a navmesh.
        private int[] _triSegStart;   // CSR offsets, valid over [0, triCount] — may be oversized
        private int[] _triSegList;    // segment indices grouped by owner triangle — may be oversized
        private FPNavMeshObstacleExtractor.ExtractScratch _extractScratch;
        // Generation of the avoidance obstacle load captured when the CSR was built, used as a
        // desync guard: if the avoidance is re-loaded out from under us, the CSR is stale -> fall back.
        private int _obstacleLoadGenerationCache = -1;

        // BFS buffers (GC-0, generation-stamped — same pattern as FPNavMeshQuery.MoveAlongSurface).
        // Frontier is a monotone-tail queue, so the cap equals the visited bound. Sized for horizon
        // coverage (NOT copied from MOVE_MAX_QUEUE, which is a single-tick move size).
        private const int BFS_FRONTIER_CAP = 256;
        private int[] _bfsStamp;      // length triCount, generation stamp
        private int _bfsGeneration;
        private readonly int[] _bfsFrontier = new int[BFS_FRONTIER_CAP];
        // ×3 = (max 3 boundary edges per triangle) × the frontier/visited cap: candCount is bounded by
        // 3·BFS_FRONTIER_CAP == this length, so candidate collection can never truncate and needs no
        // overflow counter (unlike the frontier). Keep this coupling — shrinking it turns the
        // `candCount < _candidateSegs.Length` guard below into a silent, undiagnosed coverage cliff.
        private readonly int[] _candidateSegs = new int[BFS_FRONTIER_CAP * 3];
        private int _bfsFrontierOverflowCount;

        /// <summary>Diagnostic: times the BFS frontier cap truncated a query (coverage cliff).</summary>
        public int DebugBfsFrontierOverflowCount => _bfsFrontierOverflowCount;

        private int _collisionResolveTruncatedCount;

        /// <summary>
        /// Diagnostic: agents dropped from the position-correction pass because the array was
        /// longer than <see cref="MAX_AGENTS"/>, accumulated over the lifetime of this instance
        /// (never reset — same convention as <see cref="DebugBfsFrontierOverflowCount"/>, so a
        /// tick split across several <see cref="Update"/> calls sums instead of overwriting).
        /// <para>
        /// Reading it: a rollback resimulation counts the same tick again, so the total runs ahead
        /// of the number of distinct ticks that truncated. And a non-zero count only means agents
        /// were skipped — whether that is observable depends on the consumer model documented on
        /// <see cref="Update"/>.
        /// </para>
        /// </summary>
        public int DebugCollisionResolveTruncatedCount => _collisionResolveTruncatedCount;

        /// <summary>
        /// Upper bound for the position-correction pass buffers. entityCount beyond this is still
        /// moved by ProcessMovement but not collision-resolved (guarded, no overflow). Callers should
        /// keep nav agent count within this bound.
        /// </summary>
        public const int MAX_AGENTS = 64;

        // Position-correction pass (DetourCrowd-style). Zero-GC pre-allocated buffers.
        private const int COLLISION_RESOLVE_ITERATIONS = 4;
        private static readonly FP64 COLLISION_RESOLVE_FACTOR = FP64.FromDouble(0.7);
        private static readonly FP64 COINCIDENT_PEN = FP64.FromDouble(0.01);
        private static readonly FP64 POS_EPSILON = FP64.FromRaw(100);
        private readonly FPVector2[] _disp = new FPVector2[MAX_AGENTS];
        private readonly int[] _dispWeight = new int[MAX_AGENTS];

        /// <summary>
        /// Distance threshold for waypoint arrival detection.
        /// </summary>
        /// <remarks>
        /// Group-convergence guidance: when N agents share one destination and the
        /// position-correction pass is active, they settle into a non-overlapping cluster whose
        /// outermost members sit up to ~radius/(2·sin(π/N)) from the shared point (single-ring
        /// bound; 2D packing is tighter). The pass converges regardless of this value, but to let
        /// every converging agent register arrival inside the ball, set WaypointThreshold at least
        /// that large for big same-destination groups (or give agents slightly offset destinations).
        /// </remarks>
        public FP64 WaypointThreshold;

        /// <summary>
        /// Y difference threshold between floors. Triangles differing more than this are considered different floors.
        /// </summary>
        public FP64 MultiFloorYThreshold = FP64.FromDouble(2.0);

        /// <summary>
        /// Graph-local obstacle BFS climb cap: an agent's query never expands to a triangle whose
        /// centerY differs from the seed triangle's by more than this. On meshes that record a
        /// bake slope (FPNavMesh.BakeMaxSlopeDeg &gt; 0) the query auto-derives the sound bound
        /// obstRange*sin(bakeMaxSlope) per agent and combines it with this value via min(), so the
        /// default ∞ already gets the tightest safe cap; set this only to tighten FURTHER on a
        /// specific stage. Never set it below the auto bound's formula — a smaller cap drops
        /// genuinely reachable walls (clip hazard). Meshes without a recorded slope (0 = unknown,
        /// e.g. synthetic fixtures) get no auto bound. Only used by the graph path.
        /// </summary>
        public FP64 MaxClimbWithinHorizon = FP64.MaxValue;

        /// <summary>
        /// Consecutive off-corridor tick threshold. Triggers repath when exceeded continuously.
        /// </summary>
        public int OffCorridorRepathThreshold = 10;

        /// <summary>
        /// The areaMask this system passes to a path or a walk when the agent names none: every
        /// baked area, but not <see cref="FPNavMeshAreas.BUILDING_AREA"/> — such an agent neither
        /// plans through nor walks into a retained building footprint. See
        /// <see cref="FPNavMeshAreas"/>.
        /// </summary>
        /// <remarks>
        /// This is the value a ZERO override resolves to, not a value forced on everyone: an agent
        /// can name its own plan and walk masks through
        /// <see cref="NavAgentComponent.SetAreaMask"/>, and asking for different ones on the two
        /// sides is the point (see <see cref="ResolvePlanMask"/>). Leaving both at zero — which is
        /// what every agent carries until something assigns them — keeps this constant in force,
        /// so per-agent masks changed no existing behaviour.
        /// </remarks>
        public const int DEFAULT_AREA_MASK = FPNavMeshAreas.DEFAULT_AGENT_MASK;

        /// <summary>
        /// The mask for PLANNING this agent's path: what it may route through.
        ///
        /// <para>Separate from <see cref="ResolveWalkMask"/> because a game may want an agent to
        /// plan as if a building were not there and then discover it by walking into it. That is
        /// the asymmetry per-agent masks exist for: plan permissively, walk restrictively, and the
        /// unit learns by contact instead of by knowing.</para>
        /// </summary>
        private static int ResolvePlanMask(in NavAgentComponent nav)
            => nav.PlanAreaMaskOverride != 0 ? nav.PlanAreaMaskOverride : DEFAULT_AREA_MASK;

        /// <summary>
        /// The mask for WALKING: what this agent may actually enter. A triangle this refuses is a
        /// wall to the walk, exactly like an unwalkable one.
        /// </summary>
        private static int ResolveWalkMask(in NavAgentComponent nav)
            => nav.WalkAreaMaskOverride != 0 ? nav.WalkAreaMaskOverride : DEFAULT_AREA_MASK;

        public FPNavAgentSystem(FPNavMesh navMesh, FPNavMeshQuery query,
            FPNavMeshPathfinder pathfinder, FPNavMeshFunnel funnel, IKLogger logger)
        {
            _navMesh = navMesh;
            _query = query;
            _pathfinder = pathfinder;
            _funnel = funnel;
            _logger = logger;

            WaypointThreshold = FP64.FromDouble(0.3);
            _avoidance = null;
        }

        /// <summary>
        /// Sets the ORCA avoidance system. Pass null to disable avoidance.
        /// </summary>
        public void SetAvoidance(FPNavAvoidance avoidance)
        {
            _avoidance = avoidance;
        }

        /// <summary>
        /// Extracts this system's NavMesh boundary as ORCA static obstacles into the current
        /// avoidance. MUST be called AFTER SetAvoidance. No-op if avoidance or NavMesh is null.
        /// Load-time only (not the hot path) — allocation here is fine. Idempotent: re-extracts
        /// and replaces on each call (safe across stage changes).
        /// </summary>
        public void LoadNavMeshObstacles()
        {
            if (_avoidance == null)
                return;

            // This runs on every SwapNavMesh, not just at load — see the note on this method's
            // summary. The scratch is what keeps the re-extract from re-allocating its working
            // set: the visited flags, the segment map, the counting-sort cursor, and this pair.
            // It is a field rather than a shared pool because a snapshot extract can happen on a
            // worker thread; ownership by the object that re-extracts is what makes serial use
            // structural (FPNavMeshObstacleExtractor.ExtractScratch).
            //
            // EVERYTHING here comes back OVERSIZED — the ring vertices and offsets as well as the
            // CSR pair. Read them through the counts, never through Length: the CSR through
            // (_triSegList[_triSegStart[t] .. _triSegStart[t+1])), the other two through the
            // counts handed to LoadObstacles below. Do not add a Length-based loop over any of
            // them; that is the whole reason this path allocates nothing per swap.
            _extractScratch = _extractScratch ?? new FPNavMeshObstacleExtractor.ExtractScratch();
            FPNavMeshObstacleExtractor.Extract(_navMesh, _extractScratch,
                out var vertices, out int vertexCount,
                out var polygonOffsets, out int polygonCount,
                out _triSegStart, out _triSegList);
            _avoidance.LoadObstacles(vertices, vertexCount, polygonOffsets, polygonCount);
            _obstacleLoadGenerationCache = _avoidance.ObstacleLoadGeneration;
            // The baked asset records its own bake Agent Radius (VERSION 3): apply it as the
            // obstacle inset so clearance is not double-charged (boundary inset + full radius).
            // Riding the asset keeps lockstep peers symmetric by construction — no hand-synced
            // constant. Consumers may still override the field after this call.
            _avoidance.ObstacleRadiusInset = _navMesh?.BakeAgentRadius ?? FP64.Zero;

            // Size the BFS visited-stamp to the navmesh (load-time; hot path stays GC-0). The
            // frontier/candidate buffers are fixed-cap and allocated once with the system.
            //
            // The generation reset belongs INSIDE the grow branch and nowhere else. SwapNavMesh
            // re-enters this method mid-match, and the array is only reallocated when it is too
            // SMALL — so a rebake that does not grow the triangle count (removing a building)
            // keeps stamps from before the swap. Restarting the counter at 0 there would make
            // generation 1 alias every slot still holding 1, and the BFS would treat those
            // triangles as visited and drop their wall segments. Scoped like this the counter
            // only restarts alongside a freshly zeroed array, and on reuse it keeps climbing
            // past every stale value, so a collision is impossible rather than merely unlikely.
            int triCount = _navMesh?.TriangleCount ?? 0;
            if (_bfsStamp == null || _bfsStamp.Length < triCount)
            {
                _bfsStamp = new int[triCount];
                _bfsGeneration = 0;
            }
        }

        /// <summary>
        /// Swaps in a rebaked navmesh, rebinding the query, pathfinder and funnel this system
        /// already holds. This is the cheap form: their working arrays are kept and only grown
        /// when the new mesh has more triangles, which on a large stage is the difference between
        /// a megabyte-and-a-half per building and nothing.
        ///
        /// <para><b>Follow with <see cref="ReseedAgents"/> before the next Update.</b> The swap is
        /// one line now, which makes it easy to read as the whole job — it is not. Every agent is
        /// still holding a triangle index and corridor that point into the mesh being replaced,
        /// and both are hashed frame state, so skipping the reseed desyncs the peer rather than
        /// merely misplacing an agent.</para>
        ///
        /// <para>MUST be invoked only from the deterministic command stream (same tick, same order
        /// on all peers), at a tick boundary. Re-extracts ORCA obstacles from the new mesh (R5)
        /// and invalidates the graph-local CSR.</para>
        ///
        /// <para>Requires the trio to be installed already — by the constructor or by the
        /// four-argument overload. That is the one thing this form cannot do that the other can:
        /// it rebinds what is there rather than replacing it.</para>
        /// </summary>
        public void SwapNavMesh(FPNavMesh newMesh)
        {
            if (newMesh == null)
                throw new System.ArgumentException("FPNavAgentSystem.SwapNavMesh: newMesh must be non-null");
            if (_query == null || _pathfinder == null || _funnel == null)
                throw new System.InvalidOperationException(
                    "FPNavAgentSystem.SwapNavMesh(mesh): no query/pathfinder/funnel to rebind. " +
                    "Install them through the constructor or the four-argument overload first.");

            _query.Rebind(newMesh);
            _pathfinder.Rebind(newMesh);
            _funnel.Rebind(newMesh);

            InstallSwappedMesh(newMesh);
        }

        /// <summary>
        /// Swaps in a rebaked navmesh, taking a query, pathfinder and funnel the caller built
        /// against <paramref name="newMesh"/>. Prefer <see cref="SwapNavMesh(FPNavMesh)"/>, which
        /// reuses the existing trio instead of allocating a new one; this form stays for games
        /// that own their own instances, and for tests that need to install a specific trio.
        ///
        /// <para><b>Follow with <see cref="ReseedAgents"/> before the next Update</b> — see the
        /// other overload for why. MUST be invoked only from the deterministic command stream
        /// (same tick, same order on all peers), at a tick boundary.</para>
        /// </summary>
        public void SwapNavMesh(FPNavMesh newMesh, FPNavMeshQuery query,
            FPNavMeshPathfinder pathfinder, FPNavMeshFunnel funnel)
        {
            if (newMesh == null || query == null || pathfinder == null || funnel == null)
                throw new System.ArgumentException("FPNavAgentSystem.SwapNavMesh: all arguments must be non-null");

            _query = query;
            _pathfinder = pathfinder;
            _funnel = funnel;

            InstallSwappedMesh(newMesh);
        }

        /// <summary>
        /// The part of a swap that is the same whichever way the trio got bound: adopt the mesh,
        /// drop the graph-local CSR, re-extract obstacles, and say so. Shared so the two
        /// overloads cannot drift — a swap that skipped any of this would be observably different.
        /// </summary>
        private void InstallSwappedMesh(FPNavMesh newMesh)
        {
            _navMesh = newMesh;

            // Invalidate the graph-local obstacle CSR; LoadNavMeshObstacles rebuilds it (and the
            // BFS stamp sizing + radius inset) from the new mesh when avoidance is wired.
            _triSegStart = null;
            _triSegList = null;
            _obstacleLoadGenerationCache = -1;
            LoadNavMeshObstacles();

            _logger?.KInformation($"[FPNavAgentSystem] navmesh swapped: {newMesh.Triangles.Length} triangles, " +
                $"fingerprint 0x{GetNavFingerprint():X16}");
        }

        /// <summary>
        /// Reseeds every agent after a navmesh swap: re-queries
        /// CurrentTriangleIndex from the agent position on the new mesh and invalidates the
        /// cached corridor (both live in the hashed frame state — stale values over rebuilt
        /// triangle indices would corrupt determinism and gameplay alike). Agents that still
        /// have a destination and were moving, planning or
        /// <see cref="FPNavAgentStatus.Blocked"/> are set to PathPending with the repath cooldown
        /// bypassed, so the next Update repaths them deterministically. Arrived and PathFailed are
        /// left alone on purpose: the first would re-plan every rebake, and re-trying "no route
        /// exists" on every mesh swap is a policy the game decides, not this pass. Agents whose
        /// position no longer lies on the mesh (e.g. standing inside a carved hole — placement
        /// rules should prevent this) fail their path and are reported.
        /// </summary>
        public unsafe void ReseedAgents(ref Frame frame, EntityRef[] entities, int entityCount)
        {
            // The caller hands in its own array and count, and nothing about that pair tells the
            // engine whether it is the WHOLE agent set or a truncated view of it. An agent that
            // is missed here keeps a CurrentTriangleIndex and corridor that index the mesh that
            // was just replaced — and because both fields are hashed frame state, a peer that
            // reseeds fewer agents than another diverges rather than merely misbehaving.
            //
            // That is not hypothetical: the Brawler seam once collected into a fixed-size array
            // and stopped at its length, so the post-fullstate swap (which can run before that
            // peer's first Update has grown the array) reseeded fewer agents than the authority.
            // The engine cannot fix the caller's bookkeeping, but it can refuse to let it pass
            // silently — which is the only reason this class of bug is expensive.
            int actual = 0;
            var audit = frame.Filter<NavAgentComponent>();
            while (audit.Next(out _))
                actual++;
            if (actual != entityCount)
            {
                _logger?.KError(
                    $"[FPNavAgentSystem] ReseedAgents: caller passed {entityCount} agent(s) but the frame " +
                    $"has {actual} — the difference is NOT reseeded and keeps corridor/triangle indices " +
                    $"into the replaced mesh. Both fields are hashed state, so peers that disagree here " +
                    $"desync. Fix the caller's collection (grow, do not truncate).");
            }

            int lost = 0;
            for (int i = 0; i < entityCount; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);

                nav.CurrentTriangleIndex = _query.FindTriangle(nav.Position.ToXZ(), nav.Position.y);
                nav.CorridorLength = 0;
                nav.PathIsValid = false;
                nav.HasPath = false;
                nav.OffCorridorTicks = 0;

                if (nav.CurrentTriangleIndex < 0)
                {
                    // Off the new mesh — typically a building was placed on top of this agent.
                    //
                    // NOTE: leaving it at -1 means it is frozen for good, not just this tick.
                    // Nothing else in the engine writes CurrentTriangleIndex back to a valid
                    // value: MoveAlongSurface returns early on startTri < 0 and hands the -1
                    // straight back, so the next rebake's reseed is the only thing that can
                    // recover it. Brawler hides this in its own layer (BotFSMSystem re-snaps
                    // nav.Position from the transform every tick and steers straight at the
                    // destination when velocity is zero), which is why the gap has never shown
                    // up in the sample.
                    //
                    // That is a deliberate deferral, not an oversight. The current behaviour is
                    // pinned by FPNavAgentOffMeshFreezeTests, whose tests are written to FAIL once
                    // a recovery path exists, and which record the conditions that should reopen
                    // the question.
                    lost++;
                    if (nav.HasNavDestination)
                        nav.Status = (byte)FPNavAgentStatus.PathFailed;
                    continue;
                }

                // Blocked belongs here and it is not obvious: that state is TERMINAL — once set,
                // ProcessSteering and ProcessMovement both return on `Status != Moving`, so no
                // engine path writes the status again. The mesh swap is the one event that can
                // make the block untrue (the building that stopped this agent is gone), and
                // leaving it out meant a demolished building left its units frozen for good.
                if (nav.HasNavDestination
                    && (nav.Status == (byte)FPNavAgentStatus.Moving
                        || nav.Status == (byte)FPNavAgentStatus.PathPending
                        || nav.Status == (byte)FPNavAgentStatus.Blocked))
                {
                    nav.Status = (byte)FPNavAgentStatus.PathPending;
                    nav.LastRepathTick = 0; // bypass the repath cooldown: repath on the next Update
                }
            }

            if (lost > 0)
                _logger?.KWarning($"[FPNavAgentSystem] ReseedAgents: {lost}/{entityCount} agent(s) off the new mesh " +
                    $"(inside a carved region?) — paths failed");
            else
                _logger?.KInformation($"[FPNavAgentSystem] ReseedAgents: {entityCount} agent(s) reseeded");
        }

        /// <summary>
        /// Cross-peer navigation fingerprint: folded into the FullState resync
        /// static-geometry check. Never 0 while a mesh is present.
        /// </summary>
        public long GetNavFingerprint()
        {
            if (_navMesh == null)
                return 0;
            long fp = unchecked((long)FPNavMeshRebaker.ComputeFingerprint(_navMesh));
            return fp == 0 ? 1L : fp;
        }

        /// <summary>
        /// Number of obstacle vertices currently loaded into the avoidance (0 if no avoidance).
        /// Setup-time diagnostic: after wiring, a value of 0 while avoidance is set signals a
        /// missing LoadNavMeshObstacles wiring (SD desync hazard) or a boundary-free NavMesh.
        /// </summary>
        public int DebugObstacleCount => _avoidance?.DebugObstacleCount ?? 0;

        /// <summary>
        /// Constrains a position to the NavMesh (uses MoveAlongSurface internally).
        /// </summary>
        /// <remarks>
        /// <para>The mask is a required argument, following <c>MoveAlongSurface</c>: this method
        /// takes no agent, so it cannot resolve one, and defaulting it would silently constrain a
        /// narrow-masked agent against ground it may not stand on. Pass
        /// <see cref="NavAgentComponent.WalkAreaMaskOverride"/> (or
        /// <see cref="DEFAULT_AREA_MASK"/> when it is zero) to agree with what that agent's walk
        /// will accept.</para>
        ///
        /// <para><b>It pairs with the WALK, not with the path</b>, and that is a correction: this
        /// remark used to promise agreement with <c>FindPath</c>, which held only while one mask
        /// served both. It no longer does — an agent may plan through a building on purpose and be
        /// refused entry to it, so agreeing with the path would mean constraining a position INTO
        /// a footprint the agent cannot occupy. Constrain answers "where may this agent stand",
        /// which is the walk's question.</para>
        /// </remarks>
        public FPVector3 ConstrainToNavMesh(FPVector3 newPos, FPVector3 oldPos, int currentTri,
            int areaMask)
        {
            var (resultPos, _) = _query.MoveAlongSurface(oldPos, newPos, currentTri,
                areaMask, MultiFloorYThreshold);
            return resultPos;
        }

        /// <summary>
        /// Updates all agents by one tick based on NavAgentComponent data.
        /// </summary>
        /// <remarks>
        /// The caller owns the array: who is in it, in what order, and how many calls a tick takes
        /// are all game-side decisions. Three consequences worth knowing before splitting it up:
        /// <list type="bullet">
        /// <item><description>
        /// <b>Past <see cref="MAX_AGENTS"/> the position-correction pass silently drops the tail.</b>
        /// Every agent is still steered and moved; only the pass that pushes residual overlaps apart
        /// takes the first <see cref="MAX_AGENTS"/>. Nothing throws and nothing logs —
        /// <see cref="DebugCollisionResolveTruncatedCount"/> is the only signal.
        /// </description></item>
        /// <item><description>
        /// <b>That pass is effective only for position-authoritative consumers.</b> It writes
        /// <c>NavAgentComponent.Position</c> and nothing else, so an integration that drives the
        /// character from <c>Velocity</c> and re-syncs Position from an external transform each tick
        /// never observes it — there the counter can be non-zero with no behavioural change at all.
        /// </description></item>
        /// <item><description>
        /// <b>Diagnostic counters accumulate over the instance lifetime and are not rollback-aware.</b>
        /// A resimulated tick is counted again. They are diagnostic fields, outside the state hash,
        /// wire and replay.
        /// </description></item>
        /// </list>
        /// </remarks>
        public unsafe void Update(ref Frame frame, EntityRef[] entities, int entityCount, int currentTick, FP64 dt)
        {
            for (int i = 0; i < entityCount; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                ProcessPathRequest(ref nav, currentTick);
                ProcessSteering(ref nav);
            }

            if (_avoidance != null)
            {
                // Graph-local obstacle query is usable only when the CSR was built from THIS navmesh
                // and no external LoadObstacles re-load has desynced it, verified by the generation guard.
                bool graphAvailable = _triSegStart != null
                    && _obstacleLoadGenerationCache == _avoidance.ObstacleLoadGeneration
                    && _avoidance.DebugObstacleCount > 0;
                FP64 timeHorizonObst = _avoidance.TimeHorizonObst; // read the same field the adopt gate uses (F2)

                for (int i = 0; i < entityCount; i++)
                {
                    ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                    if (nav.Status != (byte)FPNavAgentStatus.Moving)
                        continue;

                    int seedTri = nav.CurrentTriangleIndex;
                    if (graphAvailable && seedTri >= 0)
                    {
                        FP64 obstRange = timeHorizonObst * nav.Speed + nav.Radius;
                        int candCount = CollectGraphLocalObstacles(seedTri, nav.Position.ToXZ(), obstRange);
                        nav.DesiredVelocity = _avoidance.ComputeNewVelocity(
                            i, ref frame, entities, entityCount, dt, _candidateSegs, candCount, true);
                    }
                    else
                    {
                        // Fallback: no navmesh CSR / unlocalized agent (seed -1) -> brute-force scan.
                        nav.DesiredVelocity = _avoidance.ComputeNewVelocity(
                            i, ref frame, entities, entityCount, dt);
                    }
                }
            }

            for (int i = 0; i < entityCount; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                ProcessMovement(ref nav, dt);
            }

            // Pass 4: position-based collision resolution.
            // Complements velocity-space ORCA/LP3 by pushing residual overlaps apart in position
            // space (incl. Arrived/stopped agents that ORCA does not steer). Gated on avoidance so
            // non-avoidance configs stay bit-identical to before.
            if (_avoidance != null)
                ResolveCollisions(ref frame, entities, entityCount);
        }

        /// <summary>
        /// Graph-local obstacle candidate collection. BFS the navmesh adjacency from the
        /// agent's seed triangle to within obstRange, gathering the boundary segments of every
        /// visited triangle into <see cref="_candidateSegs"/> (unsorted). Only the topological
        /// selection lives here; the exact point-segment gate, nearest-first sort, and line
        /// generation stay in FPNavAvoidance. Returns the candidate count. GC-0.
        ///
        /// Expansion gates (all node-local, so the visited set is pop-order independent):
        ///  - not blocked (a blocked neighbor is a wall; don't reach walls behind it),
        ///  - step-delta floor: |centerY(nb) - centerY(cur)| ≤ MultiFloorYThreshold (per-edge, lets
        ///    ramps through; NOT the seed-band that pathfinding movement uses),
        ///  - climb cap: |centerY(nb) - centerY(seed)| ≤ MaxClimbWithinHorizon (∞ default),
        ///  - XZ padding: the triangle's nearest point to the agent ≤ obstRange (padded, so a
        ///    triangle whose center is out of range but whose wall edge is in range is still
        ///    visited — the adopt gate then exactly re-tests each segment).
        /// </summary>
        private int CollectGraphLocalObstacles(int seedTri, FPVector2 agentXZ, FP64 obstRange)
        {
            var tris = _navMesh.Triangles;
            if (seedTri < 0 || seedTri >= tris.Length || _triSegStart == null || _bfsStamp == null)
                return 0;

            FP64 obstRangeSqr = obstRange * obstRange;

            // Effective climb cap: combine the manual cap with the sound bound derived from the
            // mesh's recorded bake slope — within the horizon the agent walks at most obstRange,
            // gaining at most obstRange*sin(maxSlope) height on a mesh baked with that slope limit,
            // so higher walls are unreachable and cutting them only removes phantoms. Recorded
            // slope 0 = unknown → no auto bound (manual cap / ∞ as before). min() keeps a manual
            // cap meaningful only when it tightens further. FP64.Sin is LUT-based (deterministic).
            FP64 climbCap = MaxClimbWithinHorizon;
            if (_navMesh.BakeMaxSlopeDeg > FP64.Zero)
            {
                FP64 capAuto = obstRange * FP64.Sin(_navMesh.BakeMaxSlopeDeg * FP64.Deg2Rad);
                if (capAuto < climbCap)
                    climbCap = capAuto;
            }

            // Generation stamp: bump per query, wrap resets (no per-call clear).
            _bfsGeneration++;
            if (_bfsGeneration == int.MaxValue)
            {
                System.Array.Clear(_bfsStamp, 0, _bfsStamp.Length);
                _bfsGeneration = 1;
            }

            int candCount = 0;
            int head = 0, tail = 0;
            _bfsFrontier[tail++] = seedTri;
            _bfsStamp[seedTri] = _bfsGeneration;
            FP64 seedCenterY = tris[seedTri].centerY;

            while (head < tail)
            {
                int t = _bfsFrontier[head++];

                // Collect this triangle's boundary segments (CSR[t]) — 1:1 partition, no dedup.
                int segEnd = _triSegStart[t + 1];
                for (int k = _triSegStart[t]; k < segEnd; k++)
                {
                    if (candCount < _candidateSegs.Length)
                        _candidateSegs[candCount++] = _triSegList[k];
                }

                FP64 curCenterY = tris[t].centerY;
                for (int e = 0; e < 3; e++)
                {
                    int nb = tris[t].GetNeighbor(e);
                    if (nb < 0)
                        continue; // boundary edge: adopted above via CSR, not an expansion target
                    if (_bfsStamp[nb] == _bfsGeneration)
                        continue;
                    if (tris[nb].isBlocked)
                        continue;

                    FP64 nbCenterY = tris[nb].centerY;
                    FP64 stepDelta = nbCenterY - curCenterY;
                    if (stepDelta < FP64.Zero) stepDelta = -stepDelta;
                    if (stepDelta > MultiFloorYThreshold)
                        continue;

                    FP64 climb = nbCenterY - seedCenterY;
                    if (climb < FP64.Zero) climb = -climb;
                    if (climb > climbCap)
                        continue;

                    if (TriangleNearestDistSqr(agentXZ, nb, tris) > obstRangeSqr)
                        continue;

                    _bfsStamp[nb] = _bfsGeneration;
                    if (tail < _bfsFrontier.Length)
                        _bfsFrontier[tail++] = nb;
                    else
                        _bfsFrontierOverflowCount++;
                }
            }
            return candCount;
        }

        /// <summary>
        /// Squared XZ distance from a point to a triangle (0 if inside; else nearest edge). Used as
        /// the padded BFS expansion gate — never under-approximates for a point outside the
        /// triangle, so an in-range wall edge is never skipped.
        /// </summary>
        private FP64 TriangleNearestDistSqr(FPVector2 p, int triIdx, ReadOnlySpan<FPNavMeshTriangle> tris)
        {
            ref readonly FPNavMeshTriangle tri = ref tris[triIdx];
            FPVector2 a = _navMesh.Vertices[tri.v0].ToXZ();
            FPVector2 b = _navMesh.Vertices[tri.v1].ToXZ();
            FPVector2 c = _navMesh.Vertices[tri.v2].ToXZ();
            if (FPNavMeshQuery.PointInTriangle2D(p, a, b, c))
                return FP64.Zero;
            FP64 d = FPVector2.SqrDistance(p, FPNavMeshQuery.ClosestPointOnSegment2D(p, a, b));
            FP64 d1 = FPVector2.SqrDistance(p, FPNavMeshQuery.ClosestPointOnSegment2D(p, b, c));
            if (d1 < d) d = d1;
            FP64 d2 = FPVector2.SqrDistance(p, FPNavMeshQuery.ClosestPointOnSegment2D(p, c, a));
            if (d2 < d) d = d2;
            return d;
        }

        private unsafe void ProcessPathRequest(ref NavAgentComponent nav, int currentTick)
        {
            if (!nav.HasNavDestination || nav.HasPath)
                return;

            if (nav.Status != (byte)FPNavAgentStatus.PathPending)
                return;

            {
                FP64 distToTarget = FPVector2.Distance(nav.Position.ToXZ(), nav.Destination.ToXZ());
                FP64 yDistToTarget = FP64.Abs(nav.Position.y - nav.Destination.y);
                if (distToTarget < WaypointThreshold && yDistToTarget < MultiFloorYThreshold)
                {
                    nav.Status = (byte)FPNavAgentStatus.Arrived;
                    nav.Velocity = FPVector2.Zero;
                    nav.DesiredVelocity = FPVector2.Zero;
                    return;
                }
            }

            FP64 ticksSinceLast = FP64.FromInt(currentTick - nav.LastRepathTick);
            if (ticksSinceLast < nav.PathRepathCooldown && nav.LastRepathTick > 0)
                return;

            nav.LastRepathTick = currentTick;

            bool found = _pathfinder.FindPath(nav.Position, nav.Destination, ResolvePlanMask(nav),
                out int[] corridor, out int corridorLength);
            if (found)
            {
                fixed (int* dst = nav.Corridor)
                {
                    NavCorridorHelper.SetCorridor(dst, ref nav.CorridorLength,
                        NavAgentComponent.MAX_CORRIDOR, corridor, corridorLength);
                }
                nav.PathTarget = nav.Destination;
                nav.PathId = nav.PathRequestId;
                nav.PathIsValid = true;
                nav.HasPath = true;
                nav.Status = (byte)FPNavAgentStatus.Moving;
            }
            else
            {
                nav.Status = (byte)FPNavAgentStatus.PathFailed;
            }
        }

        private unsafe void ProcessSteering(ref NavAgentComponent nav)
        {
            if (nav.Status != (byte)FPNavAgentStatus.Moving)
                return;

            if (!nav.PathIsValid || nav.CorridorLength <= 0)
            {
                nav.Status = (byte)FPNavAgentStatus.Arrived;
                nav.Velocity = FPVector2.Zero;
                nav.DesiredVelocity = FPVector2.Zero;
                return;
            }

            fixed (int* src = nav.Corridor)
            {
                for (int k = 0; k < nav.CorridorLength; k++)
                    _corridorBuffer[k] = src[k];
            }

            int cornerCount = _funnel.FindCorners(_corridorBuffer, nav.CorridorLength,
                nav.Position, nav.PathTarget, 4);
            FPVector3[] corners = _funnel.Corners;

            if (cornerCount == 0)
            {
                if (nav.CorridorLength > 1)
                {
                    nav.HasPath = false;
                    nav.Status = (byte)FPNavAgentStatus.PathPending;
                    return;
                }
                nav.Status = (byte)FPNavAgentStatus.Arrived;
                nav.Velocity = FPVector2.Zero;
                nav.DesiredVelocity = FPVector2.Zero;
                return;
            }

            FPVector3 nextCorner = corners[0];
            FPVector2 posXZ = nav.Position.ToXZ();
            FPVector2 cornerXZ = nextCorner.ToXZ();

            FPVector2 direction = (cornerXZ - posXZ).normalized;
            nav.DesiredVelocity = direction * nav.Speed;

            FPVector2 targetXZ = nav.PathTarget.ToXZ();
            FP64 distToTarget = FPVector2.Distance(posXZ, targetXZ);

            if (nav.Acceleration > FP64.Zero)
            {
                FP64 brakingRadius = nav.Speed * nav.Speed / (nav.Acceleration * FP64.FromInt(2));
                if (distToTarget < brakingRadius)
                {
                    nav.DesiredVelocity = nav.DesiredVelocity * distToTarget / brakingRadius;
                }
            }

            if (nav.StoppingDistance > FP64.Zero)
            {
                FP64 yDist = FP64.Abs(nav.Position.y - nav.PathTarget.y);
                if (yDist < MultiFloorYThreshold)
                {
                    if (distToTarget < nav.StoppingDistance)
                    {
                        nav.DesiredVelocity = nav.DesiredVelocity * distToTarget / nav.StoppingDistance;
                    }
                }
            }
        }

        private unsafe void ProcessMovement(ref NavAgentComponent nav, FP64 dt)
        {
            if (nav.Status != (byte)FPNavAgentStatus.Moving)
            {
                nav.Velocity = FPVector2.Zero;
                nav.CurrentSpeed = FP64.Zero;
                return;
            }

            FPVector2 diff = nav.DesiredVelocity - nav.Velocity;
            FP64 maxAccelStep = nav.Acceleration * dt;
            FP64 diffSqrMag = diff.sqrMagnitude;

            if (diffSqrMag > maxAccelStep * maxAccelStep)
            {
                diff = diff.normalized * maxAccelStep;
            }

            nav.Velocity = nav.Velocity + diff;

            FP64 velSqrMag = nav.Velocity.sqrMagnitude;
            if (velSqrMag > nav.Speed * nav.Speed)
            {
                nav.Velocity = nav.Velocity.normalized * nav.Speed;
            }

            nav.CurrentSpeed = nav.Velocity.magnitude;

            FPVector3 displacement = new FPVector3(
                nav.Velocity.x * dt,
                FP64.Zero,
                nav.Velocity.y * dt);
            FPVector3 newPos = nav.Position + displacement;

            int walkMask = ResolveWalkMask(nav);
            var (resultPos, resultTri) = _query.MoveAlongSurfaceWithVisited(
                nav.Position, newPos, nav.CurrentTriangleIndex, walkMask,
                MultiFloorYThreshold, _visitedBuffer, out int visitedCount);

            // Stop-on-contact: this agent's corridor leads into ground its walk mask refuses, and
            // it has now run into it. Two conditions, and BOTH are needed.
            //
            //   (1) the walk did not move. A refused neighbour is a wall to MoveAlongSurface, so a
            //       head-on approach is clipped to the edge and comes back where it started. The
            //       walk cannot tell us WHY it refused — every reason falls through the same wall
            //       path, and that is deliberate (the query re-tests the mask only to log it, so
            //       the production path pays nothing) — which is why the reason is derived here.
            //   (2) the next corridor triangle is refused by this mask. This is what separates a
            //       building from a real wall: against a real wall the corridor's next triangle is
            //       still enterable, so the agent keeps sliding, which is the behaviour that must
            //       survive.
            //
            // Condition (1) alone would fire on any wall. Condition (2) alone would fire on the
            // FIRST tick of every such path — an agent crossing its current triangle is "not
            // advancing" for as many ticks as the crossing takes, so it would stop before it ever
            // touched anything.
            //
            // And (1) needs the ATTEMPT, not just the outcome: `resultPos == nav.Position` is also
            // true of an agent that requested no displacement at all, so a unit held still by a
            // crowd one triangle short of a footprint used to report Blocked without having
            // touched anything. Comparing against newPos costs nothing — it is already computed —
            // and it is what makes this state mean "stopped where it touched" rather than "is not
            // moving". The cost is that Blocked now arrives on the tick the agent pushes against
            // the edge instead of the tick its velocity reaches zero, which is the correct
            // definition of contact.
            //
            // Left to itself this stall is silent: the agent keeps Status = Moving and a valid
            // path, and the off-corridor repath never fires because standing still inside the
            // corridor's current triangle counts as being ON the corridor and resets that counter.
            // Naming it is the whole point — the game decides what to do about a Blocked agent.
            //
            // Which triangle is "the next one" is decided by THIS agent's index in its corridor,
            // found once here and used twice — by the verdict below and by the advance further
            // down, which used to search for it a second time. Off the corridor the search comes
            // back empty (idx < 0) and both readers take their off-corridor branch: the verdict is
            // SKIPPED, because the corridor head is not an off-corridor agent's next step and
            // latching the terminal Blocked on it would pre-empt the off-corridor repath that is
            // the actual answer for that agent.
            int corridorIdx = -1;
            if (nav.PathIsValid && nav.CorridorLength > 0)
            {
                fixed (int* p = nav.Corridor)
                {
                    for (int i = 0; i < nav.CorridorLength; i++)
                    {
                        if (p[i] == resultTri)
                        {
                            corridorIdx = i;
                            break;
                        }
                    }
                }
            }

            if (corridorIdx >= 0 && corridorIdx + 1 < nav.CorridorLength
                && newPos != nav.Position && resultPos == nav.Position)
            {
                int nextTri;
                fixed (int* p = nav.Corridor)
                    nextTri = p[corridorIdx + 1];

                if (nextTri >= 0 && nextTri < _navMesh.Triangles.Length
                    && (walkMask & _navMesh.Triangles[nextTri].areaMask) == 0)
                {
                    nav.Velocity = FPVector2.Zero;
                    nav.DesiredVelocity = FPVector2.Zero;
                    nav.CurrentSpeed = FP64.Zero;
                    nav.Status = (byte)FPNavAgentStatus.Blocked;
                    return;
                }
            }

            nav.CurrentTriangleIndex = resultTri;
            nav.Position = resultPos;

            if (nav.PathIsValid && nav.CorridorLength > 0)
            {
                int advanceIdx = corridorIdx; // found once, above the contact verdict
                fixed (int* p = nav.Corridor)
                {
                    if (advanceIdx > 0)
                    {
                        int newLen = nav.CorridorLength - advanceIdx;
                        for (int i = 0; i < newLen; i++)
                            p[i] = p[i + advanceIdx];
                        nav.CorridorLength = newLen;
                        nav.OffCorridorTicks = 0;

                        if (newLen == 1)
                        {
                            FP64 currentSpeed = nav.Velocity.magnitude;
                            if (currentSpeed > FP64.Zero)
                            {
                                FPVector2 desiredDir = nav.DesiredVelocity.normalized;
                                if (desiredDir.sqrMagnitude > FP64.Zero)
                                {
                                    nav.Velocity = desiredDir * currentSpeed;
                                }
                            }
                        }
                    }
                    else if (advanceIdx == 0)
                    {
                        nav.OffCorridorTicks = 0;
                    }
                    else
                    {
                        nav.OffCorridorTicks++;
                        if (nav.OffCorridorTicks >= OffCorridorRepathThreshold)
                        {
                            nav.HasPath = false;
                            nav.Status = (byte)FPNavAgentStatus.PathPending;
                            nav.OffCorridorTicks = 0;
                            return;
                        }
                    }
                }
            }

            {
                FP64 distToTarget = FPVector2.Distance(
                    nav.Position.ToXZ(), nav.PathTarget.ToXZ());
                FP64 yDistToTarget = FP64.Abs(nav.Position.y - nav.PathTarget.y);
                if (distToTarget < WaypointThreshold && yDistToTarget < MultiFloorYThreshold)
                {
                    nav.Status = (byte)FPNavAgentStatus.Arrived;
                    nav.Velocity = FPVector2.Zero;
                    nav.DesiredVelocity = FPVector2.Zero;
                }
            }
        }

        /// <summary>
        /// Position-based collision resolution (RVO2/DetourCrowd style). Iteratively pushes
        /// overlapping agent pairs apart in position space, compute-then-apply per iteration for
        /// order-independence. Pushes all states (incl. Arrived) but never writes Velocity, and
        /// does not re-trigger Arrived agents to Moving. Mesh-clamped per agent.
        /// </summary>
        /// <remarks>
        /// Consumer model: this pass writes only <c>NavAgentComponent.Position</c>. It is effective
        /// only for POSITION-AUTHORITATIVE consumers that treat Position as the entity's canonical
        /// location. VELOCITY-AUTHORITATIVE integrations (which drive the character from
        /// <c>Velocity</c> and re-sync Position from an external transform each tick — e.g. the
        /// Brawler sample) do not observe this correction; there the velocity-space fixes
        /// (coincident guard, LinearProgram3) apply but overlapping STOPPED agents are separated at
        /// the transform/physics layer instead (out of nav scope).
        /// </remarks>
        private void ResolveCollisions(ref Frame frame, EntityRef[] entities, int entityCount)
        {
            int n = entityCount < MAX_AGENTS ? entityCount : MAX_AGENTS;
            if (entityCount > MAX_AGENTS)
                _collisionResolveTruncatedCount += entityCount - MAX_AGENTS;

            for (int iter = 0; iter < COLLISION_RESOLVE_ITERATIONS; iter++)
            {
                // Sub-pass 1: accumulate displacement from current positions (barrier → order-independent)
                for (int a = 0; a < n; a++)
                {
                    _disp[a] = FPVector2.Zero;
                    _dispWeight[a] = 0;
                }

                for (int a = 0; a < n; a++)
                {
                    ref readonly var na = ref frame.GetReadOnly<NavAgentComponent>(entities[a]);
                    FPVector2 pa = na.Position.ToXZ();

                    for (int b = a + 1; b < n; b++)
                    {
                        ref readonly var nb = ref frame.GetReadOnly<NavAgentComponent>(entities[b]);

                        FP64 combinedRadius = na.Radius + nb.Radius;
                        FPVector2 diff = pa - nb.Position.ToXZ();
                        FP64 distSqr = diff.sqrMagnitude;

                        if (distSqr >= combinedRadius * combinedRadius)
                            continue; // no overlap

                        if (distSqr <= POS_EPSILON)
                        {
                            // Coincident: idx fixed axis (matches the coincident guard — lower idx → -x).
                            // a < b → a pushed -x, b pushed +x. Small fixed nudge; radial resolve follows.
                            _disp[a] = _disp[a] + new FPVector2(-COINCIDENT_PEN, FP64.Zero);
                            _disp[b] = _disp[b] + new FPVector2(COINCIDENT_PEN, FP64.Zero);
                        }
                        else
                        {
                            FP64 dist = FP64.Sqrt(distSqr);
                            // scaled = (1/dist) * ((r₁+r₂ − dist) * 0.5) * factor ; diff (len=dist) → unit push
                            FP64 scaled = (FP64.One / dist)
                                * ((combinedRadius - dist) * FP64.Half)
                                * COLLISION_RESOLVE_FACTOR;
                            FPVector2 push = diff * scaled;
                            _disp[a] = _disp[a] + push; // a away from b
                            _disp[b] = _disp[b] - push; // b away from a
                        }

                        _dispWeight[a]++;
                        _dispWeight[b]++;
                    }
                }

                // Sub-pass 2: apply averaged displacement (mesh-clamped). Position only — Velocity untouched.
                for (int a = 0; a < n; a++)
                {
                    if (_dispWeight[a] == 0)
                        continue; // no overlap → no-op (bit-identical when no collisions)

                    ref var na = ref frame.Get<NavAgentComponent>(entities[a]);
                    FP64 iw = FP64.One / FP64.FromInt(_dispWeight[a]);
                    FPVector2 d = _disp[a] * iw;

                    FPVector3 newPos = na.Position + new FPVector3(d.x, FP64.Zero, d.y);
                    // This agent's own walk mask, NOT FPNavMeshAreas.ALL_AREAS: a crowd may press a
                    // unit against ground it is not allowed to enter, never into it. Unfiltered,
                    // this pass is the one way an agent ends up standing inside a retained
                    // building's footprint — and if it is Blocked when that happens it parks there,
                    // because Blocked is terminal and nothing recomputes it.
                    //
                    // Passing the mask does NOT trap an agent that is already inside forbidden
                    // ground: the walk's expansion rule exempts refused neighbours while the
                    // triangle it expands FROM is refused too, so such an agent can be pushed
                    // across its forbidden component and out. That asymmetry — the mask blocks
                    // entering, not leaving — lives in MoveAlongSurface and must not be
                    // re-implemented here; ALL_AREAS would additionally allow what that rule
                    // deliberately forbids, carrying an agent between two SEPARATE forbidden
                    // regions.
                    var (resultPos, resultTri) = _query.MoveAlongSurface(
                        na.Position, newPos, na.CurrentTriangleIndex, ResolveWalkMask(na),
                        MultiFloorYThreshold);

                    na.CurrentTriangleIndex = resultTri;
                    na.Position = resultPos;
                }
            }
        }
    }
}
