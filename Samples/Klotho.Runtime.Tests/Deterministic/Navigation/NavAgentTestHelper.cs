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
            out EntityRef entity, out EntityRef[] entities)
        {
            var frame = new Frame(MAX_ENTITIES, null);
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
            out EntityRef[] entities)
        {
            var frame = new Frame(MAX_ENTITIES, null);
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
            out EntityRef[] entities)
        {
            var frame = new Frame(MAX_ENTITIES, null);
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
        {
            var query = new FPNavMeshQuery(mesh, logger);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, logger);
            var funnel = new FPNavMeshFunnel(mesh, query, logger);
            return new FPNavAgentSystem(mesh, query, pathfinder, funnel, logger);
        }

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
