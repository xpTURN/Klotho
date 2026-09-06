using System;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// A* triangle graph search.
    /// Holds an FPNavMesh reference and performs zero-GC search using pre-allocated arrays.
    /// </summary>
    public class FPNavMeshPathfinder
    {
        private FPNavMesh _navMesh;
        private readonly FPNavMeshQuery _query;
        private readonly IKLogger _logger;

        // Pre-allocated A* buffers
        private FPNavMeshBinaryHeap _openSet;
        private FP64[] _gScores;
        private int[] _cameFrom;
        private bool[] _closed;
        private int[] _nodeGeneration;
        private FPVector2[] _entryPoints;
        private int _generation;

        // Corridor result buffer
        private readonly int[] _corridor;

        /// <remarks><b>Defaults, not this instance's values</b> — the search is sized from
        /// <see cref="FPNavTuning.CorridorCap"/> and <see cref="FPNavTuning.MaxIterations"/>.
        /// <c>MAX_CORRIDOR</c> is also the compile-time ceiling for the search buffer, which the
        /// cap is validated against.</remarks>
        public const int MAX_CORRIDOR = 128;
        /// <inheritdoc cref="MAX_CORRIDOR"/>
        public const int MAX_ITERATIONS = 4096;

        // Diagnostic counters. Deliberately never reset: they are totals, and a caller that wants
        // a delta takes two readings. What "lifetime" means across a navmesh swap depends on which
        // overload the swap went through, and the two differ — SwapNavMesh(mesh) rebinds THIS
        // instance (Rebind resizes buffers and leaves these alone), so the totals carry across a
        // runtime rebake, which is the path FPNavAgentInstaller takes and what you want in a match
        // that rebakes repeatedly. The four-argument overload installs a caller-built pathfinder,
        // so the totals start again from zero there. FPNavAgentSystem's own counter survives both,
        // being on the system rather than here.
        private int _corridorTruncatedCount;
        private int _iterationExhaustedCount;
        private int _blockedEndpointCount;
        private int _areaMaskRejectedCount;
        private int _maskedStartCount;

        /// <summary>
        /// Diagnostic: paths whose triangle chain was longer than <see cref="MAX_CORRIDOR"/> and
        /// therefore returned truncated (the end-side triangles are dropped, so the agent runs off
        /// the corridor end and repaths). Accumulated over this instance's lifetime, never reset;
        /// outside the state hash.
        /// </summary>
        public int DebugCorridorTruncatedCount => _corridorTruncatedCount;

        /// <summary>
        /// Diagnostic: <c>FindPath</c> calls that failed with search work still queued — i.e. the
        /// <see cref="MAX_ITERATIONS"/> budget ran out rather than the graph being exhausted. This
        /// is the half of the silent <c>false</c> that means "ask again with a shorter path", as
        /// opposed to "there is genuinely no route". Accumulated over this instance's lifetime.
        /// </summary>
        public int DebugIterationExhaustedCount => _iterationExhaustedCount;

        /// <summary>
        /// Diagnostic: calls rejected because the start or end triangle is flagged
        /// <c>isBlocked</c>. This is the one silent failure a game produces on purpose — nothing in
        /// the engine sets that flag, and the rebaker carves geometry away rather than blocking it,
        /// so a non-zero count means someone closed a triangle through
        /// <c>FPNavMesh.TrianglesMutable</c> (a gate, a door) and units are now being ordered
        /// through it. The search never starts, so this is not a budget problem.
        /// </summary>
        public int DebugBlockedEndpointCount => _blockedEndpointCount;

        /// <summary>
        /// Diagnostic: calls rejected because the requested <c>areaMask</c> shares no bit with the
        /// <b>end</b> triangle — a destination on ground the mask forbids.
        ///
        /// <para>The START is not counted here and no longer refuses the call at all; it is exempt,
        /// and <see cref="DebugMaskedStartCount"/> reports it instead. Before that exemption this
        /// counter conflated the two, and the start case is the one a game is likely to care about
        /// (a unit standing on ground it may not use), so they are split.</para>
        /// </summary>
        public int DebugAreaMaskRejectedCount => _areaMaskRejectedCount;

        /// <summary>
        /// Diagnostic: searches that STARTED on ground the requested <c>areaMask</c> forbids.
        ///
        /// <para>Not a failure — the search runs, and the escape rule lets it leave the forbidden
        /// region it began in. It is here because the alternative is silence: the agent gets an
        /// ordinary route out and nothing in the result says it was ever stuck inside a building.
        /// A game that wants to notice that (to play a rescue, to refund, to warn) has this and
        /// nothing else. Same conventions as the other counters — outside the state hash, the wire
        /// and replay, never reset, and a resimulated tick counts again.</para>
        /// </summary>
        public int DebugMaskedStartCount => _maskedStartCount;

        private readonly FPNavTuning _tuning;

        /// <summary>The sizes this instance was built with (see <see cref="FPNavTuning"/>).</summary>
        public FPNavTuning Tuning => _tuning;

        public FPNavMeshPathfinder(FPNavMesh navMesh, FPNavMeshQuery query, IKLogger logger,
            FPNavTuning? tuning = null)
        {
            _navMesh = navMesh;
            _query = query;
            _logger = logger;

            _tuning = tuning ?? FPNavTuning.Default;
            _tuning.Validate();

            int triCount = navMesh.Triangles.Length;
            _openSet = new FPNavMeshBinaryHeap(triCount);
            _gScores = new FP64[triCount];
            _cameFrom = new int[triCount];
            _closed = new bool[triCount];
            _nodeGeneration = new int[triCount];
            _entryPoints = new FPVector2[triCount];
            _generation = 0;
            _corridor = new int[_tuning.CorridorCap];
        }

        /// <summary>
        /// Points this pathfinder at a different mesh, keeping the working arrays.
        /// Part of a navmesh swap — see FPNavAgentSystem.SwapNavMesh, which is the only thing
        /// that should call it.
        /// </summary>
        internal void Rebind(FPNavMesh newMesh)
        {
            _navMesh = newMesh;

            int triCount = newMesh.Triangles.Length;
            if (triCount > _gScores.Length)
            {
                // All of these were sized together, so they run out together. The heap is rebuilt
                // through its constructor rather than patched: it fills _positions with -1, and a
                // hand-grown one would be all zeros — which Contains reads as "in the heap at
                // slot 0", making every triangle look like it is already queued.
                var openSet = new FPNavMeshBinaryHeap(triCount);
                var gScores = new FP64[triCount];
                var cameFrom = new int[triCount];
                var closed = new bool[triCount];
                var nodeGeneration = new int[triCount];
                var entryPoints = new FPVector2[triCount];

                // Allocate first, install after — see FPNavMeshQuery.Rebind.
                _openSet = openSet;
                _gScores = gScores;
                _cameFrom = cameFrom;
                _closed = closed;
                _nodeGeneration = nodeGeneration;
                _entryPoints = entryPoints;
            }

            // _generation is deliberately left alone — see FPNavMeshQuery.Rebind for why. Reset()
            // pre-increments and the constructor starts at 0, so 0 is the permanent "never
            // touched" sentinel; a grown array reads as untouched for free.
        }

        /// <summary>
        /// A* pathfinding. Returns the corridor (triangle index sequence).
        /// </summary>
        /// <param name="start">Start 3D position</param>
        /// <param name="end">Target 3D position</param>
        /// <param name="areaMask">Area filter mask</param>
        /// <param name="corridor">Resulting corridor array. Warning: this is a reference to the internal buffer and is overwritten on the next FindPath call. Consume it immediately or copy it.</param>
        /// <param name="corridorLength">Corridor length</param>
        /// <returns>Whether a path was found</returns>
        public bool FindPath(FPVector3 start, FPVector3 end, int areaMask,
            out int[] corridor, out int corridorLength)
        {
            corridor = _corridor;
            corridorLength = 0;

            // Triangle lookup that considers Y height. The END breaks height ties toward ground
            // this query may use: a snapped destination sits on a triangle EDGE, which belongs to
            // both neighbours at the same interpolated height, and the plain lookup would hand back
            // whichever has the lower index — refusing, half the time, a destination the projection
            // had just certified as walkable. The tie is broken only between candidates on the SAME
            // SURFACE, so the multi-floor answer is untouched (two floors are equidistant at the
            // midpoint between them, and that is not a tie this rule takes), and where every
            // same-surface candidate is passable it degenerates to the plain lookup's answer.
            //
            // The START keeps the plain lookup on purpose: its mask check is an exemption whose
            // whole value is being reported (DebugMaskedStartCount), and resolving an ambiguous
            // start toward walkable ground would silence that at exactly the boundary positions it
            // exists to report.
            // Moving either lookup changes the corridor for unchanged inputs — bump
            // FPNavAgentSystem.NAV_BEHAVIOUR_REVISION with it, or old and new builds diverge silently.
            int startTri = _query.FindTriangle(start.ToXZ(), start.y);
            int endTri = _query.FindTriangleForEndpoint(end.ToXZ(), end.y, areaMask);

            if (startTri < 0 || endTri < 0)
            {
                if (startTri < 0)
                    _logger?.KError($"[FindPath] start={start} is outside NavMesh (startTri=-1)");

                if (endTri < 0)
                    _logger?.KError($"[FindPath] end={end} is outside NavMesh (endTri=-1)");

                return false;
            }

            // The two rejections below happen before the search starts, so counting them costs
            // nothing on the hot path — and without the counters they are indistinguishable from
            // "no route exists", which is the failure they most look like from the outside.
            if (_navMesh.Triangles[startTri].isBlocked || _navMesh.Triangles[endTri].isBlocked)
            {
                _blockedEndpointCount++;
                return false;
            }

            // The END is still refused: a destination on ground the mask forbids has no answer,
            // and that is the half of the filter callers rely on.
            if ((areaMask & _navMesh.Triangles[endTri].areaMask) == 0)
            {
                _areaMaskRejectedCount++;
                return false;
            }

            // The START is exempt, matching the walk, which never gates the triangle an agent is
            // already standing on. Refusing it produced a unit that could not be given a route out
            // of ground it had been placed on — a building dropped on top of it, most concretely —
            // and since the agent system only walks along a corridor, no corridor meant no
            // movement at all. Counted separately so the situation stays observable: the endpoint
            // counter above would otherwise fall silent on the one case a game most wants to see.
            if ((areaMask & _navMesh.Triangles[startTri].areaMask) == 0)
                _maskedStartCount++;

            // Same triangle
            if (startTri == endTri)
            {
                _corridor[0] = startTri;
                corridorLength = 1;
                return true;
            }

            // A* initialization
            Reset();

            TouchNode(startTri);
            _entryPoints[startTri] = start.ToXZ();
            FP64 h = FPVector2.Distance(start.ToXZ(), end.ToXZ());
            _gScores[startTri] = FP64.Zero;
            _cameFrom[startTri] = -1;
            _openSet.Push(startTri, h);

            int iterations = 0;

            while (_openSet.Count > 0 && iterations < _tuning.MaxIterations)
            {
                iterations++;
                int current = _openSet.Pop();

                if (current == endTri)
                {
                    corridorLength = ReconstructCorridor(current);
                    return corridorLength > 0;
                }

                _closed[current] = true;

                // Iterate in neighbor0, neighbor1, neighbor2 order (deterministic)
                for (int e = 0; e < 3; e++)
                {
                    int neighbor = _navMesh.Triangles[current].GetNeighbor(e);
                    if (neighbor < 0)
                        continue;
                    if (IsClosed(neighbor))
                        continue;
                    if (_navMesh.Triangles[neighbor].isBlocked)
                        continue;
                    // The escape rule, identical to the walk's (FPNavMeshQuery.MoveAlongSurface):
                    // a neighbour the mask refuses is still expandable when the node we expand FROM
                    // is refused too, so a path can leave forbidden ground but never enter it.
                    //
                    // Admissibility therefore depends on the parent, which normally breaks a closed
                    // set — the same node would be reachable through one parent and not another.
                    // It does not break here, and the reason is worth keeping: the rule forbids
                    // accepted -> refused, so a refused node is only ever reached from a refused
                    // parent, chaining back to the start. The refused nodes in the search are
                    // exactly the start's own connected refused component, and every accepted
                    // node's admissibility is parent-independent as before. No (node, inside) state
                    // pairing is needed. Observed by
                    // FPNavMaskedStartEscapeTests.E7_TheEscapeIsConfinedToTheRegionYouStandIn.
                    if ((areaMask & _navMesh.Triangles[neighbor].areaMask) == 0
                        && (areaMask & _navMesh.Triangles[current].areaMask) != 0)
                        continue;

                    TouchNode(neighbor);

                    _navMesh.Triangles[current].GetEdgeVertices(e, out int va, out int vb);
                    FPVector2 edgeMid = (_navMesh.Vertices[va].ToXZ() + _navMesh.Vertices[vb].ToXZ()) * FP64.Half;

                    FP64 edgeCost = FPVector2.Distance(_entryPoints[current], edgeMid)
                        * _navMesh.Triangles[neighbor].costMultiplier;

                    FP64 tentativeG = _gScores[current] + edgeCost;

                    if (_openSet.Contains(neighbor))
                    {
                        if (tentativeG < _gScores[neighbor])
                        {
                            _gScores[neighbor] = tentativeG;
                            _cameFrom[neighbor] = current;
                            _entryPoints[neighbor] = edgeMid;
                            FP64 f = tentativeG + FPVector2.Distance(edgeMid, end.ToXZ());
                            _openSet.DecreaseKey(neighbor, f);
                        }
                    }
                    else
                    {
                        _gScores[neighbor] = tentativeG;
                        _cameFrom[neighbor] = current;
                        _entryPoints[neighbor] = edgeMid;
                        FP64 f = tentativeG + FPVector2.Distance(edgeMid, end.ToXZ());
                        _openSet.Push(neighbor, f);
                    }
                }
            }

            // Budget exhaustion vs a genuinely exhausted graph. The discriminator is the OPEN SET,
            // not the iteration count: a search whose last pop empties the set on exactly the
            // MAX_ITERATIONS-th iteration completed its work and found nothing, and counting that
            // as a truncation would report a budget problem that does not exist.
            if (_openSet.Count > 0)
                _iterationExhaustedCount++;

            return false;
        }

        private void Reset()
        {
            _openSet.Clear();
            _generation++;
            if (_generation == int.MaxValue)
            {
                // Generation 0 is the permanent "never touched" sentinel, so wrap to 1, not 0.
                // Only _nodeGeneration needs clearing — TouchNode rewrites the other four
                // whenever it sees a generation mismatch, so they follow from this one.
                Array.Clear(_nodeGeneration, 0, _nodeGeneration.Length);
                _generation = 1;
            }
        }


        private void TouchNode(int idx)
        {
            if (_nodeGeneration[idx] != _generation)
            {
                _nodeGeneration[idx] = _generation;
                _gScores[idx] = FP64.MaxValue;
                _cameFrom[idx] = -1;
                _closed[idx] = false;
                _entryPoints[idx] = FPVector2.Zero;
            }
        }

        private bool IsClosed(int idx)
        {
            return _nodeGeneration[idx] == _generation && _closed[idx];
        }

        private int ReconstructCorridor(int endTri)
        {
            // cameFrom walks end -> start. Count the full chain first so that, on overflow,
            // we can skip the triangles nearest the destination and keep the ones nearest
            // the agent's actual start triangle instead. Keeping the end-side segment (the
            // previous behavior) produces a corridor that never touches the agent's current
            // triangle, which desyncs corridor-advance tracking in FPNavAgentSystem.
            // The totalLength bound guards against a malformed (cyclic) cameFrom chain.
            int totalLength = 0;
            int node = endTri;
            while (node >= 0 && totalLength <= _cameFrom.Length)
            {
                totalLength++;
                node = _cameFrom[node];
            }

            int corridorCap = _tuning.CorridorCap;
            int skip = totalLength > corridorCap ? totalLength - corridorCap : 0;
            if (skip > 0)
                _corridorTruncatedCount++;

            int count = 0;
            int index = 0;
            node = endTri;
            while (node >= 0 && count < corridorCap)
            {
                if (index >= skip)
                {
                    _corridor[count] = node;
                    count++;
                }
                index++;
                node = _cameFrom[node];
            }

            // Reverse into start -> end order (returns the collected partial path even on overflow)
            for (int i = 0; i < count / 2; i++)
            {
                int tmp = _corridor[i];
                _corridor[i] = _corridor[count - 1 - i];
                _corridor[count - 1 - i] = tmp;
            }

            return count;
        }
    }
}
