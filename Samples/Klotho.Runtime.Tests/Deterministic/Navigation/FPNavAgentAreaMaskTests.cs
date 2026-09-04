using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation.Tests
{
    /// <summary>
    /// Per-agent area masks: each agent names its own PLAN mask and WALK mask, and the two
    /// may disagree on purpose.
    ///
    /// <para><b>Why the destination goes INSIDE the footprint in every gate here.</b> "The corridor
    /// passes through the building" cannot be asserted by putting start and goal on opposite sides:
    /// A*'s corridor cost is a portal-midpoint polyline, so a permissive agent routes AROUND a
    /// small footprint just like a restrictive one and the assertion passes without measuring
    /// anything — a trap this repository has now walked into twice.
    /// An endpoint inside the footprint makes the corridor contain building ground BY CONSTRUCTION,
    /// which is what these gates need.</para>
    /// </summary>
    [TestFixture]
    public class FPNavAgentAreaMaskTests
    {
        private const int Cells = 8;                 // open field spans 0..16
        private const double Bx = 8.0, Bz = 8.0;     // building centre, comfortably interior
        private const double StartX = 2.0;           // far enough to walk for many ticks first

        private const int Permissive = FPNavMeshAreas.ALL_AREAS;
        private const int Restrictive = FPNavAgentSystem.DEFAULT_AREA_MASK;

        private static FPVector3 P(double x, double z) =>
            new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z));

        /// <summary>Open field with one RETAINED building at (Bx, Bz) — ground, stamped.</summary>
        private static FPNavMesh MeshWithRetainedBuilding()
        {
            var basemesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            return NavAgentTestHelper.RebakeWithBuildings(
                basemesh, NavAgentTestHelper.Building(Bx, Bz, retain: true));
        }

        private static Frame AgentAt(
            FPNavMesh mesh, FPVector3 start, out EntityRef entity, out EntityRef[] entities)
        {
            var query = new FPNavMeshQuery(mesh, null);
            return NavAgentTestHelper.CreateFrameWithAgent(
                start, query.FindTriangle(start.ToXZ()), out entity, out entities);
        }

        private static bool IsBuilding(FPNavMesh mesh, int tri) =>
            tri >= 0 && mesh.Triangles[tri].areaMask == FPNavMeshAreas.BUILDING_MASK;

        // ── W-9: the plan goes through, the walk does not ────────────────────

        [Test]
        public void W9_PlanMaskRoutesThroughTheFootprint_WhileTheWalkMaskRefusesToEnterIt()
        {
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var frame = AgentAt(mesh, P(StartX, Bz), out EntityRef e, out EntityRef[] es);

            ref var nav = ref frame.Get<NavAgentComponent>(e);
            NavAgentComponent.SetAreaMask(ref nav, Permissive, Restrictive);
            NavAgentComponent.SetDestination(ref nav, P(Bx, Bz));

            bool corridorHitBuilding = false;
            int ticksInsideFootprint = 0;

            for (int t = 1; t <= 200; t++)
            {
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);
                ref var cur = ref frame.Get<NavAgentComponent>(e);

                unsafe
                {
                    fixed (int* p = cur.Corridor)
                        for (int i = 0; i < cur.CorridorLength; i++)
                            if (IsBuilding(mesh, p[i]))
                                corridorHitBuilding = true;
                }

                if (IsBuilding(mesh, cur.CurrentTriangleIndex))
                    ticksInsideFootprint++;
            }

            // Half one: planning did not know about the building.
            Assert.IsTrue(corridorHitBuilding,
                "the permissive plan mask must produce a corridor containing building ground — the "
                + "destination is inside the footprint, so this can only fail if FindPath received "
                + "the restrictive mask instead of the plan mask");

            // Half two: walking did.
            Assert.AreEqual(0, ticksInsideFootprint,
                "the restrictive walk mask must never let the agent stand on building ground");
        }

        [Test]
        public void W9_OptIn_AnAgentThatNamesNoMask_StillRoutesAroundTheBuilding()
        {
            // Zero means "no override", so per-agent masks changed no existing behaviour. This
            // is the one gate that reddens if zero is ever read as ALL_AREAS.
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null, out var pathfinder);
            var frame = AgentAt(mesh, P(StartX, Bz), out EntityRef e, out EntityRef[] es);

            ref var nav = ref frame.Get<NavAgentComponent>(e);
            Assert.AreEqual(0, nav.PlanAreaMaskOverride, "Init must leave the override at zero");
            Assert.AreEqual(0, nav.WalkAreaMaskOverride, "Init must leave the override at zero");

            NavAgentComponent.SetDestination(ref nav, P(Bx, Bz));
            system.Update(ref frame, es, es.Length, 1, NavAgentTestHelper.DT);

            ref var cur = ref frame.Get<NavAgentComponent>(e);
            Assert.AreEqual(FPNavAgentStatus.PathFailed, (FPNavAgentStatus)cur.Status,
                "an agent naming no mask must behave exactly as before these fields existed: the "
                + "destination is inside a retained footprint, so its plan is refused");
            Assert.AreEqual(1, pathfinder.DebugAreaMaskRejectedCount,
                "and the refusal must land in the plan-side counter");
        }

        // ── W-10 / W-12: stop on contact, and not before ─────────────────────

        [Test]
        public void W10_TheAgentStopsWhereItTouches_AndSaysSo()
        {
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var frame = AgentAt(mesh, P(StartX, Bz), out EntityRef e, out EntityRef[] es);

            ref var nav = ref frame.Get<NavAgentComponent>(e);
            NavAgentComponent.SetAreaMask(ref nav, Permissive, Restrictive);
            NavAgentComponent.SetDestination(ref nav, P(Bx, Bz));

            int blockedAtTick = -1;
            for (int t = 1; t <= 200 && blockedAtTick < 0; t++)
            {
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);
                if ((FPNavAgentStatus)frame.Get<NavAgentComponent>(e).Status
                    == FPNavAgentStatus.Blocked)
                    blockedAtTick = t;
            }

            Assert.Greater(blockedAtTick, 0,
                "the agent walked into ground its mask refuses and nothing named it — that is the "
                + "silent stall this state exists to end (Moving, valid path, no repath)");

            ref var cur = ref frame.Get<NavAgentComponent>(e);
            Assert.AreEqual(FP64.Zero, cur.CurrentSpeed, "a blocked agent is not moving");
            Assert.IsFalse(IsBuilding(mesh, cur.CurrentTriangleIndex),
                "it stopped outside the footprint, not inside it");

            // And the game can release it. Widening the walk mask must un-park the agent — without
            // the Status write in SetAreaMask it would stay Blocked forever.
            NavAgentComponent.SetAreaMask(ref cur, Permissive, Permissive);
            Assert.AreNotEqual(FPNavAgentStatus.Blocked, (FPNavAgentStatus)cur.Status,
                "SetAreaMask must release a blocked agent");

            for (int t = 201; t <= 500; t++)
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);

            Assert.AreEqual(FPNavAgentStatus.Arrived,
                (FPNavAgentStatus)frame.Get<NavAgentComponent>(e).Status,
                "once allowed through, it must finish the journey the plan already described");
        }

        [Test]
        public void W10_ControlGroup_ARealWallDoesNotBlock_ItStillSlides()
        {
            // The teeth of the stop-on-contact rule: it must key on the MASK, not on "the walk did
            // not move". An agent pressed against the field's own boundary is not blocked — it
            // slides, as it always has. Without this, a wide trigger would stop every agent at
            // every wall, which is a far worse regression than the one being fixed.
            var mesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var system = NavAgentTestHelper.CreateSystem(mesh, null);

            // Start near the x=16 edge, aim diagonally past the corner so the walk must clip.
            var frame = AgentAt(mesh, P(15.5, 8.0), out EntityRef e, out EntityRef[] es);
            ref var nav = ref frame.Get<NavAgentComponent>(e);
            NavAgentComponent.SetAreaMask(ref nav, Restrictive, Restrictive);
            NavAgentComponent.SetDestination(ref nav, P(15.9, 14.0));

            for (int t = 1; t <= 300; t++)
            {
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);
                Assert.AreNotEqual(FPNavAgentStatus.Blocked,
                    (FPNavAgentStatus)frame.Get<NavAgentComponent>(e).Status,
                    $"tick {t}: a boundary wall must never produce Blocked — that state means "
                    + "'an area your mask refuses', and this mesh has no stamped ground at all");
            }

            Assert.Greater(frame.Get<NavAgentComponent>(e).Position.z.ToDouble(), 8.5,
                "the control group is only meaningful if the agent actually travelled along the "
                + "wall; if it never moved, the assertion above proved nothing");
        }

        [Test]
        public void W12_ItDoesNotStopOnTheFirstTick_OnlyOnContact()
        {
            // The trigger cannot be "the corridor's next triangle is refused" alone: an agent
            // crossing its current triangle is not advancing for as many ticks as the crossing
            // takes, so that form would park it before it had gone anywhere.
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var frame = AgentAt(mesh, P(StartX, Bz), out EntityRef e, out EntityRef[] es);

            ref var nav = ref frame.Get<NavAgentComponent>(e);
            NavAgentComponent.SetAreaMask(ref nav, Permissive, Restrictive);
            NavAgentComponent.SetDestination(ref nav, P(Bx, Bz));

            FP64 startX = nav.Position.x;

            for (int t = 1; t <= 10; t++)
            {
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);
                Assert.AreNotEqual(FPNavAgentStatus.Blocked,
                    (FPNavAgentStatus)frame.Get<NavAgentComponent>(e).Status,
                    $"tick {t}: nothing has been touched yet — the footprint is over 4 units away");
            }

            Assert.Greater(frame.Get<NavAgentComponent>(e).Position.x.ToDouble(), startX.ToDouble(),
                "and it must have actually set off, or the ticks above assert nothing");
        }

        // ── W-11: two agents, same tick, different outcomes ──────────────────

        [Test]
        public void W11_TwoAgentsDifferingOnlyInTheirWalkMask_SplitOnTheSameTick()
        {
            // The only direct evidence that the mask is PER AGENT: same mesh, same destination,
            // same plan mask, and the walk masks decide who gets in.
            //
            // Both plan masks are permissive on purpose. Leaving the second agent's plan mask at
            // the default would send it around the building instead of into it, and it would arrive
            // without ever touching anything — the gate would pass while measuring nothing.
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var query = new FPNavMeshQuery(mesh, null);

            FPVector3 start = P(StartX, Bz);
            int startTri = query.FindTriangle(start.ToXZ());
            var frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[] { start, start }, new[] { startTri, startTri }, out EntityRef[] es);

            ref var a = ref frame.Get<NavAgentComponent>(es[0]);
            NavAgentComponent.SetAreaMask(ref a, Permissive, Permissive);   // may enter
            NavAgentComponent.SetDestination(ref a, P(Bx, Bz));

            ref var b = ref frame.Get<NavAgentComponent>(es[1]);
            NavAgentComponent.SetAreaMask(ref b, Permissive, Restrictive);  // may not
            NavAgentComponent.SetDestination(ref b, P(Bx, Bz));

            bool aEnteredFootprint = false;
            for (int t = 1; t <= 600; t++)
            {
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);
                if (IsBuilding(mesh, frame.Get<NavAgentComponent>(es[0]).CurrentTriangleIndex))
                    aEnteredFootprint = true;
            }

            Assert.IsTrue(aEnteredFootprint,
                "the permissive walker must actually stand on building ground");
            Assert.AreEqual(FPNavAgentStatus.Blocked,
                (FPNavAgentStatus)frame.Get<NavAgentComponent>(es[1]).Status,
                "while its twin, differing only in the walk mask, is stopped at the edge");
        }

        // ── W-2: zero is the old behaviour ───────────────────────────────────

        [Test]
        public void W2_ZeroOverride_WalksTheSameTrajectoryAsTheExplicitDefault()
        {
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var query = new FPNavMeshQuery(mesh, null);

            FPVector3 start = P(StartX, 2.0);
            int startTri = query.FindTriangle(start.ToXZ());
            var frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[] { start, start }, new[] { startTri, startTri }, out EntityRef[] es);

            // es[0] keeps the zeros Init left. es[1] names the default explicitly.
            ref var b = ref frame.Get<NavAgentComponent>(es[1]);
            NavAgentComponent.SetAreaMask(ref b, Restrictive, Restrictive);

            FPVector3 goal = P(14.0, 14.0);
            NavAgentComponent.SetDestination(ref frame.Get<NavAgentComponent>(es[0]), goal);
            NavAgentComponent.SetDestination(ref frame.Get<NavAgentComponent>(es[1]), goal);

            for (int t = 1; t <= 400; t++)
            {
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);

                ref var x = ref frame.Get<NavAgentComponent>(es[0]);
                ref var y = ref frame.Get<NavAgentComponent>(es[1]);
                Assert.AreEqual(x.Position, y.Position, $"tick {t}: positions diverged");
                Assert.AreEqual(x.CurrentTriangleIndex, y.CurrentTriangleIndex,
                    $"tick {t}: triangles diverged");
                Assert.AreEqual(x.Status, y.Status, $"tick {t}: status diverged");
                Assert.AreEqual(x.CorridorLength, y.CorridorLength,
                    $"tick {t}: corridor length diverged");
            }
        }

        [Test]
        public void W2_Teeth_APermissiveOverrideDoesChangeTheOutcome()
        {
            // Without this, the gate above would also pass if the override were ignored entirely.
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var frame = AgentAt(mesh, P(StartX, Bz), out EntityRef e, out EntityRef[] es);

            ref var nav = ref frame.Get<NavAgentComponent>(e);
            NavAgentComponent.SetAreaMask(ref nav, Permissive, Permissive);
            NavAgentComponent.SetDestination(ref nav, P(Bx, Bz));
            system.Update(ref frame, es, es.Length, 1, NavAgentTestHelper.DT);

            Assert.AreNotEqual(FPNavAgentStatus.PathFailed,
                (FPNavAgentStatus)frame.Get<NavAgentComponent>(e).Status,
                "the same destination that a zero-override agent is refused must be planned for a "
                + "permissive one — otherwise the field is not being read");
        }

        // ── W-4: changing a mask drops what the old one planned ──────────────

        [Test]
        public void W4_SetAreaMask_DropsTheCorridorAndRepathsWithoutWaitingOutTheCooldown()
        {
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var frame = AgentAt(mesh, P(StartX, 2.0), out EntityRef e, out EntityRef[] es);

            ref var nav = ref frame.Get<NavAgentComponent>(e);
            NavAgentComponent.SetDestination(ref nav, P(14.0, 14.0));
            for (int t = 1; t <= 5; t++)
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);

            ref var mid = ref frame.Get<NavAgentComponent>(e);
            Assert.Greater(mid.CorridorLength, 0, "the fixture needs a corridor to invalidate");
            int requestBefore = mid.PathRequestId;

            NavAgentComponent.SetAreaMask(ref mid, Permissive, Permissive);

            Assert.AreEqual(0, mid.CorridorLength, "the old corridor must be dropped");
            Assert.IsFalse(mid.PathIsValid);
            Assert.IsFalse(mid.HasPath);
            Assert.AreEqual(FPNavAgentStatus.PathPending, (FPNavAgentStatus)mid.Status);
            Assert.AreEqual(requestBefore + 1, mid.PathRequestId);
            Assert.AreEqual(0, mid.LastRepathTick,
                "and the cooldown must be cleared, or the next plan waits PathRepathCooldown ticks "
                + "— the same delay a game hits when it tries to fix this with SetDestination");

            system.Update(ref frame, es, es.Length, 6, NavAgentTestHelper.DT);
            Assert.Greater(frame.Get<NavAgentComponent>(e).CorridorLength, 0,
                "the very next tick must replan");
        }

        // ── W-5: the mask is frame state ─────────────────────────────────────

        [Test]
        public void W5_TheMasksAreFrameState_SoARollbackRestoresThem()
        {
            var mesh = MeshWithRetainedBuilding();
            var frame = AgentAt(mesh, P(StartX, Bz), out EntityRef e, out EntityRef[] _);

            // Direct field assignment, NOT SetAreaMask: the setter also bumps PathRequestId and
            // rewrites the corridor fields by design, so calling it three times could never return
            // the frame to an earlier hash. What is under test here is narrower — that these two
            // ints are hashed frame state — so the round trip has to touch only them.
            ref var nav = ref frame.Get<NavAgentComponent>(e);
            nav.PlanAreaMaskOverride = Permissive;
            nav.WalkAreaMaskOverride = Restrictive;
            ulong hashWithMasks = frame.CalculateHash();

            frame.Get<NavAgentComponent>(e).PlanAreaMaskOverride = Restrictive;
            ulong hashAfterChange = frame.CalculateHash();

            Assert.AreNotEqual(hashWithMasks, hashAfterChange,
                "the masks must be hashed — that is what makes two peers disagreeing about them a "
                + "loud desync instead of a silent path difference. The generator folds every "
                + "[KlothoComponent] field, so this holds without anything being enumerated by "
                + "hand; the gate is here because that is a property of the generator rather than "
                + "of this struct");

            frame.Get<NavAgentComponent>(e).PlanAreaMaskOverride = Permissive;
            Assert.AreEqual(hashWithMasks, frame.CalculateHash(),
                "and restoring the same values must restore the same hash — a rollback replays "
                + "these fields like any other frame state");
        }

        // ── W-8: the public constrain pairs with the WALK ────────────────────

        [Test]
        public void W8_PublicConstrain_AgreesWithTheWalkMaskItIsGiven()
        {
            // The remark on ConstrainToNavMesh used to promise agreement with FindPath. Under a
            // split plan/walk mask that would be actively wrong: it would constrain a position INTO
            // a footprint the agent may not occupy. The pairing is with the walk.
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var query = new FPNavMeshQuery(mesh, null);

            FPVector3 outside = P(Bx - 3.0, Bz);
            FPVector3 intoBuilding = P(Bx, Bz);
            int tri = query.FindTriangle(outside.ToXZ());

            FPVector3 permissive = system.ConstrainToNavMesh(
                intoBuilding, outside, tri, Permissive);
            FPVector3 restrictive = system.ConstrainToNavMesh(
                intoBuilding, outside, tri, Restrictive);

            Assert.AreEqual(intoBuilding, permissive,
                "ALL_AREAS may stand on retained ground, so the target is legal as-is");
            Assert.AreNotEqual(intoBuilding, restrictive,
                "the default mask may not, so the position has to be pulled back out — if these "
                + "two agree, the mask argument is being ignored");
            Assert.IsFalse(IsBuilding(mesh, query.FindTriangle(restrictive.ToXZ())),
                "and what it was pulled back to must be ground that mask accepts");
        }

        // ── 회복 경로: 종단 Blocked 에서 나오는 문 ──

        /// <summary>
        /// G1 (F-1). A unit stopped by a retained building must start moving again once that
        /// building is gone. The mesh swap + reseed is the only engine-side event that can say so,
        /// and until this gate existed it did not: the reseed's status mapping covered
        /// Moving/PathPending only, so a Blocked agent stayed Blocked over a mesh where nothing
        /// blocks it any more.
        /// </summary>
        [Test]
        public unsafe void R1_ReseedRevivesABlockedAgent_AfterTheBuildingIsDemolished()
        {
            var basemesh = NavAgentTestHelper.CreateOpenFieldNavMesh(Cells);
            var withBuilding = NavAgentTestHelper.RebakeWithBuildings(
                basemesh, NavAgentTestHelper.Building(Bx, Bz, retain: true));
            var system = NavAgentTestHelper.CreateSystem(withBuilding, null);

            FPVector3 start = P(StartX, Bz);
            var frame = AgentAt(withBuilding, start, out EntityRef e, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(e);
            NavAgentComponent.SetAreaMask(ref nav, Permissive, Restrictive);
            NavAgentComponent.SetDestination(ref nav, P(Bx, Bz));

            int lastTick = 0;
            for (int t = 1; t <= 600; t++)
            {
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);
                lastTick = t;
                if (frame.Get<NavAgentComponent>(e).Status == (byte)FPNavAgentStatus.Blocked)
                    break;
            }
            Assert.AreEqual((byte)FPNavAgentStatus.Blocked, nav.Status,
                "precondition: the walk mask must actually stop this agent at the footprint edge");

            // Demolish: the remaining building set is empty, so the rebake returns plain ground.
            var demolished = NavAgentTestHelper.RebakeWithBuildings(basemesh);
            var newQuery = new FPNavMeshQuery(demolished, null);
            system.SwapNavMesh(demolished, newQuery,
                new FPNavMeshPathfinder(demolished, newQuery, null),
                new FPNavMeshFunnel(demolished, newQuery, null));
            system.ReseedAgents(ref frame, entities, 1);

            Assert.IsFalse(IsBuilding(demolished, nav.CurrentTriangleIndex),
                "the demolished mesh must have no stamped ground left under the agent");
            Assert.AreEqual((byte)FPNavAgentStatus.PathPending, nav.Status,
                "a Blocked agent that still has a destination must be handed back to the planner — "
                + "nothing else in the engine ever writes its status again");
            Assert.AreEqual(0, nav.LastRepathTick, "and the repath cooldown must be bypassed");

            system.Update(ref frame, entities, 1, lastTick + 1, NavAgentTestHelper.DT);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, nav.Status,
                "the very next tick must repath: the ground it was refused is ordinary ground now");
        }

        /// <summary>
        /// G5 + G8 (F-3). Crowd pressure must not shove an agent into ground its own walk mask
        /// refuses. Two gates in one body on purpose: the push has to be OBSERVED (G8), because
        /// the correction pass is gated on avoidance — without <c>SetAvoidance</c> nothing moves
        /// and an "it did not enter" assertion passes while measuring nothing.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void R2_CrowdPushDoesNotShoveAnAgentIntoARetainedFootprint(bool blocked)
        {
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            system.SetAvoidance(new FPNavAvoidance());          // the correction pass is gated on it
            var query = new FPNavMeshQuery(mesh, null);

            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);
            FPVector3 pushed = P(Bx - half - 0.1, Bz);          // just outside the west edge
            FPVector3 pusher = P(Bx - half - 0.5, Bz);          // 0.4 apart => overlapping (r=0.5 each)
            var frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[] { pushed, pusher },
                new[] { query.FindTriangle(pushed.ToXZ()), query.FindTriangle(pusher.ToXZ()) },
                out EntityRef[] es);

            Assert.IsFalse(IsBuilding(mesh, frame.Get<NavAgentComponent>(es[0]).CurrentTriangleIndex),
                "precondition: the pushed agent starts on ground its mask accepts");

            if (blocked)
            {
                ref var b = ref frame.Get<NavAgentComponent>(es[0]);
                b.Status = (byte)FPNavAgentStatus.Blocked;       // the state that never recomputes
            }

            for (int t = 1; t <= 30; t++)
            {
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);
                Assert.IsFalse(IsBuilding(mesh, frame.Get<NavAgentComponent>(es[0]).CurrentTriangleIndex),
                    $"tick {t}: the crowd may press this agent against the footprint, never into it");
            }

            Assert.AreNotEqual(pushed, frame.Get<NavAgentComponent>(es[0]).Position,
                "G8: the push must actually have happened — otherwise the assertions above are "
                + "measuring an agent nobody moved (SetAvoidance missing?)");
        }

        /// <summary>
        /// G6 (F-3). The other direction, and the reason the clamp may carry the agent's own walk
        /// mask at all: an agent standing INSIDE forbidden ground can still be pushed out of it.
        /// It works because the walk's expansion rule exempts refused ground when the triangle it
        /// expands FROM is refused too — not because the clamp is unfiltered.
        /// </summary>
        [Test]
        public void R3_CrowdPushCanShoveAnAgentOutOfARetainedFootprint()
        {
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            system.SetAvoidance(new FPNavAvoidance());
            var query = new FPNavMeshQuery(mesh, null);

            double half = NavAgentTestHelper.ExpandedBuildingHalf(mesh);
            FPVector3 inside = P(Bx - half + 0.4, Bz);          // inside, near the west edge
            FPVector3 pusher = P(Bx - half + 0.5, Bz);          // 0.1 apart => pushes `inside` west
            var frame = NavAgentTestHelper.CreateFrameWithAgents(
                new[] { inside, pusher },
                new[] { query.FindTriangle(inside.ToXZ()), query.FindTriangle(pusher.ToXZ()) },
                out EntityRef[] es);

            Assert.IsTrue(IsBuilding(mesh, frame.Get<NavAgentComponent>(es[0]).CurrentTriangleIndex),
                "precondition: this agent starts on retained ground its mask refuses");

            for (int t = 1; t <= 30; t++)
                system.Update(ref frame, es, es.Length, t, NavAgentTestHelper.DT);

            Assert.IsFalse(IsBuilding(mesh, frame.Get<NavAgentComponent>(es[0]).CurrentTriangleIndex),
                "the mask forbids ENTERING, not LEAVING — an agent already inside must be able to "
                + "come out, or narrowing a mask under a crowd buries units in buildings");
        }

        /// <summary>
        /// G3b (F-2). An agent that is OFF its corridor must not be handed the terminal Blocked
        /// state because the corridor's HEAD is masked out — the head is not its next step, and the
        /// off-corridor repath is the answer for such an agent.
        ///
        /// <para><b>This gate does not distinguish the two implementations, and that is recorded on
        /// purpose.</b> It passes against the old reading (<c>p[p[0] == resultTri ? 1 : 0]</c>) as
        /// well, because reaching that reading's false verdict needs a conjunction that resisted
        /// construction: the walk must hand back the EXACT position it was given, so the steering
        /// has to meet a non-masked wall head-on with no tangential slide (measured: an agent flush
        /// against a carved hole slides along it and keeps moving), while the corridor head happens
        /// to be masked, all inside the ten-tick window before the off-corridor repath fires. So
        /// the fix is a guard, and this is the contract it guards — an off-corridor agent repaths,
        /// it does not acquire a terminal status.</para>
        ///
        /// <para>Construction: the corridor is planned from INSIDE the retained footprint (so its
        /// head is stamped ground the walk mask refuses), then the agent is moved off that corridor
        /// into a mesh corner while its corridor index becomes -1.</para>
        /// </summary>
        [Test]
        public unsafe void R4_OffCorridorAgent_IsNotBlockedByTheCorridorHead()
        {
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);   // no avoidance: it would
            var query = new FPNavMeshQuery(mesh, null);                 // overwrite DesiredVelocity

            FPVector3 insideFootprint = P(Bx, Bz);
            var frame = AgentAt(mesh, insideFootprint, out EntityRef e, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(e);
            NavAgentComponent.SetAreaMask(ref nav, Permissive, Restrictive);
            NavAgentComponent.SetDestination(ref nav, P(StartX, StartX));

            system.Update(ref frame, entities, 1, 1, NavAgentTestHelper.DT);
            Assert.AreEqual((byte)FPNavAgentStatus.Moving, nav.Status,
                "precondition: a masked-out start still plans (the escape rule exempts it)");
            Assert.Greater(nav.CorridorLength, 1, "precondition: a corridor to be off");
            fixed (int* p = nav.Corridor)
                Assert.IsTrue(IsBuilding(mesh, p[0]),
                    "precondition: the corridor HEAD must be ground the walk mask refuses — that is "
                    + "the triangle the old verdict looked at");

            // Off the corridor: its own triangle is nowhere in that corridor, so the corridor
            // index is -1 and the head is the only triangle the old reading could have looked at.
            FPVector3 corner = P(0.05, 0.05);
            nav.Position = corner;
            nav.CurrentTriangleIndex = query.FindTriangle(corner.ToXZ());
            nav.Velocity = FPVector2.Zero;
            nav.OffCorridorTicks = 0;

            byte[] seen = new byte[24];
            for (int t = 2; t <= 25; t++)
            {
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);
                seen[t - 2] = nav.Status;
                Assert.AreNotEqual((byte)FPNavAgentStatus.Blocked, nav.Status,
                    $"tick {t}: this agent is off its corridor and touched nothing its mask "
                    + "refuses — Blocked here freezes it for good and pre-empts the repath");
            }

            Assert.Contains((byte)FPNavAgentStatus.PathPending, seen,
                "and the off-corridor repath must be what actually fires instead");
        }

        /// <summary>
        /// F-5. <c>SetAreaMask</c> writes a status because that is what releases an agent parked in
        /// <see cref="FPNavAgentStatus.Blocked"/> — but <c>PathPending</c> is a lie on an agent that
        /// has nowhere to go, and the spawn order (<c>Init</c> then the mask, which is exactly what
        /// both editor simulators do) hits that case every time. Nothing can advance it either: the
        /// planner returns on <c>!HasNavDestination</c>, so the agent sits there reported as
        /// planning until the first destination arrives.
        /// </summary>
        [Test]
        public void R5_SetAreaMaskWithNoDestination_LeavesTheAgentIdle()
        {
            var mesh = MeshWithRetainedBuilding();
            var system = NavAgentTestHelper.CreateSystem(mesh, null);
            var frame = AgentAt(mesh, P(StartX, Bz), out EntityRef e, out EntityRef[] entities);
            ref var nav = ref frame.Get<NavAgentComponent>(e);
            Assert.AreEqual((byte)FPNavAgentStatus.Idle, nav.Status,
                "precondition: Init leaves a fresh agent Idle");

            NavAgentComponent.SetAreaMask(ref nav, Permissive, Restrictive);

            Assert.AreEqual((byte)FPNavAgentStatus.Idle, nav.Status,
                "an agent with no destination is Idle, not planning");
            Assert.AreEqual(Permissive, nav.PlanAreaMaskOverride, "and the masks still landed");
            Assert.AreEqual(Restrictive, nav.WalkAreaMaskOverride);

            for (int t = 1; t <= 5; t++)
                system.Update(ref frame, entities, 1, t, NavAgentTestHelper.DT);
            Assert.AreEqual((byte)FPNavAgentStatus.Idle, nav.Status,
                "and ticking cannot fix a wrong status here — the planner returns on "
                + "!HasNavDestination, which is why the write has to be right at the source");

            // Same call, same masks, but now it has somewhere to go: the release write is unchanged.
            NavAgentComponent.SetDestination(ref nav, P(Bx - 3.0, Bz));
            NavAgentComponent.SetAreaMask(ref nav, Permissive, Restrictive);
            Assert.AreEqual((byte)FPNavAgentStatus.PathPending, nav.Status,
                "with a destination the status write is the one Blocked relies on");
        }
    }
}
