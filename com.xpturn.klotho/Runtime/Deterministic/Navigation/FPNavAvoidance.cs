using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// ORCA half-plane.
    /// </summary>
    [Serializable]
    public struct FPOrcaLine
    {
        public FPVector2 point;
        public FPVector2 direction;
    }

    /// <summary>
    /// ORCA (Optimal Reciprocal Collision Avoidance) avoidance system.
    /// Agent-to-agent ORCA half-planes + 2D linear program solver.
    /// FP64 deterministic implementation.
    /// </summary>
    public class FPNavAvoidance
    {
        /// <remarks><b>Defaults, not this instance's values</b> — sized from
        /// <see cref="FPNavTuning.MaxOrcaLines"/> / <see cref="FPNavTuning.MaxNeighbors"/>.</remarks>
        public const int MAX_ORCA_LINES = 64;
        /// <inheritdoc cref="MAX_ORCA_LINES"/>
        public const int MAX_NEIGHBORS = 16;

        // Obstacle line budget. Reserves MAX_NEIGHBORS slots for agent lines so obstacle
        // lines (filled first) can never starve agent-to-agent avoidance.
        public const int MAX_OBST_LINES = MAX_ORCA_LINES - MAX_NEIGHBORS;

        private readonly FPNavTuning _tuning;

        /// <summary>The sizes this instance was built with (see <see cref="FPNavTuning"/>).</summary>
        public FPNavTuning Tuning => _tuning;

        // Load-time coordinate range guard: keeps FP64 (Q32.32) products off the saturation
        // range. Products of two values must stay below ~sqrt(2^31) ~= 46340.
        private static readonly FP64 COORD_ABS_MAX = FP64.FromInt(46340);

        /// <summary>
        /// Neighbor search radius.
        /// </summary>
        public FP64 NeighborDist;

        /// <summary>
        /// Agent-to-agent time horizon.
        /// </summary>
        public FP64 TimeHorizon;

        /// <summary>
        /// Static obstacle time horizon.
        /// </summary>
        public FP64 TimeHorizonObst;

        /// <summary>
        /// Bake inset already carried by the loaded obstacle boundary (a navmesh baked with Agent
        /// Radius R_bake has its boundary R_bake inside the physical wall). Obstacle lines then
        /// keep clearance max(0, agent.Radius - ObstacleRadiusInset) from the boundary, so the
        /// total physical clearance stays exactly agent.Radius instead of double-charging
        /// (inset + full radius). Default 0 = no correction (raw obstacle polygons, e.g. convex
        /// block sources, or an uncorrected navmesh boundary). With the boundary baked at the
        /// agent radius, set this to that radius: the effective obstacle radius becomes 0 and the
        /// boundary itself is the constraint — consistent with the point-agent funnel path, which
        /// hugs boundary corners (a nonzero effective radius fights those corners and shows up as
        /// slowdown at every hugged edge). Per-peer local config: lockstep peers must agree.
        /// </summary>
        public FP64 ObstacleRadiusInset;

        // Pre-allocated buffers
        private readonly FPOrcaLine[] _orcaLines;
        private int _orcaLineCount;

        // Neighbor selection buffers (avoid per-frame allocation)
        private readonly FP64[] _neighborDistSqr;
        private readonly int[] _neighborIndices;
        private int _neighborCount;

        // Projected constraint buffer for LinearProgram3 (RVO2 projLines) — zero-GC
        private readonly FPOrcaLine[] _projLines;
        private int _infeasibleCount;

        // Static obstacle data (loaded once via LoadObstacles, tick-independent)
        private FPObstacleVertex[] _obstacles;
        private int _obstacleCount;
        private int _numObstLines;

        // Obstacle neighbor selection buffers (sorted by point-segment distSq; nearest-first)
        private readonly FP64[] _obstNeighborDistSqr;
        private readonly int[] _obstNeighborIndices;
        private int _obstNeighborCount;

        // Hot-path event counter (no logging on the tick path — GC 0)
        private int _obstLineOverflowCount;

        // Bumped on every LoadObstacles. A graph-local query owner (FPNavAgentSystem) caches this
        // at CSR-build time and falls back to brute-force if it ever diverges — guards against an
        // external LoadObstacles re-load silently desyncing the tri->segment map from _obstacles.
        private int _obstacleLoadGeneration;

        // Which obstacle-selection path the last ComputeNewVelocity ran (graph-local vs brute-force).
        // Test/diagnostic only — not part of the wire/state.
        private bool _lastObstaclePathWasGraph;

        private static readonly FP64 EPSILON = FP64.FromRaw(100);
        private static readonly FP64 COINCIDENT_FACTOR = FP64.FromDouble(0.9);

        // Parallel threshold for the projLines path (LP3 bisector gate + inner LP narrowing).
        // Wider than EPSILON by design: keeps bisector intersection quotients in the exact
        // FP64 Q31.32 range (overflow bound: P·(1 + 2/eps) < ~46,340, worst project P = 36.25).
        private static readonly FP64 LP3_PARALLEL_EPSILON = FP64.FromDouble(0.01);

        public FPOrcaLine[] DebugOrcaLines => _orcaLines;
        public int DebugOrcaLineCount => _orcaLineCount;
        public int DebugInfeasibleCount => _infeasibleCount;
        public int DebugNumObstLines => _numObstLines;
        public int DebugObstLineOverflowCount => _obstLineOverflowCount;
        public FPObstacleVertex[] DebugObstacles => _obstacles;
        public int DebugObstacleCount => _obstacleCount;

        // Load generation for the graph-local query desync guard (see field doc).
        public int ObstacleLoadGeneration => _obstacleLoadGeneration;

        // Selected obstacle segments from the last ComputeNewVelocity (raw buffer + count, house
        // style like DebugObstacles/DebugObstacleCount — no allocation). Snapshot of the last call:
        // entries past DebugSelectedObstacleCount are stale from earlier calls.
        public int[] DebugSelectedObstacleSegments => _obstNeighborIndices;
        public int DebugSelectedObstacleCount => _obstNeighborCount;
        public bool DebugLastObstaclePathWasGraph => _lastObstaclePathWasGraph;

        public FPNavAvoidance(FPNavTuning? tuning = null)
        {
            _tuning = tuning ?? FPNavTuning.Default;
            _tuning.Validate();

            _neighborDistSqr = new FP64[_tuning.MaxNeighbors];
            _neighborIndices = new int[_tuning.MaxNeighbors];
            _projLines = new FPOrcaLine[_tuning.MaxOrcaLines];
            _obstNeighborDistSqr = new FP64[_tuning.MaxObstLines];
            _obstNeighborIndices = new int[_tuning.MaxObstLines];

            NeighborDist = FP64.FromInt(5);
            TimeHorizon = FP64.FromInt(3);
            // Static walls do not move, so a short anticipation window suffices; a long one turns
            // every wall within TimeHorizonObst*speed+radius into a hard LP constraint and traps
            // path-following agents in corners/corridors (measured: 1.0 stalls, <=0.25 reaches).
            TimeHorizonObst = FP64.FromDouble(0.25);
            _orcaLines = new FPOrcaLine[_tuning.MaxOrcaLines];
            _orcaLineCount = 0;
        }

        /// <summary>
        /// Maintains a sorted buffer of the MAX_NEIGHBORS closest candidates.
        /// </summary>
        private void InsertNeighbor(int index, FP64 distSqr)
        {
            if (_neighborCount < _neighborIndices.Length)
            {
                // Buffer not full — insert in sorted position
                int pos = _neighborCount;
                while (pos > 0 && distSqr < _neighborDistSqr[pos - 1])
                {
                    _neighborDistSqr[pos] = _neighborDistSqr[pos - 1];
                    _neighborIndices[pos] = _neighborIndices[pos - 1];
                    pos--;
                }
                _neighborDistSqr[pos] = distSqr;
                _neighborIndices[pos] = index;
                _neighborCount++;
            }
            else if (distSqr < _neighborDistSqr[_neighborCount - 1])
            {
                // Closer than the farthest kept neighbor — replace and shift down
                int pos = _neighborCount - 1;
                while (pos > 0 && distSqr < _neighborDistSqr[pos - 1])
                {
                    _neighborDistSqr[pos] = _neighborDistSqr[pos - 1];
                    _neighborIndices[pos] = _neighborIndices[pos - 1];
                    pos--;
                }
                _neighborDistSqr[pos] = distSqr;
                _neighborIndices[pos] = index;
            }
        }

        /// <summary>
        /// Loads static obstacles from polygon rings into the flat obstacle array. Rings may be
        /// convex or non-convex; the caller guarantees the winding convention (free space on the
        /// right of each edge's unitDir): solid blocks CCW, walkable-boundary loops CW.
        /// polygonOffsets holds the start index of each ring; ring p spans
        /// [polygonOffsets[p], polygonOffsets[p+1]) (or vertices.Length for the last ring).
        /// A trailing CSR-style sentinel entry (== vertices.Length) is tolerated.
        ///
        /// <para>This form takes both arrays at their exact length, which is what a caller that
        /// built the rings itself has — procedural terrain blocks, a hand-written test fixture.
        /// The navmesh boundary path uses <see cref="LoadObstacles(FPVector2[], int, int[], int)"/>
        /// instead, because its arrays are reused across rebakes and come back oversized.</para>
        ///
        /// <para>Discrete-event scope, not per-tick: re-run whenever the geometry is replaced, which
        /// on a game with runtime rebake means every building placement. The hot path stays GC-0
        /// regardless — the allocation here is the obstacle array, and it only grows.</para>
        /// </summary>
        public void LoadObstacles(FPVector2[] vertices, int[] polygonOffsets)
            => LoadObstacles(vertices, vertices?.Length ?? 0,
                             polygonOffsets, polygonOffsets?.Length ?? 0);

        /// <summary>
        /// <inheritdoc cref="LoadObstacles(FPVector2[], int[])" path="/summary/node()[1]"/>
        ///
        /// <para>The counted form. Both arrays may be longer than the data in them, which is what
        /// lets the navmesh extractor hand over a working buffer it reuses across rebakes rather
        /// than a fresh exact-size one every time — on a large stage that is a quarter of a
        /// megabyte per building placement.</para>
        ///
        /// <para><paramref name="polygonCount"/> is the number of RINGS and does not count a
        /// trailing sentinel. The two-argument form derives both counts from the arrays' lengths,
        /// which is why a sentinel is merely tolerated there rather than expected.</para>
        /// </summary>
        /// <param name="vertexCount">Live entries in <paramref name="vertices"/>; the rest is ignored.</param>
        /// <param name="polygonCount">Number of rings in <paramref name="polygonOffsets"/>.</param>
        public void LoadObstacles(FPVector2[] vertices, int vertexCount,
            int[] polygonOffsets, int polygonCount)
        {
            _obstacleLoadGeneration++;

            if (vertices == null || vertexCount <= 0)
            {
                _obstacleCount = 0;
                return;
            }

            if (vertexCount > vertices.Length)
                throw new System.ArgumentException(
                    $"FPNavAvoidance.LoadObstacles: vertexCount {vertexCount} exceeds the array's {vertices.Length}");
            if (polygonOffsets != null && polygonCount > polygonOffsets.Length)
                throw new System.ArgumentException(
                    $"FPNavAvoidance.LoadObstacles: polygonCount {polygonCount} exceeds the array's {polygonOffsets.Length}");

            int n = vertexCount;
            if (_obstacles == null || _obstacles.Length < n)
                _obstacles = new FPObstacleVertex[n];

            bool haveRings = polygonOffsets != null && polygonCount > 0;
            int polyCount = haveRings ? polygonCount : 1;
            for (int p = 0; p < polyCount; p++)
            {
                int start = haveRings ? polygonOffsets[p] : 0;
                int end = (haveRings && p + 1 < polyCount) ? polygonOffsets[p + 1] : n;

                for (int i = start; i < end; i++)
                {
                    int next = (i + 1 < end) ? i + 1 : start;
                    int prev = (i > start) ? i - 1 : end - 1;

                    FPVector2 cur = vertices[i];
                    FPVector2 edge = vertices[next] - cur;

                    // Load-time contract checks: coordinate range, segment length.
                    System.Diagnostics.Debug.Assert(
                        FP64.Abs(cur.x) < COORD_ABS_MAX && FP64.Abs(cur.y) < COORD_ABS_MAX,
                        "FPNavAvoidance.LoadObstacles: vertex coordinate exceeds safe FP64 range");
                    System.Diagnostics.Debug.Assert(
                        edge.sqrMagnitude > EPSILON,
                        "FPNavAvoidance.LoadObstacles: degenerate (zero-length) obstacle segment");
                    // Convex (as-wound) if the turn from incoming to outgoing edge is a left turn
                    // (cross >= 0, collinear counts as convex). Non-convex (reflex) vertices are
                    // allowed — handled by ComputeObstacleOrcaLine's non-convex arms.
                    bool convex = FPVector2.Cross(cur - vertices[prev], edge) >= FP64.Zero;

                    _obstacles[i] = new FPObstacleVertex
                    {
                        point = cur,
                        unitDir = edge.normalized,
                        isConvex = convex,
                        nextIndex = next,
                        prevIndex = prev,
                        polygonIndex = p,
                    };
                }
            }

            _obstacleCount = n;
        }

        /// <summary>
        /// Maintains a sorted buffer of the MAX_OBST_LINES closest obstacle segments.
        /// Equal distances keep ascending-index order (deterministic tiebreak).
        /// </summary>
        private void InsertObstacleNeighbor(int index, FP64 distSqr)
        {
            if (_obstNeighborCount < _obstNeighborIndices.Length)
            {
                int pos = _obstNeighborCount;
                while (pos > 0 && distSqr < _obstNeighborDistSqr[pos - 1])
                {
                    _obstNeighborDistSqr[pos] = _obstNeighborDistSqr[pos - 1];
                    _obstNeighborIndices[pos] = _obstNeighborIndices[pos - 1];
                    pos--;
                }
                _obstNeighborDistSqr[pos] = distSqr;
                _obstNeighborIndices[pos] = index;
                _obstNeighborCount++;
            }
            else if (distSqr < _obstNeighborDistSqr[_obstNeighborCount - 1])
            {
                int pos = _obstNeighborCount - 1;
                while (pos > 0 && distSqr < _obstNeighborDistSqr[pos - 1])
                {
                    _obstNeighborDistSqr[pos] = _obstNeighborDistSqr[pos - 1];
                    _obstNeighborIndices[pos] = _obstNeighborIndices[pos - 1];
                    pos--;
                }
                _obstNeighborDistSqr[pos] = distSqr;
                _obstNeighborIndices[pos] = index;
            }
        }

        /// <summary>
        /// Squared distance from point c to segment [a, b] (RVO2 distSqPointLineSegment).
        /// </summary>
        private static FP64 DistSqPointSegment(FPVector2 a, FPVector2 b, FPVector2 c)
        {
            FPVector2 ab = b - a;
            FP64 abLenSqr = ab.sqrMagnitude;
            if (abLenSqr <= EPSILON)
                return (c - a).sqrMagnitude;

            FP64 r = FPVector2.Dot(c - a, ab) / abLenSqr;
            if (r < FP64.Zero)
                return (c - a).sqrMagnitude;
            if (r > FP64.One)
                return (c - b).sqrMagnitude;

            return (c - (a + ab * r)).sqrMagnitude;
        }

        /// <summary>
        /// Computes the obstacle ORCA half-plane for the segment starting at obstIdx (RVO2
        /// Agent.cpp obstacle block, full port including non-convex vertex arms). Handles both
        /// convex and non-convex (reflex) vertices; convex CCW input exercises only the convex
        /// paths. Obstacle lines take full responsibility — line.point is
        /// anchored at the cutoff geometry (no reciprocal +velocity / *0.5, unlike the agent line).
        /// Returns false when no line is produced (already covered, non-convex-ignored, degenerate,
        /// or a foreign-leg projection).
        /// </summary>
        private bool ComputeObstacleOrcaLine(FPVector2 agentPos, FPVector2 agentVel, FP64 agentRadius,
            int obstIdx, out FPOrcaLine line)
        {
            line = default;

            FP64 invTimeHorizonObst = FP64.One / TimeHorizonObst;

            FPObstacleVertex obstacle1 = _obstacles[obstIdx];
            FPObstacleVertex obstacle2 = _obstacles[obstacle1.nextIndex];

            FPVector2 relativePosition1 = obstacle1.point - agentPos;
            FPVector2 relativePosition2 = obstacle2.point - agentPos;

            // Already covered by a previously constructed obstacle ORCA line?
            for (int j = 0; j < _orcaLineCount; j++)
            {
                FP64 c1 = FPVector2.Cross(relativePosition1 * invTimeHorizonObst - _orcaLines[j].point,
                    _orcaLines[j].direction) - invTimeHorizonObst * agentRadius;
                FP64 c2 = FPVector2.Cross(relativePosition2 * invTimeHorizonObst - _orcaLines[j].point,
                    _orcaLines[j].direction) - invTimeHorizonObst * agentRadius;
                if (c1 >= -EPSILON && c2 >= -EPSILON)
                    return false;
            }

            FP64 distSq1 = relativePosition1.sqrMagnitude;
            FP64 distSq2 = relativePosition2.sqrMagnitude;
            FP64 radiusSq = agentRadius * agentRadius;

            FPVector2 obstacleVector = obstacle2.point - obstacle1.point;
            FP64 obsLenSq = obstacleVector.sqrMagnitude;
            if (obsLenSq <= EPSILON)
                return false; // degenerate segment (Choros min-thickness gate should prevent this)

            FP64 s = FPVector2.Dot(-relativePosition1, obstacleVector) / obsLenSq;
            FP64 distSqLine = (-relativePosition1 - obstacleVector * s).sqrMagnitude;

            // --- Collision cases ---
            if (s < FP64.Zero && distSq1 <= radiusSq)
            {
                // Collision with left vertex. Ignore if non-convex.
                if (obstacle1.isConvex && distSq1 > EPSILON)
                {
                    line.point = FPVector2.Zero;
                    line.direction = new FPVector2(-relativePosition1.y, relativePosition1.x).normalized;
                    return true;
                }
                return false;
            }
            else if (s > FP64.One && distSq2 <= radiusSq)
            {
                // Collision with right vertex. Ignore if non-convex or if it will be taken care of
                // by the neighboring obstacle (det >= 0 is the neighbor-yield condition, not a convex guard).
                if (obstacle2.isConvex && distSq2 > EPSILON
                    && FPVector2.Cross(relativePosition2, obstacle2.unitDir) >= FP64.Zero)
                {
                    line.point = FPVector2.Zero;
                    line.direction = new FPVector2(-relativePosition2.y, relativePosition2.x).normalized;
                    return true;
                }
                return false;
            }
            else if (s >= FP64.Zero && s <= FP64.One && distSqLine <= radiusSq)
            {
                // Collision with the segment interior — and `s <= One` rather than `s < One`
                // deliberately, because the closed end is a hole between the three guards above.
                //
                // At s == 1 the agent's projection is exactly on obstacle2, so distSqLine ==
                // distSq2 identically. The right-vertex guard needs s > 1 and this one used to need
                // s < 1, so that configuration fell through to the leg computation below, where the
                // divisor is distSq2 — zero when the agent stands ON the vertex, and smaller than
                // radiusSq when it stands inside the vertex's clearance, which reaches
                // Sqrt(negative) first. Both threw. Neither is exotic: agent positions and obstacle
                // ring vertices live on the same snap lattice, and a runtime rebake can put a new
                // hole ring where an agent already stands.
                //
                // The closed end is also what makes the two ends of a segment agree. s == 0 was
                // always caught here (`s >= Zero`), and every shared ring vertex is one segment's
                // end and the next one's start — so the same position was handled through one and
                // fatal through the other.
                line.point = FPVector2.Zero;
                line.direction = -obstacle1.unitDir;
                return true;
            }

            // --- No collision: compute legs. Non-convex vertex legs extend the cut-off line. ---
            FPVector2 leftLegDirection;
            FPVector2 rightLegDirection;
            bool obstEqual = false;

            if (s < FP64.Zero && distSqLine <= radiusSq)
            {
                // Obliquely viewed — left vertex defines the velocity obstacle. Ignore if non-convex.
                if (!obstacle1.isConvex)
                    return false;
                obstacle2 = obstacle1;
                obstEqual = true;
                FP64 leg1 = FP64.Sqrt(distSq1 - radiusSq);
                leftLegDirection = new FPVector2(
                    relativePosition1.x * leg1 - relativePosition1.y * agentRadius,
                    relativePosition1.x * agentRadius + relativePosition1.y * leg1) / distSq1;
                rightLegDirection = new FPVector2(
                    relativePosition1.x * leg1 + relativePosition1.y * agentRadius,
                    -relativePosition1.x * agentRadius + relativePosition1.y * leg1) / distSq1;
            }
            else if (s > FP64.One && distSqLine <= radiusSq)
            {
                // Obliquely viewed — right vertex defines the velocity obstacle. Ignore if non-convex.
                if (!obstacle2.isConvex)
                    return false;
                obstacle1 = obstacle2;
                obstEqual = true;
                FP64 leg2 = FP64.Sqrt(distSq2 - radiusSq);
                leftLegDirection = new FPVector2(
                    relativePosition2.x * leg2 - relativePosition2.y * agentRadius,
                    relativePosition2.x * agentRadius + relativePosition2.y * leg2) / distSq2;
                rightLegDirection = new FPVector2(
                    relativePosition2.x * leg2 + relativePosition2.y * agentRadius,
                    -relativePosition2.x * agentRadius + relativePosition2.y * leg2) / distSq2;
            }
            else
            {
                // Usual situation. Convex vertex → cone leg (sqrt); non-convex → cut-off line extension.
                if (obstacle1.isConvex)
                {
                    FP64 leg1 = FP64.Sqrt(distSq1 - radiusSq);
                    leftLegDirection = new FPVector2(
                        relativePosition1.x * leg1 - relativePosition1.y * agentRadius,
                        relativePosition1.x * agentRadius + relativePosition1.y * leg1) / distSq1;
                }
                else
                {
                    leftLegDirection = -obstacle1.unitDir;
                }

                if (obstacle2.isConvex)
                {
                    FP64 leg2 = FP64.Sqrt(distSq2 - radiusSq);
                    rightLegDirection = new FPVector2(
                        relativePosition2.x * leg2 + relativePosition2.y * agentRadius,
                        -relativePosition2.x * agentRadius + relativePosition2.y * leg2) / distSq2;
                }
                else
                {
                    // Right non-convex leg uses obstacle1.unitDir (RVO2 verbatim — not obstacle2).
                    rightLegDirection = obstacle1.unitDir;
                }
            }

            // Legs must not point into a neighboring edge (convex vertex): clamp to the neighbor's
            // cutoff line. If the velocity projects onto such a "foreign" leg, no constraint is added.
            FPObstacleVertex leftNeighbor = _obstacles[obstacle1.prevIndex];
            bool isLeftLegForeign = false;
            bool isRightLegForeign = false;

            if (obstacle1.isConvex && FPVector2.Cross(leftLegDirection, -leftNeighbor.unitDir) >= FP64.Zero)
            {
                leftLegDirection = -leftNeighbor.unitDir;
                isLeftLegForeign = true;
            }
            if (obstacle2.isConvex && FPVector2.Cross(rightLegDirection, obstacle2.unitDir) <= FP64.Zero)
            {
                rightLegDirection = obstacle2.unitDir;
                isRightLegForeign = true;
            }

            // Cut-off centers (velocity space, relative to agent).
            FPVector2 leftCutoff = (obstacle1.point - agentPos) * invTimeHorizonObst;
            FPVector2 rightCutoff = (obstacle2.point - agentPos) * invTimeHorizonObst;
            FPVector2 cutoffVec = rightCutoff - leftCutoff;

            FP64 radInvT = agentRadius * invTimeHorizonObst;

            FP64 t = obstEqual
                ? FP64.Half
                : FPVector2.Dot(agentVel - leftCutoff, cutoffVec) / cutoffVec.sqrMagnitude;
            FP64 tLeft = FPVector2.Dot(agentVel - leftCutoff, leftLegDirection);
            FP64 tRight = FPVector2.Dot(agentVel - rightCutoff, rightLegDirection);

            if ((t < FP64.Zero && tLeft < FP64.Zero) || (obstEqual && tLeft < FP64.Zero && tRight < FP64.Zero))
            {
                // Project on the left cut-off circle.
                FPVector2 w = agentVel - leftCutoff;
                FP64 wLen = w.magnitude;
                if (wLen <= EPSILON)
                    return false;
                FPVector2 unitW = w / wLen;
                line.direction = new FPVector2(unitW.y, -unitW.x);
                line.point = leftCutoff + unitW * radInvT;
                return true;
            }
            else if (t > FP64.One && tRight < FP64.Zero)
            {
                // Project on the right cut-off circle.
                FPVector2 w = agentVel - rightCutoff;
                FP64 wLen = w.magnitude;
                if (wLen <= EPSILON)
                    return false;
                FPVector2 unitW = w / wLen;
                line.direction = new FPVector2(unitW.y, -unitW.x);
                line.point = rightCutoff + unitW * radInvT;
                return true;
            }

            // Project on the left leg, right leg, or cut-off line — whichever is closest.
            FP64 distSqCutoff = (t < FP64.Zero || t > FP64.One || obstEqual)
                ? FP64.MaxValue
                : (agentVel - (leftCutoff + cutoffVec * t)).sqrMagnitude;
            FP64 distSqLeft = (tLeft < FP64.Zero)
                ? FP64.MaxValue
                : (agentVel - (leftCutoff + leftLegDirection * tLeft)).sqrMagnitude;
            FP64 distSqRight = (tRight < FP64.Zero)
                ? FP64.MaxValue
                : (agentVel - (rightCutoff + rightLegDirection * tRight)).sqrMagnitude;

            if (distSqCutoff <= distSqLeft && distSqCutoff <= distSqRight)
            {
                // Project on the cut-off line.
                line.direction = -obstacle1.unitDir;
                line.point = leftCutoff + new FPVector2(-line.direction.y, line.direction.x) * radInvT;
                return true;
            }
            else if (distSqLeft <= distSqRight)
            {
                if (isLeftLegForeign)
                    return false;
                line.direction = leftLegDirection;
                line.point = leftCutoff + new FPVector2(-line.direction.y, line.direction.x) * radInvT;
                return true;
            }
            else
            {
                if (isRightLegForeign)
                    return false;
                line.direction = -rightLegDirection;
                line.point = rightCutoff + new FPVector2(-line.direction.y, line.direction.x) * radInvT;
                return true;
            }
        }

        /// <summary>
        /// Computes the ORCA half-plane between agents using FP64 fixed-point arithmetic.
        /// </summary>
        private static bool ComputeAgentOrcaLine(FPVector2 agentVelocity, FPVector2 relPos, FPVector2 relVel,
            FP64 combinedRadius, FP64 timeHorizon, FP64 dt, out FPOrcaLine line)
        {
            line = default;

            FP64 distSqr = relPos.sqrMagnitude;
            FP64 combinedRadiusSqr = combinedRadius * combinedRadius;
            FP64 invTimeHorizon = FP64.One / timeHorizon;

            FPVector2 u;

            if (distSqr > combinedRadiusSqr)
            {
                // No collision
                FPVector2 w = relVel - relPos * invTimeHorizon;
                FP64 wLenSqr = w.sqrMagnitude;
                FP64 dotProduct = FPVector2.Dot(w, relPos);

                if (dotProduct < FP64.Zero && dotProduct * dotProduct > combinedRadiusSqr * wLenSqr)
                {
                    // Project onto cutoff circle
                    FP64 wLen = FP64.Sqrt(wLenSqr);
                    if (wLen <= EPSILON)
                        return false;

                    FPVector2 unitW = w / wLen;
                    line.direction = new FPVector2(unitW.y, -unitW.x);
                    u = unitW * (combinedRadius * invTimeHorizon - wLen);
                }
                else
                {
                    // Project onto cone legs
                    FP64 leg = FP64.Sqrt(distSqr - combinedRadiusSqr);
                    if (leg <= EPSILON)
                        return false;

                    if (FPVector2.Cross(relPos, w) > FP64.Zero)
                    {
                        // Left leg
                        line.direction = new FPVector2(
                            relPos.x * leg - relPos.y * combinedRadius,
                            relPos.x * combinedRadius + relPos.y * leg) / distSqr;
                    }
                    else
                    {
                        // Right leg
                        line.direction = -new FPVector2(
                            relPos.x * leg + relPos.y * combinedRadius,
                            -relPos.x * combinedRadius + relPos.y * leg) / distSqr;
                    }

                    FP64 dotVelDir = FPVector2.Dot(relVel, line.direction);
                    u = line.direction * dotVelDir - relVel;
                }
            }
            else
            {
                // Already colliding → separate immediately
                FP64 invDt = FP64.One / dt;
                FPVector2 w = relVel - relPos * invDt;
                FP64 wLen = w.magnitude;

                if (wLen <= EPSILON)
                {
                    // If w is zero, separate along relPos direction
                    FP64 dist = FP64.Sqrt(distSqr);
                    FPVector2 unitRelPos = dist > EPSILON
                        ? relPos / dist
                        : new FPVector2(FP64.One, FP64.Zero);
                    line.direction = new FPVector2(-unitRelPos.y, unitRelPos.x);
                    u = -unitRelPos * (combinedRadius - dist);
                }
                else
                {
                    FPVector2 unitW = w / wLen;
                    line.direction = new FPVector2(unitW.y, -unitW.x);
                    u = unitW * (combinedRadius * invDt - wLen);
                }
            }

            // ORCA: shared responsibility (1/2 each)
            line.point = agentVelocity + u * FP64.Half;
            return true;
        }

        /// <summary>
        /// Computes the ORCA avoidance velocity based on NavAgentComponent.
        /// Brute-force obstacle path (source-agnostic: scans every loaded segment). Overload for
        /// callers without a graph-local candidate set (existing tests, non-navmesh sources).
        /// </summary>
        public FPVector2 ComputeNewVelocity(int agentIdx, ref Frame frame, EntityRef[] entities, int entityCount, FP64 dt)
            => ComputeNewVelocity(agentIdx, ref frame, entities, entityCount, dt, null, 0, false);

        /// <summary>
        /// Computes the ORCA avoidance velocity, selecting obstacle segments from either a
        /// graph-local candidate set or a brute-force scan. When <paramref name="graphQueried"/> is
        /// true, only <paramref name="candidateSegs"/>[0..<paramref name="candidateCount"/>) are
        /// considered (the caller — FPNavAgentSystem — pre-selected them by navmesh topology); an
        /// empty candidate set then means "no walls in range → no obstacle lines", NOT a fallback
        /// (a genuinely open agent must not regress to brute-force phantom walls). When false, every
        /// loaded segment is scanned (source-agnostic fallback). Both paths share the exact
        /// point-segment gate, nearest-first sort (InsertObstacleNeighbor), and line generation.
        /// </summary>
        public FPVector2 ComputeNewVelocity(int agentIdx, ref Frame frame, EntityRef[] entities, int entityCount, FP64 dt,
            int[] candidateSegs, int candidateCount, bool graphQueried)
        {
            ref var agent = ref frame.Get<NavAgentComponent>(entities[agentIdx]);
            _orcaLineCount = 0;

            FP64 neighborDistSqrMax = NeighborDist * NeighborDist;
            FPVector2 agentPosXZ = agent.Position.ToXZ();

            // --- Obstacle ORCA lines (must precede agent lines: LP3 keeps obstacle lines hard) ---
            _numObstLines = 0;
            if (_obstacleCount > 0)
            {
                _lastObstaclePathWasGraph = graphQueried;
                FP64 obstRange = TimeHorizonObst * agent.Speed + agent.Radius;
                FP64 obstRangeSqr = obstRange * obstRange;

                // Select the closest obstacle segments (nearest-first, RVO2 order). The exact
                // point-segment gate is applied here on both paths; the graph path only narrows
                // WHICH segments are considered (graph-reachable ones), never how they are gated.
                _obstNeighborCount = 0;
                if (graphQueried)
                {
                    for (int c = 0; c < candidateCount; c++)
                    {
                        int i = candidateSegs[c];
                        FP64 distSqr = DistSqPointSegment(_obstacles[i].point,
                            _obstacles[_obstacles[i].nextIndex].point, agentPosXZ);
                        if (distSqr > obstRangeSqr)
                            continue;
                        InsertObstacleNeighbor(i, distSqr);
                    }
                }
                else
                {
                    for (int i = 0; i < _obstacleCount; i++)
                    {
                        FP64 distSqr = DistSqPointSegment(_obstacles[i].point,
                            _obstacles[_obstacles[i].nextIndex].point, agentPosXZ);
                        if (distSqr > obstRangeSqr)
                            continue;
                        InsertObstacleNeighbor(i, distSqr);
                    }
                }

                // Effective obstacle clearance: subtract the bake inset the boundary already
                // carries (see ObstacleRadiusInset). Selection range above stays at the full
                // agent.Radius on purpose — over-selection is safe, and it keeps the AgentSystem
                // graph-expansion radius coupling untouched. Line MATH is unchanged: the corrected
                // radius is passed as the call argument, the RVO2-verbatim body never changes.
                FP64 effObstRadius = agent.Radius - ObstacleRadiusInset;
                if (effObstRadius < FP64.Zero)
                    effObstRadius = FP64.Zero;

                for (int n = 0; n < _obstNeighborCount; n++)
                {
                    if (_orcaLineCount >= _tuning.MaxObstLines)
                    {
                        _obstLineOverflowCount++;
                        break;
                    }

                    if (ComputeObstacleOrcaLine(agentPosXZ, agent.Velocity, effObstRadius,
                        _obstNeighborIndices[n], out FPOrcaLine obstLine))
                    {
                        _orcaLines[_orcaLineCount++] = obstLine;
                    }
                }

                _numObstLines = _orcaLineCount;
            }

            // Select up to MAX_NEIGHBORS closest neighbors
            _neighborCount = 0;
            for (int i = 0; i < entityCount; i++)
            {
                if (i == agentIdx)
                    continue;

                ref var other = ref frame.Get<NavAgentComponent>(entities[i]);
                FP64 distSqr = (other.Position.ToXZ() - agentPosXZ).sqrMagnitude;

                if (distSqr > neighborDistSqrMax)
                    continue;

                InsertNeighbor(i, distSqr);
            }

            // Build ORCA lines from selected neighbors
            for (int n = 0; n < _neighborCount && _orcaLineCount < _tuning.MaxOrcaLines; n++)
            {
                int i = _neighborIndices[n];
                ref var other = ref frame.Get<NavAgentComponent>(entities[i]);

                FP64 combinedRadius = agent.Radius + other.Radius;
                FPVector2 relPos = other.Position.ToXZ() - agentPosXZ;
                if (relPos.sqrMagnitude <= EPSILON)
                {
                    FP64 fallbackDistance = combinedRadius * COINCIDENT_FACTOR;
                    relPos = i < agentIdx
                        ? new FPVector2(-fallbackDistance, FP64.Zero)
                        : new FPVector2(fallbackDistance, FP64.Zero);
                }

                FPVector2 relVel = agent.Velocity - other.Velocity;

                if (ComputeAgentOrcaLine(agent.Velocity, relPos, relVel, combinedRadius, TimeHorizon, dt,
                    out FPOrcaLine line))
                {
                    _orcaLines[_orcaLineCount++] = line;
                }
            }

            FPVector2 result = FPVector2.Zero;
            int lineFail = LinearProgram2D(_orcaLines, _orcaLineCount, agent.Speed,
                agent.DesiredVelocity, false, EPSILON, ref result);
            if (lineFail < _orcaLineCount)
            {
                _infeasibleCount++;
                LinearProgram3(lineFail, agent.Speed, _numObstLines, ref result);
            }
            return result;
        }

        /// <summary>
        /// 2D linear program (RVO2 linearProgram2): finds the velocity closest to optVelocity
        /// that satisfies all half-planes, using incremental constraint addition.
        /// Returns the index of the first failing line, or count when all constraints hold.
        /// </summary>
        private static int LinearProgram2D(FPOrcaLine[] lines, int count, FP64 maxSpeed,
            FPVector2 optVelocity, bool directionOpt, FP64 parallelEpsilon, ref FPVector2 result)
        {
            if (directionOpt)
            {
                // optVelocity is a unit direction — maximize along it
                result = optVelocity * maxSpeed;
            }
            else
            {
                result = optVelocity;

                // Max speed constraint (circular)
                FP64 maxSpeedSqr = maxSpeed * maxSpeed;
                if (result.sqrMagnitude > maxSpeedSqr)
                {
                    result = result.normalized * maxSpeed;
                }
            }

            // Add half-plane constraints one by one
            for (int i = 0; i < count; i++)
            {
                // Left normal of direction = Perpendicular
                // det < 0 → result violates the half-plane
                FP64 det = FPVector2.Cross(lines[i].direction, result - lines[i].point);

                if (det < FP64.Zero)
                {
                    FPVector2 tempResult = result;
                    if (!ProjectOntoOrcaLine(lines, i, maxSpeed, optVelocity, directionOpt,
                        parallelEpsilon, ref result))
                    {
                        result = tempResult;
                        return i;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Projects onto an ORCA line while satisfying all previous constraints and max speed.
        /// RVO2 linearProgram1 approach: clamps to [tLeft, tRight] range.
        /// Returns false when the constraint is infeasible (result left unchanged).
        /// </summary>
        private static bool ProjectOntoOrcaLine(FPOrcaLine[] lines, int lineIdx, FP64 maxSpeed,
            FPVector2 optVelocity, bool directionOpt, FP64 parallelEpsilon, ref FPVector2 result)
        {
            ref FPOrcaLine line = ref lines[lineIdx];

            // Intersection range [tLeft, tRight] of the line with the max-speed circle
            FP64 dotProduct = FPVector2.Dot(line.point, line.direction);
            FP64 discriminant = dotProduct * dotProduct
                + maxSpeed * maxSpeed - line.point.sqrMagnitude;

            if (discriminant < FP64.Zero)
            {
                // Line does not intersect the speed circle → no valid projection
                return false;
            }

            FP64 sqrtDisc = FP64.Sqrt(discriminant);
            FP64 tLeft = -dotProduct - sqrtDisc;
            FP64 tRight = -dotProduct + sqrtDisc;

            // Narrow [tLeft, tRight] using previous constraints (0..lineIdx-1)
            for (int j = 0; j < lineIdx; j++)
            {
                FP64 denom = FPVector2.Cross(line.direction, lines[j].direction);
                FP64 numer = FPVector2.Cross(lines[j].direction,
                    line.point - lines[j].point);

                if (FP64.Abs(denom) <= parallelEpsilon)
                {
                    // Parallel constraints — ignore if same direction, infeasible if opposite
                    if (numer < FP64.Zero)
                        return false;
                    continue;
                }

                FP64 tLine = numer / denom;

                if (denom >= FP64.Zero)
                {
                    // Constraint j narrows the right boundary
                    if (tLine < tRight)
                        tRight = tLine;
                }
                else
                {
                    // Constraint j narrows the left boundary
                    if (tLine > tLeft)
                        tLeft = tLine;
                }

                if (tLeft > tRight)
                    return false;
            }

            if (directionOpt)
            {
                // Maximize along the optVelocity direction (RVO2 linearProgram1 directionOpt)
                result = FPVector2.Dot(optVelocity, line.direction) > FP64.Zero
                    ? line.point + line.direction * tRight
                    : line.point + line.direction * tLeft;
            }
            else
            {
                // Clamp the projection of result onto line to [tLeft, tRight]
                FP64 t = FPVector2.Dot(line.direction, result - line.point);

                if (t < tLeft)
                    t = tLeft;
                else if (t > tRight)
                    t = tRight;

                result = line.point + line.direction * t;
            }

            return true;
        }

        /// <summary>
        /// RVO2 linearProgram3: when the 2D program is infeasible, relaxes to the velocity
        /// that minimizes the maximum constraint violation (progressive bisector projection).
        /// Obstacle lines [0, numObstLines) are hard constraints — copied verbatim into the
        /// projected set (never relaxed to a bisector), so the agent cannot penetrate walls.
        /// </summary>
        private void LinearProgram3(int beginLine, FP64 maxSpeed, int numObstLines, ref FPVector2 result)
        {
            FP64 distance = FP64.Zero;

            for (int i = beginLine; i < _orcaLineCount; i++)
            {
                // Skip lines whose violation is within the current maximum
                if (FPVector2.Cross(_orcaLines[i].direction, _orcaLines[i].point - result) <= distance)
                    continue;

                int projCount = 0;

                // Obstacle lines are hard constraints: always seed the projected set with the full
                // [0, numObstLines) verbatim, independent of i. i can be < numObstLines when the
                // obstacle constraints are themselves infeasible; seeding only [0, i) there would
                // drop obstacle lines [i, numObstLines) and let LP3 relax them. Matches RVO2
                // linearProgram3, which seeds projLines with the first numObstLines lines.
                for (int j = 0; j < numObstLines; j++)
                    _projLines[projCount++] = _orcaLines[j];

                // Agent lines [numObstLines, i): relax to the bisector between line i and line j.
                for (int j = numObstLines; j < i; j++)
                {
                    FP64 determinant = FPVector2.Cross(_orcaLines[i].direction, _orcaLines[j].direction);
                    FPOrcaLine line;

                    if (FP64.Abs(determinant) <= LP3_PARALLEL_EPSILON)
                    {
                        // Parallel lines — skip if same direction, midpoint if opposing
                        if (FPVector2.Dot(_orcaLines[i].direction, _orcaLines[j].direction) > FP64.Zero)
                            continue;

                        line.point = (_orcaLines[i].point + _orcaLines[j].point) * FP64.Half;
                    }
                    else
                    {
                        line.point = _orcaLines[i].point + _orcaLines[i].direction *
                            (FPVector2.Cross(_orcaLines[j].direction,
                                _orcaLines[i].point - _orcaLines[j].point) / determinant);
                    }

                    // Bisector: boundary where violation(j) <= violation(i)
                    line.direction = (_orcaLines[j].direction - _orcaLines[i].direction).normalized;
                    _projLines[projCount++] = line;
                }

                FPVector2 tempResult = result;
                FPVector2 optDir = new FPVector2(-_orcaLines[i].direction.y, _orcaLines[i].direction.x);
                if (LinearProgram2D(_projLines, projCount, maxSpeed, optDir, true,
                    LP3_PARALLEL_EPSILON, ref result) < projCount)
                {
                    // Numerical corner: keep the previous best result (RVO2 verbatim)
                    result = tempResult;
                }

                distance = FPVector2.Cross(_orcaLines[i].direction, _orcaLines[i].point - result);
            }
        }

    }
}
