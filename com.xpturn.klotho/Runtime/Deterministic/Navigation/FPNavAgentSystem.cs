using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Per-tick agent update system.
    /// Handles path requests, steering, movement, and NavMesh constraints.
    /// </summary>
    public class FPNavAgentSystem
    {
        private readonly FPNavMesh _navMesh;
        private readonly FPNavMeshQuery _query;
        private readonly FPNavMeshPathfinder _pathfinder;
        private readonly FPNavMeshFunnel _funnel;
        private readonly IKLogger _logger;

        private FPNavAvoidance _avoidance;

        private const int VISITED_BUFFER_SIZE = 48;
        private readonly int[] _visitedBuffer = new int[VISITED_BUFFER_SIZE];
        private readonly int[] _corridorBuffer = new int[NavAgentComponent.MAX_CORRIDOR];

        // --- Graph-local obstacle query (BFS here, reusing the navmesh topology directly) ---
        // Forward triangle->segment CSR built alongside LoadObstacles (segment index aligns with
        // FPNavAvoidance._obstacles). null until LoadNavMeshObstacles runs on a navmesh.
        private int[] _triSegStart;   // CSR offsets, length triCount+1
        private int[] _triSegList;    // segment indices grouped by owner triangle
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
        /// Default areaMask value (all areas allowed).
        /// </summary>
        public const int DEFAULT_AREA_MASK = ~0;

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

            FPNavMeshObstacleExtractor.Extract(_navMesh, out var vertices, out var polygonOffsets,
                out _triSegStart, out _triSegList);
            _avoidance.LoadObstacles(vertices, polygonOffsets);
            _obstacleLoadGenerationCache = _avoidance.ObstacleLoadGeneration;
            // The baked asset records its own bake Agent Radius (VERSION 3): apply it as the
            // obstacle inset so clearance is not double-charged (boundary inset + full radius).
            // Riding the asset keeps lockstep peers symmetric by construction — no hand-synced
            // constant. Consumers may still override the field after this call.
            _avoidance.ObstacleRadiusInset = _navMesh?.BakeAgentRadius ?? FP64.Zero;

            // Size the BFS visited-stamp to the navmesh (load-time; hot path stays GC-0). The
            // frontier/candidate buffers are fixed-cap and allocated once with the system.
            int triCount = _navMesh?.Triangles?.Length ?? 0;
            if (_bfsStamp == null || _bfsStamp.Length < triCount)
                _bfsStamp = new int[triCount];
            _bfsGeneration = 0;
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
        public FPVector3 ConstrainToNavMesh(FPVector3 newPos, FPVector3 oldPos, int currentTri)
        {
            var (resultPos, _) = _query.MoveAlongSurface(oldPos, newPos, currentTri, MultiFloorYThreshold);
            return resultPos;
        }

        /// <summary>
        /// Updates all agents by one tick based on NavAgentComponent data.
        /// </summary>
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
        private FP64 TriangleNearestDistSqr(FPVector2 p, int triIdx, FPNavMeshTriangle[] tris)
        {
            ref FPNavMeshTriangle tri = ref tris[triIdx];
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

            bool found = _pathfinder.FindPath(nav.Position, nav.Destination, DEFAULT_AREA_MASK,
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

            var (resultPos, resultTri) = _query.MoveAlongSurfaceWithVisited(
                nav.Position, newPos, nav.CurrentTriangleIndex, MultiFloorYThreshold,
                _visitedBuffer, out int visitedCount);

            nav.CurrentTriangleIndex = resultTri;
            nav.Position = resultPos;

            if (nav.PathIsValid && nav.CorridorLength > 0)
            {
                int advanceIdx = -1;
                fixed (int* p = nav.Corridor)
                {
                    for (int i = 0; i < nav.CorridorLength; i++)
                    {
                        if (p[i] == resultTri)
                        {
                            advanceIdx = i;
                            break;
                        }
                    }

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
                    var (resultPos, resultTri) = _query.MoveAlongSurface(
                        na.Position, newPos, na.CurrentTriangleIndex, MultiFloorYThreshold);

                    na.CurrentTriangleIndex = resultTri;
                    na.Position = resultPos;
                }
            }
        }
    }
}
