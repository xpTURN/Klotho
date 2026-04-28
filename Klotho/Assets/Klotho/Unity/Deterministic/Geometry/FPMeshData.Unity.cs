using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry
{
    public static class FPMeshDataExtensions
    {
        public static FPMeshData FromMesh(this FPMeshData @this, UnityEngine.Mesh mesh)
        {
            var unityVerts = mesh.vertices;
            var unityIndices = mesh.triangles;

            var fpVerts = new FPVector3[unityVerts.Length];
            for (int i = 0; i < unityVerts.Length; i++)
                fpVerts[i] = unityVerts[i].ToFPVector3();

            @this.SetData(fpVerts, unityIndices);
            return @this;
        }

        public static FPMeshData ToFPMeshData(this UnityEngine.Mesh @this)
        {
            var unityVerts = @this.vertices;
            var unityIndices = @this.triangles;

            var fpVerts = new FPVector3[unityVerts.Length];
            for (int i = 0; i < unityVerts.Length; i++)
                fpVerts[i] = unityVerts[i].ToFPVector3();

            return new FPMeshData(fpVerts, unityIndices);
        }

        public static UnityEngine.Mesh ToMesh(this FPMeshData @this)
        {
            var unityVerts = new UnityEngine.Vector3[@this.vertices.Length];
            for (int i = 0; i < @this.vertices.Length; i++)
                unityVerts[i] = @this.vertices[i].ToVector3();

            var mesh = new UnityEngine.Mesh();
            mesh.vertices = unityVerts;
            mesh.triangles = @this.indices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}