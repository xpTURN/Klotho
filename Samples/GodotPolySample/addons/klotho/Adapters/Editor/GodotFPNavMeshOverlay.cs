// Scene overlay for the FPNavMesh visualizer. Static geometry and dynamic (path/agent) geometry
// live in two MeshInstance3D nodes (ImmediateMesh) parented under the edited scene root. Text
// labels are collected here and drawn by the controller via _forward_3d_force_draw_over_viewport.
#if TOOLS
using System.Collections.Generic;

using global::Godot;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;

namespace xpTURN.Klotho.Godot
{
    internal class GodotFPNavMeshOverlay
    {
        // Geometry layer
        public bool ShowTriangles = true;
        public bool ShowEdges = true;
        public bool ShowBoundaryEdges = true;
        public bool ShowObstacleRings;
        public bool ShowVertices;
        public bool ShowTriangleIndices;
        public bool ShowTriangleCenters;
        public bool ShowBlockedTriangles = true;
        public bool ShowCostHeatmap;

        // Path layer
        public bool ShowCorridor = true;
        public bool ShowWaypoints = true;
        public bool ShowPortals;
        public bool ShowStartEndMarkers = true;

        // Grid layer
        public bool ShowGrid;
        public bool ShowGridLabels;

        // Agent layer
        public bool ShowAgents = true;
        public bool ShowAgentPaths = true;
        public bool ShowAgentVelocities = true;
        public bool ShowOrcaLines;

        // Hover info (set by interaction)
        public int HoveredTriangleIndex = -1;
        public (int col, int row) HoveredCell = (-1, -1);

        private GodotFPNavMeshVisualizerData _data;
        private GodotFPNavMeshAgentSimulator _agentSim;

        private MeshInstance3D _staticNode;
        private MeshInstance3D _dynamicNode;
        private ImmediateMesh _staticMesh;
        private ImmediateMesh _dynamicMesh;
        private StandardMaterial3D _fillMat;
        private StandardMaterial3D _lineMat;

        // Labels collected for 2D viewport draw (worldPos, text). Rebuilt with the meshes.
        private readonly List<(Vector3 pos, string text)> _staticLabels = new List<(Vector3, string)>();
        private readonly List<(Vector3 pos, string text)> _dynamicLabels = new List<(Vector3, string)>();

        public void SetData(GodotFPNavMeshVisualizerData data) => _data = data;
        public void SetAgentSimulator(GodotFPNavMeshAgentSimulator sim) => _agentSim = sim;

        public IEnumerable<(Vector3 pos, string text)> Labels
        {
            get
            {
                foreach (var l in _staticLabels) yield return l;
                foreach (var l in _dynamicLabels) yield return l;
            }
        }

        public void Attach(Node parent)
        {
            if (parent == null || _staticNode != null) return;

            _fillMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                VertexColorUseAsAlbedo = true,
            };
            _fillMat.SetFlag(BaseMaterial3D.Flags.DisableDepthTest, true);

            _lineMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                VertexColorUseAsAlbedo = true,
            };
            _lineMat.SetFlag(BaseMaterial3D.Flags.DisableDepthTest, true);

            _staticMesh = new ImmediateMesh();
            _dynamicMesh = new ImmediateMesh();

            _staticNode = new MeshInstance3D { Mesh = _staticMesh, Name = "FPNavMeshOverlayStatic" };
            _dynamicNode = new MeshInstance3D { Mesh = _dynamicMesh, Name = "FPNavMeshOverlayDynamic" };

