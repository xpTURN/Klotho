using System.Collections.Generic;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    internal static class NavAgentTestHelper
    {
        public const int MAX_ENTITIES = 32;

        public static FP64 DT => FP64.FromFloat(1f / 60f);

        /// <summary>
        /// Create lightweight Frame + 1 NavAgentComponent agent.
        /// </summary>
        public static Frame CreateFrameWithAgent(FPVector3 position, int triangleIndex,
            out EntityRef entity, out EntityRef[] entities, int maxEntities = MAX_ENTITIES)
        {
            var frame = new Frame(maxEntities, null);
            entity = frame.CreateEntity();
            frame.Add(entity, default(NavAgentComponent));
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.Init(ref nav, position);
            nav.CurrentTriangleIndex = triangleIndex;
            entities = new[] { entity };
            return frame;
        }

        /// <summary>
        /// Create lightweight Frame + N NavAgentComponent agents.
        /// </summary>
        public static Frame CreateFrameWithAgents(FPVector3[] positions, int[] triangleIndices,
            out EntityRef[] entities, int maxEntities = MAX_ENTITIES)
        {
            var frame = new Frame(maxEntities, null);
            entities = new EntityRef[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                entities[i] = frame.CreateEntity();
                frame.Add(entities[i], default(NavAgentComponent));
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                NavAgentComponent.Init(ref nav, positions[i]);
                nav.CurrentTriangleIndex = triangleIndices[i];
            }
            return frame;
        }

        /// <summary>
        /// For ORCA testing: create agents in Moving state.
        /// </summary>
        public static Frame CreateFrameWithMovingAgents(
            FPVector3[] positions, FPVector2[] velocities,
            out EntityRef[] entities, int maxEntities = MAX_ENTITIES)
        {
            var frame = new Frame(maxEntities, null);
            entities = new EntityRef[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                entities[i] = frame.CreateEntity();
                frame.Add(entities[i], default(NavAgentComponent));
                ref var nav = ref frame.Get<NavAgentComponent>(entities[i]);
                NavAgentComponent.Init(ref nav, positions[i]);
                nav.Velocity = velocities[i];
                nav.DesiredVelocity = velocities[i];
                nav.Status = (byte)FPNavAgentStatus.Moving;
            }
            return frame;
        }

        public static FPNavAgentSystem CreateSystem(FPNavMesh mesh, IKLogger logger)
            => CreateSystem(mesh, logger, out _);

        /// <summary>
        /// Same system, with the pathfinder handed back so a caller can read its diagnostic
        /// counters. The system keeps the instance private on purpose — reaching for it is a test
        /// concern, not an engine API.
        /// </summary>
        public static FPNavAgentSystem CreateSystem(FPNavMesh mesh, IKLogger logger,
            out FPNavMeshPathfinder pathfinder)
        {
            var query = new FPNavMeshQuery(mesh, logger);
            pathfinder = new FPNavMeshPathfinder(mesh, query, logger);
            var funnel = new FPNavMeshFunnel(mesh, query, logger);
            return new FPNavAgentSystem(mesh, query, pathfinder, funnel, logger);
        }

        #region Large synthetic meshes (crowd-scaling harness)

        // Every large-mesh helper below emits unit quads on an integer lattice and hands them to
        // the real build pipeline, so adjacency, the broadphase grid and the bake block come out
        // exactly as they do for a baked asset. CELL is the world size of one lattice cell: it is
        // wider than the default agent radius (0.5) so a one-cell corridor stays walkable.
        private const double CELL = 2.0;

        private sealed class QuadMeshBuilder
        {
            private readonly Dictionary<(int, int), int> _index = new Dictionary<(int, int), int>();
            private readonly List<FPVector3> _vertices = new List<FPVector3>();
            private readonly List<int> _indices = new List<int>();
            private readonly List<int> _areas = new List<int>();

            private int Vertex(int gx, int gz)
            {
                if (_index.TryGetValue((gx, gz), out int existing))
                    return existing;
                int id = _vertices.Count;
                _vertices.Add(new FPVector3(
                    FP64.FromDouble(gx * CELL), FP64.Zero, FP64.FromDouble(gz * CELL)));
                _index[(gx, gz)] = id;
                return id;
            }

            /// <summary>Adds the two CCW triangles of lattice cell (gx, gz), area 0.</summary>
            public void AddCell(int gx, int gz) => AddCell(gx, gz, 0);

            /// <summary>
            /// Adds the two CCW triangles of lattice cell (gx, gz) with the given NavMesh area
            /// index. The pipeline turns it into <c>areaMask = 1 &lt;&lt; area</c> per triangle and
            /// carries it through canonicalisation, so a caller picks areas by CELL and never has
            /// to know the emitted triangle order.
            /// </summary>
            public void AddCell(int gx, int gz, int area)
            {
                _areas.Add(area);
                _areas.Add(area);
                int v00 = Vertex(gx, gz);
                int v10 = Vertex(gx + 1, gz);
                int v11 = Vertex(gx + 1, gz + 1);
                int v01 = Vertex(gx, gz + 1);
                _indices.Add(v00); _indices.Add(v10); _indices.Add(v11);
                _indices.Add(v00); _indices.Add(v11); _indices.Add(v01);
            }

            public FPNavMesh Build() => Build(CELL * 4.0);

            /// <summary>
            /// <paramref name="gridCellSize"/> is the BROADPHASE cell, not the lattice cell: it
            /// sizes the grid the point queries search, so it is the knob a test needs when the
            /// thing under measurement is how far a projection can reach (its fallback searches
            /// one cell ring, so the reach scales with this and nothing else).
            /// </summary>
            public FPNavMesh Build(double gridCellSize)
            {
                var vertices = _vertices.ToArray();
                var indices = _indices.ToArray();
                return FPNavMeshBuildPipeline.Build(
                    vertices, indices, _areas.ToArray(), gridCellSize,
                    null, bakeAgentRadius: 0.5);
            }
        }

        /// <summary>
        /// Open square field, <paramref name="cells"/> × <paramref name="cells"/> lattice cells
        /// (2 triangles each). The best case for A*: the distance heuristic is nearly perfect, so
        /// expansion tracks the straight line however large the field gets. Use it to measure what
        /// a wide-open map costs, not to stress the iteration budget.
        /// </summary>
        public static FPNavMesh CreateOpenFieldNavMesh(int cells)
        {
            var builder = new QuadMeshBuilder();
            for (int gz = 0; gz < cells; gz++)
                for (int gx = 0; gx < cells; gx++)
                    builder.AddCell(gx, gz);
            return builder.Build();
        }

        /// <summary>
        /// Two square floors at the SAME xz, stacked <paramref name="floorGap"/> apart in y and
        /// sharing no vertex — the only shape in which the multi-floor lookup's Y disambiguation
        /// does any work. Every other fixture here is a continuous surface, and on one of those the
        /// triangles containing a point are all at the same height, so a Y-blind lookup is
        /// indistinguishable from a Y-aware one.
        ///
        /// <para>The upper floor carries <paramref name="upperArea"/> so a query whose mask omits
        /// that area must refuse a destination up there rather than quietly reroute to the walkable
        /// floor below. The default is 3, not <see cref="FPNavMeshAreas.BUILDING_AREA"/>: the build
        /// pipeline REFUSES area 1 as authored input (it is reserved for what the rebaker stamps on
        /// a retained footprint), so a fixture that wants forbidden ground straight out of the bake
        /// has to pick another index and a mask to match.</para>
        /// </summary>
        public static FPNavMesh CreateStackedFloorsNavMesh(
            int cells, double floorGap, int upperArea = 3)
        {
            var vertices = new List<FPVector3>();
            var indices = new List<int>();
            var areas = new List<int>();

            for (int level = 0; level < 2; level++)
            {
                double y = level == 0 ? 0.0 : floorGap;
                int area = level == 0 ? 0 : upperArea;
                var index = new Dictionary<(int, int), int>();

                int Vertex(int gx, int gz)
                {
                    if (index.TryGetValue((gx, gz), out int existing))
                        return existing;
                    int id = vertices.Count;
                    vertices.Add(new FPVector3(
                        FP64.FromDouble(gx * CELL), FP64.FromDouble(y), FP64.FromDouble(gz * CELL)));
                    index[(gx, gz)] = id;
                    return id;
                }

                for (int gz = 0; gz < cells; gz++)
                {
                    for (int gx = 0; gx < cells; gx++)
                    {
                        areas.Add(area);
                        areas.Add(area);
                        int v00 = Vertex(gx, gz);
                        int v10 = Vertex(gx + 1, gz);
                        int v11 = Vertex(gx + 1, gz + 1);
                        int v01 = Vertex(gx, gz + 1);
                        indices.Add(v00); indices.Add(v10); indices.Add(v11);
                        indices.Add(v00); indices.Add(v11); indices.Add(v01);
                    }
                }
            }

            return FPNavMeshBuildPipeline.Build(
                vertices.ToArray(), indices.ToArray(), areas.ToArray(), CELL * 4.0,
                null, bakeAgentRadius: 0.5);
        }

        /// <summary>
        /// <see cref="CreateOpenFieldNavMesh(int)"/> with an explicit broadphase cell size, for
        /// tests about how far a point query can see. The default field's grid cell (8 world units)
        /// is wider than any footprint this helper can place, so on it the projection's one-ring
        /// reach never binds and the limit cannot be observed.
        /// </summary>
        public static FPNavMesh CreateOpenFieldNavMesh(int cells, double gridCellSize)
        {
            var builder = new QuadMeshBuilder();
            for (int gz = 0; gz < cells; gz++)
                for (int gx = 0; gx < cells; gx++)
                    builder.AddCell(gx, gz);
            return builder.Build(gridCellSize);
        }

        /// <summary>
        /// Serpentine corridor: <paramref name="rows"/> horizontal runs of <paramref name="width"/>
        /// cells, joined at alternating ends by a single connector cell, with the rows themselves
        /// one empty lattice row apart so no shortcut exists. The route is forced through very
        /// nearly every cell, which is what makes this the worst case for A*: the heuristic points
        /// straight at the goal while the only path doubles back, so expansion count scales with
        /// the corridor rather than with the straight-line distance.
        /// <para>
        /// Two gates ride on that: a long enough serpentine overruns the corridor buffer
        /// (<c>MAX_CORRIDOR</c>), and a longer one overruns the A* iteration budget
        /// (<c>MAX_ITERATIONS</c>) before it can reach the far end.
        /// </para>
        /// Cell (0,0) is the start; <paramref name="endCell"/> returns the far end.
        /// </summary>
        public static FPNavMesh CreateSerpentineNavMesh(int width, int rows, out (int gx, int gz) endCell)
        {
            var builder = new QuadMeshBuilder();
            int lastX = 0;
            for (int r = 0; r < rows; r++)
            {
                int gz = r * 2;
                for (int gx = 0; gx < width; gx++)
                    builder.AddCell(gx, gz);

                lastX = (r % 2 == 0) ? width - 1 : 0;
                if (r < rows - 1)
                    builder.AddCell(lastX, gz + 1);   // connector into the next run
            }
            endCell = (lastX, (rows - 1) * 2);
            return builder.Build();
        }

        /// <summary>
        /// Two open patches of <paramref name="cells"/> × <paramref name="cells"/> with a lattice
        /// gap between them, in one mesh. Both endpoints are on-mesh and walkable, but no edge
        /// joins the patches — this is "there is genuinely no route", the case a budget-overrun
        /// counter must not claim. Keep <paramref name="cells"/> small enough that draining the
        /// start patch costs far fewer than <c>MAX_ITERATIONS</c> expansions.
        /// </summary>
        public static FPNavMesh CreateSplitFieldNavMesh(int cells, out (int gx, int gz) farCell)
            => CreateSplitFieldNavMesh(cells, cells, out farCell);

        /// <summary>
        /// Rectangular variant. The start patch holds <c>width × height × 2</c> triangles, and A*
        /// pops every one of them before giving up — which makes the cell count the dial for
        /// "how much search happens before the open set drains", exactly the quantity that decides
        /// whether a failure is a budget overrun or an exhausted graph.
        /// </summary>
        public static FPNavMesh CreateSplitFieldNavMesh(int width, int height, out (int gx, int gz) farCell)
        {
            var builder = new QuadMeshBuilder();
            for (int gz = 0; gz < height; gz++)
                for (int gx = 0; gx < width; gx++)
                    builder.AddCell(gx, gz);

            int offset = width + 2;   // one empty lattice column is enough to break adjacency
            for (int gz = 0; gz < 2; gz++)
                for (int gx = 0; gx < 2; gx++)
                    builder.AddCell(offset + gx, gz);

            farCell = (offset, 0);
            return builder.Build();
        }

        /// <summary>
        /// Open square field split into TWO NavMesh areas by lattice column: cells with
        /// <c>gx &lt; splitGx</c> get area 0 (<c>areaMask == 1</c>), the rest area 3
        /// (<c>areaMask == 8</c>). The two halves are edge-adjacent, so a mask of 1 makes the
        /// seam a wall while the geometry stays fully connected — which is what separates "the
        /// filter is being applied" from "the mesh has a hole in it". Area 3 rather than 1 because
        /// 1 is <see cref="FPNavMeshAreas.BUILDING_AREA"/>, reserved for the runtime and refused by
        /// the build pipeline.
        ///
        /// <para>Areas are assigned through the build pipeline's <c>areas</c> argument, the same
        /// producer a baked asset uses (the Unity exporter forwards
        /// <c>NavMeshTriangulation.areas</c>), rather than by stamping
        /// <c>TrianglesMutable</c> afterwards. Tests about the runtime's own stamp want the other
        /// producer: <see cref="RebakeWithBuildings"/> with a retained building.</para>
        /// </summary>
        public static FPNavMesh CreateTwoAreaFieldNavMesh(int cells, int splitGx)
        {
            var builder = new QuadMeshBuilder();
            for (int gz = 0; gz < cells; gz++)
                for (int gx = 0; gx < cells; gx++)
                    builder.AddCell(gx, gz, gx < splitGx ? 0 : 3);
            return builder.Build();
        }

        /// <summary>
        /// One square building shape, half extent 1.25 world units — deliberately off the 2-unit
        /// lattice, so an expanded corner never coincides with a base vertex (the retain fixture's
        /// reasoning, see <c>FPNavMeshRetainPlacementTests.SquareCatalog</c>).
        /// </summary>
        public static readonly FPBuildingShapeCatalog SquareBuildingCatalog = MakeSquareCatalog();

        private static FPBuildingShapeCatalog MakeSquareCatalog()
        {
            long h = 5 * FPGeoPredicates.SNAP_UNITS_PER_WORLD / 4;
            FPBuildingShapeCatalog.ObbOffsets(h, h, 4, out long[] x, out long[] z, out int[] entryStart);
            return new FPBuildingShapeCatalog(x, z, entryStart);
        }

        /// <summary>Touch policy, building contact allowed — the rules every helper rebake uses.</summary>
        public static readonly FPBuildingPlacementRules TouchRules =
            new FPBuildingPlacementRules(allowBuildingTouch: true);

        /// <summary>
        /// Half extent, in world units, of the footprint the rebaker actually carves or retains for
        /// <see cref="SquareBuildingCatalog"/> on <paramref name="mesh"/> — read from the engine's own
        /// expansion, which pads conservatively (measured 1.751953125, not 1.25 + radius).
        /// </summary>
        public static double ExpandedBuildingHalf(FPNavMesh mesh) =>
            new FPBuildingShapeExpansion(SquareBuildingCatalog, mesh.BakeAgentRadius).ExpandedX[0]
            / (double)FPGeoPredicates.SNAP_UNITS_PER_WORLD;

        public static FPBuildingPlacement Building(double x, double z, bool retain) =>
            new FPBuildingPlacement(0, FP64.FromDouble(x), FP64.FromDouble(z), FP64.Zero, retain);

        /// <summary>
        /// <paramref name="mesh"/> with <paramref name="placements"/> placed through the real
        /// rebaker under <see cref="TouchRules"/>. Carved buildings come back as holes; retained ones
        /// as ground stamped <see cref="FPNavMeshAreas.BUILDING_MASK"/>. Throws on a rejection.
        /// </summary>
        public static FPNavMesh RebakeWithBuildings(FPNavMesh mesh, params FPBuildingPlacement[] placements)
        {
            var snapshot = FPNavMeshRebaker.CreateSnapshot(
                mesh, null, prewarm: false, shapeCatalog: SquareBuildingCatalog);
            return FPNavMeshRebaker.RebakePlacements(snapshot, placements, null, TouchRules);
        }

        /// <summary>World-space centre of lattice cell (gx, gz), on the mesh surface.</summary>
        public static FPVector3 CellCenter(int gx, int gz) => new FPVector3(
            FP64.FromDouble((gx + 0.5) * CELL), FP64.Zero, FP64.FromDouble((gz + 0.5) * CELL));

        #endregion

        /// <summary>
        /// 4-triangle strip NavMesh (same as FPNavMeshPathfinderTests).
        ///   v2(0,4)---v3(4,4)---v5(8,4)
        ///     |  \T1  / |  \T3  / |
        ///     |   \  /  |   \  /  |
        ///     | T0 \/   | T2 \/   |
        ///   v0(0,0)---v1(4,0)---v4(8,0)
        /// </summary>
        public static FPNavMesh Create4TriNavMesh()
        {
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.Zero),
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)),
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.FromInt(4)),
                new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.FromInt(4)),
            };

            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 3,
                neighbor0 = -1, neighbor1 = 3, neighbor2 = 1,
                centerXZ = new FPVector2(FP64.FromFloat(8f / 3f), FP64.FromFloat(4f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var t1 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 3, v2 = 2,
                neighbor0 = 0, neighbor1 = -1, neighbor2 = -1,
                centerXZ = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(8f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var t2 = new FPNavMeshTriangle
            {
                v0 = 1, v1 = 4, v2 = 5,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = 3,
                centerXZ = new FPVector2(FP64.FromFloat(20f / 3f), FP64.FromFloat(4f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var t3 = new FPNavMeshTriangle
            {
                v0 = 1, v1 = 5, v2 = 3,
                neighbor0 = 2, neighbor1 = -1, neighbor2 = 0,
                centerXZ = new FPVector2(FP64.FromFloat(16f / 3f), FP64.FromFloat(8f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var triangles = new[] { t0, t1, t2, t3 };

            var bounds = new FPBounds2(
                new FPVector2(FP64.FromInt(4), FP64.FromInt(2)),
                new FPVector2(FP64.FromInt(8), FP64.FromInt(4))
            );

            var gridCells = new[] { 0, 2, 2, 2 };
            var gridTriangles = new[] { 0, 1, 2, 3 };

            return new FPNavMesh(
                vertices, triangles, bounds,
                gridCells, gridTriangles,
                gridWidth: 2, gridHeight: 1,
                gridCellSize: FP64.FromInt(4),
                gridOrigin: FPVector2.Zero
            );
        }
        /// <summary>
        /// L-shaped NavMesh — straight path not possible, must pass through corner.
        ///
        ///   v2(0,8)---v3(4,8)
        ///     |  \T1  / |
        ///     |   \  /  |
        ///     | T0 \/   |
        ///   v0(0,4)---v1(4,4)---v6(8,4)
        ///       \       |  \T3  / |
        ///        \ T4  |   \  /  |
        ///         \     | T2 \/   |
        ///          \    |         |
        ///         v4(4,0)---v5(8,0)
        ///
        /// Left vertical block: T0(v0,v1,v3), T1(v0,v3,v2)
        /// Right horizontal block: T2(v4,v5,v6), T3(v4,v6,v1)
        /// Corner bridge:    T4(v0,v4,v1)
        ///
        /// Path T1→T0→T4→T3→T2: must pass through corner (v1=4,4).
        /// Straight (1,7)→(6,1) is impossible since it crosses outside the NavMesh (empty region).
        /// </summary>
        public static FPNavMesh CreateLShapedNavMesh()
        {
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)),       // v0(0,4)
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.FromInt(4)), // v1(4,4) — corner
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(8)),       // v2(0,8)
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.FromInt(8)), // v3(4,8)
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.Zero),       // v4(4,0)
                new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero),       // v5(8,0)
                new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.FromInt(4)), // v6(8,4)
            };

            // T0: lower part of left block — v0(0,4), v1(4,4), v3(4,8)
            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 3,
                neighbor0 = 4,  // T4 via edge v0-v1
                neighbor1 = -1, // boundary (v1-v3)
                neighbor2 = 1,  // T1 via edge v3-v0
                centerXZ = new FPVector2(FP64.FromFloat(4f / 3f + 4f / 3f), FP64.FromFloat(16f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            // T1: upper part of left block — v0(0,4), v3(4,8), v2(0,8)
            var t1 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 3, v2 = 2,
                neighbor0 = 0,  // T0 via edge v0-v3
                neighbor1 = -1, // boundary
                neighbor2 = -1, // boundary
                centerXZ = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(20f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            // T2: lower part of right block — v4(4,0), v5(8,0), v6(8,4)
            var t2 = new FPNavMeshTriangle
            {
                v0 = 4, v1 = 5, v2 = 6,
                neighbor0 = -1, // boundary
                neighbor1 = -1, // boundary
                neighbor2 = 3,  // T3 via edge v6-v4
                centerXZ = new FPVector2(FP64.FromFloat(20f / 3f), FP64.FromFloat(4f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            // T3: upper part of right block — v4(4,0), v6(8,4), v1(4,4)
            var t3 = new FPNavMeshTriangle
            {
                v0 = 4, v1 = 6, v2 = 1,
                neighbor0 = 2,  // T2 via edge v4-v6
                neighbor1 = -1, // boundary
                neighbor2 = 4,  // T4 via edge v1-v4
                centerXZ = new FPVector2(FP64.FromFloat(16f / 3f), FP64.FromFloat(8f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            // T4: corner bridge — v0(0,4), v4(4,0), v1(4,4)
            var t4 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 4, v2 = 1,
                neighbor0 = -1, // boundary (v0-v4 diagonal)
                neighbor1 = 3,  // T3 via edge v4-v1
                neighbor2 = 0,  // T0 via edge v1-v0
                centerXZ = new FPVector2(FP64.FromFloat(8f / 3f), FP64.FromFloat(8f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var triangles = new[] { t0, t1, t2, t3, t4 };

            var bounds = new FPBounds2(
                new FPVector2(FP64.FromInt(4), FP64.FromInt(4)),
                new FPVector2(FP64.FromInt(8), FP64.FromInt(8))
            );

            // 2x2 grid (cell size 4)
            // cell(0,0) z=0..4: T4
            // cell(1,0) z=0..4: T2, T3
            // cell(0,1) z=4..8: T0, T1
            // cell(1,1) z=4..8: (empty)
            var gridCells = new[]
            {
                0, 1,  // cell(0,0): T4
                1, 2,  // cell(1,0): T2, T3
                3, 2,  // cell(0,1): T0, T1
                5, 0,  // cell(1,1): empty
            };
            var gridTriangles = new[] { 4, 2, 3, 0, 1 };

            return new FPNavMesh(
                vertices, triangles, bounds,
                gridCells, gridTriangles,
                gridWidth: 2, gridHeight: 2,
                gridCellSize: FP64.FromInt(4),
                gridOrigin: FPVector2.Zero
            );
        }

        /// <summary>
        /// A CLOCKWISE two-triangle square.
        ///
        /// Every other hand-written fixture is counter-clockwise, which stores every portal in the
        /// edge's own vertex order `(va,vb)` — the `flip = 0` case of the one-bit portal encoding.
        /// This one is the opposite, so it is the only fixture that covers the `flip = 1` path
        /// where a portal is stored as `(vb,va)`. Four of the baked assets are clockwise too, but
        /// reaching those needs a file load; this fixture is what covers the path in the layers
        /// that run without assets.
        ///
        /// <code>
        ///   v3(0,4)---v2(4,4)
        ///     |  \  T1  |          T0 = (v0, v2, v1)   clockwise
        ///     | T0 \    |          T1 = (v0, v3, v2)   clockwise
        ///   v0(0,0)---v1(4,0)      shared edge = {v0,v2}
        /// </code>
        ///
        /// The stored portals are the baker's own rule (`ComputePortalLeftRight`) applied by hand.
        /// Its test value is `Cross(d, a-b) = -2 * SignedArea`; a clockwise triangle has negative
        /// area, so the sign flips and the `else` branch (`left = vb`) is taken — both interior
        /// edges therefore store `(vb, va)`.
        /// </summary>
        public static FPNavMesh CreateCwSquareNavMesh()
        {
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),                       // v0 (0,0)
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.Zero),                 // v1 (4,0)
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.FromInt(4)),           // v2 (4,4)
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)),                 // v3 (0,4)
            };

            // T0 = (v0, v2, v1): Cross(v2-v0, v1-v0) = Cross((4,4),(4,0)) = -16 < 0 → CW
            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 2, v2 = 1,
                neighbor0 = 1,   // e0 = (v0,v2) = shared edge -> T1
                neighbor1 = -1,  // e1 = (v2,v1) boundary
                neighbor2 = -1,  // e2 = (v1,v0) boundary
                portalFlip = 0x01,   // e0 is the only interior edge and it is CW -> bit 0 (portal = (vb,va))
                centerXZ = new FPVector2(FP64.FromFloat(8f / 3f), FP64.FromFloat(4f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            // T1 = (v0, v3, v2): Cross(v3-v0, v2-v0) = Cross((0,4),(4,4)) = -16 < 0 → CW
            var t1 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 3, v2 = 2,
                neighbor0 = -1,  // e0 = (v0,v3) boundary
                neighbor1 = -1,  // e1 = (v3,v2) boundary
                neighbor2 = 0,   // e2 = (v2,v0) = shared edge -> T0
                portalFlip = 0x04,   // e2 is the only interior edge and it is CW -> bit 2 (portal = (vb,va))
                centerXZ = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(8f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var triangles = new[] { t0, t1 };

            var bounds = new FPBounds2(
                new FPVector2(FP64.FromInt(2), FP64.FromInt(2)),
                new FPVector2(FP64.FromInt(4), FP64.FromInt(4))
            );

            // 1x1 grid (cell size 4) — both triangles in one cell
            var gridCells = new[] { 0, 2 };
            var gridTriangles = new[] { 0, 1 };

            return new FPNavMesh(
                vertices, triangles, bounds,
                gridCells, gridTriangles,
                gridWidth: 1, gridHeight: 1,
                gridCellSize: FP64.FromInt(4),
                gridOrigin: FPVector2.Zero
            );
        }
    }
}
