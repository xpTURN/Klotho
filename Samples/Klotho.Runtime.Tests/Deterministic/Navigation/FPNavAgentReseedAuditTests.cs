using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// ReseedAgents' caller audit. The reseed takes the caller's own array and count, and nothing
    /// about that pair says whether it is the whole agent set or a truncated view — so a caller
    /// that collects into a fixed-size buffer and stops at its length leaves the remaining agents
    /// holding a CurrentTriangleIndex and corridor that index the mesh that was just replaced.
    ///
    /// Both fields are hashed frame state, which is what makes this a desync rather than a
    /// glitch: the authority (whose buffer has grown over many Updates) reseeds everyone while a
    /// peer swapping right after a full-state apply may still be at its initial size and reseed
    /// fewer. The engine cannot fix the caller's bookkeeping — it can only refuse to let the
    /// mismatch pass in silence, which is the entire cost of this class of bug.
    /// </summary>
    [TestFixture]
    public class FPNavAgentReseedAuditTests
    {
        private sealed class CapturingLogger : IKLogger
        {
            public readonly List<string> Errors = new List<string>();

            public bool IsEnabled(KLogLevel level) => true;

            public void Log(KLogLevel level, string message, System.Exception exception)
            {
                if (level >= KLogLevel.Error)
                    Errors.Add(message);
            }
        }

        private static FPNavMesh BuildBase()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -10; x <= 10; x += 5)
                for (int z = -10; z <= 10; z += 5)
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

        private static (FPNavAgentSystem system, FPNavMeshQuery query) CreateSystem(
            FPNavMesh mesh, IKLogger logger)
        {
            var query = new FPNavMeshQuery(mesh, logger);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, logger);
            var funnel = new FPNavMeshFunnel(mesh, query, logger);
            return (new FPNavAgentSystem(mesh, query, pathfinder, funnel, logger), query);
        }

        private static Frame FrameWithAgents(FPNavMeshQuery query, int count, out EntityRef[] entities)
        {
            var positions = new FPVector3[count];
            var triangles = new int[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = new FPVector3(FP64.FromInt(-8 + i), FP64.Zero, FP64.Zero);
                triangles[i] = query.FindTriangle(positions[i].ToXZ(), positions[i].y);
            }
            return NavAgentTestHelper.CreateFrameWithAgents(positions, triangles, out entities);
        }

        [Test]
        public unsafe void TruncatedCallerCount_IsReported()
        {
            var log = new CapturingLogger();
            FPNavMesh mesh = BuildBase();
            var (system, query) = CreateSystem(mesh, log);

            Frame frame = FrameWithAgents(query, 5, out EntityRef[] entities);

            // The caller's buffer held only 3 — exactly the shape of a fixed-size collector that
            // stops at its length instead of growing.
            system.ReseedAgents(ref frame, entities, 3);

            Assert.AreEqual(1, log.Errors.Count, "a truncated reseed must be reported, not silent");
            StringAssert.Contains("3", log.Errors[0]);
            StringAssert.Contains("5", log.Errors[0]);
        }

        [Test]
        public unsafe void MatchingCount_IsSilent()
        {
            // The counterpart, so the audit above is known to be a signal and not a constant:
            // the normal path must not produce an error, or the check would be noise that
            // callers learn to ignore.
            var log = new CapturingLogger();
            FPNavMesh mesh = BuildBase();
            var (system, query) = CreateSystem(mesh, log);

            Frame frame = FrameWithAgents(query, 5, out EntityRef[] entities);
            system.ReseedAgents(ref frame, entities, entities.Length);

            CollectionAssert.IsEmpty(log.Errors, "a complete reseed must be silent");
        }

        [Test]
        public unsafe void StaleCallerList_IsReportedThenThrows()
        {
            // The other direction: a caller whose cached array still holds last tick's entities
            // passes a count larger than the frame's agent set. The audit reports it — and then
            // the reseed loop throws on the entity that no longer carries the component.
            //
            // Both halves are pinned deliberately. The throw is pre-existing behaviour and is the
            // right one for hashed state (a caller feeding a stale list must not get a half-done
            // reseed), but on its own it is an IndexOutOfRange with no explanation. The audit is
            // what turns it into a diagnosis, so the two belong together and neither should be
            // "fixed" into silence without the other being reconsidered.
            var log = new CapturingLogger();
            FPNavMesh mesh = BuildBase();
            var (system, query) = CreateSystem(mesh, log);

            Frame frame = FrameWithAgents(query, 3, out EntityRef[] entities);
            frame.Remove<NavAgentComponent>(entities[2]);

            Frame captured = frame;
            Assert.Throws<System.IndexOutOfRangeException>(
                () => system.ReseedAgents(ref captured, entities, 3),
                "a stale caller list must not produce a half-done reseed");

            Assert.AreEqual(1, log.Errors.Count,
                "and the audit must have named the mismatch before the throw — otherwise the "
                + "exception carries no clue about which caller is wrong");
        }
    }
}
