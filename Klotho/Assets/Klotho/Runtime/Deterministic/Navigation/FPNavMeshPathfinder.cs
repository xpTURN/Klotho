using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// A* triangle graph search.
    /// Holds an FPNavMesh reference and performs zero-GC search using pre-allocated arrays.
    /// </summary>
    public class FPNavMeshPathfinder
    {
        private readonly FPNavMesh _navMesh;
        private readonly FPNavMeshQuery _query;
        private readonly ILogger _logger;

        // Pre-allocated A* buffers
        private FPNavMeshBinaryHeap _openSet;
        private readonly FP64[] _gScores;
        private readonly int[] _cameFrom;
        private readonly bool[] _closed;
        private readonly int[] _nodeGeneration;
        private readonly FPVector2[] _entryPoints;
        private int _generation;

        // Corridor result buffer
        private readonly int[] _corridor;

        public const int MAX_CORRIDOR = 128;
        public const int MAX_ITERATIONS = 4096;

        public FPNavMeshPathfinder(FPNavMesh navMesh, FPNavMeshQuery query, ILogger logger)
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
                    _logger?.ZLogError($"[FindPath] start={start} is outside NavMesh (startTri=-1)");

                if (endTri < 0)
                    _logger?.ZLogError($"[FindPath] end={end} is outside NavMesh (endTri=-1)");

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
            // Record into _corridor in reverse order, then reverse
            int count = 0;
            int current = endTri;

            while (current >= 0 && count < MAX_CORRIDOR)
            {
                _corridor[count] = current;
                count++;
                current = _cameFrom[current];
            }

            // Reverse (returns the collected partial path even on overflow)
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
