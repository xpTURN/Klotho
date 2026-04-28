using System;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;

using NUnit.Framework;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    [TestFixture]
    public class FPNavMeshSerializerTests
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

        private static FPNavMesh CreateTestNavMesh()
        {
            var vertices = new[]
            {
                new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero),
                new FPVector3(FP64.FromInt(4), FP64.FromInt(2), FP64.Zero),
                new FPVector3(FP64.Zero, FP64.Zero, FP64.FromInt(4)),
                new FPVector3(FP64.FromInt(4), FP64.FromInt(4), FP64.FromInt(4)),
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
                areaMask = 3,
                costMultiplier = FP64.FromFloat(2.0f),
                isBlocked = true,
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

        private static byte[] SerializeToBytes(FPNavMesh navMesh)
        {
            int size = FPNavMeshSerializer.GetSerializedSize(navMesh);
            var buffer = new byte[size];
            var writer = new SpanWriter(buffer);
            FPNavMeshSerializer.Serialize(ref writer, navMesh);
            return buffer.AsSpan(0, writer.Position).ToArray();
        }

        private static FPNavMesh DeserializeFromBytes(byte[] data)
        {
            var reader = new SpanReader(data);
            return FPNavMeshSerializer.Deserialize(ref reader);
        }

        #region Roundtrip

        [Test]
        public void Roundtrip_Vertices_Preserved()
        {
            var original = CreateTestNavMesh();
            byte[] data = SerializeToBytes(original);
            var restored = DeserializeFromBytes(data);

            Assert.AreEqual(original.Vertices.Length, restored.Vertices.Length);
            for (int i = 0; i < original.Vertices.Length; i++)
            {
                Assert.AreEqual(original.Vertices[i].x.RawValue, restored.Vertices[i].x.RawValue);
                Assert.AreEqual(original.Vertices[i].y.RawValue, restored.Vertices[i].y.RawValue);
                Assert.AreEqual(original.Vertices[i].z.RawValue, restored.Vertices[i].z.RawValue);
            }
        }

        [Test]
        public void Roundtrip_TriangleVertexIndices_Preserved()
        {
            var original = CreateTestNavMesh();
            byte[] data = SerializeToBytes(original);
            var restored = DeserializeFromBytes(data);

            Assert.AreEqual(original.Triangles.Length, restored.Triangles.Length);
            for (int i = 0; i < original.Triangles.Length; i++)
            {
                Assert.AreEqual(original.Triangles[i].v0, restored.Triangles[i].v0);
                Assert.AreEqual(original.Triangles[i].v1, restored.Triangles[i].v1);
                Assert.AreEqual(original.Triangles[i].v2, restored.Triangles[i].v2);
            }
        }

        [Test]
        public void Roundtrip_TriangleNeighbors_Preserved()
        {
            var original = CreateTestNavMesh();
            byte[] data = SerializeToBytes(original);
            var restored = DeserializeFromBytes(data);

            for (int i = 0; i < original.Triangles.Length; i++)
            {
                Assert.AreEqual(original.Triangles[i].neighbor0, restored.Triangles[i].neighbor0);
                Assert.AreEqual(original.Triangles[i].neighbor1, restored.Triangles[i].neighbor1);
                Assert.AreEqual(original.Triangles[i].neighbor2, restored.Triangles[i].neighbor2);
            }
        }

        [Test]
        public void Roundtrip_TrianglePortals_Preserved()
        {
            var original = CreateTestNavMesh();
            byte[] data = SerializeToBytes(original);
            var restored = DeserializeFromBytes(data);

            for (int i = 0; i < original.Triangles.Length; i++)
            {
                Assert.AreEqual(original.Triangles[i].portal0Left, restored.Triangles[i].portal0Left);
                Assert.AreEqual(original.Triangles[i].portal0Right, restored.Triangles[i].portal0Right);
                Assert.AreEqual(original.Triangles[i].portal1Left, restored.Triangles[i].portal1Left);
                Assert.AreEqual(original.Triangles[i].portal1Right, restored.Triangles[i].portal1Right);
                Assert.AreEqual(original.Triangles[i].portal2Left, restored.Triangles[i].portal2Left);
                Assert.AreEqual(original.Triangles[i].portal2Right, restored.Triangles[i].portal2Right);
            }
        }

        [Test]
        public void Roundtrip_TrianglePrecomputed_Preserved()
        {
            var original = CreateTestNavMesh();
            byte[] data = SerializeToBytes(original);
            var restored = DeserializeFromBytes(data);

            for (int i = 0; i < original.Triangles.Length; i++)
            {
                Assert.AreEqual(original.Triangles[i].centerXZ.x.RawValue, restored.Triangles[i].centerXZ.x.RawValue);
                Assert.AreEqual(original.Triangles[i].centerXZ.y.RawValue, restored.Triangles[i].centerXZ.y.RawValue);
                Assert.AreEqual(original.Triangles[i].area.RawValue, restored.Triangles[i].area.RawValue);
                Assert.AreEqual(original.Triangles[i].areaMask, restored.Triangles[i].areaMask);
                Assert.AreEqual(original.Triangles[i].costMultiplier.RawValue, restored.Triangles[i].costMultiplier.RawValue);
                Assert.AreEqual(original.Triangles[i].isBlocked, restored.Triangles[i].isBlocked);
            }
        }

        [Test]
        public void Roundtrip_Bounds_Preserved()
        {
            var original = CreateTestNavMesh();
            byte[] data = SerializeToBytes(original);
            var restored = DeserializeFromBytes(data);

            Assert.AreEqual(original.BoundsXZ.center.x.RawValue, restored.BoundsXZ.center.x.RawValue);
            Assert.AreEqual(original.BoundsXZ.center.y.RawValue, restored.BoundsXZ.center.y.RawValue);
            Assert.AreEqual(original.BoundsXZ.extents.x.RawValue, restored.BoundsXZ.extents.x.RawValue);
            Assert.AreEqual(original.BoundsXZ.extents.y.RawValue, restored.BoundsXZ.extents.y.RawValue);
        }

        [Test]
        public void Roundtrip_GridArrays_Preserved()
        {
            var original = CreateTestNavMesh();
            byte[] data = SerializeToBytes(original);
            var restored = DeserializeFromBytes(data);

            Assert.AreEqual(original.GridCells.Length, restored.GridCells.Length);
            for (int i = 0; i < original.GridCells.Length; i++)
                Assert.AreEqual(original.GridCells[i], restored.GridCells[i]);

            Assert.AreEqual(original.GridTriangles.Length, restored.GridTriangles.Length);
            for (int i = 0; i < original.GridTriangles.Length; i++)
                Assert.AreEqual(original.GridTriangles[i], restored.GridTriangles[i]);
        }

        [Test]
        public void Roundtrip_GridParams_Preserved()
        {
            var original = CreateTestNavMesh();
            byte[] data = SerializeToBytes(original);
            var restored = DeserializeFromBytes(data);

            Assert.AreEqual(original.GridWidth, restored.GridWidth);
            Assert.AreEqual(original.GridHeight, restored.GridHeight);
            Assert.AreEqual(original.GridCellSize.RawValue, restored.GridCellSize.RawValue);
            Assert.AreEqual(original.GridOrigin.x.RawValue, restored.GridOrigin.x.RawValue);
            Assert.AreEqual(original.GridOrigin.y.RawValue, restored.GridOrigin.y.RawValue);
        }

        #endregion

        #region Functional verification

        [Test]
        public void Roundtrip_DeserializedMesh_QueryWorks()
        {
            var original = CreateTestNavMesh();
            byte[] data = SerializeToBytes(original);
            var restored = DeserializeFromBytes(data);

            var query = new FPNavMeshQuery(restored, _logger);
            int tri = query.FindTriangle(new FPVector2(3, 1));
            Assert.AreEqual(0, tri);
        }

        [Test]
        public void Roundtrip_BitExact()
        {
            var original = CreateTestNavMesh();
            byte[] data1 = SerializeToBytes(original);
            var restored = DeserializeFromBytes(data1);
            byte[] data2 = SerializeToBytes(restored);

            Assert.AreEqual(data1.Length, data2.Length);
            for (int i = 0; i < data1.Length; i++)
                Assert.AreEqual(data1[i], data2[i], $"Byte mismatch at index {i}");
        }

        #endregion
    }
}
