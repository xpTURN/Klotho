using System;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// FPNavMesh binary serialization/deserialization.
    /// </summary>
    public static class FPNavMeshSerializer
    {
        private const int VERSION = 2;

        // FPNavMeshTriangle: int(4)*12 + FPVector2(16) + FP64(8) + int(4) + FP64(8) + bool(1) + FP64*3(24) = 109 bytes
        // (+24 for minY, maxY, centerY added in VERSION 2)
        private const int TRIANGLE_SIZE = 109;

        // === Span-based serialization (cross-platform, no GC) ===

        public static int GetSerializedSize(FPNavMesh navMesh)
        {
            // version(4) + vertCount(4) + vertices + triCount(4) + triangles
            // + boundsXZ(32) + cellCount(4) + cells + gridTriCount(4) + gridTris
            // + gridWidth(4) + gridHeight(4) + gridCellSize(8) + gridOrigin(16)
            return 4 + 4 + navMesh.Vertices.Length * 24
                + 4 + navMesh.Triangles.Length * TRIANGLE_SIZE
                + 32
                + 4 + navMesh.GridCells.Length * 4
                + 4 + navMesh.GridTriangles.Length * 4
                + 32;
        }

        public static void Serialize(ref SpanWriter writer, FPNavMesh navMesh)
        {
            // version
            writer.WriteInt32(VERSION);

            // vertices
            writer.WriteInt32(navMesh.Vertices.Length);
            for (int i = 0; i < navMesh.Vertices.Length; i++)
                writer.WriteFP(navMesh.Vertices[i]);

            // triangles
            writer.WriteInt32(navMesh.Triangles.Length);
            for (int i = 0; i < navMesh.Triangles.Length; i++)
                WriteTriangle(ref writer, ref navMesh.Triangles[i]);

            // BoundsXZ (center + extents = 4 FP64)
            writer.WriteFP(navMesh.BoundsXZ.center);
            writer.WriteFP(navMesh.BoundsXZ.extents);

            // GridCells
            writer.WriteInt32(navMesh.GridCells.Length);
            for (int i = 0; i < navMesh.GridCells.Length; i++)
                writer.WriteInt32(navMesh.GridCells[i]);

            // GridTriangles
            writer.WriteInt32(navMesh.GridTriangles.Length);
            for (int i = 0; i < navMesh.GridTriangles.Length; i++)
                writer.WriteInt32(navMesh.GridTriangles[i]);

            // Grid parameters
            writer.WriteInt32(navMesh.GridWidth);
            writer.WriteInt32(navMesh.GridHeight);
            writer.WriteFP(navMesh.GridCellSize);
            writer.WriteFP(navMesh.GridOrigin);
        }

        public static FPNavMesh Deserialize(string path)
        {
            var buffer = System.IO.File.ReadAllBytes(path);
            return Deserialize(buffer);
        }

        public static FPNavMesh Deserialize(ReadOnlySpan<byte> buffer)
        {
            var reader = new SpanReader(buffer);
            return Deserialize(ref reader);
        }

        public static FPNavMesh Deserialize(ref SpanReader reader)
        {
            // version
            int version = reader.ReadInt32();
            if (version != VERSION)
                throw new InvalidOperationException(
                    $"FPNavMesh version mismatch: expected {VERSION}, got {version}. Re-export required.");

            // vertices
            int vertCount = reader.ReadInt32();
            var vertices = new FPVector3[vertCount];
            for (int i = 0; i < vertCount; i++)
                vertices[i] = reader.ReadFPVector3();

            // triangles
            int triCount = reader.ReadInt32();
            var triangles = new FPNavMeshTriangle[triCount];
            for (int i = 0; i < triCount; i++)
                triangles[i] = ReadTriangle(ref reader);

            // BoundsXZ
            FPBounds2 bounds;
            bounds.center = reader.ReadFPVector2();
            bounds.extents = reader.ReadFPVector2();

            // GridCells
            int cellCount = reader.ReadInt32();
            var gridCells = new int[cellCount];
            for (int i = 0; i < cellCount; i++)
                gridCells[i] = reader.ReadInt32();

            // GridTriangles
            int gridTriCount = reader.ReadInt32();
            var gridTriangles = new int[gridTriCount];
            for (int i = 0; i < gridTriCount; i++)
                gridTriangles[i] = reader.ReadInt32();

            // Grid parameters
            int gridWidth = reader.ReadInt32();
            int gridHeight = reader.ReadInt32();
            FP64 gridCellSize = reader.ReadFP64();
            FPVector2 gridOrigin = reader.ReadFPVector2();

            return new FPNavMesh(
                vertices, triangles, bounds,
                gridCells, gridTriangles,
                gridWidth, gridHeight,
                gridCellSize, gridOrigin
            );
        }

        private static void WriteTriangle(ref SpanWriter writer, ref FPNavMeshTriangle tri)
        {
            // vertex indices
            writer.WriteInt32(tri.v0);
            writer.WriteInt32(tri.v1);
            writer.WriteInt32(tri.v2);

            // adjacent triangles
            writer.WriteInt32(tri.neighbor0);
            writer.WriteInt32(tri.neighbor1);
            writer.WriteInt32(tri.neighbor2);

            // portals
            writer.WriteInt32(tri.portal0Left);
            writer.WriteInt32(tri.portal0Right);
            writer.WriteInt32(tri.portal1Left);
            writer.WriteInt32(tri.portal1Right);
            writer.WriteInt32(tri.portal2Left);
            writer.WriteInt32(tri.portal2Right);

            // precomputed values
            writer.WriteFP(tri.centerXZ);
            writer.WriteFP(tri.area);
            writer.WriteInt32(tri.areaMask);
            writer.WriteFP(tri.costMultiplier);
            writer.WriteBool(tri.isBlocked);

            // Y-axis height range (multi-floor support, added in VERSION 2)
            writer.WriteFP(tri.minY);
            writer.WriteFP(tri.maxY);
            writer.WriteFP(tri.centerY);
        }

        private static FPNavMeshTriangle ReadTriangle(ref SpanReader reader)
        {
            var tri = new FPNavMeshTriangle();

            // vertex indices
            tri.v0 = reader.ReadInt32();
            tri.v1 = reader.ReadInt32();
            tri.v2 = reader.ReadInt32();

            // adjacent triangles
            tri.neighbor0 = reader.ReadInt32();
            tri.neighbor1 = reader.ReadInt32();
            tri.neighbor2 = reader.ReadInt32();

            // portals
            tri.portal0Left = reader.ReadInt32();
            tri.portal0Right = reader.ReadInt32();
            tri.portal1Left = reader.ReadInt32();
            tri.portal1Right = reader.ReadInt32();
            tri.portal2Left = reader.ReadInt32();
            tri.portal2Right = reader.ReadInt32();

            // precomputed values
            tri.centerXZ = reader.ReadFPVector2();
            tri.area = reader.ReadFP64();
            tri.areaMask = reader.ReadInt32();
            tri.costMultiplier = reader.ReadFP64();
            tri.isBlocked = reader.ReadBool();

            // Y-axis height range (multi-floor support, added in VERSION 2)
            tri.minY = reader.ReadFP64();
            tri.maxY = reader.ReadFP64();
            tri.centerY = reader.ReadFP64();

            return tri;
        }
    }
}
