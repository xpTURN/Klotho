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

        public const int MAX_CORRIDOR = 128;
        public const int MAX_ITERATIONS = 4096;

        public FPNavMeshPathfinder(FPNavMesh navMesh, FPNavMeshQuery query, IKLogger logger)
        {
            _navMesh = navMesh;
            _query = query;
            _logger = logger;

            int triCount = navMesh.Triangles.Length;
            _openSet = new FPNavMeshBinaryHeap(triCount);
            _gScores = new FP64[triCount];
            _cameFrom = new int[triCount];
            _closed = new bool[triCount];
            _nodeGeneration = new int[triCount];
            _entryPoints = new FPVector2[triCount];
            _generation = 0;
            _corridor = new int[MAX_CORRIDOR];
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

            // Triangle lookup that considers Y height
            int startTri = _query.FindTriangle(start.ToXZ(), start.y);
            int endTri = _query.FindTriangle(end.ToXZ(), end.y);

            if (startTri < 0 || endTri < 0)
            {
                if (startTri < 0)
                    _logger?.KError($"[FindPath] start={start} is outside NavMesh (startTri=-1)");

                if (endTri < 0)
                    _logger?.KError($"[FindPath] end={end} is outside NavMesh (endTri=-1)");

                return false;
            }

            if (_navMesh.Triangles[startTri].isBlocked || _navMesh.Triangles[endTri].isBlocked)
                return false;

            if ((areaMask & _navMesh.Triangles[startTri].areaMask) == 0 ||
                (areaMask & _navMesh.Triangles[endTri].areaMask) == 0)
                return false;

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

            while (_openSet.Count > 0 && iterations < MAX_ITERATIONS)
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
                    if ((areaMask & _navMesh.Triangles[neighbor].areaMask) == 0)
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

            int skip = totalLength > MAX_CORRIDOR ? totalLength - MAX_CORRIDOR : 0;

            int count = 0;
            int index = 0;
            node = endTri;
            while (node >= 0 && count < MAX_CORRIDOR)
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