            parent.AddChild(_staticNode);
            parent.AddChild(_dynamicNode);
            // Editor-temporary nodes: keep owner=null so they are not serialized into the scene.
            _staticNode.Owner = null;
            _dynamicNode.Owner = null;
        }

        public void Detach()
        {
            _staticNode?.QueueFree();
            _dynamicNode?.QueueFree();
            _staticNode = null;
            _dynamicNode = null;
            _staticMesh = null;
            _dynamicMesh = null;
            _staticLabels.Clear();
            _dynamicLabels.Clear();
        }

        #region Static geometry

        public void RebuildStatic()
        {
            if (_staticMesh == null) return;
            _staticMesh.ClearSurfaces();
            _staticLabels.Clear();
            if (_data == null || !_data.IsLoaded) return;

            // Fills
            var fill = new Surf(_staticMesh, Mesh.PrimitiveType.Triangles, _fillMat);
            if (ShowTriangles)
            {
                for (int i = 0; i < _data.CachedTriangles.Length; i++)
                {
                    ref TriangleRenderData tri = ref _data.CachedTriangles[i];
                    if (tri.isBlocked && !ShowBlockedTriangles) continue;
                    Color c = tri.isBlocked
                        ? GodotFPNavMeshVisualizerStyles.TriangleFillBlocked
                        : GodotFPNavMeshVisualizerStyles.TriangleFill;
                    AddTri(fill, c, tri.v0, tri.v1, tri.v2);
                }
            }
            if (ShowCostHeatmap) AddCostHeatmap(fill);
            fill.End();

            // Lines
            var line = new Surf(_staticMesh, Mesh.PrimitiveType.Lines, _lineMat);
            if (ShowEdges)
            {
                var c = GodotFPNavMeshVisualizerStyles.EdgeInternal;
                for (int i = 0; i < _data.InternalEdges.Count; i++)
                    AddLine(line, c, _data.InternalEdges[i].a, _data.InternalEdges[i].b);
            }
            if (ShowBoundaryEdges)
            {
                var c = GodotFPNavMeshVisualizerStyles.EdgeBoundary;
                for (int i = 0; i < _data.BoundaryEdges.Count; i++)
                    AddLine(line, c, _data.BoundaryEdges[i].a, _data.BoundaryEdges[i].b);
            }
            if (ShowObstacleRings && _data.ObstacleRings != null)
            {
                for (int r = 0; r < _data.ObstacleRings.Count; r++)
                {
                    var ring = _data.ObstacleRings[r];
                    int n = ring.Points.Length;
                    if (n < 2) continue;

                    // Ring edges — colored by winding (outer CW vs hole/pillar CCW).
                    Color ringColor = ring.IsCCW
                        ? GodotFPNavMeshVisualizerStyles.ObstacleRingHole
                        : GodotFPNavMeshVisualizerStyles.ObstacleRingOuter;
                    Vector3 centroid = Vector3.Zero;
                    for (int i = 0; i < n; i++)
                    {
                        AddLine(line, ringColor, ring.Points[i], ring.Points[(i + 1) % n]);
                        centroid += ring.Points[i];
                    }
                    centroid /= n;

                    // Per-vertex convexity markers (convex = white, reflex = amber).
                    for (int i = 0; i < n; i++)
                    {
                        Color dot = ring.Convex[i]
                            ? GodotFPNavMeshVisualizerStyles.ObstacleConvex
                            : GodotFPNavMeshVisualizerStyles.ObstacleReflex;
                        AddCross(line, dot, ring.Points[i], GodotFPNavMeshVisualizerStyles.ObstacleDotSize);
                    }

                    // Ring label at the centroid (index · winding · vertex count). Stacked labels at
                    // one spot reveal coincident/duplicate rings from raised (multi-level) geometry.
                    _staticLabels.Add((centroid + Vector3.Up * 0.1f, $"r{r}·{(ring.IsCCW ? "H" : "O")}·{n}"));
                }
            }
            if (ShowVertices)
            {
                var c = GodotFPNavMeshVisualizerStyles.Vertex;
                float s = GodotFPNavMeshVisualizerStyles.VertexSize;
                for (int i = 0; i < _data.CachedVertices.Length; i++)
                    AddCross(line, c, _data.CachedVertices[i], s);
            }
            if (ShowTriangleCenters)
            {
                var c = GodotFPNavMeshVisualizerStyles.TriangleCenter;
                for (int i = 0; i < _data.CachedTriangles.Length; i++)
                    AddCross(line, c, _data.CachedTriangles[i].center, 0.05f);
            }
            if (ShowGrid) AddGrid(line);
            line.End();

            // Labels
            if (ShowTriangleIndices)
            {
                for (int i = 0; i < _data.CachedTriangles.Length; i++)
                    _staticLabels.Add((_data.CachedTriangles[i].center + Vector3.Up * 0.1f, i.ToString()));
            }
            if (ShowGridLabels)
                CollectGridLabels();
        }

        private void AddCostHeatmap(Surf fill)
        {
            float minCost = float.MaxValue, maxCost = float.MinValue;
            for (int i = 0; i < _data.CachedTriangles.Length; i++)
            {
                float cost = _data.CachedTriangles[i].costMultiplier;
                if (cost < minCost) minCost = cost;
                if (cost > maxCost) maxCost = cost;
            }
            float range = maxCost - minCost;
            if (range < 0.001f) return;

            for (int i = 0; i < _data.CachedTriangles.Length; i++)
            {
                ref TriangleRenderData tri = ref _data.CachedTriangles[i];
                float t = (tri.costMultiplier - minCost) / range;
                Color c = new Color(0f, 1f, 0f, 0.3f).Lerp(new Color(1f, 0f, 0f, 0.3f), t);
                AddTri(fill, c, tri.v0, tri.v1, tri.v2);
            }
        }

        private void AddGrid(Surf line)
        {
            if (_data.NavMesh == null) return;
            float originX = _data.NavMesh.GridOrigin.x.ToFloat();
            float originZ = _data.NavMesh.GridOrigin.y.ToFloat();
            float cellSize = _data.NavMesh.GridCellSize.ToFloat();
            int gridW = _data.NavMesh.GridWidth;
            int gridH = _data.NavMesh.GridHeight;
            var c = GodotFPNavMeshVisualizerStyles.GridLine;

            for (int col = 0; col <= gridW; col++)
            {
                float x = originX + col * cellSize;
                AddLine(line, c, new Vector3(x, 0, originZ), new Vector3(x, 0, originZ + gridH * cellSize));
            }
            for (int row = 0; row <= gridH; row++)
            {
                float z = originZ + row * cellSize;
                AddLine(line, c, new Vector3(originX, 0, z), new Vector3(originX + gridW * cellSize, 0, z));
            }
        }

        private void AddGridHover()
        {
            if (_data.NavMesh == null) return;
            if (HoveredCell.col < 0 || HoveredCell.row < 0 ||
                HoveredCell.col >= _data.NavMesh.GridWidth || HoveredCell.row >= _data.NavMesh.GridHeight)
                return;

            float originX = _data.NavMesh.GridOrigin.x.ToFloat();
            float originZ = _data.NavMesh.GridOrigin.y.ToFloat();
            float cellSize = _data.NavMesh.GridCellSize.ToFloat();
            float cx = originX + HoveredCell.col * cellSize;
            float cz = originZ + HoveredCell.row * cellSize;
            var c = GodotFPNavMeshVisualizerStyles.GridHighlight;

            var s = new Surf(_dynamicMesh, Mesh.PrimitiveType.Triangles, _fillMat);
            var a0 = new Vector3(cx, 0.01f, cz);
            var a1 = new Vector3(cx + cellSize, 0.01f, cz);
            var a2 = new Vector3(cx + cellSize, 0.01f, cz + cellSize);
            var a3 = new Vector3(cx, 0.01f, cz + cellSize);
            AddTri(s, c, a0, a1, a2);
            AddTri(s, c, a0, a2, a3);
            s.End();
        }

        private void CollectGridLabels()
        {
            if (_data.NavMesh == null) return;
            float originX = _data.NavMesh.GridOrigin.x.ToFloat();
            float originZ = _data.NavMesh.GridOrigin.y.ToFloat();
            float cellSize = _data.NavMesh.GridCellSize.ToFloat();
            int gridW = _data.NavMesh.GridWidth;
            int gridH = _data.NavMesh.GridHeight;

            for (int row = 0; row < gridH; row++)
            {
                for (int col = 0; col < gridW; col++)
                {
                    var center = new Vector3(originX + (col + 0.5f) * cellSize, 0.05f, originZ + (row + 0.5f) * cellSize);
                    int cellIdx = row * gridW + col;
                    int triCount = _data.NavMesh.GridCells[cellIdx * 2 + 1];
                    if (triCount > 0)
                        _staticLabels.Add((center, $"({col},{row})\n{triCount}"));
                }
            }
        }

        #endregion

        #region Dynamic geometry (path + agents)

        public void RebuildDynamic()
        {
            if (_dynamicMesh == null) return;
            _dynamicMesh.ClearSurfaces();
            _dynamicLabels.Clear();
            if (_data == null || !_data.IsLoaded) return;

            // Fills (corridor + agent corridors)
            var fill = new Surf(_dynamicMesh, Mesh.PrimitiveType.Triangles, _fillMat);
            if (ShowCorridor && _data.HasPath)
            {
                var c = GodotFPNavMeshVisualizerStyles.CorridorFill;
                for (int i = 0; i < _data.CorridorLength; i++)
                {
                    ref TriangleRenderData tri = ref _data.CachedTriangles[_data.Corridor[i]];
                    AddTri(fill, c, tri.v0, tri.v1, tri.v2);
                }
            }
            if (ShowAgentPaths && _agentSim != null) AddAgentCorridorFills(fill);
            fill.End();

            // Lines (waypoints, portals, markers, agents, velocities, orca)
            var line = new Surf(_dynamicMesh, Mesh.PrimitiveType.Lines, _lineMat);
            if (_data.HasPath && ShowWaypoints) AddWaypoints(line);
            if (_data.HasPath && ShowPortals) AddPortals(line);
            if (ShowStartEndMarkers) AddStartEndMarkers(line);
            if (_agentSim != null)
            {
                if (ShowAgents) AddAgents(line);
                if (ShowAgentPaths) AddAgentPathLines(line);
                if (ShowAgentVelocities) AddAgentVelocities(line);
                if (ShowOrcaLines) AddOrcaVisualization(line);
            }
            line.End();

            // Grid hover highlight — its own fill surface (hover changes per mouse-move).
            if (ShowGrid) AddGridHover();
        }

        private void AddWaypoints(Surf line)
        {
            if (_data.WaypointCount < 2) return;
            var lc = GodotFPNavMeshVisualizerStyles.WaypointLine;
            for (int i = 0; i < _data.WaypointCount - 1; i++)
                AddLine(line, lc, _data.Waypoints[i], _data.Waypoints[i + 1]);
            var dc = GodotFPNavMeshVisualizerStyles.WaypointDot;
            for (int i = 0; i < _data.WaypointCount; i++)
                AddCross(line, dc, _data.Waypoints[i], GodotFPNavMeshVisualizerStyles.WaypointDotSize);
        }

        private void AddPortals(Surf line)
        {
            var c = GodotFPNavMeshVisualizerStyles.PortalLine;
            for (int i = 0; i < _data.Portals.Count; i++)
                AddLine(line, c, _data.Portals[i].left, _data.Portals[i].right);
        }

        private void AddStartEndMarkers(Surf line)
        {
            if (_data.HasStart)
                AddWireDisc(line, GodotFPNavMeshVisualizerStyles.StartMarker,
                    _data.StartPoint, GodotFPNavMeshVisualizerStyles.MarkerSize);
            if (_data.HasEnd)
                AddWireDisc(line, GodotFPNavMeshVisualizerStyles.EndMarker,
                    _data.EndPoint, GodotFPNavMeshVisualizerStyles.MarkerSize);
        }

        private void AddAgents(Surf line)
        {
            for (int i = 0; i < _agentSim.AgentCount; i++)
            {
                var rd = _agentSim.GetAgentRenderData(i);
                AddWireDisc(line, GodotFPNavMeshVisualizerStyles.AgentBody, rd.position, rd.radius);
                _dynamicLabels.Add((rd.position + Vector3.Up * (rd.radius + 0.2f), $"#{i}"));
                if (rd.hasDestination)
                    AddWireDisc(line, GodotFPNavMeshVisualizerStyles.AgentDestination, rd.destination, 0.15f);
            }
        }

        private void AddAgentCorridorFills(Surf fill)
        {
            var c = GodotFPNavMeshVisualizerStyles.CorridorFill;
            for (int i = 0; i < _agentSim.AgentCount; i++)
            {
                var rd = _agentSim.GetAgentRenderData(i);
                if (!rd.hasPath || rd.corridor == null) continue;
                for (int j = 0; j < rd.corridorLength; j++)
                {
                    int triIdx = rd.corridor[j];
                    if (triIdx < 0 || triIdx >= _data.CachedTriangles.Length) continue;
                    ref TriangleRenderData tri = ref _data.CachedTriangles[triIdx];
                    AddTri(fill, c, tri.v0, tri.v1, tri.v2);
                }
            }
        }

        private void AddAgentPathLines(Surf line)
        {
            if (_data.Funnel == null) return;
            for (int i = 0; i < _agentSim.AgentCount; i++)
            {
                var rd = _agentSim.GetAgentRenderData(i);
                if (!rd.hasPath || rd.corridor == null || rd.corridorLength <= 0) continue;

                FPVector3 currentPos = rd.position.ToFPVector3();
                FPVector3 target = rd.destination.ToFPVector3();
                int cornerCount = _data.Funnel.FindCorners(rd.corridor, rd.corridorLength, currentPos, target, 8);
                FPVector3[] corners = _data.Funnel.Corners;
                if (cornerCount <= 0) continue;

                var lc = GodotFPNavMeshVisualizerStyles.WaypointLine;
                Vector3 prev = rd.position;
                for (int c = 0; c < cornerCount; c++)
                {
                    Vector3 cp = corners[c].ToVector3();
                    AddLine(line, lc, prev, cp);
                    prev = cp;
                }
                var dc = GodotFPNavMeshVisualizerStyles.WaypointDot;
                for (int c = 0; c < cornerCount; c++)
                    AddCross(line, dc, corners[c].ToVector3(), GodotFPNavMeshVisualizerStyles.WaypointDotSize);
            }
        }

        private void AddAgentVelocities(Surf line)
        {
            for (int i = 0; i < _agentSim.AgentCount; i++)
            {
                var rd = _agentSim.GetAgentRenderData(i);
                if (rd.velocity.LengthSquared() > 0.001f)
                {
                    Vector3 end = rd.position + new Vector3(rd.velocity.X, 0, rd.velocity.Y);
                    AddArrow(line, GodotFPNavMeshVisualizerStyles.AgentVelocity, rd.position, end);
                }
                if (rd.desiredVelocity.LengthSquared() > 0.001f)
                {
                    Vector3 end = rd.position + new Vector3(rd.desiredVelocity.X, 0, rd.desiredVelocity.Y);
                    AddArrow(line, GodotFPNavMeshVisualizerStyles.AgentDesiredVel, rd.position, end);
                }
            }
        }

        private void AddOrcaVisualization(Surf line)
        {
            if (_agentSim.Avoidance == null) return;
            int lineCount = _agentSim.Avoidance.DebugOrcaLineCount;
            if (lineCount <= 0) return;

            int selectedIdx = _agentSim.LastOrcaComputedAgentIndex;
            if (selectedIdx < 0 || selectedIdx >= _agentSim.AgentCount) return;

            var rd = _agentSim.GetAgentRenderData(selectedIdx);
            Vector3 agentPos = rd.position;
            float lineLen = 3f;

            for (int i = 0; i < lineCount; i++)
            {
                var orca = _agentSim.Avoidance.DebugOrcaLines[i];
                Vector3 point = agentPos + new Vector3(orca.point.x.ToFloat(), 0, orca.point.y.ToFloat());
                Vector3 dir = new Vector3(orca.direction.x.ToFloat(), 0, orca.direction.y.ToFloat());

                AddLine(line, GodotFPNavMeshVisualizerStyles.OrcaLine, point - dir * lineLen, point + dir * lineLen);
                Vector3 normal = new Vector3(-dir.Z, 0, dir.X);
                AddLine(line, new Color(1f, 0.5f, 0f, 0.3f), point, point + normal * 0.5f);
            }

            Vector3 orcaVelEnd = agentPos + new Vector3(rd.desiredVelocity.X, 0, rd.desiredVelocity.Y);
            AddArrow(line, GodotFPNavMeshVisualizerStyles.OrcaVelocity, agentPos, orcaVelEnd, 0.2f);
        }

        #endregion

        #region ImmediateMesh helpers

        // Lazily begins a surface on the first vertex and only ends it if opened — avoids Godot's
        // "No vertices were added" / "Already creating a new surface" errors on empty surfaces.
        private sealed class Surf
        {
            private readonly ImmediateMesh _m;
            private readonly Mesh.PrimitiveType _prim;
            private readonly Material _mat;
            private bool _open;

            public Surf(ImmediateMesh m, Mesh.PrimitiveType prim, Material mat)
            {
                _m = m;
                _prim = prim;
                _mat = mat;
            }

            public void Vert(Color c, Vector3 v)
            {
                if (!_open)
                {
                    _m.SurfaceBegin(_prim, _mat);
                    _open = true;
                }
                _m.SurfaceSetColor(c);
                _m.SurfaceAddVertex(v);
            }

            public void End()
            {
                if (_open)
                {
                    _m.SurfaceEnd();
                    _open = false;
                }
            }
        }

        private static void AddTri(Surf s, Color c, Vector3 v0, Vector3 v1, Vector3 v2)
        {
            s.Vert(c, v0);
            s.Vert(c, v1);
            s.Vert(c, v2);
        }

        private static void AddLine(Surf s, Color c, Vector3 a, Vector3 b)
        {
            s.Vert(c, a);
            s.Vert(c, b);
        }

        private static void AddCross(Surf s, Color c, Vector3 center, float size)
        {
            AddLine(s, c, center - new Vector3(size, 0, 0), center + new Vector3(size, 0, 0));
            AddLine(s, c, center - new Vector3(0, 0, size), center + new Vector3(0, 0, size));
            AddLine(s, c, center - new Vector3(0, size, 0), center + new Vector3(0, size, 0));
        }

        private static void AddWireDisc(Surf s, Color c, Vector3 center, float radius, int seg = 24)
        {
            Vector3 prev = center + new Vector3(radius, 0, 0);
            for (int i = 1; i <= seg; i++)
            {
                float ang = Mathf.Tau * i / seg;
                Vector3 p = center + new Vector3(Mathf.Cos(ang) * radius, 0, Mathf.Sin(ang) * radius);
                AddLine(s, c, prev, p);
                prev = p;
            }
        }

        private static void AddArrow(Surf s, Color c, Vector3 from, Vector3 to, float headSize = 0.15f)
        {
            AddLine(s, c, from, to);
            Vector3 dir = to - from;
            if (dir.LengthSquared() < 0.0001f) return;
            dir = dir.Normalized();
            Vector3 right = dir.Cross(Vector3.Up);
            if (right.LengthSquared() < 0.0001f) right = dir.Cross(Vector3.Forward);
            right = right.Normalized();
            Vector3 head = to - dir * headSize;
            AddLine(s, c, to, head + right * headSize * 0.5f);
            AddLine(s, c, to, head - right * headSize * 0.5f);
        }

        #endregion
    }
}
#endif
