using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    [TestFixture]
    public class FPNavMeshFunnelTests
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
        /// 2-triangle NavMesh (same as FPNavMeshQueryTests).
        ///   v2(0,4)---v3(4,4)
        ///     |  \T1  / |
        ///     |   \  /  |
        ///     | T0 \/   |
        ///   v0(0,0)---v1(4,0)
        /// T0: v0,v1,v3   T1: v0,v3,v2
        /// </summary>
        private static FPNavMesh Create2TriNavMesh()
        {
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.Zero),
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)),
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.FromInt(4)),
            };

            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 3,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = 1,
                portal0Left = -1, portal0Right = -1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = 3, portal2Right = 0,
                centerXZ = new FPVector2(FP64.FromFloat(8f / 3f), FP64.FromFloat(4f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1,
                costMultiplier = FP64.One,
                isBlocked = false,
            };

            var t1 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 3, v2 = 2,
                neighbor0 = 0, neighbor1 = -1, neighbor2 = -1,
                portal0Left = 0, portal0Right = 3,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(8f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1,
                costMultiplier = FP64.One,
                isBlocked = false,
            };

            var triangles = new[] { t0, t1 };

            var bounds = new FPBounds2(
                new FPVector2(FP64.FromInt(2), FP64.FromInt(2)),
                new FPVector2(FP64.FromInt(4), FP64.FromInt(4))
            );

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

        /// <summary>
        /// 4-triangle strip NavMesh (same as FPNavMeshPathfinderTests).
        /// </summary>
        private static FPNavMesh Create4TriNavMesh()
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
                neighbor0 = -1, neighbor1 = 2, neighbor2 = 1,
                portal0Left = -1, portal0Right = -1,
                portal1Left = 1, portal1Right = 3,
                portal2Left = 3, portal2Right = 0,
                centerXZ = new FPVector2(FP64.FromFloat(8f / 3f), FP64.FromFloat(4f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var t1 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 3, v2 = 2,
                neighbor0 = 0, neighbor1 = -1, neighbor2 = -1,
                portal0Left = 0, portal0Right = 3,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(8f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var t2 = new FPNavMeshTriangle
            {
                v0 = 1, v1 = 4, v2 = 5,
                neighbor0 = -1, neighbor1 = 3, neighbor2 = 0,
                portal0Left = -1, portal0Right = -1,
                portal1Left = 4, portal1Right = 5,
                portal2Left = 3, portal2Right = 1,
                centerXZ = new FPVector2(FP64.FromFloat(16f / 3f), FP64.FromFloat(4f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var t3 = new FPNavMeshTriangle
            {
                v0 = 1, v1 = 5, v2 = 3,
                neighbor0 = 2, neighbor1 = -1, neighbor2 = -1,
                portal0Left = 5, portal0Right = 1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
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

        #endregion

        #region Single triangle

        [Test]
        public void Funnel_SingleTriangle_Returns2Waypoints()
        {
            var mesh = Create2TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            int[] corridor = { 0 };
            var start = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1));
            var end = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.FromInt(1));

            funnel.Funnel(corridor, 1, start, end,
                out FPVector3[] wps, out int wpCount);

            Assert.AreEqual(2, wpCount);
            Assert.AreEqual(1f, wps[0].x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, wps[0].z.ToFloat(), EPSILON);
            Assert.AreEqual(3f, wps[1].x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, wps[1].z.ToFloat(), EPSILON);
        }

        #endregion

        #region 2-triangle corridor

        [Test]
        public void Funnel_TwoTriangles_ProducesPath()
        {
            var mesh = Create2TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            int[] corridor = { 0, 1 };
            var start = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.FromInt(1));  // inside T0
            var end = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(3));    // inside T1

            funnel.Funnel(corridor, 2, start, end,
                out FPVector3[] wps, out int wpCount);

            Assert.IsTrue(wpCount >= 2);

            // Verify start and end points
            Assert.AreEqual(3f, wps[0].x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, wps[0].z.ToFloat(), EPSILON);
            Assert.AreEqual(1f, wps[wpCount - 1].x.ToFloat(), EPSILON);
            Assert.AreEqual(3f, wps[wpCount - 1].z.ToFloat(), EPSILON);
        }

        [Test]
        public void Funnel_TwoTriangles_HeightIsZero()
        {
            var mesh = Create2TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            int[] corridor = { 0, 1 };
            var start = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.FromInt(1));
            var end = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(3));

            funnel.Funnel(corridor, 2, start, end,
                out FPVector3[] wps, out int wpCount);

            // Y=0 plane, so all waypoint heights are 0
            for (int i = 0; i < wpCount; i++)
                Assert.AreEqual(0f, wps[i].y.ToFloat(), EPSILON);
        }

        #endregion

        #region 4-triangle corridor

        [Test]
        public void Funnel_FourTriangles_ProducesValidPath()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            // T1 → T0 → T2 → T3
            int[] corridor = { 1, 0, 2, 3 };
            var start = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(3));  // inside T1
            var end = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(3));    // inside T3

            funnel.Funnel(corridor, 4, start, end,
                out FPVector3[] wps, out int wpCount);

            Assert.IsTrue(wpCount >= 2);

            // Start point
            Assert.AreEqual(1f, wps[0].x.ToFloat(), EPSILON);
            Assert.AreEqual(3f, wps[0].z.ToFloat(), EPSILON);

            // End point
            Assert.AreEqual(5f, wps[wpCount - 1].x.ToFloat(), EPSILON);
            Assert.AreEqual(3f, wps[wpCount - 1].z.ToFloat(), EPSILON);
        }

        [Test]
        public void Funnel_StraightLine_MinimalWaypoints()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            // T0 → T2: straight-line path (z=1 line)
            int[] corridor = { 0, 2 };
            var start = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1));
            var end = new FPVector3(FP64.FromInt(7), FP64.Zero, FP64.FromInt(1));

            funnel.Funnel(corridor, 2, start, end,
                out FPVector3[] wps, out int wpCount);

            // Straight line, so start+end = 2 should be sufficient
            Assert.IsTrue(wpCount >= 2);
            Assert.AreEqual(1f, wps[0].x.ToFloat(), EPSILON);
            Assert.AreEqual(7f, wps[wpCount - 1].x.ToFloat(), EPSILON);
        }

        #endregion

        #region Edge cases

        [Test]
        public void Funnel_EmptyCorridor_ReturnsZeroWaypoints()
        {
            var mesh = Create2TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            funnel.Funnel(new int[0], 0, FPVector3.Zero, FPVector3.Zero,
                out _, out int wpCount);

            Assert.AreEqual(0, wpCount);
        }

        [Test]
        public void Funnel_StartEqualsEnd_Returns2Waypoints()
        {
            var mesh = Create2TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            int[] corridor = { 0 };
            var p = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1));

            funnel.Funnel(corridor, 1, p, p,
                out FPVector3[] wps, out int wpCount);

            Assert.AreEqual(2, wpCount);
            Assert.AreEqual(2f, wps[0].x.ToFloat(), EPSILON);
            Assert.AreEqual(2f, wps[1].x.ToFloat(), EPSILON);
        }

        [Test]
        public void Funnel_SingleTriangle_StartOnEdge()
        {
            var mesh = Create2TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            // Start on edge (v0-v1 edge, near z=0)
            int[] corridor = { 0 };
            var start = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromFloat(0.01f));
            var end = new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.FromInt(1));

            funnel.Funnel(corridor, 1, start, end,
                out FPVector3[] wps, out int wpCount);

            Assert.AreEqual(2, wpCount);
            Assert.AreEqual(2f, wps[0].x.ToFloat(), EPSILON);
        }

        [Test]
        public void Funnel_WaypointCountDoesNotExceedMax()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            // Long corridor
            int[] corridor = { 1, 0, 2, 3 };
            var start = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(3));
            var end = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(3));

            funnel.Funnel(corridor, 4, start, end,
                out FPVector3[] wps, out int wpCount);

            Assert.IsTrue(wpCount <= FPNavMeshFunnel.MAX_WAYPOINTS);
            Assert.IsTrue(wpCount >= 2);
        }

        [Test]
        public void Funnel_PathIsWithinNavMesh()
        {
            var mesh = Create4TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            int[] corridor = { 1, 0, 2, 3 };
            var start = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(3));
            var end = new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromInt(3));

            funnel.Funnel(corridor, 4, start, end,
                out FPVector3[] wps, out int wpCount);

            // All waypoints must be within the NavMesh bounds
            for (int i = 0; i < wpCount; i++)
            {
                float x = wps[i].x.ToFloat();
                float z = wps[i].z.ToFloat();
                Assert.IsTrue(x >= -EPSILON && x <= 8f + EPSILON,
                    $"waypoint[{i}].x={x} outside NavMesh bounds");
                Assert.IsTrue(z >= -EPSILON && z <= 4f + EPSILON,
                    $"waypoint[{i}].z={z} outside NavMesh bounds");
            }
        }

        [Test]
        public void Funnel_SameTriangleTwice_InCorridor()
        {
            var mesh = Create2TriNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            // Same triangle repeated twice (abnormal, but must not crash)
            int[] corridor = { 0, 0 };
            var start = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromInt(1));
            var end = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.FromInt(1));

            // Must not crash
            funnel.Funnel(corridor, 2, start, end,
                out FPVector3[] wps, out int wpCount);

            Assert.IsTrue(wpCount >= 2);
        }

        #endregion

        #region Thin triangle FP precision

        /// <summary>
        /// Thin triangle strip NavMesh.
        /// Simulates a corridor passing through nearly collinear portals.
        ///
        ///   v2(0,0.01) --- v3(4,0.01) --- v5(8,0.01)
        ///      |   T1  /   |   T3  /   |
        ///      |      /    |      /    |
        ///      |  T0 /     |  T2 /     |
        ///   v0(0,0)  --- v1(4,0)  --- v4(8,0)
        ///
        /// Extremely thin triangle strip with a height of 0.01.
        /// </summary>
        private static FPNavMesh CreateThinStripNavMesh()
        {
            var thin = FP64.FromFloat(0.01f);
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),                 // v0: (0,0)
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.Zero),           // v1: (4,0)
                new FPVector3(FP64.Zero, FP64.Zero, thin),                      // v2: (0,0.01)
                new FPVector3(FP64.FromInt(4), FP64.Zero, thin),                // v3: (4,0.01)
                new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero),           // v4: (8,0)
                new FPVector3(FP64.FromInt(8), FP64.Zero, thin),                // v5: (8,0.01)
            };

            // T0: v0,v1,v3 (lower triangle)  T1: v0,v3,v2 (upper triangle)
            // T2: v1,v4,v5 (lower triangle)  T3: v1,v5,v3 (upper triangle)
            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 3,
                neighbor0 = -1, neighbor1 = 2, neighbor2 = 1,
                portal0Left = -1, portal0Right = -1,
                portal1Left = 1, portal1Right = 3,
                portal2Left = 3, portal2Right = 0,
                centerXZ = new FPVector2(FP64.FromFloat(8f / 3f), FP64.FromFloat(0.01f / 3f)),
                area = FP64.FromFloat(0.02f),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var t1 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 3, v2 = 2,
                neighbor0 = 0, neighbor1 = -1, neighbor2 = -1,
                portal0Left = 0, portal0Right = 3,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(0.02f / 3f)),
                area = FP64.FromFloat(0.02f),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var t2 = new FPNavMeshTriangle
            {
                v0 = 1, v1 = 4, v2 = 5,
                neighbor0 = -1, neighbor1 = 3, neighbor2 = 0,
                portal0Left = -1, portal0Right = -1,
                portal1Left = 4, portal1Right = 5,
                portal2Left = 3, portal2Right = 1,
                centerXZ = new FPVector2(FP64.FromFloat(16f / 3f), FP64.FromFloat(0.01f / 3f)),
                area = FP64.FromFloat(0.02f),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var t3 = new FPNavMeshTriangle
            {
                v0 = 1, v1 = 5, v2 = 3,
                neighbor0 = 2, neighbor1 = -1, neighbor2 = -1,
                portal0Left = 5, portal0Right = 1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromFloat(16f / 3f), FP64.FromFloat(0.02f / 3f)),
                area = FP64.FromFloat(0.02f),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var triangles = new[] { t0, t1, t2, t3 };

            var bounds = new FPBounds2(
                new FPVector2(FP64.FromInt(4), FP64.FromFloat(0.005f)),
                new FPVector2(FP64.FromInt(8), thin)
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

        [Test]
        public void Funnel_ThinTriangleStrip_DoesNotCrash()
        {
            var mesh = CreateThinStripNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            // T0 → T2 corridor (passing through thin triangle portal)
            int[] corridor = { 0, 2 };
            var start = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromFloat(0.003f));
            var end = new FPVector3(FP64.FromInt(7), FP64.Zero, FP64.FromFloat(0.003f));

            funnel.Funnel(corridor, 2, start, end,
                out FPVector3[] wps, out int wpCount);

            Assert.IsTrue(wpCount >= 2,
                "At least 2 waypoints generated on thin triangle strip");
        }

        [Test]
        public void Funnel_ThinTriangleStrip_WaypointsWithinBounds()
        {
            var mesh = CreateThinStripNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            int[] corridor = { 0, 2 };
            var start = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromFloat(0.003f));
            var end = new FPVector3(FP64.FromInt(7), FP64.Zero, FP64.FromFloat(0.003f));

            funnel.Funnel(corridor, 2, start, end,
                out FPVector3[] wps, out int wpCount);

            for (int i = 0; i < wpCount; i++)
            {
                float z = wps[i].z.ToFloat();
                Assert.IsTrue(z >= -0.1f && z <= 0.1f,
                    $"waypoint[{i}].z={z} is outside thin strip bounds");
            }
        }

        [Test]
        public void Funnel_ThinTriangleStrip_NoExcessiveWaypoints()
        {
            var mesh = CreateThinStripNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            // T1 → T0 → T2 → T3 (4-triangle corridor)
            int[] corridor = { 1, 0, 2, 3 };
            var start = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromFloat(0.007f));
            var end = new FPVector3(FP64.FromInt(7), FP64.Zero, FP64.FromFloat(0.007f));

            funnel.Funnel(corridor, 4, start, end,
                out FPVector3[] wps, out int wpCount);

            // Nearly straight-line path — before the epsilon fix, thin portals could
            // generate a large number of unnecessary waypoints
            // Linearizer is applied so the final result must be concise
            Assert.IsTrue(wpCount >= 2);
            Assert.IsTrue(wpCount <= 6,
                $"{wpCount} waypoints is excessive for a straight path on thin triangles");
        }

        [Test]
        public void Funnel_ThinTriangleSingleCorridor_Returns2Waypoints()
        {
            var mesh = CreateThinStripNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var funnel = new FPNavMeshFunnel(mesh, query, _logger);

            // Single thin triangle
            int[] corridor = { 0 };
            var start = new FPVector3(FP64.FromInt(1), FP64.Zero, FP64.FromFloat(0.002f));
            var end = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.FromFloat(0.002f));

            funnel.Funnel(corridor, 1, start, end,
                out FPVector3[] wps, out int wpCount);

            Assert.AreEqual(2, wpCount);
            Assert.AreEqual(1f, wps[0].x.ToFloat(), EPSILON);
            Assert.AreEqual(3f, wps[1].x.ToFloat(), EPSILON);
        }

        #endregion
    }
}
