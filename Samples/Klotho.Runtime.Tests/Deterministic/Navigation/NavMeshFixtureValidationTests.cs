using NUnit.Framework;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Structural validation of hand-authored NavMesh fixtures against the baker's
    /// stored invariants (FPNavMeshBuildPipeline). Catches the class of hand-authored data
    /// defects — wrong adjacency / portal / centroid / winding — that a real bake would never
    /// produce. The check is a per-edge guard tree (interior edge vs boundary edge) plus
    /// centroid and winding-consistency; it reuses FPNavMeshTriangle.GetEdgeVertices and
    /// the baker's opposite-vertex mapping rather than re-hardcoding the edge layout.
    /// </summary>
    internal static class NavMeshFixtureValidator
    {
        private static readonly FP64 CENTROID_EPSILON = FP64.FromFloat(0.01f);

        public static void Validate(FPNavMesh mesh, string label)
        {
            int triCount = mesh.Triangles.Length;

            // ── winding consistency: every triangle shares the sign of triangle 0 ──
            // (check 4 is winding-agnostic; funnel string-pull assumes a globally
            //  consistent winding, so this cheap check closes that class.)
            bool? windingPositive = null;
            for (int i = 0; i < triCount; i++)
            {
                var t = mesh.Triangles[i];
                FPVector2 e1 = mesh.Vertices[t.v1].ToXZ() - mesh.Vertices[t.v0].ToXZ();
                FPVector2 e2 = mesh.Vertices[t.v2].ToXZ() - mesh.Vertices[t.v0].ToXZ();
                FP64 cross = FPVector2.Cross(e1, e2);
                Assert.IsTrue(cross != FP64.Zero, $"[{label}] T{i}: degenerate winding (cross=0)");
                bool positive = cross > FP64.Zero;
                if (windingPositive == null)
                    windingPositive = positive;
                else
                    Assert.AreEqual(windingPositive.Value, positive,
                        $"[{label}] T{i}: winding sign differs from T0 (mixed winding)");
            }

            // ── per-triangle / per-edge guard tree ──
            for (int i = 0; i < triCount; i++)
            {
                var t = mesh.Triangles[i];

                // centroid (vertex XZ average)
                FPVector2 centroid = (mesh.Vertices[t.v0].ToXZ()
                    + mesh.Vertices[t.v1].ToXZ()
                    + mesh.Vertices[t.v2].ToXZ()) / FP64.FromInt(3);
                Assert.IsTrue(
                    FP64.Abs(t.centerXZ.x - centroid.x) < CENTROID_EPSILON
                    && FP64.Abs(t.centerXZ.y - centroid.y) < CENTROID_EPSILON,
                    $"[{label}] T{i}: centerXZ ({t.centerXZ.x},{t.centerXZ.y}) != centroid ({centroid.x},{centroid.y})");

                for (int e = 0; e < 3; e++)
                {
                    int nb = t.GetNeighbor(e);
                    t.GetEdgeVertices(e, out int va, out int vb);
                    t.GetPortal(e, out int pl, out int pr);

                    if (nb < 0)
                    {
                        // boundary edge: baker keeps portal (-1,-1)
                        Assert.IsTrue(pl == -1 && pr == -1,
                            $"[{label}] T{i} e{e}: boundary edge but portal=({pl},{pr}), expected (-1,-1)");
                        continue;
                    }

                    // interior edge
                    Assert.IsTrue(nb < triCount,
                        $"[{label}] T{i} e{e}: neighbor {nb} out of range");
                    var b = mesh.Triangles[nb];

                    // 1. edge-matched symmetry: B references T back over the SAME vertex pair
                    bool symmetric = false;
                    for (int eb = 0; eb < 3; eb++)
                    {
                        if (b.GetNeighbor(eb) != i)
                            continue;
                        b.GetEdgeVertices(eb, out int bva, out int bvb);
                        if (SamePair(va, vb, bva, bvb))
                        {
                            symmetric = true;
                            break;
                        }
                    }
                    Assert.IsTrue(symmetric,
                        $"[{label}] T{i} e{e}→T{nb}: no edge-matched back-reference for vertices ({va},{vb})");

                    // 2. edge actually shared: both endpoints belong to B
                    Assert.IsTrue(HasVertex(b, va) && HasVertex(b, vb),
                        $"[{label}] T{i} e{e}→T{nb}: edge ({va},{vb}) not both in neighbor's vertices");

                    // 3. portal vertices == edge vertex pair (unordered) — precondition of 4
                    Assert.IsTrue(SamePair(pl, pr, va, vb),
                        $"[{label}] T{i} e{e}: portal ({pl},{pr}) != edge ({va},{vb})");

                    // 4. portal direction matches baker invariant: Cross(d, left-right) < 0
                    //    d = edgeMid - oppositeVertex (stored portalLeft/Right used verbatim)
                    int opp = OppositeVertex(t, e);
                    FPVector2 edgeMid = (mesh.Vertices[va].ToXZ() + mesh.Vertices[vb].ToXZ()) * FP64.Half;
                    FPVector2 d = edgeMid - mesh.Vertices[opp].ToXZ();
                    FP64 cross = FPVector2.Cross(d, mesh.Vertices[pl].ToXZ() - mesh.Vertices[pr].ToXZ());
                    Assert.IsTrue(cross < FP64.Zero,
                        $"[{label}] T{i} e{e}: portal direction Cross={cross} >= 0 (left/right reversed)");
                }
            }
        }

        private static bool SamePair(int a0, int a1, int b0, int b1)
            => (a0 == b0 && a1 == b1) || (a0 == b1 && a1 == b0);

        private static bool HasVertex(FPNavMeshTriangle t, int v)
            => t.v0 == v || t.v1 == v || t.v2 == v;

        // baker opposite-vertex mapping: e0→v2, e1→v0, e2→v1
        private static int OppositeVertex(FPNavMeshTriangle t, int e)
        {
            switch (e)
            {
                case 0: return t.v2;
                case 1: return t.v0;
                case 2: return t.v1;
                default: return -1;
            }
        }
    }

    [TestFixture]
    public class NavMeshFixtureValidationTests
    {
        [Test]
        public void Validate_4TriFixture_MatchesBakerInvariants()
            => NavMeshFixtureValidator.Validate(NavAgentTestHelper.Create4TriNavMesh(), "4-tri");

        [Test]
        public void Validate_LShapedFixture_MatchesBakerInvariants()
            => NavMeshFixtureValidator.Validate(NavAgentTestHelper.CreateLShapedNavMesh(), "L-shaped");
    }
}
