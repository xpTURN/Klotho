using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    [TestFixture]
    public class FPNavMeshQueryTests
    {
        private const float EPSILON = 0.01f;

        // Shared triangle: (0,0), (4,0), (0,3)
        private FPVector2 _a;
        private FPVector2 _b;
        private FPVector2 _c;

        ILogger _logger = null;
        
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Configure LoggerFactory (same as ZLogger)
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });

            _logger = loggerFactory.CreateLogger("Tests");
        }

        [SetUp]
        public void SetUp()
        {
            _a = new FPVector2(0, 0);
            _b = new FPVector2(4, 0);
            _c = new FPVector2(0, 3);
        }

        #region NavMesh helpers

        /// <summary>
        /// Create a 2-triangle NavMesh for testing.
        ///
        ///   v2(0,0,4)
        ///   |\
        ///   | \ T0
        ///   |  \
        ///   |   v1(4,0,0)
        ///   |  /
        ///   | / T1
        ///   |/
        ///   v0(0,0,0)
        ///              v3(4,0,4) acts as the third vertex of T0
        ///
        /// Actual layout (XZ plane):
        ///   v2(0,4) --- v3(4,4)
        ///     |  \T0  /  |
        ///     |   \  /   |
        ///     |    \/    |
        ///     |    v1(4,0)|
        ///     |   /      |
        ///     |  / T1    |
        ///     | /        |
        ///   v0(0,0)------+
        ///
        /// Simplified: split the square [0,4]x[0,4] along the diagonal
        ///   v0(0,0,0), v1(4,0,0), v2(0,0,4), v3(4,0,4)
        ///   T0: v0, v1, v3  (lower triangle)
        ///   T1: v0, v3, v2  (upper triangle)
        /// </summary>
        private static FPNavMesh CreateTestNavMesh()
        {
            // 4 vertices on the Y=0 plane (used as XZ)
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),             // v0: (0,0)
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.Zero),       // v1: (4,0)
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)),       // v2: (0,4)
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.FromInt(4)), // v3: (4,4)
            };

            var two = FP64.FromInt(2);

            // T0: v0, v1, v3 (lower triangle)
            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 3,
                neighbor0 = -1,  // edge v0-v1: boundary (bottom)
                neighbor1 = -1,  // edge v1-v3: boundary (right)
                neighbor2 = 1,   // edge v3-v0: shared with T1
                portal2Left = 3, portal2Right = 0,
                portal0Left = -1, portal0Right = -1,
                portal1Left = -1, portal1Right = -1,
                centerXZ = new FPVector2(FP64.FromFloat(8f / 3f), FP64.FromFloat(4f / 3f)),
                area = FP64.FromInt(8),
                areaMask = 1,
                costMultiplier = FP64.One,
                isBlocked = false,
            };

            // T1: v0, v3, v2 (upper triangle)
            var t1 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 3, v2 = 2,
                neighbor0 = 0,   // edge v0-v3: shared with T0
                neighbor1 = -1,  // edge v3-v2: boundary (top)
                neighbor2 = -1,  // edge v2-v0: boundary (left)
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

            // Bounds: center=(2,2), size=(4,4)
            var bounds = new FPBounds2(
                new FPVector2(two, two),
                new FPVector2(FP64.FromInt(4), FP64.FromInt(4))
            );

            // Single-cell grid (entire NavMesh = 1 cell)
            var gridCells = new[] { 0, 2 }; // start=0, count=2
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
        /// For height validation: v0 y=0, v1 y=2, v2 y=0, v3 y=4 (slope)
        /// </summary>
        private static FPNavMesh CreateSlopedNavMesh()
        {
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),                          // v0
                new FPVector3(FP64.FromInt(4), FP64.FromInt(2), FP64.Zero),               // v1
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)),                     // v2
                new FPVector3(FP64.FromInt(4), FP64.FromInt(4), FP64.FromInt(4)),          // v3
            };

            var two = FP64.FromInt(2);

            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 3,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = 1,
                portal2Left = 3, portal2Right = 0,
                portal0Left = -1, portal0Right = -1,
                portal1Left = -1, portal1Right = -1,
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
                new FPVector2(two, two),
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

        #endregion

        #region PointInTriangle2D

        [Test]
        public void PointInTriangle2D_InsidePoint_ReturnsTrue()
        {
            var p = new FPVector2(1, 1);
            Assert.IsTrue(FPNavMeshQuery.PointInTriangle2D(p, _a, _b, _c));
        }

        [Test]
        public void PointInTriangle2D_OnEdge_ReturnsTrue()
        {
            // Midpoint of edge AB (2, 0)
            var p = new FPVector2(2, 0);
            Assert.IsTrue(FPNavMeshQuery.PointInTriangle2D(p, _a, _b, _c));
        }

        [Test]
        public void PointInTriangle2D_OnVertex_ReturnsTrue()
        {
            Assert.IsTrue(FPNavMeshQuery.PointInTriangle2D(_a, _a, _b, _c));
            Assert.IsTrue(FPNavMeshQuery.PointInTriangle2D(_b, _a, _b, _c));
            Assert.IsTrue(FPNavMeshQuery.PointInTriangle2D(_c, _a, _b, _c));
        }

        [Test]
        public void PointInTriangle2D_OutsidePoint_ReturnsFalse()
        {
            var p = new FPVector2(3, 3);
            Assert.IsFalse(FPNavMeshQuery.PointInTriangle2D(p, _a, _b, _c));
        }

        [Test]
        public void PointInTriangle2D_FarOutside_ReturnsFalse()
        {
            var p = new FPVector2(-1, -1);
            Assert.IsFalse(FPNavMeshQuery.PointInTriangle2D(p, _a, _b, _c));
        }

        #endregion

        #region BarycentricCoords2D

        [Test]
        public void BarycentricCoords2D_AtVertexA_Returns100()
        {
            FPNavMeshQuery.BarycentricCoords2D(_a, _a, _b, _c, out FP64 u, out FP64 v, out FP64 w);

            Assert.AreEqual(1f, u.ToFloat(), EPSILON);
            Assert.AreEqual(0f, v.ToFloat(), EPSILON);
            Assert.AreEqual(0f, w.ToFloat(), EPSILON);
        }

        [Test]
        public void BarycentricCoords2D_AtVertexB_Returns010()
        {
            FPNavMeshQuery.BarycentricCoords2D(_b, _a, _b, _c, out FP64 u, out FP64 v, out FP64 w);

            Assert.AreEqual(0f, u.ToFloat(), EPSILON);
            Assert.AreEqual(1f, v.ToFloat(), EPSILON);
            Assert.AreEqual(0f, w.ToFloat(), EPSILON);
        }

        [Test]
        public void BarycentricCoords2D_AtVertexC_Returns001()
        {
            FPNavMeshQuery.BarycentricCoords2D(_c, _a, _b, _c, out FP64 u, out FP64 v, out FP64 w);

            Assert.AreEqual(0f, u.ToFloat(), EPSILON);
            Assert.AreEqual(0f, v.ToFloat(), EPSILON);
            Assert.AreEqual(1f, w.ToFloat(), EPSILON);
        }

        [Test]
        public void BarycentricCoords2D_SumIsOne()
        {
            var p = new FPVector2(1, 1);
            FPNavMeshQuery.BarycentricCoords2D(p, _a, _b, _c, out FP64 u, out FP64 v, out FP64 w);

            float sum = u.ToFloat() + v.ToFloat() + w.ToFloat();
            Assert.AreEqual(1f, sum, EPSILON);
        }

        [Test]
        public void BarycentricCoords2D_Centroid_EqualWeights()
        {
            // Not an equilateral triangle, so centroid = (a+b+c)/3
            var centroid = new FPVector2(
                FP64.FromFloat(4f / 3f),
                FP64.FromFloat(1f)
            );
            FPNavMeshQuery.BarycentricCoords2D(centroid, _a, _b, _c, out FP64 u, out FP64 v, out FP64 w);

            float expected = 1f / 3f;
            Assert.AreEqual(expected, u.ToFloat(), EPSILON);
            Assert.AreEqual(expected, v.ToFloat(), EPSILON);
            Assert.AreEqual(expected, w.ToFloat(), EPSILON);
        }

        #endregion

        #region ClosestPointOnSegment2D

        [Test]
        public void ClosestPointOnSegment2D_ProjectsOntoMiddle()
        {
            var a = new FPVector2(0, 0);
            var b = new FPVector2(4, 0);
            var p = new FPVector2(2, 3);

            var result = FPNavMeshQuery.ClosestPointOnSegment2D(p, a, b);

            Assert.AreEqual(2f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPointOnSegment2D_ClampsToStart()
        {
            var a = new FPVector2(0, 0);
            var b = new FPVector2(4, 0);
            var p = new FPVector2(-2, 1);

            var result = FPNavMeshQuery.ClosestPointOnSegment2D(p, a, b);

            Assert.AreEqual(0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPointOnSegment2D_ClampsToEnd()
        {
            var a = new FPVector2(0, 0);
            var b = new FPVector2(4, 0);
            var p = new FPVector2(6, 1);

            var result = FPNavMeshQuery.ClosestPointOnSegment2D(p, a, b);

            Assert.AreEqual(4f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(0f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPointOnSegment2D_DegenerateSegment_ReturnsA()
        {
            var a = new FPVector2(2, 3);
            var p = new FPVector2(5, 7);

            var result = FPNavMeshQuery.ClosestPointOnSegment2D(p, a, a);

            Assert.AreEqual(2f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(3f, result.y.ToFloat(), EPSILON);
        }

        #endregion

        #region TriangleArea2D

        [Test]
        public void TriangleArea2D_CCW_PositiveArea()
        {
            // (0,0), (4,0), (0,3) -> CCW -> area = 6
            FP64 area = FPNavMeshQuery.TriangleArea2D(_a, _b, _c);
            Assert.AreEqual(6f, area.ToFloat(), EPSILON);
        }

        [Test]
        public void TriangleArea2D_CW_NegativeArea()
        {
            // Reversed winding: (0,0), (0,3), (4,0) -> CW -> area = -6
            FP64 area = FPNavMeshQuery.TriangleArea2D(_a, _c, _b);
            Assert.AreEqual(-6f, area.ToFloat(), EPSILON);
        }

        [Test]
        public void TriangleArea2D_Degenerate_Zero()
        {
            // Collinear triangle
            var a = new FPVector2(0, 0);
            var b = new FPVector2(2, 0);
            var c = new FPVector2(4, 0);

            FP64 area = FPNavMeshQuery.TriangleArea2D(a, b, c);
            Assert.AreEqual(0f, area.ToFloat(), EPSILON);
        }

        [Test]
        public void TriangleArea2D_UnitTriangle()
        {
            // (0,0), (1,0), (0,1) -> area = 0.5
            var a = new FPVector2(0, 0);
            var b = new FPVector2(1, 0);
            var c = new FPVector2(0, 1);

            FP64 area = FPNavMeshQuery.TriangleArea2D(a, b, c);
            Assert.AreEqual(0.5f, area.ToFloat(), EPSILON);
        }

        #endregion

        #region FindTriangle

        [Test]
        public void FindTriangle_InsideLowerTriangle_Returns0()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // (3, 1) -> inside T0 lower triangle (x > z region)
            int tri = query.FindTriangle(new FPVector2(3, 1));
            Assert.AreEqual(0, tri);
        }

        [Test]
        public void FindTriangle_InsideUpperTriangle_Returns1()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // (1, 3) -> inside T1 upper triangle (z > x region)
            int tri = query.FindTriangle(new FPVector2(1, 3));
            Assert.AreEqual(1, tri);
        }

        [Test]
        public void FindTriangle_OutsideBounds_ReturnsMinus1()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            int tri = query.FindTriangle(new FPVector2(5, 5));
            Assert.AreEqual(-1, tri);
        }

        [Test]
        public void FindTriangle_OnDiagonal_ReturnsValid()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // On the diagonal (2, 2) -> T0 or T1 (both valid)
            int tri = query.FindTriangle(new FPVector2(2, 2));
            Assert.IsTrue(tri == 0 || tri == 1);
        }

        [Test]
        public void FindTriangle_AtVertex_ReturnsValid()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // v0 (0, 0) -- vertex
            int tri = query.FindTriangle(FPVector2.Zero);
            Assert.IsTrue(tri >= 0);
        }

        #endregion

        #region SampleHeight

        [Test]
        public void SampleHeight_FlatMesh_ReturnsZero()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // Y=0 plane, so height is 0 everywhere
            FP64 h = query.SampleHeight(new FPVector2(2, 1), 0);
            Assert.AreEqual(0f, h.ToFloat(), EPSILON);
        }

        [Test]
        public void SampleHeight_SlopedMesh_AtV1_ReturnsHeight()
        {
            // v1 = (4, y=2, 0) -> at xz=(4,0) height is 2
            var mesh = CreateSlopedNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            FP64 h = query.SampleHeight(new FPVector2(4, 0), 0);
            Assert.AreEqual(2f, h.ToFloat(), EPSILON);
        }

        [Test]
        public void SampleHeight_SlopedMesh_AtV0_ReturnsZero()
        {
            // v0 = (0, y=0, 0)
            var mesh = CreateSlopedNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            FP64 h = query.SampleHeight(FPVector2.Zero, 0);
            Assert.AreEqual(0f, h.ToFloat(), EPSILON);
        }

        [Test]
        public void SampleHeight_SlopedMesh_Midpoint_Interpolated()
        {
            // T0 center (8/3, 4/3) -> barycentric interpolation of v0(y=0), v1(y=2), v3(y=4)
            var mesh = CreateSlopedNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            FP64 h = query.SampleHeight(new FPVector2(FP64.FromInt(2), FP64.FromInt(1)), 0);
            // (2,1) is inside T0. Barycentric interpolation of v0(0), v1(2), v3(4)
            Assert.IsTrue(h > FP64.Zero && h < FP64.FromInt(4));
        }

        #endregion

        #region ClosestPointOnNavMesh

        [Test]
        public void ClosestPointOnNavMesh_InsidePoint_ReturnsSame()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            var p = new FPVector2(2, 1);
            FPVector2 result = query.ClosestPointOnNavMesh(p, out int triIdx);

            Assert.IsTrue(triIdx >= 0);
            Assert.AreEqual(2f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPointOnNavMesh_OutsidePoint_ReturnsEdgePoint()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // (-1, 2) -> outside NavMesh, closest point on left edge (v0-v2) = (0, 2)
            var p = new FPVector2(-1, 2);
            FPVector2 result = query.ClosestPointOnNavMesh(p, out int triIdx);

            Assert.IsTrue(triIdx >= 0);
            Assert.AreEqual(0f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(2f, result.y.ToFloat(), EPSILON);
        }

        #endregion

        #region ProjectToNavMesh

        [Test]
        public void ProjectToNavMesh_WithinMaxDist_ReturnsSnapped()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // (-0.5, 2) -> distance 0.5, maxDist=1 -> success
            var p = new FPVector2(FP64.FromFloat(-0.5f), FP64.FromInt(2));
            FPVector2 result = query.ProjectToNavMesh(p, FP64.One, out int triIdx);

            Assert.IsTrue(triIdx >= 0);
            Assert.AreEqual(0f, result.x.ToFloat(), EPSILON);
        }

        [Test]
        public void ProjectToNavMesh_BeyondMaxDist_ReturnsMinus1()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // (-5, 2) -> distance 5, maxDist=1 -> failure
            var p = new FPVector2(-5, 2);
            query.ProjectToNavMesh(p, FP64.One, out int triIdx);

            Assert.AreEqual(-1, triIdx);
        }

        #endregion

        #region Edge cases

        [Test]
        public void FindTriangle_NegativeCoords_ReturnsMinus1()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            int tri = query.FindTriangle(new FPVector2(-1, -1));
            Assert.AreEqual(-1, tri);
        }

        [Test]
        public void FindTriangle_AtCornerVertex_ReturnsValid()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // v0 = (0,0) vertex -- at the grid origin, so inside the cell
            int tri = query.FindTriangle(new FPVector2(FP64.Zero, FP64.Zero));
            Assert.IsTrue(tri >= 0);
        }

        [Test]
        public void FindTriangle_JustInsideBoundary_ReturnsValid()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // Just inside the boundary
            int tri = query.FindTriangle(new FPVector2(FP64.FromFloat(0.01f), FP64.FromFloat(0.01f)));
            Assert.IsTrue(tri >= 0);
        }

        [Test]
        public void FindTriangle_JustOutsideBoundary_ReturnsMinus1()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // Just outside the boundary
            int tri = query.FindTriangle(new FPVector2(FP64.FromFloat(4.1f), FP64.FromFloat(4.1f)));
            Assert.AreEqual(-1, tri);
        }

        [Test]
        public void SampleHeight_SlopedMesh_AtV3_ReturnsHeight()
        {
            // v3 = (4, y=4, 4)
            var mesh = CreateSlopedNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            FP64 h = query.SampleHeight(new FPVector2(4, 4), 0);
            Assert.AreEqual(4f, h.ToFloat(), EPSILON);
        }

        [Test]
        public void ClosestPointOnNavMesh_NearOutside_ReturnsNearestEdge()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // Just outside the NavMesh (within adjacent cell search range)
            var p = new FPVector2(FP64.FromInt(5), FP64.FromInt(2));
            FPVector2 result = query.ClosestPointOnNavMesh(p, out int triIdx);

            Assert.IsTrue(triIdx >= 0);
            // Should be a point on the edge of the NavMesh range [0,4]x[0,4]
            Assert.IsTrue(result.x.ToFloat() >= -EPSILON && result.x.ToFloat() <= 4f + EPSILON);
            Assert.IsTrue(result.y.ToFloat() >= -EPSILON && result.y.ToFloat() <= 4f + EPSILON);
        }

        [Test]
        public void ClosestPointOnNavMesh_FarOutside_ReturnsMinus1()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // Very far point -- outside adjacent cell range, so not found
            var p = new FPVector2(100, 100);
            query.ClosestPointOnNavMesh(p, out int triIdx);

            Assert.AreEqual(-1, triIdx);
        }

        [Test]
        public void ClosestPointOnNavMesh_InsidePoint_ReturnsSameTriangleIndex()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // (1, 1) -> inside T0
            var p = new FPVector2(1, 1);
            query.ClosestPointOnNavMesh(p, out int triIdx);

            // Same result as FindTriangle
            int expected = query.FindTriangle(p);
            Assert.AreEqual(expected, triIdx);
        }

        [Test]
        public void ProjectToNavMesh_InsidePoint_ReturnsSame()
        {
            var mesh = CreateTestNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            var p = new FPVector2(2, 1);
            FPVector2 result = query.ProjectToNavMesh(p, FP64.FromInt(10), out int triIdx);

            Assert.IsTrue(triIdx >= 0);
            Assert.AreEqual(2f, result.x.ToFloat(), EPSILON);
            Assert.AreEqual(1f, result.y.ToFloat(), EPSILON);
        }

        [Test]
        public void PointInTriangle2D_NearEdgeMiss_ReturnsFalse()
        {
            // Just outside the edge
            var p = new FPVector2(FP64.FromFloat(2.1f), FP64.FromFloat(1.6f));
            // For triangle (0,0) (4,0) (0,3), on edge AC: x + 4y/3 <= 4
            // 2.1 + 4*1.6/3 = 2.1 + 2.133 = 4.233 > 4 -> outside
            Assert.IsFalse(FPNavMeshQuery.PointInTriangle2D(p, _a, _b, _c));
        }

        [Test]
        public void BarycentricCoords2D_EdgeMidpoint_ValidCoords()
        {
            // Midpoint of edge AB (2, 0)
            var p = new FPVector2(2, 0);
            FPNavMeshQuery.BarycentricCoords2D(p, _a, _b, _c, out FP64 u, out FP64 v, out FP64 w);

            Assert.AreEqual(0.5f, u.ToFloat(), EPSILON);
            Assert.AreEqual(0.5f, v.ToFloat(), EPSILON);
            Assert.AreEqual(0f, w.ToFloat(), EPSILON);
        }

        #endregion

        #region Thin triangle FP precision

        /// <summary>
        /// Create a very thin triangle (height 0.001, width 10).
        /// </summary>
        private static FPNavMesh CreateThinTriangleNavMesh()
        {
            // Extremely thin triangle: (0,0) - (10,0) - (5,0.001)
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),                                   // v0: (0,0)
                new FPVector3(FP64.FromInt(10), FP64.Zero, FP64.Zero),                             // v1: (10,0)
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromFloat(0.001f)),                 // v2: (5,0.001)
                new FPVector3(FP64.FromInt(5), FP64.Zero, FP64.FromFloat(-0.001f)),                // v3: (5,-0.001)
            };

            // T0: v0, v1, v2 (upper thin triangle)
            // T1: v0, v3, v1 (lower thin triangle, mirrored)
            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 2,
                neighbor0 = 1, neighbor1 = -1, neighbor2 = -1,
                portal0Left = 0, portal0Right = 1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromInt(5), FP64.FromFloat(0.0003f)),
                area = FP64.FromFloat(0.005f),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var t1 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 3, v2 = 1,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = 0,
                portal0Left = -1, portal0Right = -1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = 1, portal2Right = 0,
                centerXZ = new FPVector2(FP64.FromInt(5), FP64.FromFloat(-0.0003f)),
                area = FP64.FromFloat(0.005f),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var triangles = new[] { t0, t1 };

            var bounds = new FPBounds2(
                new FPVector2(FP64.FromInt(5), FP64.Zero),
                new FPVector2(FP64.FromInt(10), FP64.FromFloat(0.002f))
            );

            var gridCells = new[] { 0, 2 };
            var gridTriangles = new[] { 0, 1 };

            return new FPNavMesh(
                vertices, triangles, bounds,
                gridCells, gridTriangles,
                gridWidth: 1, gridHeight: 1,
                gridCellSize: FP64.FromInt(10),
                gridOrigin: FPVector2.Zero
            );
        }

        /// <summary>
        /// Coordinates for testing a degenerate triangle (3 collinear points).
        /// </summary>
        private static void GetDegenerateTriangle(out FPVector2 a, out FPVector2 b, out FPVector2 c)
        {
            a = new FPVector2(FP64.Zero, FP64.Zero);
            b = new FPVector2(FP64.FromInt(10), FP64.Zero);
            c = new FPVector2(FP64.FromInt(5), FP64.Zero);
        }

        /// <summary>
        /// NavMesh for a thin sloped triangle with varying heights.
        /// v0=(0,y=0,0), v1=(10,y=5,0), v2=(5,y=2.5,0.001)
        /// </summary>
        private static FPNavMesh CreateThinSlopedNavMesh()
        {
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(10), FP64.FromInt(5), FP64.Zero),
                new FPVector3(FP64.FromInt(5), FP64.FromFloat(2.5f), FP64.FromFloat(0.001f)),
            };

            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 2,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = -1,
                portal0Left = -1, portal0Right = -1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromInt(5), FP64.FromFloat(0.0003f)),
                area = FP64.FromFloat(0.005f),
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var triangles = new[] { t0 };

            var bounds = new FPBounds2(
                new FPVector2(FP64.FromInt(5), FP64.Zero),
                new FPVector2(FP64.FromInt(10), FP64.FromFloat(0.002f))
            );

            var gridCells = new[] { 0, 1 };
            var gridTriangles = new[] { 0 };

            return new FPNavMesh(
                vertices, triangles, bounds,
                gridCells, gridTriangles,
                gridWidth: 1, gridHeight: 1,
                gridCellSize: FP64.FromInt(10),
                gridOrigin: FPVector2.Zero
            );
        }

        [Test]
        public void PointInTriangle2D_ThinTriangle_PointOnEdge_ReturnsTrue()
        {
            // Very thin triangle: (0,0)-(10,0)-(5,0.001)
            var a = new FPVector2(FP64.Zero, FP64.Zero);
            var b = new FPVector2(FP64.FromInt(10), FP64.Zero);
            var c = new FPVector2(FP64.FromInt(5), FP64.FromFloat(0.001f));

            // Midpoint of the bottom edge -- cross is nearly 0
            var p = new FPVector2(FP64.FromInt(5), FP64.Zero);
            Assert.IsTrue(FPNavMeshQuery.PointInTriangle2D(p, a, b, c),
                "A point on the edge of a thin triangle should be considered inside");
        }

        [Test]
        public void PointInTriangle2D_ThinTriangle_NearApex_ReturnsTrue()
        {
            // (0,0)-(10,0)-(5,0.001)
            var a = new FPVector2(FP64.Zero, FP64.Zero);
            var b = new FPVector2(FP64.FromInt(10), FP64.Zero);
            var c = new FPVector2(FP64.FromInt(5), FP64.FromFloat(0.001f));

            // Near vertex c -- cross value is very small
            var p = new FPVector2(FP64.FromInt(5), FP64.FromFloat(0.0005f));
            Assert.IsTrue(FPNavMeshQuery.PointInTriangle2D(p, a, b, c),
                "A point near a vertex of a thin triangle should be considered inside");
        }

        [Test]
        public void PointInTriangle2D_ThinTriangle_OutsideApex_ReturnsFalse()
        {
            // (0,0)-(10,0)-(5,0.001)
            var a = new FPVector2(FP64.Zero, FP64.Zero);
            var b = new FPVector2(FP64.FromInt(10), FP64.Zero);
            var c = new FPVector2(FP64.FromInt(5), FP64.FromFloat(0.001f));

            // Outside the vertex -- clearly outside
            var p = new FPVector2(FP64.FromInt(5), FP64.FromFloat(0.01f));
            Assert.IsFalse(FPNavMeshQuery.PointInTriangle2D(p, a, b, c),
                "A point outside a thin triangle should be false");
        }

        [Test]
        public void PointInTriangle2D_DegenerateTriangle_EdgePoint_ReturnsTrueOrFalse()
        {
            // Fully degenerate triangle (3 collinear points) -- should not crash
            GetDegenerateTriangle(out var a, out var b, out var c);

            var p = new FPVector2(FP64.FromInt(5), FP64.Zero);
            // Not crashing matters more than the result itself
            FPNavMeshQuery.PointInTriangle2D(p, a, b, c);
            Assert.Pass("Ran without crashing on a degenerate triangle");
        }

        [Test]
        public void BarycentricCoords2D_DegenerateTriangle_EqualWeightFallback()
        {
            // Degenerate triangle (3 collinear points) -> denom ~= 0 -> equal-weight fallback
            GetDegenerateTriangle(out var a, out var b, out var c);

            var p = new FPVector2(FP64.FromInt(5), FP64.Zero);
            FPNavMeshQuery.BarycentricCoords2D(p, a, b, c, out FP64 u, out FP64 v, out FP64 w);

            float third = 1f / 3f;
            Assert.AreEqual(third, u.ToFloat(), EPSILON, "u = 1/3 on a degenerate triangle");
            Assert.AreEqual(third, v.ToFloat(), EPSILON, "v = 1/3 on a degenerate triangle");
            Assert.AreEqual(third, w.ToFloat(), EPSILON, "w = 1/3 on a degenerate triangle");
        }

        [Test]
        public void BarycentricCoords2D_ThinTriangle_SumIsOne()
        {
            // Sum of barycentric coordinates should be 1 even on a very thin triangle
            var a = new FPVector2(FP64.Zero, FP64.Zero);
            var b = new FPVector2(FP64.FromInt(10), FP64.Zero);
            var c = new FPVector2(FP64.FromInt(5), FP64.FromFloat(0.001f));

            var p = new FPVector2(FP64.FromInt(3), FP64.FromFloat(0.0002f));
            FPNavMeshQuery.BarycentricCoords2D(p, a, b, c, out FP64 u, out FP64 v, out FP64 w);

            float sum = u.ToFloat() + v.ToFloat() + w.ToFloat();
            Assert.AreEqual(1f, sum, EPSILON, "u+v+w should equal 1 even on a thin triangle");
        }

        [Test]
        public void SampleHeight_ThinSlopedTriangle_HeightClampedToVertexRange()
        {
            // On a thin sloped triangle, the height should not exceed the vertex range
            var mesh = CreateThinSlopedNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // Sample height at a point inside the triangle
            var p = new FPVector2(FP64.FromInt(3), FP64.FromFloat(0.0002f));
            FP64 h = query.SampleHeight(p, 0);

            // v0.y=0, v1.y=5, v2.y=2.5 -> range [0, 5]
            Assert.IsTrue(h >= FP64.Zero, $"height {h.ToFloat()} < 0 (vertex min)");
            Assert.IsTrue(h <= FP64.FromInt(5), $"height {h.ToFloat()} > 5 (vertex max)");
        }

        [Test]
        public void SampleHeight_DegenerateTriangle_DoesNotCrash()
        {
            // SampleHeight should not crash on a degenerate triangle (3 collinear points)
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(10), FP64.FromInt(5), FP64.Zero),
                new FPVector3(FP64.FromInt(5), FP64.FromFloat(2.5f), FP64.Zero),  // collinear
            };

            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 2,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = -1,
                portal0Left = -1, portal0Right = -1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromInt(5), FP64.Zero),
                area = FP64.Zero,
                areaMask = 1, costMultiplier = FP64.One, isBlocked = false,
            };

            var bounds = new FPBounds2(
                new FPVector2(FP64.FromInt(5), FP64.Zero),
                new FPVector2(FP64.FromInt(10), FP64.FromFloat(0.1f))
            );

            var mesh = new FPNavMesh(
                vertices, new[] { t0 }, bounds,
                new[] { 0, 1 }, new[] { 0 },
                gridWidth: 1, gridHeight: 1,
                gridCellSize: FP64.FromInt(10),
                gridOrigin: FPVector2.Zero
            );
            var query = new FPNavMeshQuery(mesh, _logger);

            // Should not crash
            FP64 h = query.SampleHeight(new FPVector2(FP64.FromInt(5), FP64.Zero), 0);

            // Equal-weight fallback: (0 + 5 + 2.5) / 3 = 2.5 -> range [0, 5]
            Assert.IsTrue(h >= FP64.Zero && h <= FP64.FromInt(5),
                $"degenerate triangle height {h.ToFloat()} is outside vertex range");
        }

        [Test]
        public void FindTriangle_ThinTriangle_CenterPoint_ReturnsValid()
        {
            var mesh = CreateThinTriangleNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            // Center of the thin triangle -- detection may fail without an epsilon
            var p = new FPVector2(FP64.FromInt(5), FP64.FromFloat(0.0003f));
            int tri = query.FindTriangle(p);

            Assert.IsTrue(tri >= 0,
                "The center of a thin triangle should be valid under FindTriangle");
        }

        #endregion

        #region Multi-level space tests (Y-based disambiguation)

        /// <summary>
        /// Create a NavMesh with two triangles at the same XZ but different Y (multi-level map).
        ///
        /// Lower triangle (Y=0): v0(0,0,0), v1(4,0,0), v2(0,0,3)
        /// Upper triangle (Y=3): v3(0,3,0), v4(4,3,0), v5(0,3,3)
        ///
        /// XZ coordinates are identical, but Y differs.
        /// </summary>
        private static FPNavMesh CreateMultiLevelNavMesh()
        {
            var vertices = new FPVector3[6]
            {
                // Lower (Y=0)
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),           // v0
                new FPVector3(FP64.FromInt(4), FP64.Zero, FP64.Zero),     // v1
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(3)),     // v2
                // Upper (Y=3)
                new FPVector3(FP64.Zero, FP64.FromInt(3), FP64.Zero),     // v3
                new FPVector3(FP64.FromInt(4), FP64.FromInt(3), FP64.Zero),// v4
                new FPVector3(FP64.Zero, FP64.FromInt(3), FP64.FromInt(3)),// v5
            };

            // Lower triangle (Y=0)
            var triLower = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 2,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = -1,
                portal0Left = -1, portal0Right = -1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(1f)),
                area = FP64.FromInt(6),
                areaMask = 1,
                costMultiplier = FP64.One,
                isBlocked = false,
                minY = FP64.Zero,
                maxY = FP64.Zero,
                centerY = FP64.Zero,
            };

            // Upper triangle (Y=3)
            var triUpper = new FPNavMeshTriangle
            {
                v0 = 3, v1 = 4, v2 = 5,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = -1,
                portal0Left = -1, portal0Right = -1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(1f)),
                area = FP64.FromInt(6),
                areaMask = 1,
                costMultiplier = FP64.One,
                isBlocked = false,
                minY = FP64.FromInt(3),
                maxY = FP64.FromInt(3),
                centerY = FP64.FromInt(3),
            };

            var triangles = new[] { triLower, triUpper };

            var bounds = new FPBounds2(
                new FPVector2(FP64.FromFloat(2f), FP64.FromFloat(1.5f)),
                new FPVector2(FP64.FromInt(4), FP64.FromInt(3))
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

        [Test]
        public void FindTriangle_OverlappingXZ_SelectsByY()
        {
            // Arrange
            var mesh = CreateMultiLevelNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            var queryXZ = new FPVector2(1, 1);  // (1, 1) XZ coordinate

            // Act
            int triAtY0 = query.FindTriangle(queryXZ, FP64.Zero);
            int triAtY3 = query.FindTriangle(queryXZ, FP64.FromInt(3));

            // Assert
            Assert.IsTrue(triAtY0 >= 0, "A Y=0 query should find the lower triangle");
            Assert.IsTrue(triAtY3 >= 0, "A Y=3 query should find the upper triangle");
            Assert.AreNotEqual(triAtY0, triAtY3, "Different Y values should return different triangles");

            // Check the Y range of the selected triangle
            var triLower = mesh.Triangles[triAtY0];
            var triUpper = mesh.Triangles[triAtY3];

            // Lower: near Y=0
            Assert.IsTrue(triLower.minY <= FP64.Zero && FP64.Zero <= triLower.maxY,
                "Y=0 should fall within the Y range of the lower triangle");

            // Upper: near Y=3
            Assert.IsTrue(triUpper.minY <= FP64.FromInt(3) && FP64.FromInt(3) <= triUpper.maxY,
                "Y=3 should fall within the Y range of the upper triangle");
        }

        [Test]
        public void FindTriangle_WithoutY_StillWorks()
        {
            // Arrange
            var mesh = CreateMultiLevelNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);
            var queryXZ = new FPVector2(1, 1);

            // Act: use the existing 2D overload
            int tri = query.FindTriangle(queryXZ);

            // Assert: the 2D overload should still work
            Assert.IsTrue(tri >= 0, "The 2D FindTriangle overload should still find a triangle (backward compatibility)");
        }

        #endregion

        #region Raycast bug demonstrations

        /// <summary>
        /// 2x1 cell NavMesh (cellSize=1, gridOrigin=(0,0)).
        ///
        ///   X[0,1]       X[1,2]
        ///  ┌──────────┬──────────┐  Z=1
        ///  │ cell 0   │ cell 1   │
        ///  │  [T0]    │ [T0, T1] │
        ///  └──────────┴──────────┘  Z=0
        ///
        /// T0: (0,0,0)-(2,0,0)-(2,0,1)  Y=0,     XZ[0,2] spans both cells
        /// T1: (1,0.025,0)-(2,0.025,0)-(1,0.025,1)  Y=0.025,  XZ[1,2] cell 1 only
        ///
        /// cell 0 -> gridTriangles[T0]
        /// cell 1 -> gridTriangles[T0, T1]
        /// </summary>
        private static FPNavMesh CreateTwoCellRaycastNavMesh()
        {
            FP64 y1 = FP64.FromDouble(0.025);

            var vertices = new[]
            {
                new FPVector3(FP64.Zero,         FP64.Zero, FP64.Zero),      // v0: T0 (0,0,0)
                new FPVector3(FP64.FromInt(2),   FP64.Zero, FP64.Zero),      // v1: T0 (2,0,0)
                new FPVector3(FP64.FromInt(2),   FP64.Zero, FP64.One),       // v2: T0 (2,0,1)
                new FPVector3(FP64.One,          y1,        FP64.Zero),      // v3: T1 (1,0.025,0)
                new FPVector3(FP64.FromInt(2),   y1,        FP64.Zero),      // v4: T1 (2,0.025,0)
                new FPVector3(FP64.One,          y1,        FP64.One),       // v5: T1 (1,0.025,1)
            };

            // T0: v0-v1-v2, Y=0, XZ[0,2]x[0,1]
            var t0 = new FPNavMeshTriangle
            {
                v0 = 0, v1 = 1, v2 = 2,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = -1,
                portal0Left = -1, portal0Right = -1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ    = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(1f / 3f)),
                area         = FP64.One,
                areaMask     = 1,
                costMultiplier = FP64.One,
                isBlocked    = false,
            };

            // T1: v3-v4-v5, Y=0.025, XZ[1,2]x[0,1]
            var t1 = new FPNavMeshTriangle
            {
                v0 = 3, v1 = 4, v2 = 5,
                neighbor0 = -1, neighbor1 = -1, neighbor2 = -1,
                portal0Left = -1, portal0Right = -1,
                portal1Left = -1, portal1Right = -1,
                portal2Left = -1, portal2Right = -1,
                centerXZ    = new FPVector2(FP64.FromFloat(4f / 3f), FP64.FromFloat(1f / 3f)),
                area         = FP64.Half,
                areaMask     = 1,
                costMultiplier = FP64.One,
                isBlocked    = false,
                minY = y1, maxY = y1, centerY = y1,
            };

            var bounds = new FPBounds2(
                new FPVector2(FP64.One, FP64.Half),
                new FPVector2(FP64.FromInt(2), FP64.One));

            // cell 0: T0 only (count=1), cell 1: T0+T1 (count=2)
            var gridCells      = new[] { 0, 1, 1, 2 };
            var gridTriangles  = new[] { 0, 0, 1 };   // [cell0->T0] [cell1->T0,T1]

            return new FPNavMesh(
                vertices, new[] { t0, t1 }, bounds,
                gridCells, gridTriangles,
                gridWidth: 2, gridHeight: 1,
                gridCellSize: FP64.One,
                gridOrigin: FPVector2.Zero);
        }

        /// <summary>
        /// Bug 1 demonstration: early commit caused by incorrectly applied scale.
        ///
        /// origin=(0,0.75,0.5)  direction=(0.7,-0.5,0)
        ///   scale = |dirXZ|/|dir| = 0.7/0.86 ~= 0.814
        ///   tDeltaX = 1/0.7 ~= 1.429  ->  cell 0 tExit=1.429
        ///
        ///   T0 hit: t=1.5,  XZ=(1.05,0.5)  -> cell 1 region but registered in cell 0 grid
        ///   T1 hit: t=1.45, XZ=(1.015,0.5) -> cell 1 only
        ///
        /// Bug: dist2D = 1.5 * 0.814 = 1.22 &lt;= tExit=1.429 -> early commit on T0
        ///       Returns T0 without seeing T1 (t=1.45, closer)
        ///
        /// After fix: 1.5 - 0 = 1.5 &gt; 1.429 -> cell 0 not committed -> T1 returned from cell 1
        /// </summary>
        [Test]
        public void Raycast_CloserTriangleInAdjacentCell_ReturnsCloserTriangle()
        {
            var mesh  = CreateTwoCellRaycastNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            var origin    = new FPVector3(FP64.Zero,             FP64.FromFloat(0.75f), FP64.Half);
            var direction = new FPVector3(FP64.FromFloat(0.7f),  FP64.FromFloat(-0.5f), FP64.Zero);

            bool hit = query.Raycast(origin, direction, out FPVector3 hitPoint, out int triIdx);

            Assert.IsTrue(hit);
            // T1 (triIdx=1, t=1.45) is closer than T0 (triIdx=0, t=1.5), so T1 is returned
            Assert.AreEqual(1, triIdx,
                "Should return the closer T1 (triIdx=1), but the bug returns T0 (triIdx=0)");
            Assert.AreEqual(1.015f, hitPoint.x.ToFloat(), 0.05f);
            Assert.AreEqual(0.025f, hitPoint.y.ToFloat(), 0.01f);
        }

        /// <summary>
        /// Bug 2 demonstration: tStart reference point mismatch always returns false when origin is outside the grid.
        ///
        /// origin=(6,3,0.5)  direction=(-0.7,-0.5,0)
        ///   tStart = (2-6)/(-0.7) ~= 5.71  (AABB slab, X axis)
        ///   startXZ = (2, 0.5)  ->  col=1 (clamp), tExit=1.429
        ///   T0 hit: t=6 (Y=0), XZ=(1.8, 0.5) -> inside T0
        ///
        /// Bug: dist2D = 6 * 0.814 = 4.88 &gt; tExit=1.429 -> never commits across the cell traversal
        ///       Returns false after exhausting all cells
        ///
        /// After fix: 6 - 5.71 = 0.29 &lt;= tExit=1.429 -> T0 committed immediately
        /// </summary>
        [Test]
        public void Raycast_OriginOutsideGrid_FindsHit()
        {
            var mesh  = CreateTwoCellRaycastNavMesh();
            var query = new FPNavMeshQuery(mesh, _logger);

            var origin    = new FPVector3(FP64.FromInt(6),        FP64.FromInt(3),      FP64.Half);
            var direction = new FPVector3(FP64.FromFloat(-0.7f),  FP64.FromFloat(-0.5f), FP64.Zero);

            bool hit = query.Raycast(origin, direction, out FPVector3 hitPoint, out int triIdx);

            Assert.IsTrue(hit,
                "Ray from an out-of-grid origin pointing into the grid returns false due to the tStart bug");
            Assert.AreEqual(0, triIdx);
            Assert.AreEqual(1.8f, hitPoint.x.ToFloat(), 0.05f);
            Assert.AreEqual(0.0f, hitPoint.y.ToFloat(), 0.01f);
        }

        #endregion
    }
}
