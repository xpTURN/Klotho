using NUnit.Framework;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Geometry.Tests
{
    [TestFixture]
    public class FPMeshDataTests
    {
        static FPVector3[] Tri(FP64 yOffset = default)
            => new[]
            {
                new FPVector3(FP64.Zero, yOffset, FP64.Zero),
                new FPVector3(FP64.One,  yOffset, FP64.Zero),
                new FPVector3(FP64.Zero, yOffset, FP64.One),
            };

        static int[] TriIndices() => new[] { 0, 1, 2 };

        [Test]
        public void VertexDiff_DifferentContentHash()
        {
            var a = new FPMeshData(Tri(FP64.Zero), TriIndices());
            var b = new FPMeshData(Tri(FP64.One),  TriIndices());   // same indices, shifted vertex
            Assert.AreNotEqual(a.ContentHash, b.ContentHash);
        }

        [Test]
        public void IndexDiff_DifferentContentHash()
        {
            var verts = Tri();
            var a = new FPMeshData(verts, new[] { 0, 1, 2 });
            var b = new FPMeshData(verts, new[] { 0, 2, 1 });       // same vertices, flipped winding
            Assert.AreNotEqual(a.ContentHash, b.ContentHash);
        }

        [Test]
        public void CountSensitivity_TrailingDuplicateVertex_DifferentContentHash()
        {
            var v3 = Tri();
            var v4 = new[] { v3[0], v3[1], v3[2], v3[2] };          // identical leading content + 1 trailing dup
            var a = new FPMeshData(v3, TriIndices());
            var b = new FPMeshData(v4, TriIndices());
            Assert.AreNotEqual(a.ContentHash, b.ContentHash);
        }

        [Test]
        public void IndependentConstruction_SameContentHash()
        {
            // Two peers loading the same asset → independent instances, identical content.
            var a = new FPMeshData(Tri(), TriIndices());
            var b = new FPMeshData(Tri(), TriIndices());
            Assert.AreEqual(a.ContentHash, b.ContentHash);
        }

        [Test]
        public void SerializeRoundTrip_ContentHashStable()
        {
            var original = new FPMeshData(Tri(), TriIndices());
            byte[] buffer = new byte[original.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            original.Serialize(ref writer);
            var reader = new SpanReader(buffer);
            var restored = FPMeshData.Deserialize(ref reader);
            Assert.AreEqual(original.ContentHash, restored.ContentHash);
        }

        [Test]
        public void EmptyMesh_Deterministic()
        {
            // ContentHash need not be non-zero (the "never returns 0" contract is on
            // GetStaticFingerprint's return, not this) — only deterministic across peers.
            var a = new FPMeshData(new FPVector3[0], new int[0]);
            var b = new FPMeshData(new FPVector3[0], new int[0]);
            Assert.AreEqual(a.ContentHash, b.ContentHash);
        }
    }
}
