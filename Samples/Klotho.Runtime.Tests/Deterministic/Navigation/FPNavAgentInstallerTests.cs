using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// The call protocol for putting a rebaked mesh in front of the agents.
    ///
    /// <para>Everything here is about the SHAPE of the two calls rather than about the mesh: what
    /// the pieces do individually is already pinned by <c>FPNavAgentSystemSwapTests</c>. What was
    /// never pinned — and what four live matches of desync came out of — is the protocol around
    /// them: reseed and install are separate, and reseed re-collects.</para>
    /// </summary>
    [TestFixture]
    public class FPNavAgentInstallerTests
    {
        private static FPNavMesh Slab()
        {
            FP64 lo = FP64.FromInt(-10), hi = FP64.FromInt(10);
            var vertices = new[]
            {
                new FPVector3(lo, FP64.Zero, lo), new FPVector3(hi, FP64.Zero, lo),
                new FPVector3(hi, FP64.Zero, hi), new FPVector3(lo, FP64.Zero, hi),
            };
            var xs = new long[4];
            var zs = new long[4];
            for (int i = 0; i < 4; i++)
            {
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        private static FPNavAgentSystem SystemOver(FPNavMesh mesh)
        {
            var query = new FPNavMeshQuery(mesh, null);
            return new FPNavAgentSystem(
                mesh, query, new FPNavMeshPathfinder(mesh, query, null),
                new FPNavMeshFunnel(mesh, query, null), null);
        }

        [Test]
        public void Reseed_RecollectsFromTheFrame_RatherThanTrustingTheCallersBuffer()
        {
            // The caller owns the buffer, and it may hold LAST tick's set: a command phase
            // runs before the update that maintains it. This is also the path a boundary tick takes
            // when the mesh it wants is already installed, so there is no Swap ahead of it to have
            // refreshed anything.
            //
            // Reseeding a stale set writes hashed NavAgent state for the wrong entities — and every
            // peer that does it agrees with itself, so no state hash objects. The failure is only
            // visible as agents whose corridor indexes a mesh they are not on.
            FPNavMesh mesh = Slab();
            FPNavAgentSystem system = SystemOver(mesh);

            Frame frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[] { FPVector3.Zero, new FPVector3(FP64.FromInt(2), FP64.Zero, FP64.Zero) },
                new[] { 0, 0 }, out EntityRef[] real);

            // What the game's buffer might look like: one entry, and not even a live one.
            var stale = new EntityRef[16];
            stale[0] = default;

            int count = FPNavAgentInstaller.Reseed(ref frame, system, ref stale);

            Assert.AreEqual(real.Length, count,
                "Reseed reported the caller's stale count. It must re-collect — the buffer it is "
                + "handed can be last tick's, and on the reseed-only path nothing else refreshes it");
            for (int i = 0; i < count; i++)
                Assert.AreEqual(real[i], stale[i],
                    $"entry {i} was not refreshed from the frame");
        }

        [Test]
        public void Collect_GrowsTheCallersBuffer_AndNeverTruncates()
        {
            // Grow rather than cut, because an agent past a cut keeps a CurrentTriangleIndex and
            // corridor that index the OLD mesh — exactly what the reseed exists to prevent. The
            // asymmetry bites hardest right after a FullState apply: that peer may not have run a
            // single update, so its array is still at its initial size while the authority's grew.
            var positions = new FPVector3[9];
            var triangles = new int[9];
            for (int i = 0; i < 9; i++)
                positions[i] = new FPVector3(FP64.FromInt(i - 4), FP64.Zero, FP64.Zero);

            Frame frame = NavAgentTestHelper.CreateFrameWithAgents(
                positions, triangles, out EntityRef[] real);

            var small = new EntityRef[2];
            int count = FPNavAgentInstaller.Collect(ref frame, ref small);

            Assert.AreEqual(real.Length, count,
                "the collection truncated to the buffer it was given instead of growing it");
            Assert.GreaterOrEqual(small.Length, count, "the buffer did not grow to fit");
            for (int i = 0; i < count; i++)
                Assert.AreEqual(real[i], small[i], $"entry {i} is not the frame's agent");
        }

        [Test]
        public void Swap_DoesNotReseed_SoTheTwoHalvesStaySeparable()
        {
            // The four-match divergence in one assertion. Installing a mesh is derived state and
            // may be skipped when the right one is already in place; reseeding writes hashed frame
            // state and may not. Fusing them makes the write conditional on a peer-local comparison
            // that does not roll back — a boundary re-executed after its mesh was already installed
            // then skips the reseed, and the client keeps the triangle index it had before the swap.
            //
            // Observable because ReseedAgents clears the corridor and path flags unconditionally:
            // an agent that still carries them after a Swap proves the two halves are separate.
            FPNavMesh mesh = Slab();
            FPNavAgentSystem system = SystemOver(mesh);

            Frame frame = NavAgentTestHelper.CreateFrameWithAgent(
                FPVector3.Zero, 0, out EntityRef entity, out EntityRef[] agents);
            ref var before = ref frame.Get<NavAgentComponent>(entity);
            before.CorridorLength = 3;
            before.PathIsValid = true;

            FPNavAgentInstaller.Swap(ref frame, system, mesh, ref agents);

            ref readonly var after = ref frame.GetReadOnly<NavAgentComponent>(entity);
            Assert.AreEqual(3, after.CorridorLength,
                "Swap reseeded. The two halves must stay separate: a reseed folded into the install "
                + "becomes conditional on peer-local state that does not roll back");
            Assert.IsTrue(after.PathIsValid, "Swap reseeded — see above");
        }
    }
}
