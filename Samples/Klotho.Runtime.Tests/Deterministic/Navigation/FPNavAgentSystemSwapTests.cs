using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Navmesh swap and agent reseed envelope tests.
    /// Pins: SwapNavMesh rebinds the mesh and re-extracts ORCA obstacles (hole ring adds
    /// segments), GetNavFingerprint is non-zero and building-sensitive, ReseedAgents re-queries
    /// CurrentTriangleIndex / clears hashed corridor state / bypasses the repath cooldown so the
    /// next Update repaths around the new building, and an agent standing inside a carved hole
    /// fails its path instead of corrupting state.
    /// </summary>
    [TestFixture]
    public class FPNavAgentSystemSwapTests
    {
        #region Fixture

        private static FPNavMesh BuildBase()
        {
            var pts = new List<(int x, int z)>();
            for (int x = -10; x <= 10; x += 5)
            {
                for (int z = -10; z <= 10; z += 5)
                    pts.Add((x, z));
            }
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

        private static (FPNavAgentSystem system, FPNavMeshQuery query) CreateSystem(FPNavMesh mesh)
        {
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            var funnel = new FPNavMeshFunnel(mesh, query, null);
            var system = new FPNavAgentSystem(mesh, query, pathfinder, funnel, null);
            system.SetAvoidance(new FPNavAvoidance());
            system.LoadNavMeshObstacles();
            return (system, query);
        }

        private static void Swap(FPNavAgentSystem system, FPNavMesh newMesh, out FPNavMeshQuery newQuery)
        {
            newQuery = new FPNavMeshQuery(newMesh, null);
            var pathfinder = new FPNavMeshPathfinder(newMesh, newQuery, null);
            var funnel = new FPNavMeshFunnel(newMesh, newQuery, null);
            system.SwapNavMesh(newMesh, newQuery, pathfinder, funnel);
        }

        private static FPBuildingRect CenterBuilding()
        {
            // 2x2 at the origin; radius 0.5 -> expanded hole exactly 3x3.
            return new FPBuildingRect(
                FP64.FromInt(-1), FP64.FromInt(-1), FP64.FromInt(1), FP64.FromInt(1), FP64.Zero);
        }

        // Same wiring as CreateSystem, but hands back the avoidance — the only seam that exposes
        // what the graph-local BFS actually collected — and widens the obstacle horizon so a
        // single query covers the whole fixture (obstRange = TimeHorizonObst * 5 + 0.5, against a
        // 20x20 mesh). That matters for the stale-stamp test below: it makes one query's stamp set
        // a superset of the next query's frontier, so aliasing is total rather than partial and
        // the comparison cannot pass by luck.
        private static FPNavAgentSystem CreateSystemForBfs(
            FPNavMesh mesh, out FPNavMeshQuery query, out FPNavAvoidance avoidance)
        {
            query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            var funnel = new FPNavMeshFunnel(mesh, query, null);
            var system = new FPNavAgentSystem(mesh, query, pathfinder, funnel, null);
            avoidance = new FPNavAvoidance();
            avoidance.TimeHorizonObst = FP64.FromInt(20);
            system.SetAvoidance(avoidance);
            system.LoadNavMeshObstacles();
            return system;
        }

        // One Update with one Moving agent = exactly one graph-local BFS = one generation bump.
        private static void DriveOneQuery(FPNavAgentSystem system, FPNavMeshQuery query,
            FPVector3 position, FPVector3 destination, int tick)
        {
            Frame frame = NavAgentTestHelper.CreateFrameWithAgent(
                position, query.FindTriangle(position.ToXZ(), position.y),
                out EntityRef entity, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav, destination);
            system.Update(ref frame, entities, 1, tick, NavAgentTestHelper.DT);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, nav.Status,
                "probe agent must be Moving — the avoidance loop skips every other status, so a "
                + "non-Moving probe would run no BFS at all and the test would prove nothing");
        }

        private static HashSet<int> SelectedSegments(FPNavAvoidance avoidance)
        {
            var set = new HashSet<int>();
            int[] segments = avoidance.DebugSelectedObstacleSegments;
            for (int i = 0; i < avoidance.DebugSelectedObstacleCount; i++)
                set.Add(segments[i]);
            return set;
        }

        #endregion

        [Test]
        public void CurrentMeshAndQuery_TrackTheSwap_OnTheDirectPath()
        {
            // FPNavAgentSystem is the single source of truth for
            // "the mesh the simulation is on", so a diagnostics provider delegating here cannot
            // go stale. Exercised on the DIRECT SwapNavMesh path — no game system involved —
            // which is exactly what a per-game duplicate of this state would fail to cover.
            FPNavMesh baseMesh = BuildBase();
            var (system, query) = CreateSystem(baseMesh);

            Assert.AreSame(baseMesh, system.CurrentMesh, "before any swap the base mesh is current");
            Assert.AreSame(query, system.CurrentQuery, "before any swap the base query is current");

            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });
            Swap(system, rebaked, out FPNavMeshQuery newQuery);

            Assert.AreSame(rebaked, system.CurrentMesh, "CurrentMesh must follow the swap");
            Assert.AreSame(newQuery, system.CurrentQuery, "CurrentQuery must follow the swap");
            Assert.AreNotSame(baseMesh, system.CurrentMesh, "the base mesh must no longer be reported as current");

            // Reference identity alone would pass on any unrelated instance — pin the geometry
            // too, so "current" means the rebake output and not just "something different".
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(rebaked),
                FPNavMeshRebaker.ComputeFingerprint(system.CurrentMesh),
                "CurrentMesh must be the rebake output, not merely a different instance");
        }

        [Test]
        public void CurrentMesh_TracksEverySwap_AcrossSuccessiveRebakes()
        {
            FPNavMesh baseMesh = BuildBase();
            var (system, _) = CreateSystem(baseMesh);

            FPNavMesh first = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });
            Swap(system, first, out _);
            Assert.AreSame(first, system.CurrentMesh);

            FPNavMesh second = FPNavMeshRebaker.Rebake(baseMesh, new[]
            {
                CenterBuilding(),
                new FPBuildingRect(FP64.FromInt(4), FP64.FromInt(4), FP64.FromInt(6), FP64.FromInt(6), FP64.Zero),
            });
            Swap(system, second, out _);
            Assert.AreSame(second, system.CurrentMesh, "a second swap must be tracked too");
            Assert.AreEqual(
                FPNavMeshRebaker.ComputeFingerprint(second),
                FPNavMeshRebaker.ComputeFingerprint(system.CurrentMesh));
        }

        [Test]
        public void SwapNavMesh_ReloadsObstacles_FingerprintChanges()
        {
            FPNavMesh baseMesh = BuildBase();
            var (system, _) = CreateSystem(baseMesh);

            int baseObstacles = system.DebugObstacleCount;
            long baseFp = system.GetNavFingerprint();
            Assert.Greater(baseObstacles, 0, "base boundary must load as ORCA obstacles");
            Assert.AreNotEqual(0L, baseFp, "fingerprint must be non-zero while a mesh is present");

            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });
            Swap(system, rebaked, out _);

            Assert.Greater(system.DebugObstacleCount, baseObstacles,
                "carved hole ring must add ORCA obstacle vertices");
            Assert.AreNotEqual(baseFp, system.GetNavFingerprint(),
                "fingerprint must change with the building set");
        }

        // ── V8 — the one-argument overload is the four-argument one, minus the allocation ──

        [Test]
        public void SwapNavMesh_OneArg_MatchesFourArg_OnObservables()
        {
            // "Equivalent" here means what the simulation can observe: the mesh, the obstacle
            // set, the fingerprint, and the path an agent would follow. It deliberately does NOT
            // mean reference identity — the one-argument form keeps its query instance, which is
            // the entire point, and the test below pins that difference so nobody "fixes" the
            // rebind into allocating again just to make an AreSame pass.
            FPNavMesh baseMesh = BuildBase();
            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });

            var (fourArg, _) = CreateSystem(baseMesh);
            Swap(fourArg, rebaked, out FPNavMeshQuery replacedQuery);

            var (oneArg, keptQuery) = CreateSystem(baseMesh);
            oneArg.SwapNavMesh(rebaked);

            Assert.AreSame(rebaked, oneArg.CurrentMesh, "the mesh must be adopted either way");
            Assert.AreEqual(fourArg.DebugObstacleCount, oneArg.DebugObstacleCount,
                "obstacle re-extraction must produce the same set");
            Assert.AreEqual(fourArg.GetNavFingerprint(), oneArg.GetNavFingerprint(),
                "the nav fingerprint is what peers compare — it cannot depend on which overload ran");

            var start = new FPVector3(FP64.FromInt(-8), FP64.Zero, FP64.FromInt(-8));
            var end = new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.FromInt(8));
            bool okFour = fourArg.CurrentQuery != null;
            Assert.IsTrue(okFour);

            var pfFour = new FPNavMeshPathfinder(rebaked, fourArg.CurrentQuery, null);
            var pfOne = new FPNavMeshPathfinder(rebaked, oneArg.CurrentQuery, null);
            bool foundFour = pfFour.FindPath(start, end, ~0, out int[] corridorFour, out int lenFour);
            var snapshot = new int[lenFour];
            Array.Copy(corridorFour, snapshot, lenFour);
            bool foundOne = pfOne.FindPath(start, end, ~0, out int[] corridorOne, out int lenOne);

            Assert.AreEqual(foundFour, foundOne, "the two overloads must agree on whether a path exists");
            Assert.AreEqual(lenFour, lenOne, "corridor length must not depend on the overload");
            for (int i = 0; i < lenFour; i++)
                Assert.AreEqual(snapshot[i], corridorOne[i], $"corridor[{i}] depends on the overload");

            // The one difference, asserted so it stays intentional.
            Assert.AreSame(keptQuery, oneArg.CurrentQuery,
                "the one-argument form rebinds in place — the caller's query reference stays valid");
            Assert.AreNotSame(keptQuery, replacedQuery,
                "the four-argument form installs whatever the caller built, so its query is a new object");
        }

        [Test]
        public unsafe void ReseedAgents_RepathsAroundNewBuilding()
        {
            FPNavMesh baseMesh = BuildBase();
            var (system, query) = CreateSystem(baseMesh);

            // Agent walking straight through the future building site.
            var start = new FPVector3(FP64.FromInt(-8), FP64.Zero, FP64.Zero);
            var dest = new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero);
            Frame frame = NavAgentTestHelper.CreateFrameWithAgent(
                start, query.FindTriangle(start.ToXZ(), start.y), out EntityRef entity, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav, dest);

            system.Update(ref frame, entities, 1, currentTick: 1, NavAgentTestHelper.DT);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, nav.Status, "path must compute on the base mesh");
            Assert.Greater(nav.CorridorLength, 0);
            int staleTriangle = nav.CurrentTriangleIndex;

            // Rebake with the building, swap, reseed.
            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });
            Swap(system, rebaked, out FPNavMeshQuery newQuery);
            system.ReseedAgents(ref frame, entities, 1);

            Assert.AreEqual(newQuery.FindTriangle(start.ToXZ(), start.y), nav.CurrentTriangleIndex,
                "CurrentTriangleIndex must be re-queried on the new mesh");
            Assert.GreaterOrEqual(nav.CurrentTriangleIndex, 0);
            Assert.AreEqual(0, nav.CorridorLength, "hashed corridor must be invalidated");
            Assert.IsFalse(nav.PathIsValid);
            Assert.AreEqual((byte)FPNavAgentStatus.PathPending, nav.Status, "agent with destination must repath");
            Assert.AreEqual(0, nav.LastRepathTick, "repath cooldown must be bypassed");

            // Next tick repaths around the hole: corridor exists and never enters the carved 3x3.
            system.Update(ref frame, entities, 1, currentTick: 2, NavAgentTestHelper.DT);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, nav.Status, "repath must succeed around the building");
            Assert.Greater(nav.CorridorLength, 0);

            long h = 3 * 1536; // 1.5 world * 1024, x3 (centroid trick)
            for (int i = 0; i < nav.CorridorLength; i++)
            {
                var t = rebaked.Triangles[nav.Corridor[i]];
                long cx = FPGeoPredicates.Snap(rebaked.Vertices[t.v0].x)
                        + FPGeoPredicates.Snap(rebaked.Vertices[t.v1].x)
                        + FPGeoPredicates.Snap(rebaked.Vertices[t.v2].x);
                long cz = FPGeoPredicates.Snap(rebaked.Vertices[t.v0].z)
                        + FPGeoPredicates.Snap(rebaked.Vertices[t.v1].z)
                        + FPGeoPredicates.Snap(rebaked.Vertices[t.v2].z);
                bool insideHole = cx > -h && cx < h && cz > -h && cz < h;
                Assert.IsFalse(insideHole, $"corridor triangle {i} must not enter the carved hole");
            }
        }

        [Test]
        public void ReseedAgents_AgentInsideCarvedHole_PathFails()
        {
            FPNavMesh baseMesh = BuildBase();
            var (system, query) = CreateSystem(baseMesh);

            var inHole = new FPVector3(FP64.Zero, FP64.Zero, FP64.Zero);
            Frame frame = NavAgentTestHelper.CreateFrameWithAgent(
                inHole, query.FindTriangle(inHole.ToXZ(), inHole.y), out EntityRef entity, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav, new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero));

            FPNavMesh rebaked = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });
            Swap(system, rebaked, out _);
            system.ReseedAgents(ref frame, entities, 1);

            Assert.AreEqual(-1, nav.CurrentTriangleIndex, "agent inside the hole is off-mesh");
            Assert.AreEqual((byte)FPNavAgentStatus.PathFailed, nav.Status);
            Assert.AreEqual(0, nav.CorridorLength);
        }

        [Test]
        public void GetNavFingerprint_MatchesRebakerFingerprint_NeverZero()
        {
            FPNavMesh baseMesh = BuildBase();
            var (system, _) = CreateSystem(baseMesh);

            long expected = unchecked((long)FPNavMeshRebaker.ComputeFingerprint(baseMesh));
            Assert.AreEqual(expected == 0 ? 1L : expected, system.GetNavFingerprint());
        }

        [Test]
        public unsafe void ReseedAgents_OnAnIdenticalMesh_StillRewritesHashedState()
        {
            // The premise the FullState path's fix rests on: ReseedAgents is a STATE WRITE, not a
            // repair. Swap to a byte-identical mesh — nothing about any agent has been invalidated
            // — and it still clears the corridor and the path flags, all of which are hashed
            // NavAgentComponent state.
            //
            // That is fine on the command path, where every peer executes the same command on the
            // same tick and therefore writes the same thing. It is not fine on the FullState hook,
            // which only the APPLYING peer runs, right after that peer's hash was verified against
            // the authority's. Hence BotFSMSystem.SwapForRestoredState.
            FPNavMesh baseMesh = BuildBase();
            FPNavMesh meshA = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });
            FPNavMesh meshB = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });
            Assert.AreEqual(FPNavMeshRebaker.ComputeFingerprint(meshA),
                FPNavMeshRebaker.ComputeFingerprint(meshB),
                "fixture: the two rebakes must be byte-identical, so nothing an agent holds is stale");

            var (system, query) = CreateSystem(meshA);
            var start = new FPVector3(FP64.FromInt(-8), FP64.Zero, FP64.Zero);
            var dest = new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero);
            Frame frame = NavAgentTestHelper.CreateFrameWithAgent(
                start, query.FindTriangle(start.ToXZ(), start.y), out EntityRef entity, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(entity);
            NavAgentComponent.SetDestination(ref nav, dest);
            system.Update(ref frame, entities, 1, currentTick: 1, NavAgentTestHelper.DT);

            Assert.AreEqual((byte)FPNavAgentStatus.Moving, nav.Status, "fixture: the agent must have pathed");
            Assert.Greater(nav.CorridorLength, 0, "fixture: there must be a corridor to lose");
            int trackedTriangle = nav.CurrentTriangleIndex;
            Assert.GreaterOrEqual(trackedTriangle, 0);

            Swap(system, meshB, out _);
            system.ReseedAgents(ref frame, entities, 1);

            // Every one of these is in the generated hash fold. On the FullState path they would
            // move on the applying peer and nowhere else.
            Assert.AreEqual(0, nav.CorridorLength,
                "the corridor is cleared even though the identical mesh left it perfectly valid");
            Assert.IsFalse(nav.PathIsValid);
            Assert.IsFalse(nav.HasPath);
            Assert.AreEqual((byte)FPNavAgentStatus.PathPending, nav.Status);
            Assert.AreEqual(0, nav.LastRepathTick);
        }

        [Test]
        public void SwapThatDoesNotGrow_MustNotAliasStaleBfsStamps()
        {
            // SwapNavMesh re-enters LoadNavMeshObstacles, and the BFS visited-stamp array is only
            // reallocated when it is too SMALL. A rebake that does not grow the triangle count —
            // removing a building — therefore keeps the previous mesh's stamps in place. Restart
            // the generation counter at 0 there and generation 1 aliases every slot still holding
            // 1: the graph-local BFS treats those triangles as already visited and never adopts
            // their wall segments, so ORCA steers through walls.
            //
            // It desyncs rather than merely misbehaving, because the stale content is a function
            // of how many queries THIS process has run — not of the replicated command stream.
            // A peer that lived through place+remove and one that joined onto the same mesh
            // disagree, and DesiredVelocity is hashed NavAgentComponent state.
            FPNavMesh baseMesh = BuildBase();
            FPNavMesh placed = FPNavMeshRebaker.Rebake(baseMesh, new[] { CenterBuilding() });

            // Removing the only building reproduces the base geometry, so the base mesh IS the
            // post-removal mesh; using it directly keeps the triangle counts below exact.
            //
            // Assert the aliasing configuration instead of assuming it — the stamp array has to
            // be grown on the way in and reused on the way back. A fixture change that stops
            // producing that must fail here, not silently turn this test into a tautology.
            Assert.Greater(placed.TriangleCount, baseMesh.TriangleCount,
                "fixture: placing must grow the triangle count, so the stamp array is reallocated");
            Assert.LessOrEqual(baseMesh.TriangleCount, placed.TriangleCount,
                "fixture: removing must not grow it, so the dirty stamp array is reused");

            var probe = new FPVector3(FP64.FromInt(-8), FP64.Zero, FP64.Zero);
            var dest = new FPVector3(FP64.FromInt(8), FP64.Zero, FP64.Zero);

            // Peer A: placed a building, ran a query against it, then removed the building.
            FPNavAgentSystem lived = CreateSystemForBfs(baseMesh, out _, out FPNavAvoidance livedAv);
            Swap(lived, placed, out FPNavMeshQuery placedQuery);
            DriveOneQuery(lived, placedQuery, probe, dest, tick: 1);
            Swap(lived, baseMesh, out FPNavMeshQuery backQuery);
            DriveOneQuery(lived, backQuery, probe, dest, tick: 2);

            // Peer B: joined onto the same final mesh and never saw those two swaps.
            FPNavAgentSystem fresh = CreateSystemForBfs(
                baseMesh, out FPNavMeshQuery freshQuery, out FPNavAvoidance freshAv);
            DriveOneQuery(fresh, freshQuery, probe, dest, tick: 2);

            HashSet<int> expected = SelectedSegments(freshAv);
            Assert.Greater(expected.Count, 1,
                "fixture: the probe must select several wall segments, otherwise an equal-set "
                + "assertion proves nothing");
            CollectionAssert.AreEquivalent(expected, SelectedSegments(livedAv),
                "a peer that lived through place+remove must collect the same obstacle segments "
                + "as one that joined straight onto the same mesh");
        }
    }
}
