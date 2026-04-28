using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using UnityEngine.TestTools;

using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    [TestFixture]
    public class FPNavMeshPathfinderTests
    {
        private const float EPSILON = 0.01f;

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // LoggerFactory configuration (same as ZLogger)
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });

            _logger = loggerFactory.CreateLogger("Tests");
        }

        #region Test NavMesh helpers

        /// <summary>
        /// 4-triangle strip NavMesh (X-axis direction):
        ///
        ///   v2(0,2)---v3(2,2)---v5(4,2)---v7(6,2)
        ///     | T1  /  | T3  /  | T5  /  |
        ///     |   /    |   /    |   /    |
        ///     | / T0   | / T2   | / T4   |
        ///   v0(0,0)---v1(2,0)---v4(4,0)---v6(6,0)
        ///
        /// 6 triangles, 8 vertices
        /// T0-T1 shared edge: v0-v3
        /// T0-T2 shared edge: v1-v3  (→ T0.neighbor1=2)
        /// T1-T3 shared edge: v3-v2? no, v3 is shared
        ///
        /// Simplified: 4-cell strip, each square split into 2 triangles
        ///
        /// Actually a simpler 4-triangle layout:
        ///   v0(0,0) v1(4,0) v2(0,4) v3(4,4) — existing 2 triangles
        ///   + v4(8,0) v5(8,4) — additional 2 triangles
        ///
        ///   v2(0,4)---v3(4,4)---v5(8,4)
        ///     |  \T1  / |  \T3  / |
        ///     |   \  /  |   \  /  |
        ///     | T0 \/   | T2 \/   |
        ///   v0(0,0)---v1(4,0)---v4(8,0)
        /// </summary>
        private static FPNavMesh Create4TriNavMesh()
        {
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),             // v0: (0,0)
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.Zero),       // v1: (4,0)
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)),       // v2: (0,4)
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.FromInt(4)), // v3: (4,4)
                new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero),       // v4: (8,0)
                new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.FromInt(4)), // v5: (8,4)
            };

            // T0: v0, v1, v3 (lower-left triangle)  — diagonal v0→v3
            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 3,
                neighbor0 = -1, neighbor1 = 2, neighbor2 = 1,
                portal0Left = -1, portal0Right = -1,
                portal1Left = 1, portal1Right = 3,   // T0→T2 shared: v1-v3
                portal2Left = 3, portal2Right = 0,   // T0→T1 shared: v3-v0
                centerXZ = new FPVector2(FP64.FromFloat(8f / 3f), FP64.FromFloat(4f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1,
                costMultiplier = FP64.One,
                isBlocked = false,
            };

            // T1: v0, v3, v2 (upper-left triangle)
            var t1 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 3, v2 = 2,
                neighbor0 = 0, neighbor1 = -1, neighbor2 = -1,
                portal0Left = 0, portal0Right = 3,   // T1→T0 shared: v0-v3
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(8f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1,
                costMultiplier = FP64.One,
                isBlocked = false,
            };

            // T2: v1, v4, v5 (lower-right triangle)  — diagonal v1→v5
            var t2 = new FPNavMeshTriangle
            {
                v0 = 1, v1 = 4, v2 = 5,
                neighbor0 = -1, neighbor1 = 3, neighbor2 = 0,
                portal0Left = -1, portal0Right = -1,
                portal1Left = 4, portal1Right = 5,   // T2→T3 shared: v4-v5
                portal2Left = 3, portal2Right = 1,   // T2→T0 shared: v3-v1
                centerXZ = new FPVector2(FP64.FromFloat(16f / 3f), FP64.FromFloat(4f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1,
                costMultiplier = FP64.One,
                isBlocked = false,
            };

            // T3: v1, v5, v3 (upper-right triangle)
            var t3 = new FPNavMeshTriangle
            {
                v0 = 1, v1 = 5, v2 = 3,
                neighbor0 = 2, neighbor1 = -1, neighbor2 = -1,
                portal0Left = 5, portal0Right = 1,   // T3→T2 shared: v5-v1
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromFloat(16f / 3f), FP64.FromFloat(8f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1,
                costMultiplier = FP64.One,
                isBlocked = false,
            };

            var triangles = new[] { t0, t1, t2, t3 };

            var bounds = new FPBounds2(
                new FPVector2(FP64.FromInt(4), FP64.FromInt(2)),
                new FPVector2(FP64.FromInt(8), FP64.FromInt(4))
            );

            // 2-cell grid (4x4 each cell)
            var gridCells = new[]
            {
                0, 2,  // cell(0,0): T0, T1
                2, 2,  // cell(1,0): T2, T3
            };
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
        /// For areaMask testing: set T2's areaMask to 2 to enable filtering.
        /// </summary>
        private static FPNavMesh Create4TriNavMesh_WithAreaMask()
        {
            var mesh = Create4TriNavMesh();
            mesh.Triangles[2].areaMask = 2; // only T2 is area 2
            return mesh;
        }

        #endregion

        #region FindPath — basic

        [Test]
        public void FindPath_SameTriangle_Returns1()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // (1, 1) and (2, 1) are both inside T0
            bool found = pf.FindPath(
                new FPVector3(1, 0, 1), new FPVector3(2, 0, 1), 0xFF,
                out int[] corridor, out int length);

            Assert.IsTrue(found);
            Assert.AreEqual(1, length);
            Assert.AreEqual(0, corridor[0]);
        }

        [Test]
        public void FindPath_AdjacentTriangles_Returns2()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // T0 (3,1) → T1 (1,3)
            bool found = pf.FindPath(
                new FPVector3(3, 0, 1), new FPVector3(1, 0, 3), 0xFF,
                out int[] corridor, out int length);

            Assert.IsTrue(found);
            Assert.AreEqual(2, length);
            Assert.AreEqual(0, corridor[0]); // T0
            Assert.AreEqual(1, corridor[1]); // T1
        }

        [Test]
        public void FindPath_AcrossTwoSquares_FindsCorridor()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // T0 (1,1) → T2 (5,1) — T0→T2 directly adjacent
            bool found = pf.FindPath(
                new FPVector3(1, 0, 1), new FPVector3(5, 0, 1), 0xFF,
                out int[] corridor, out int length);

            Assert.IsTrue(found);
            Assert.IsTrue(length >= 2);
            Assert.AreEqual(0, corridor[0]);                // start T0
            Assert.AreEqual(2, corridor[length - 1]);       // end T2
        }

        [Test]
        public void FindPath_FarTriangles_FindsPath()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // T1 (1,3) → T3 (5,3) — T1→T0→T2→T3
            bool found = pf.FindPath(
                new FPVector3(1, 0, 3), new FPVector3(5, 0, 3), 0xFF,
                out int[] corridor, out int length);

            Assert.IsTrue(found);
            Assert.IsTrue(length >= 3);
            Assert.AreEqual(1, corridor[0]);               // start T1
            Assert.AreEqual(3, corridor[length - 1]);      // end T3
        }

        #endregion

        #region FindPath — failure cases

        [Test]
        public void FindPath_OutsideNavMesh_ReturnsFalse()
        {
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("outside NavMesh"));

            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            bool found = pf.FindPath(
                new FPVector3(-5, 0, -5), new FPVector3(1, 0, 1), 0xFF,
                out _, out _);

            Assert.IsFalse(found);
        }

        [Test]
        public void FindPath_BlockedTriangle_ReturnsFalse()
        {
            var mesh = Create4TriNavMesh();
            // T0 blocked → cannot start at T0
            mesh.Triangles[0].isBlocked = true;

            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            bool found = pf.FindPath(
                new FPVector3(1, 0, 1), new FPVector3(5, 0, 1), 0xFF,
                out _, out _);

            Assert.IsFalse(found);
        }

        [Test]
        public void FindPath_BlockedMiddle_RoutesAround()
        {
            var mesh = Create4TriNavMesh();
            // T0 blocked, can start from T1 but the path to T2 must pass through T0
            // T1→T0(blocked) → no path
            mesh.Triangles[0].isBlocked = true;

            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // T1 (1,3) → T2 (5,1): T1 is only adjacent to T0, T0 blocked → unreachable
            bool found = pf.FindPath(
                new FPVector3(1, 0, 3), new FPVector3(5, 0, 1), 0xFF,
                out _, out _);

            Assert.IsFalse(found);
        }

        [Test]
        public void FindPath_AreaMaskFilter_ReturnsFalse()
        {
            var mesh = Create4TriNavMesh_WithAreaMask();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // areaMask=1 → cannot pass through T2(areaMask=2)
            // T0 (1,1) → T3 (5,3): T0→T2(filtered)→T3 blocked, T0→T1(dead end)
            bool found = pf.FindPath(
                new FPVector3(1, 0, 1), new FPVector3(5, 0, 3), 1,
                out _, out _);

            Assert.IsFalse(found);
        }

        #endregion

        #region FindPath — costMultiplier

        [Test]
        public void FindPath_CostMultiplier_AffectsRoute()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // Verify pathfinding with default cost
            bool found = pf.FindPath(
                new FPVector3(1, 0, 1), new FPVector3(5, 0, 1), 0xFF,
                out int[] corridor, out int length);

            Assert.IsTrue(found);
            Assert.IsTrue(length >= 2);
        }

        #endregion

        #region FindPath — edge cases

        [Test]
        public void FindPath_StartEqualsEnd_SameTriangle()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // Same position
            bool found = pf.FindPath(
                new FPVector3(1, 0, 1), new FPVector3(1, 0, 1), 0xFF,
                out int[] corridor, out int length);

            Assert.IsTrue(found);
            Assert.AreEqual(1, length);
        }

        [Test]
        public void FindPath_AllBlockedExceptStartEnd_NoPath()
        {
            var mesh = Create4TriNavMesh();
            // T0, T2, T3 blocked → can start from T1 but cannot go anywhere
            mesh.Triangles[0].isBlocked = true;
            mesh.Triangles[2].isBlocked = true;
            mesh.Triangles[3].isBlocked = true;

            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // T1 (1,3) → T2 (5,1): unreachable because T0 is blocked
            bool found = pf.FindPath(
                new FPVector3(1, 0, 3), new FPVector3(5, 0, 1), 0xFF,
                out _, out _);

            Assert.IsFalse(found);
        }

        [Test]
        public void FindPath_StartBlocked_ReturnsFalse()
        {
            var mesh = Create4TriNavMesh();
            mesh.Triangles[0].isBlocked = true;

            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // Start at T0 → starting triangle blocked
            bool found = pf.FindPath(
                new FPVector3(1, 0, 1), new FPVector3(5, 0, 1), 0xFF,
                out _, out _);

            Assert.IsFalse(found);
        }

        [Test]
        public void FindPath_EndBlocked_ReturnsFalse()
        {
            var mesh = Create4TriNavMesh();
            mesh.Triangles[2].isBlocked = true;

            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // T2 target triangle blocked
            bool found = pf.FindPath(
                new FPVector3(1, 0, 1), new FPVector3(5, 0, 1), 0xFF,
                out _, out _);

            Assert.IsFalse(found);
        }

        [Test]
        public void FindPath_HighCostMultiplier_StillFinds()
        {
            var mesh = Create4TriNavMesh();
            mesh.Triangles[2].costMultiplier = FP64.FromInt(100);

            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // High cost, but a path still exists
            bool found = pf.FindPath(
                new FPVector3(1, 0, 1), new FPVector3(5, 0, 1), 0xFF,
                out int[] corridor, out int length);

            Assert.IsTrue(found);
            Assert.IsTrue(length >= 2);
        }

        [Test]
        public void FindPath_CorridorContainsNoGaps()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // T1 → T3: long path
            bool found = pf.FindPath(
                new FPVector3(1, 0, 3), new FPVector3(5, 0, 3), 0xFF,
                out int[] corridor, out int length);

            Assert.IsTrue(found);

            // Consecutive triangles in the corridor must actually be adjacent
            for (int i = 0; i < length - 1; i++)
            {
                int cur = corridor[i];
                int next = corridor[i + 1];
                bool adjacent = false;
                for (int e = 0; e < 3; e++)
                {
                    if (mesh.Triangles[cur].GetNeighbor(e) == next)
                    {
                        adjacent = true;
                        break;
                    }
                }
                Assert.IsTrue(adjacent,
                    $"corridor[{i}]={cur} → corridor[{i + 1}]={next} not adjacent");
            }
        }

        [Test]
        public void FindPath_BothPointsOutside_ReturnsFalse()
        {
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("outside NavMesh"));
            LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("outside NavMesh"));

            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            bool found = pf.FindPath(
                new FPVector3(-10, 0, -10), new FPVector3(-5, 0, -5), 0xFF,
                out _, out _);

            Assert.IsFalse(found);
        }

        [Test]
        public void FindPath_AreaMaskStart_ReturnsFalse()
        {
            var mesh = Create4TriNavMesh_WithAreaMask();
            var query = new FPNavMeshQuery(mesh, _logger);
            var pf = new FPNavMeshPathfinder(mesh, query, _logger);

            // areaMask=2 → cannot start at T0(areaMask=1)
            bool found = pf.FindPath(
                new FPVector3(1, 0, 1), new FPVector3(5, 0, 1), 2,
                out _, out _);

            Assert.IsFalse(found);
        }

        #endregion
    }
}
