using System;
using System.Text;
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
        private const int VERSION = 4;

        // FPNavMeshTriangle: int(4)*6 + FPVector2(16) + FP64(8) + int(4) + bool(1) + byte(1) + FP64(8) + FP64*3(24) = 86 bytes
        // (+24 for minY, maxY, centerY added in VERSION 2;
        //  VERSION 4 replaced the six portal ints with a single portalFlip byte: 109 -> 86)
        private const int TRIANGLE_SIZE = 86;

        // === Span-based serialization (cross-platform, no GC) ===

        public static int GetSerializedSize(FPNavMesh navMesh)
        {
            // version(4) + vertCount(4) + vertices + triCount(4) + triangles
            // + boundsXZ(32) + cellCount(4) + cells + gridTriCount(4) + gridTris
            // + gridWidth(4) + gridHeight(4) + gridCellSize(8) + gridOrigin(16)
            // + bake settings block(32 = radius/slopeDeg/height/climb, VERSION 3)
            return 4 + 4 + navMesh.Vertices.Length * 24
                + 4 + navMesh.Triangles.Length * TRIANGLE_SIZE
                + 32
                + 4 + navMesh.GridCells.Length * 4
                + 4 + navMesh.GridTriangles.Length * 4
                + 32
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
                WriteTriangle(ref writer, in navMesh.Triangles[i]);

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

            // Bake settings block (VERSION 3)
            writer.WriteFP(navMesh.BakeAgentRadius);
            writer.WriteFP(navMesh.BakeMaxSlopeDeg);
            writer.WriteFP(navMesh.BakeAgentHeight);
            writer.WriteFP(navMesh.BakeAgentClimb);
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

            // Bake settings block (VERSION 3)
            FP64 bakeAgentRadius = reader.ReadFP64();
            FP64 bakeMaxSlopeDeg = reader.ReadFP64();
            FP64 bakeAgentHeight = reader.ReadFP64();
            FP64 bakeAgentClimb = reader.ReadFP64();

            return new FPNavMesh(
                vertices, triangles, bounds,
                gridCells, gridTriangles,
                gridWidth, gridHeight,
                gridCellSize, gridOrigin,
                bakeAgentRadius, bakeMaxSlopeDeg, bakeAgentHeight, bakeAgentClimb
            );
        }

        // === JSON output (debug / inspection sidecar) ===

        /// <summary>
        /// Human-readable JSON dump of an FPNavMesh (debug / inspection).
        /// Engine-agnostic — shared by the Unity and Godot exporters.
        /// </summary>
        public static string ToJson(FPNavMesh navMesh)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");

            // vertices
            sb.AppendLine("  \"vertices\": [");
            for (int i = 0; i < navMesh.Vertices.Length; i++)
            {
                var v = navMesh.Vertices[i];
                sb.Append($"    [{v.x.ToFloat()},{v.y.ToFloat()},{v.z.ToFloat()}]");
                if (i < navMesh.Vertices.Length - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.AppendLine("  ],");

            // triangles
            sb.AppendLine("  \"triangles\": [");
            for (int i = 0; i < navMesh.Triangles.Length; i++)
            {
                var t = navMesh.Triangles[i];
                sb.Append("    {");
                sb.Append($"\"v0\":{t.v0},\"v1\":{t.v1},\"v2\":{t.v2}");
                sb.Append($",\"neighbor0\":{t.neighbor0},\"neighbor1\":{t.neighbor1},\"neighbor2\":{t.neighbor2}");
                sb.Append($",\"portalFlip\":{t.portalFlip}");
                // Portal indices are derivable (edge vertex pair ordered by the flip bit) and no
                // consumer reads them, but the dump is for eyeballing so keep them spelled out.
                for (int e = 0; e < 3; e++)
                {
                    t.GetPortal(e, out int pl, out int pr);
                    sb.Append($",\"portal{e}Left\":{pl},\"portal{e}Right\":{pr}");
                }
                sb.Append($",\"centerXZ\":[{t.centerXZ.x.ToFloat()},{t.centerXZ.y.ToFloat()}]");
                sb.Append($",\"area\":{t.area.ToFloat()}");
                sb.Append($",\"areaMask\":{t.areaMask}");
                sb.Append($",\"costMultiplier\":{t.costMultiplier.ToFloat()}");
                sb.Append($",\"isBlocked\":{(t.isBlocked ? "true" : "false")}");
                sb.Append($",\"minY\":{t.minY.ToFloat()},\"maxY\":{t.maxY.ToFloat()},\"centerY\":{t.centerY.ToFloat()}");
                sb.Append('}');
                if (i < navMesh.Triangles.Length - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.AppendLine("  ],");

            // bounds
            sb.Append($"  \"boundsXZ\": {{\"center\":[{navMesh.BoundsXZ.center.x.ToFloat()},{navMesh.BoundsXZ.center.y.ToFloat()}]");
            sb.AppendLine($",\"extents\":[{navMesh.BoundsXZ.extents.x.ToFloat()},{navMesh.BoundsXZ.extents.y.ToFloat()}]}},");

            // grid metadata
            sb.AppendLine($"  \"gridWidth\": {navMesh.GridWidth},");
            sb.AppendLine($"  \"gridHeight\": {navMesh.GridHeight},");
            sb.AppendLine($"  \"gridCellSize\": {navMesh.GridCellSize.ToFloat()},");
            sb.AppendLine($"  \"gridOrigin\": [{navMesh.GridOrigin.x.ToFloat()},{navMesh.GridOrigin.y.ToFloat()}],");
            sb.AppendLine($"  \"bakeAgentRadius\": {navMesh.BakeAgentRadius.ToFloat()},");
            sb.AppendLine($"  \"bakeMaxSlopeDeg\": {navMesh.BakeMaxSlopeDeg.ToFloat()},");
            sb.AppendLine($"  \"bakeAgentHeight\": {navMesh.BakeAgentHeight.ToFloat()},");
            sb.AppendLine($"  \"bakeAgentClimb\": {navMesh.BakeAgentClimb.ToFloat()}");

            sb.Append('}');
            return sb.ToString();
        }

        private static void WriteTriangle(ref SpanWriter writer, in FPNavMeshTriangle tri)
        {
            // vertex indices
            writer.WriteInt32(tri.v0);
            writer.WriteInt32(tri.v1);
            writer.WriteInt32(tri.v2);

            // adjacent triangles
            writer.WriteInt32(tri.neighbor0);
            writer.WriteInt32(tri.neighbor1);
            writer.WriteInt32(tri.neighbor2);

            // portal orientation, one bit per edge (VERSION 4 — was six int indices)
            writer.WriteByte(tri.portalFlip);

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

            // portal orientation, one bit per edge (VERSION 4 — was six int indices)
            tri.portalFlip = reader.ReadByte();

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
