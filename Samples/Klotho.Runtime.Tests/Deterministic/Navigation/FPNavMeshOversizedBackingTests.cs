using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The mechanical guarantee that the count conversion is
    /// COMPLETE. Builds a mesh whose backing arrays are deliberately larger than their logical
    /// content and whose tail is poisoned with plausible-but-wrong data, then asserts every
    /// consumer produces exactly what the exact-size mesh produces.
    ///
    /// The poisoning is the whole point: a tail of zeros would let a surviving
    /// `.Length` read pass unnoticed — zeroed triangles are degenerate and easy to miss. The
    /// tail here references valid vertex indices, so nothing crashes; it just describes
    /// different geometry, which is exactly how a real recycled buffer would look.
    ///
    /// These cover the highest-severity risk: a consumer that walks to Length
    /// treats the tail as real geometry — ghost ORCA obstacles, A* routing through stale
    /// triangles. Those failures are deterministic across peers, so no desync check would ever
    /// report them; these tests are the only thing that can.
    /// </summary>
    [TestFixture]
    public class FPNavMeshOversizedBackingTests
    {
        #region Fixture

        private static FPNavMesh BuildBase(int half = 8)
        {
            var pts = new List<(int x, int z)>();
            for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++)
                    pts.Add((x, z));

            var vertices = new FPVector3[pts.Count];
            var xs = new long[pts.Count];
            var zs = new long[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                vertices[i] = new FPVector3(FP64.FromInt(pts[i].x), FP64.Zero, FP64.FromInt(pts[i].z));
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        /// <summary>
        /// Same logical mesh, backing arrays doubled, tail filled with data that is valid enough
        /// not to crash and different enough to change every result if it were ever read.
        /// </summary>
        private static FPNavMesh WithPoisonedTail(FPNavMesh src)
        {
            int vc = src.VertexCount, tc = src.TriangleCount, gtc = src.GridTriangleCount;
            int gcc = src.GridWidth * src.GridHeight * 2;

            var vertices = new FPVector3[vc * 2];
            src.Vertices.CopyTo(vertices);
            for (int i = vc; i < vertices.Length; i++)
                vertices[i] = new FPVector3(FP64.FromInt(9999), FP64.FromInt(7), FP64.FromInt(-9999));

            var triangles = new FPNavMeshTriangle[tc * 2];
            src.Triangles.CopyTo(triangles);
            for (int i = tc; i < triangles.Length; i++)
            {
                // Real vertex indices (no out-of-range), but a triangle that does not exist.
                triangles[i] = src.Triangles[i % tc];
                triangles[i].v0 = (triangles[i].v0 + 7) % vc;
                triangles[i].v1 = (triangles[i].v1 + 13) % vc;
                triangles[i].v2 = (triangles[i].v2 + 23) % vc;
                triangles[i].neighbor0 = -1;
                triangles[i].neighbor1 = -1;
                triangles[i].neighbor2 = -1;
                triangles[i].areaMask = 1 << 3;
            }

            var gridCells = new int[gcc * 2];
            src.GridCells.CopyTo(gridCells);
            for (int i = gcc; i < gridCells.Length; i++)
                gridCells[i] = 0;

            var gridTriangles = new int[gtc * 2];
            src.GridTriangles.CopyTo(gridTriangles);
            for (int i = gtc; i < gridTriangles.Length; i++)
                gridTriangles[i] = (i * 31) % tc;

            return new FPNavMesh(
                vertices, triangles, src.BoundsXZ, gridCells, gridTriangles,
                src.GridWidth, src.GridHeight, src.GridCellSize, src.GridOrigin,
                src.BakeAgentRadius, src.BakeMaxSlopeDeg, src.BakeAgentHeight, src.BakeAgentClimb,
                vertexCount: vc, triangleCount: tc, gridTriangleCount: gtc);
        }

        private static byte[] SerializeToBytes(FPNavMesh mesh)
        {
            var buffer = new byte[FPNavMeshSerializer.GetSerializedSize(mesh)];
            var writer = new xpTURN.Klotho.Serialization.SpanWriter(buffer);
            FPNavMeshSerializer.Serialize(ref writer, mesh);
            return buffer;
        }

        #endregion

        [Test]
        public void Counts_AreLogical_NotBackingLength()
        {
            FPNavMesh exact = BuildBase();
            FPNavMesh padded = WithPoisonedTail(exact);

            Assert.AreEqual(exact.VertexCount, padded.VertexCount);
            Assert.AreEqual(exact.TriangleCount, padded.TriangleCount);
            Assert.AreEqual(exact.GridTriangleCount, padded.GridTriangleCount);

            // The spans must expose the logical range, which is what makes `.Length` and
            // `foreach` correct without every call site knowing about the padding.
            Assert.AreEqual(exact.Vertices.Length, padded.Vertices.Length);
            Assert.AreEqual(exact.Triangles.Length, padded.Triangles.Length);
            Assert.AreEqual(exact.GridCells.Length, padded.GridCells.Length);
            Assert.AreEqual(exact.GridTriangles.Length, padded.GridTriangles.Length);
        }

        [Test]
        public void Fingerprint_IgnoresTail()
        {
            // The fingerprint material must not see the tail. A surviving `.Length` here would
            // agree across peers (same pool history) while being wrong — the failure no desync
            // check can catch.
            FPNavMesh exact = BuildBase();
            FPNavMesh padded = WithPoisonedTail(exact);

            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(exact),
                FPNavMeshRebaker.ComputeFingerprint(padded),
                "fingerprint must be a function of the logical content only");
        }

        [Test]
        public void ObstacleExtraction_IgnoresTail()
        {
            // Highest severity: ghost ORCA obstacles from stale triangles.
            FPNavMesh exact = BuildBase();
            FPNavMesh padded = WithPoisonedTail(exact);

            FPNavMeshObstacleExtractor.Extract(exact, out var ev, out var eo);
            FPNavMeshObstacleExtractor.Extract(padded, out var pv, out var po);

            Assert.AreEqual(eo.Length, po.Length, "obstacle ring count must not change");
            Assert.AreEqual(ev.Length, pv.Length, "obstacle vertex count must not change (no ghost rings)");
            for (int i = 0; i < ev.Length; i++)
            {
                Assert.AreEqual(ev[i].x.RawValue, pv[i].x.RawValue, $"obstacle vertex {i}.x");
                Assert.AreEqual(ev[i].y.RawValue, pv[i].y.RawValue, $"obstacle vertex {i}.y");
            }
        }

        [Test]
        public void QueryAndPathfinding_IgnoreTail()
        {
            // A tail index passing as "valid" would let A* route through stale triangles.
            FPNavMesh exact = BuildBase();
            FPNavMesh padded = WithPoisonedTail(exact);

            var qe = new FPNavMeshQuery(exact, null);
            var qp = new FPNavMeshQuery(padded, null);
            var pe = new FPNavMeshPathfinder(exact, qe, null);
            var pp = new FPNavMeshPathfinder(padded, qp, null);

            FPVector3 start = new FPVector3(FP64.FromInt(-6), FP64.Zero, FP64.FromInt(-6));
            FPVector3 end = new FPVector3(FP64.FromInt(6), FP64.Zero, FP64.FromInt(6));

            Assert.AreEqual(
                qe.FindTriangle(start.ToXZ()), qp.FindTriangle(start.ToXZ()),
                "triangle lookup must not change");

            bool okE = pe.FindPath(start, end, ~0, out int[] corridorE, out int lenE);
            bool okP = pp.FindPath(start, end, ~0, out int[] corridorP, out int lenP);

            Assert.AreEqual(okE, okP, "path success must not change");
            Assert.AreEqual(lenE, lenP, "corridor length must not change");
            for (int i = 0; i < lenE; i++)
                Assert.AreEqual(corridorE[i], corridorP[i], $"corridor step {i} must not change");
        }

        [Test]
        public void Serialization_WritesLogicalRangeOnly()
        {
            // The tail must not reach the asset/replay bytes.
            FPNavMesh exact = BuildBase();
            FPNavMesh padded = WithPoisonedTail(exact);

            byte[] a = SerializeToBytes(exact);
            byte[] b = SerializeToBytes(padded);
            CollectionAssert.AreEqual(a, b, "serialized bytes must be identical");

            FPNavMesh round = FPNavMeshSerializer.Deserialize(b);
            Assert.AreEqual(exact.VertexCount, round.VertexCount);
            Assert.AreEqual(exact.TriangleCount, round.TriangleCount);
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(exact),
                FPNavMeshRebaker.ComputeFingerprint(round),
                "round-trip must preserve the logical content");
        }

        [Test]
        public void Rebake_FromPaddedBase_MatchesExact()
        {
            // The base mesh feeds CreateSnapshot/Rebake; padding it must not move the output.
            FPNavMesh exact = BuildBase();
            FPNavMesh padded = WithPoisonedTail(exact);

            var building = new[]
            {
                new FPBuildingRect(FP64.FromInt(-1), FP64.FromInt(-1), FP64.FromInt(1), FP64.FromInt(1), FP64.Zero),
            };

            FPNavMesh fromExact = FPNavMeshRebaker.Rebake(exact, building);
            FPNavMesh fromPadded = FPNavMeshRebaker.Rebake(padded, building);

            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(fromExact),
                FPNavMeshRebaker.ComputeFingerprint(fromPadded),
                "a padded base must rebake to the same mesh");
        }
    }
}
