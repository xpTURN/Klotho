using System;
using System.IO;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    /// <summary>
    /// Fixed-point mesh data. Contains vertices, indices, local bounds, and active edge flags.
    /// </summary>
    public partial class FPMeshData
    {
        private const int VERSION = 1;

        public FPVector3[] vertices;
        public int[] indices;
        public FPBounds3 localBounds;

        // Active Edge flags: bit is set if edge j (0=v0v1,1=v1v2,2=v2v0) of triangle i is active (convex/boundary)
        // index: triIndex * 3 + edgeSlot
        public bool[] activeEdgeFlags;

        public FPVector3[] Vertices => vertices;
        public int[] Indices => indices;
        public FPBounds3 LocalBounds => localBounds;
        public int TriangleCount => indices.Length / 3;

        public FPMeshData(FPVector3[] vertices, int[] indices)
        {
            SetData(vertices, indices);
        }

        public void SetData(FPVector3[] vertices, int[] indices)
        {
            this.vertices = vertices;
            this.indices = indices;
            localBounds = ComputeBounds(vertices);
            // BuildActiveEdges();
        }

        // edge key: (min(a,b), max(a,b))
        static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        public void BuildActiveEdges()
        {
            int triCount = indices.Length / 3;
            activeEdgeFlags = new bool[triCount * 3];

            // Collect adjacent triangle normals for each edge
            // edge key → (triIndex, edgeSlot, triNormal)
            var edgeMap = new System.Collections.Generic.Dictionary<long, (int tri, int slot, FPVector3 normal)>();

            for (int t = 0; t < triCount; t++)
            {
                int b = t * 3;
                int i0 = indices[b], i1 = indices[b + 1], i2 = indices[b + 2];
                FPVector3 v0 = vertices[i0], v1 = vertices[i1], v2 = vertices[i2];
                FPVector3 n = FPVector3.Cross(v1 - v0, v2 - v0);
                FP64 nLen = n.magnitude;
                if (nLen > FP64.Epsilon) n = n / nLen;

                int[] vIdx = { i0, i1, i2 };
                for (int e = 0; e < 3; e++)
                {
                    int ea = vIdx[e], eb = vIdx[(e + 1) % 3];
                    long key = EdgeKey(ea, eb);

                    if (edgeMap.TryGetValue(key, out var other))
                    {
                        // Based on outward normals:
                        // dot < 0 -> the two normals diverge -> convex edge -> active
                        // dot >= 0 -> the two normals converge -> concave/right-angle edge -> inactive
                        // threshold -0.1 ~= cos(96 degrees): includes a small numerical margin
                        FP64 dot = FPVector3.Dot(n, other.normal);
                        bool active = dot < FP64.FromDouble(-0.1);
                        activeEdgeFlags[t * 3 + e] = active;
                        activeEdgeFlags[other.tri * 3 + other.slot] = active;
                        edgeMap.Remove(key);
                    }
                    else
                    {
                        // No adjacent triangle yet -- boundary edge -> active
                        edgeMap[key] = (t, e, n);
                        activeEdgeFlags[t * 3 + e] = true;
                    }
                }
            }

            // Remaining entries in edgeMap are boundary edges -> already marked true
        }

        public void GetTriangle(int triIndex, out FPVector3 v0, out FPVector3 v1, out FPVector3 v2)
        {
            int baseIdx = triIndex * 3;
            v0 = vertices[indices[baseIdx]];
            v1 = vertices[indices[baseIdx + 1]];
            v2 = vertices[indices[baseIdx + 2]];
        }

        public int GetSerializedSize() => 4 + 4 + vertices.Length * 24 + 4 + indices.Length * 4 + 4;

        public void Serialize(ref SpanWriter writer)
        {
            writer.WriteInt32(VERSION);

            writer.WriteInt32(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
                writer.WriteFP(vertices[i]);

            writer.WriteInt32(indices.Length);
            for (int i = 0; i < indices.Length; i++)
                writer.WriteInt32(indices[i]);
        }

        public static FPMeshData Deserialize(ref SpanReader reader)
        {
            int version = reader.ReadInt32();
            if (version != VERSION)
                throw new InvalidOperationException(
                    $"FPMeshData version mismatch: expected {VERSION}, got {version}. Re-export required.");

            int vertCount = reader.ReadInt32();
            var verts = new FPVector3[vertCount];
            for (int i = 0; i < vertCount; i++)
                verts[i] = reader.ReadFPVector3();

            int idxCount = reader.ReadInt32();
            var idxs = new int[idxCount];
            for (int i = 0; i < idxCount; i++)
                idxs[i] = reader.ReadInt32();

            return new FPMeshData(verts, idxs);
        }

        public void SaveToFile(string filePath)
        {
            int size = GetSerializedSize();
            using (var buf = SerializationBuffer.Create(size))
            {
                var writer = new SpanWriter(buf.Span);
                Serialize(ref writer);
                File.WriteAllBytes(filePath, buf.Span.Slice(0, writer.Position).ToArray());
            }
        }

        public static FPMeshData LoadFromFile(string filePath)
        {
            byte[] data = File.ReadAllBytes(filePath);
            var reader = new SpanReader(data);
            return Deserialize(ref reader);
        }

        static FPBounds3 ComputeBounds(FPVector3[] verts)
        {
            if (verts.Length == 0)
                return default;

            FPVector3 mn = verts[0];
            FPVector3 mx = verts[0];

            for (int i = 1; i < verts.Length; i++)
            {
                mn = FPVector3.Min(mn, verts[i]);
                mx = FPVector3.Max(mx, verts[i]);
            }

            var bounds = default(FPBounds3);
            bounds.SetMinMax(mn, mx);
            return bounds;
        }
    }
}
