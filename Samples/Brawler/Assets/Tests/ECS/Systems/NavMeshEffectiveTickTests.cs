using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Navigation;
using Brawler;

namespace xpTURN.Klotho.Tests.ECS.Systems
{
    /// <summary>
    /// The delayed-install invariant, against a real simulation.
    ///
    /// <para><b>Why here and not in Klotho.Runtime.Tests.</b> Every one of these needs
    /// <c>BuildingComponent</c>, <c>PlatformerCommandSystem</c> and
    /// the Brawler placement seam — types the dotnet suite does not reference. The
    /// wiring facts it CAN reach it already pins as text (<c>BrawlerWiringContractTests</c>); these
    /// are the ones that need the types themselves, so they live in the assembly that has them.</para>
    ///
    /// <para><b>The oracle.</b> Every mesh assertion compares
    /// <c>FPNavMeshRebaker.ComputeFingerprint</c> against a rebake performed through a SEPARATE
    /// context, from an independently collected placement list. Reusing the production
    /// <c>CollectBuildings</c> as the oracle would make a bug in it agree with itself; reusing the
    /// production context would disturb the patch chain the thing under test is using.</para>
    /// </summary>
    [TestFixture]
    public class NavMeshEffectiveTickTests
    {
        private const int MaxEntities = 64;
        private const int MaxRollbackTicks = 120;   // wider than K, so a rollback can cross a boundary
        private const int DeltaTimeMs = 25;
        private const int Half = 10;                // a 21x21 unit grid stage

        private FPNavMesh _baseMesh;
        private FPNavMeshRebakeContext _oracleContext;

        [SetUp]
        public void SetUp()
        {
            _baseMesh = BuildGridStage(Half);
            _oracleContext = BrawlerBuildingShapes.CreateContext(_baseMesh, null);
        }

        // ---------------------------------------------------------------- fixtures

        /// <summary>
        /// A flat square plane — four corners, two triangles.
        ///
        /// <para>Not a lattice. Triangulating a regular grid of points is degenerate (every cell is
        /// cocircular), and carving a hole out of one produces near-coincident vertices that trip
        /// the rebaker's conforming-input assert. The Unity editor compiles with DEBUG, so an
        /// assert here would fail the run — the fixture has to be a stage the rebaker considers
        /// well formed, and an empty plane is the simplest one.</para>
        /// </summary>
        private static FPNavMesh BuildGridStage(int half)
        {
            FP64 lo = FP64.FromInt(-half), hi = FP64.FromInt(half);
            var vertices = new[]
            {
                new FPVector3(lo, FP64.Zero, lo),
                new FPVector3(hi, FP64.Zero, lo),
                new FPVector3(hi, FP64.Zero, hi),
                new FPVector3(lo, FP64.Zero, hi),
            };
            var xs = new long[vertices.Length];
            var zs = new long[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                xs[i] = FPGeoPredicates.Snap(vertices[i].x);
                zs[i] = FPGeoPredicates.Snap(vertices[i].z);
            }
            int[] tris = FPConstrainedDelaunay.Triangulate(xs, zs, null, eraseOuterAndHoles: false);
            return FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);
        }

        private sealed class Sim
        {
            public EcsSimulation Simulation;
            public FPNavAgentSystem Agents;
            public FPNavMeshRebakeContext Context;
            public BotFSMSystem Bots;

            private readonly List<ICommand> _none = new List<ICommand>();

            /// <summary>The tick that will execute NEXT — a step from here runs exactly this tick.</summary>
            public int Tick => Simulation.CurrentTick;

            /// <summary>
            /// The tick the installed mesh reflects. Stepping from CurrentTick T executes T and
            /// leaves CurrentTick at T+1 (measured, not assumed), so after a step the pump has last
            /// seen T — asking the oracle about CurrentTick would ask about a tick nobody has run.
            /// </summary>
            public int LastExecutedTick => Simulation.CurrentTick - 1;
            public ulong InstalledFingerprint => FPNavMeshRebaker.ComputeFingerprint(Agents.CurrentMesh);

            public void Step(params ICommand[] commands)
            {
                Simulation.SaveSnapshot();
                Simulation.Tick(commands == null || commands.Length == 0
                    ? _none : new List<ICommand>(commands));
            }

            public void StepTo(int target)
            {
                while (Tick < target) Step();
            }

            /// <summary>
            /// Stands in for KlothoEngine.OnFrameBoundary. Frames and ticks are different clocks in
            /// production; a test that wants slicing to actually happen has to turn the other one.
            /// </summary>
            public FPNavMeshRebakeDriver Pump;
            public void Frame_(int slices = 1)
            {
                for (int i = 0; i < slices; i++) Pump?.AdvanceSlice(0.016f);
            }
        }

        /// <summary>A simulation wired exactly as a match is — the pump included.</summary>
        private Sim CreateSim()
        {
            FPNavMesh mesh = BuildGridStage(Half);
            var query = new FPNavMeshQuery(mesh, null);
            var pathfinder = new FPNavMeshPathfinder(mesh, query, null);
            var funnel = new FPNavMeshFunnel(mesh, query, null);
            var agents = new FPNavAgentSystem(mesh, query, pathfinder, funnel, null);
            var bots = new BotFSMSystem(agents);
            bots.SetQuery(query);   // both hosts do this at wiring; Pass 2 snaps the spawn onto the mesh with it
            var context = BrawlerBuildingShapes.CreateContext(mesh, null);

            var simulation = new EcsSimulation(MaxEntities, MaxRollbackTicks, DeltaTimeMs);
            BrawlerSimSetup.RegisterSystems(
                simulation, logger: null, dataAssets: BrawlerSimSetup.CreateDefaultDataAssets(),
                botFSMSystem: bots, rebakeContext: context);
            simulation.Initialize();

            // The singletons the shared systems read every tick. Without them a tick throws out of
            // the command loop, which reads as a placement failure rather than as a missing fixture.
            EntityRef seed = simulation.Frame.CreateEntity();
            simulation.Frame.Add(seed, new RandomSeedComponent { Seed = 12345UL });
            EntityRef timer = simulation.Frame.CreateEntity();
            simulation.Frame.Add(timer, new GameTimerStateComponent
            {
                StartTick = -1, LastReportedSeconds = -1, GameOverFired = false,
            });

            var sim = new Sim
            {
                Simulation = simulation, Agents = agents, Context = context, Bots = bots,
                Pump = simulation.GetSystem<FPNavMeshRebakeDriver>(),
            };
            // Bots, so the nav agents are driven and their corridors cross the middle where the
            // fixtures build — a swap then invalidates a live path instead of landing on an idle
            // agent. BotFSMSystem adds the NavAgentComponent itself, on its first update.
            SpawnPlayer(sim, 1, 8, 8);
            SpawnBot(sim, 10, -6, -6);
            SpawnBot(sim, 11, 6, 6);
            SpawnBot(sim, 12, -6, 6);
            return sim;
        }

        /// <summary>
        /// Puts a BOT on the stage — not a bare nav agent.
        ///
        /// <para>Load-bearing, and the reason is specific. <c>ReseedAgents</c> has a branch that
        /// runs only for an agent that is actually going somewhere:</para>
        /// <code>
        /// if (nav.HasNavDestination &amp;&amp; (Status == Moving || Status == PathPending))
        /// { nav.Status = PathPending; nav.LastRepathTick = 0; }
        /// </code>
        /// <para>An earlier version of this fixture created bare <c>NavAgentComponent</c> holders
        /// and set a destination on them. They never moved, so that branch never executed and every
        /// reseed assertion here was value-idempotent — passing for the wrong reason. They never
        /// moved because nothing drives them: <c>BotFSMSystem</c> steps agents behind
        /// <c>if (_navEntityCount > 0)</c>, and it is the one that ADDS
        /// <c>NavAgentComponent</c> — to <c>BotComponent</c> holders only. A nav agent without a
        /// bot is a combination this game does not have.</para>
        ///
        /// <para>So the fixture spawns what the game spawns, in the shape
        /// <c>BotNavigationSyncTests.SpawnBot</c> established, and lets the bot FSM pick
        /// destinations. That is also what the live match had when it diverged.</para>
        /// </summary>
        private static EntityRef SpawnBot(Sim sim, int playerId, double x, double z)
        {
            Frame frame = sim.Simulation.Frame;
            EntityRef entity = frame.CreateEntity(WarriorPrototype.Id);

            ref CharacterComponent character = ref frame.Get<CharacterComponent>(entity);
            character.PlayerId = playerId;
            character.CharacterClass = 0;
            character.StockCount = 3;

            ref OwnerComponent owner = ref frame.Get<OwnerComponent>(entity);
            owner.OwnerId = playerId;

            var spawn = new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z));
            ref TransformComponent transform = ref frame.Get<TransformComponent>(entity);
            transform.Position = new FPVector3(spawn.x, FP64.Zero, spawn.y);

            frame.Add(entity, new BotComponent
            {
                State = (byte)BotStateId.Idle,
                Difficulty = (byte)BotDifficulty.Normal,
            });
            frame.Add(entity, new SpawnMarkerComponent { PlayerId = playerId, SpawnPosition = spawn });

            // Explicit, because the bot is spawned AFTER Initialize(): BotFSMSystem.OnInit only
            // registers the HFSM root, and Pass 2's filter requires an HFSMComponent before it will
            // give the bot a nav agent at all. Without this the bots exist, never think, never get
            // a destination, and the fixture is back to motionless agents.
            xpTURN.Klotho.ECS.FSM.HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id);
            return entity;
        }

        /// <summary>
        /// A player for the bots to chase. Without one they have nothing to path towards, so
        /// <c>BotFSMHelper.UpdateDestination</c> leaves <c>HasDestination</c> false, no nav agent is
        /// ever Moving, and reseed's live branch stays unreachable — the fixture would be back to
        /// motionless agents by a different route.
        /// </summary>
        private static EntityRef SpawnPlayer(Sim sim, int playerId, double x, double z)
        {
            Frame frame = sim.Simulation.Frame;
            EntityRef entity = frame.CreateEntity(WarriorPrototype.Id);

            ref CharacterComponent character = ref frame.Get<CharacterComponent>(entity);
            character.PlayerId = playerId;
            character.CharacterClass = 0;
            character.StockCount = 3;

            ref OwnerComponent owner = ref frame.Get<OwnerComponent>(entity);
            owner.OwnerId = playerId;

            var spawn = new FPVector2(FP64.FromDouble(x), FP64.FromDouble(z));
            ref TransformComponent transform = ref frame.Get<TransformComponent>(entity);
            transform.Position = new FPVector3(spawn.x, FP64.Zero, spawn.y);
            frame.Add(entity, new SpawnMarkerComponent { PlayerId = playerId, SpawnPosition = spawn });
            return entity;
        }

        private static PlaceBuildingCommand Place(int playerId, double x, double z)
        {
            return new PlaceBuildingCommand
            {
                PlayerId = playerId,
                ShapeId = BrawlerBuildingShapes.BoxShape,
                Orientation = 0,
                Centre = new FPVector3(FP64.FromDouble(x), FP64.Zero, FP64.FromDouble(z)),
            };
        }

        // ---------------------------------------------------------------- oracle

        /// <summary>
        /// What the mesh MUST be at <paramref name="atTick"/>, derived from the frame independently
        /// of the code under test: collect every building whose window contains the tick, order by
        /// Sequence, rebake through the spare context.
        /// </summary>
        private ulong ExpectedFingerprint(Frame frame, int atTick)
        {
            var live = new List<BuildingComponent>();
            var filter = frame.Filter<BuildingComponent>();
            while (filter.Next(out var entity))
            {
                BuildingComponent b = frame.GetReadOnly<BuildingComponent>(entity);
                if (atTick >= b.EffectiveTick && atTick < b.RemovalEffectiveTick)
                    live.Add(b);
            }
            live.Sort((l, r) => l.Sequence.CompareTo(r.Sequence));

            var placements = new FPBuildingPlacement[live.Count];
            for (int i = 0; i < live.Count; i++)
                placements[i] = new FPBuildingPlacement(
                    live[i].ShapeId, live[i].Orientation,
                    live[i].Centre.x, live[i].Centre.z, live[i].Centre.y);

            Assert.IsTrue(
                FPNavMeshRebaker.TryRebakePlacements(
                    _oracleContext, placements, out FPNavMesh mesh, out _,
                    null, PlatformerCommandSystem.PlacementRules, live.Count),
                $"the oracle rebake refused the set the frame describes at tick {atTick} — "
                + "the placement was accepted against a set it should have been validated against");
            _oracleContext.DiscardProduced();
            return FPNavMeshRebaker.ComputeFingerprint(mesh);
        }

        private static int LiveCount(Frame frame)
        {
            int n = 0;
            var filter = frame.Filter<BuildingComponent>();
            while (filter.Next(out _)) n++;
            return n;
        }

        // ------------------------------------------------- the invariant, every tick

        [Test]
        public void Tick9_InstalledMeshEqualsTheFrameFormula_OnEveryTick()
        {
            // The invariant itself, checked on every tick rather than at the boundary. A swap
            // that fires one tick early or late passes a boundary-only assertion.
            Sim sim = CreateSim();
            sim.StepTo(5);

            int placedAt = sim.Tick;          // the tick this step will execute
            sim.Step(Place(0, 0, 0));

            for (int i = 0; i < PlatformerCommandSystem.BuildDelayTicks + 10; i++)
            {
                sim.Step();
                ulong expected = ExpectedFingerprint(sim.Simulation.Frame, sim.LastExecutedTick);
                Assert.AreEqual(expected, sim.InstalledFingerprint,
                    $"tick {sim.LastExecutedTick}: the installed mesh is not what the frame says it should be "
                    + $"(placed at {placedAt}, K={PlatformerCommandSystem.BuildDelayTicks})");
            }
        }

        [Test]
        public void Tick9_TheMeshDoesNotChangeBeforeTheEffectiveTick()
        {
            // The half that would still pass if the delay were silently zero: the fingerprint
            // has to STAY at the empty-set value until the boundary and change exactly once.
            Sim sim = CreateSim();
            sim.StepTo(5);

            ulong before = sim.InstalledFingerprint;
            int placedAt = sim.Tick;
            sim.Step(Place(0, 0, 0));
            int effective = placedAt + PlatformerCommandSystem.BuildDelayTicks;

            int changes = 0;
            ulong previous = before;
            int changedAt = -1;
            while (sim.Tick < effective + 5)
            {
                sim.Step();
                ulong now = sim.InstalledFingerprint;
                if (now != previous) { changes++; changedAt = sim.LastExecutedTick; previous = now; }
            }

            Assert.AreEqual(1, changes, "the mesh changed more than once for a single placement");
            Assert.AreEqual(effective, changedAt,
                "the mesh changed on the wrong tick — the delay is not being honoured");
            Assert.AreNotEqual(before, previous, "the building never reached the mesh at all");
        }

        [Test]
        public void Tick9_DemolitionReachesTheMeshOnItsOwnBoundary()
        {
            // The reason this exists: an earlier formula omitted the upper bound, and a
            // placement-only test cannot see it — the building goes in correctly and simply never
            // comes out.
            Sim sim = CreateSim();
            sim.StepTo(5);
            sim.Step(Place(0, 0, 0));
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 2);

            ulong withBuilding = sim.InstalledFingerprint;
            EntityRef target = FirstBuilding(sim.Simulation.Frame);

            int removedAt = sim.Tick;
            sim.Step(new RemoveBuildingCommand { PlayerId = 0, TargetEntityId = target.ToId() });
            int removalTick = removedAt + PlatformerCommandSystem.BuildDelayTicks;

            sim.StepTo(removalTick - 1);
            Assert.AreEqual(withBuilding, sim.InstalledFingerprint,
                "the hole disappeared before its removal tick — a demolition must keep its hole for "
                + "the whole window, or a peer joining inside it cannot reproduce the mesh");
            Assert.AreEqual(1, LiveCount(sim.Simulation.Frame),
                "the component was destroyed at command time, which is what leaves a joiner unable "
                + "to explain the hole that is still carved");

            sim.StepTo(removalTick + 2);
            Assert.AreNotEqual(withBuilding, sim.InstalledFingerprint,
                "the building never left the mesh — this is X-2, and it is invisible to any test "
                + "that only places");
            Assert.AreEqual(0, LiveCount(sim.Simulation.Frame),
                "the tombstone was never destroyed");
            Assert.AreEqual(ExpectedFingerprint(sim.Simulation.Frame, sim.LastExecutedTick),
                sim.InstalledFingerprint);
        }

        private static EntityRef FirstBuilding(Frame frame)
        {
            var filter = frame.Filter<BuildingComponent>();
            Assert.IsTrue(filter.Next(out var entity), "no building to remove");
            return entity;
        }

        [Test]
        public void Tick9_TwoPlacementsInOneWindowSeeEachOther()
        {
            // Validation asks about the set as it will be at T+K, not as it is now. Against the
            // present set both of these pass — neither has landed yet — and the pair only becomes
            // impossible on the tick they both take effect, where there is no command left to
            // refuse and the bake simply fails.
            Sim sim = CreateSim();
            sim.StepTo(5);

            // Two units apart on x, which overlaps: the box is 2 wide before the bake radius
            // expands it another 0.5 a side. An exactly coincident pair is NOT a discriminator —
            // the carve absorbs a duplicate hole without complaint.
            sim.Step(Place(0, 0, 0));
            sim.Step(Place(1, 2, 0));               // well inside the delay window

            Assert.AreEqual(1, LiveCount(sim.Simulation.Frame),
                "the second placement was accepted on top of one already queued — it was validated "
                + "against the set as it is now instead of the set it will land in");

            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 3);
            Assert.AreEqual(ExpectedFingerprint(sim.Simulation.Frame, sim.LastExecutedTick),
                sim.InstalledFingerprint);
        }

        // ------------------------------------------- rollback across the boundary

        [Test]
        public void Tick10_RollbackAcrossTheBoundaryReplaysToTheSameHashes()
        {
            // A boundary-crossing rollback lands on the same hashes it did going forward.
            //
            // This is what caught the real one: the loaded mesh and Bake({}) are the same region
            // with different triangulations, and agents hold a triangle INDEX in frame state, so a
            // correction that installed the loaded mesh made the replay move agents differently.
            //
            // What it does NOT yet discriminate is S-4 itself — a correction that also reseeds.
            // Reseed derives its values from position and mesh, so with the static agents this
            // fixture spawns it recomputes what is already there and the hashes agree anyway.
            // Making it bite needs agents with live corridors (a destination, and the bot systems
            // driving them); until then S-4 rests on the SwapForRestoredState call being pinned by
            // BrawlerWiringContractTests, not on this.
            Sim sim = CreateSim();
            sim.StepTo(5);
            int placedAt = sim.Tick;
            sim.Step(Place(0, 0, 0));
            int boundary = placedAt + PlatformerCommandSystem.BuildDelayTicks;

            int before = boundary - 5;
            sim.StepTo(before);

            var forward = new List<long>();
            while (sim.Tick < boundary + 5) { sim.Step(); forward.Add(sim.Simulation.GetStateHash()); }
            int head = sim.Tick;

            Assert.AreEqual(before, sim.Simulation.GetNearestRollbackTick(before),
                "no snapshot at the rollback target — widen MaxRollbackTicks");
            sim.Simulation.Rollback(before);
            Assert.AreEqual(before, sim.Tick, "rollback did not restore frame.Tick");

            for (int i = 0; sim.Tick < head; i++)
            {
                sim.Step();
                Assert.AreEqual(forward[i], sim.Simulation.GetStateHash(),
                    $"replayed tick {sim.Tick} differs from its first execution — a swap or a reseed "
                    + "that is not a function of frame state is the usual cause");
            }
        }

        [Test]
        public void Tick10_TheMeshIsCorrectAfterARollbackThatUndoesThePlacement()
        {
            // The other direction, and the one a boundary-crossing hash test cannot reach: roll back
            // to BEFORE the command, replay without it, and the building must never appear. The mesh
            // does not roll back with the frame, so only re-deriving it gets this right.
            Sim sim = CreateSim();
            sim.StepTo(5);
            int beforePlacement = sim.Tick;
            ulong empty = sim.InstalledFingerprint;

            sim.Step(Place(0, 0, 0));
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 3);
            Assert.AreNotEqual(empty, sim.InstalledFingerprint, "the placement never landed");

            sim.Simulation.Rollback(beforePlacement);
            sim.StepTo(beforePlacement + PlatformerCommandSystem.BuildDelayTicks + 10);

            Assert.AreEqual(0, LiveCount(sim.Simulation.Frame), "the rollback did not undo the placement");
            Assert.AreEqual(empty, sim.InstalledFingerprint,
                "the navmesh still carries a building the frame no longer describes — derived state "
                + "does not roll back, so it has to be re-derived");
        }

        // ---------------------------------------------------------------- 슬라이싱

        [Test]
        public void Slicing_ProducesTheSameMeshAsNotSlicing()
        {
            // The property the whole feature rests on: the budget is a peer-local, wall-clock-paced
            // number, so if it could change the result it would be a desync knob. Two simulations,
            // identical commands, one advancing slices between ticks and one never advancing any.
            Sim sliced = CreateSim();
            Sim whole = CreateSim();
            Assert.IsNotNull(sliced.Pump, "the rebake driver is not registered — the rest of this proves nothing");

            for (int i = 0; i < 5; i++) { sliced.Step(); sliced.Frame_(); whole.Step(); }

            sliced.Step(Place(0, 0, 0));
            whole.Step(Place(0, 0, 0));

            int boundary = 5 + PlatformerCommandSystem.BuildDelayTicks;
            while (sliced.Tick < boundary + 5)
            {
                sliced.Step();
                sliced.Frame_(3);        // slices land between ticks, as frames do
                whole.Step();            // never sliced: the boundary absorbs the whole rebake
            }

            Assert.AreEqual(whole.InstalledFingerprint, sliced.InstalledFingerprint,
                "slicing changed the mesh — the budget is peer-local and wall-clock paced, so any "
                + "influence it has on the result is a desync waiting for a slow frame");
            Assert.AreEqual(whole.Simulation.GetStateHash(), sliced.Simulation.GetStateHash(),
                "slicing changed the frame state");

            // Without this the assertions above hold just as well when no task is ever started —
            // a slicer that does nothing produces the same mesh as one that works.
            Assert.Greater(sliced.Pump.SlicedFrames, 0, "no slice ever ran");
            Assert.AreEqual(0, whole.Pump.SlicedFrames, "the unsliced control was sliced after all");
            Assert.Greater(sliced.Pump.TaskInstalls, 0, "the install did not come from the task");

            // The point of the feature, stated as a number: with the heartbeat the boundary had
            // nothing left to do, and without it the boundary paid for the whole rebake. Equal
            // meshes either way — that is the assertion above — but not equal spikes.
            Assert.AreEqual(0, sliced.Pump.BoundaryFinishes,
                "the sliced run still finished its rebake on the boundary tick, so the work was "
                + "not actually moved off it");
            Assert.Greater(whole.Pump.BoundaryFinishes, 0,
                "the control finished without ever slicing — then it is not a control");
        }

        [Test]
        public void Slicing_AtEveryBudgetLandsOnTheSameMesh()
        {
            // Randomised budgets, because a cursor that mishandles a boundary tends to do it only
            // at particular split points — the reason to randomise rather than to rely on
            // {1, prime, whole}.
            var reference = CreateSim();
            reference.StepTo(5);
            reference.Step(Place(0, 0, 0));
            reference.StepTo(5 + PlatformerCommandSystem.BuildDelayTicks + 3);
            ulong expected = reference.InstalledFingerprint;

            var rng = new System.Random(20260816);
            for (int trial = 0; trial < 4; trial++)
            {
                Sim sim = CreateSim();
                sim.StepTo(5);
                sim.Step(Place(0, 0, 0));
                while (sim.Tick < 5 + PlatformerCommandSystem.BuildDelayTicks + 3)
                {
                    sim.Step();
                    sim.Frame_(rng.Next(0, 4));
                }
                Assert.AreEqual(expected, sim.InstalledFingerprint,
                    $"trial {trial} landed on a different mesh than the unsliced reference");
            }
        }

        [Test]
        public void Slicing_ARollbackThatChangesTheAnswerDoesNotInstallTheStaleTask()
        {
            // The in-flight task is derived state built for a PARTICULAR set. A rollback that
            // removes the placement makes it garbage, and installing it anyway would put a building
            // on the mesh that no component describes — invisible to the state hash.
            Sim sim = CreateSim();
            sim.StepTo(5);
            ulong empty = sim.InstalledFingerprint;

            sim.Step(Place(0, 0, 0));
            for (int i = 0; i < 10; i++) { sim.Step(); sim.Frame_(2); }   // task well under way

            sim.Simulation.Rollback(5);
            for (int i = 0; i < PlatformerCommandSystem.BuildDelayTicks + 6; i++)
            {
                sim.Step();
                sim.Frame_(2);
            }

            Assert.AreEqual(0, LiveCount(sim.Simulation.Frame), "the rollback did not undo the placement");
            Assert.AreEqual(empty, sim.InstalledFingerprint,
                "a rebake started for a set the frame no longer describes was installed anyway");
        }

        [Test]
        public void Slicing_ARollbackThatREPLACESThePlacementDoesNotInstallTheStaleTask()
        {
            // The case the previous test cannot reach. There, the rollback removed the placement,
            // the next boundary disappeared, and the task was dropped because nothing was pending —
            // so nothing ever checked WHICH set it was building. Here the rollback swaps one
            // placement for another: a boundary is still pending, at the same tick, for a different
            // set. Only the tag says the in-flight work is now garbage.
            Sim sim = CreateSim();
            sim.StepTo(5);

            sim.Step(Place(0, 0, 0));
            for (int i = 0; i < 8; i++) { sim.Step(); sim.Frame_(2); }   // task well under way for the first set

            sim.Simulation.Rollback(5);
            sim.Step(Place(0, -6, 0));                                    // a different building, same window
            while (sim.Tick < 5 + PlatformerCommandSystem.BuildDelayTicks + 4)
            {
                sim.Step();
                sim.Frame_(2);
            }

            Assert.AreEqual(1, LiveCount(sim.Simulation.Frame));
            Assert.AreEqual(ExpectedFingerprint(sim.Simulation.Frame, sim.LastExecutedTick),
                sim.InstalledFingerprint,
                "the mesh carries the building from before the rollback — an in-flight rebake is "
                + "built for one particular set and the frame no longer asks for that one");
        }

        [Test]
        public void Slicing_ACorrectionWhileALaterBoundaryIsPendingDoesNotOverlapThePool()
        {
            // Two placements with different effective ticks, so rolling back between them leaves a
            // correction to make AND a task in flight for the boundary still ahead. A synchronous
            // rebuild started there would be using the pool the task is holding — the
            // corruption the DEBUG overlap guard exists to catch.
            Sim sim = CreateSim();
            sim.StepTo(5);
            sim.Step(Place(0, 0, 0));                                     // due at 65

            sim.StepTo(20);
            sim.Step(Place(1, -6, 0));                                    // due at 80

            // Deliberately never sliced. A task holds its pool from the moment it starts until it
            // finishes OR is discarded — so on this fixture, where one slice completes the whole
            // rebake, advancing it would hand the pool back and there would be nothing left to
            // overlap. Not slicing is what keeps one in flight.
            while (sim.Tick < 85) sim.Step();

            // A third set, so there is something left to build. The boundary cache holds two
            // meshes, and it suppresses the task outright when the set ahead is one of them
            // outright — with only the first two placements the rollback below
            // finds everything cached and nothing in flight, which is the cache working and this
            // test proving nothing. A set no slot holds is what keeps a task alive to overlap with.
            sim.Step(Place(1, 6, 0));
            for (int i = 0; i < 3; i++) sim.Step();
            Assert.IsTrue(sim.Pump.HasPendingRebake,
                "no rebake in flight — then the rollback cannot overlap anything and this test "
                + "proves nothing");

            // Back to a tick whose set is the empty one, which by now BOTH slots have evicted — so
            // the correction has to rebake synchronously while the task holds a pool. That is the
            // overlap, and it is prevented structurally rather than by discarding the task:
            // the rebuild is routed to the slot the task is not using. If it ever goes back
            // to sharing one pool, the DEBUG guard throws right here.
            int missesBefore = sim.Pump.CacheMisses;
            sim.Simulation.Rollback(60);
            for (int i = 0; i < 25; i++) sim.Step();

            Assert.Greater(sim.Pump.CacheMisses, missesBefore,
                "the correction was served from cache, so no synchronous rebake ran and the "
                + "overlap this test exists for never happened");
            Assert.AreEqual(ExpectedFingerprint(sim.Simulation.Frame, sim.LastExecutedTick),
                sim.InstalledFingerprint);
        }

        // ------------------------------------------ 실매치 oracle 중 여기서 되는 둘

        /// <summary>
        /// Records exactly how far this fixture gets towards a MOVING bot, and fails the moment it
        /// gets further.
        ///
        /// <para>Reproducing the live swap-tick divergence needs an agent whose path a swap can
        /// invalidate, because <c>ReseedAgents</c> only writes Status and LastRepathTick for one
        /// that is Moving or PathPending. The fixture now spawns real bots — they get HFSM state,
        /// they get nav agents — but they never acquire a destination, because that needs the HFSM
        /// to reach <c>BotStateId.Chase</c> with a valid target, and detection never fires here.</para>
        ///
        /// <para>Written as an assertion on the CURRENT state rather than on the goal, so the suite
        /// stays green while the gap stays visible: the day someone makes the bots chase, this
        /// fails and says what to do about it.</para>
        /// </summary>
        [Test]
        public void Fixture_HowFarTheBotsGet_AndWhereItStops()
        {
            Sim sim = CreateSim();
            sim.StepTo(60);

            int agents = 0, withPath = 0;
            var filter = sim.Simulation.Frame.Filter<NavAgentComponent>();
            while (filter.Next(out var entity))
            {
                agents++;
                if (sim.Simulation.Frame.GetReadOnly<NavAgentComponent>(entity).HasNavDestination)
                    withPath++;
            }

            Assert.Greater(agents, 0,
                "the bots stopped getting nav agents — Pass 2 needs BotComponent + HFSMComponent "
                + "and a non-null query, and this fixture supplies all three on purpose");
            Assert.AreEqual(0, withPath,
                "a bot acquired a destination, which this fixture has never managed. That is GOOD "
                + "news: reseed's live branch is now reachable, so change this to Greater(withPath, 0) "
                + "and re-run the rollback tests — they may now reproduce the live-match divergence");
        }

        [Test]
        public void Oracle6_SlicesAdvanceOnFramesOnly_NeverOnTicks()
        {
            // Written as a live-match observation ("Step calls == frames, not
            // ticks") because the failure it guards is invisible from the state: advancing on ticks
            // produces the same mesh, just eleven times the work in the frame a catching-up client
            // can least afford. It does not need two peers — it is a statement about one peer's
            // pacing, so it belongs here.
            Sim sim = CreateSim();
            sim.StepTo(5);
            sim.Step(Place(0, 0, 0));

            int before = sim.Pump.SlicedFrames;
            for (int i = 0; i < 20; i++)
                sim.Step();                       // ticks only — no frame boundary

            Assert.AreEqual(before, sim.Pump.SlicedFrames,
                "a tick advanced the rebake. Ticks and frames are different clocks on purpose: a "
                + "client catching up runs many ticks in one frame, and spending a frame's budget "
                + "per tick stalls exactly the frame this feature exists to protect");

            // One slice per frame, and at least one — not a fixed total. This used to assert
            // exactly three, which pinned how many steps THIS fixture's rebake takes, and that is
            // not a property of the pacing: the boundary cache starts the task in the slot that
            // does NOT hold the live mesh, so the first task of a
            // match has no previous mesh to patch and full-builds instead — a different number of
            // steps for identical output. Asserting the rate keeps the guard and drops the
            // coincidence.
            int last = sim.Pump.SlicedFrames;
            int advanced = 0;
            for (int i = 0; i < 5; i++)
            {
                sim.Frame_(1);
                int now = sim.Pump.SlicedFrames;
                Assert.LessOrEqual(now - last, 1,
                    "one frame advanced more than one slice — the budget is per frame");
                advanced += now - last;
                last = now;
            }
            Assert.Greater(advanced, 0, "frames did not advance the rebake");
        }

        [Test]
        public void Oracle7_ReseedsExactlyOncePerBoundaryTick()
        {
            // The one that was thought impossible to catch automatically — every
            // peer reseeding twice is not a divergence, so no hash comparison objects. It is
            // catchable the moment the count is exposed, which is why it is.
            //
            // Three boundaries here: two placements landing on different ticks, then a demolition.
            Sim sim = CreateSim();
            sim.StepTo(5);
            Assert.AreEqual(0, sim.Bots.ReseedCount, "nothing has taken effect yet");

            // Exactly one correction so far, and it is the startup install: the pump begins
            // unsatisfied on purpose so that the mesh in use is always Bake({}) rather than the
            // loaded mesh, which is only the same REGION and not the same triangulation. Baselined
            // rather than asserted away — if it ever becomes two, the invariant is not holding.
            Assert.AreEqual(1, sim.Pump.Corrections,
                "the startup install is the only correction a forward-only run should ever make");
            int correctionsAtStart = sim.Pump.Corrections;

            sim.Step(Place(0, -6, 0));
            sim.Step(Place(1, 6, 0));
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 3);
            Assert.AreEqual(2, sim.Bots.ReseedCount,
                "two placements landing on two different ticks must reseed exactly twice — twice "
                + "that is the T+K double reseed, and it does not diverge, it is just wrong");

            EntityRef target = FirstBuilding(sim.Simulation.Frame);
            sim.Step(new RemoveBuildingCommand { PlayerId = 0, TargetEntityId = target.ToId() });
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 3);
            Assert.AreEqual(3, sim.Bots.ReseedCount, "the demolition boundary must reseed too");

            // And nothing corrected: this peer never rolled back and never joined, so every install
            // was a boundary. A correction here would mean the invariant was not holding.
            Assert.AreEqual(correctionsAtStart, sim.Pump.Corrections,
                "the mesh was corrected on an ordinary forward tick — this peer never rolled back "
                + "and never joined, so every install after startup should have been a boundary");
        }

        [Test]
        public void Tick10_ReExecutingTheBoundaryReseedsAgain_EvenThoughTheMeshIsAlreadyRight()
        {
            // The bug three live matches produced, reduced.
            //
            // The reseed writes hashed frame state, so re-executing the boundary tick has to
            // perform it again. It used to be fused to the mesh install, whose guard is peer-local
            // and does NOT roll back — so a rollback landing ON or AFTER the boundary re-executed
            // the tick with the tag already matching, skipped the whole block, and left the frame
            // with the pre-swap agent state. A server that never rolls back kept the right value;
            // every client kept the wrong one, which is why both clients agreed and the server did
            // not.
            //
            // The rollback target is what makes this different from the other rollback tests here:
            // they land BEFORE the boundary, where the correction resets the tag and the reseed
            // then happens on the way back through. Landing after it is the case that skipped.
            Sim sim = CreateSim();
            sim.StepTo(5);
            int placedAt = sim.Tick;
            sim.Step(Place(0, 0, 0));
            int boundary = placedAt + PlatformerCommandSystem.BuildDelayTicks;

            sim.StepTo(boundary + 3);
            long forwardHash = sim.Simulation.GetStateHash();
            int reseedsAfterForward = sim.Bots.ReseedCount;

            // Back to just past the boundary — the mesh is already the right one, so nothing about
            // the INSTALL needs doing on the way forward again.
            sim.Simulation.Rollback(boundary);
            Assert.AreEqual(boundary, sim.Tick);

            sim.StepTo(boundary + 3);

            Assert.Greater(sim.Bots.ReseedCount, reseedsAfterForward,
                "re-executing the boundary tick did not reseed. The mesh was already installed, so "
                + "a guard that asks 'is the mesh right' skips a frame write that the frame says "
                + "must happen — and peer-local guards do not roll back");
            Assert.AreEqual(forwardHash, sim.Simulation.GetStateHash(),
                "the replayed boundary landed on different state than its first execution");
        }

        // ------------------------------------------- a peer that only replays the frame

        [Test]
        public void Tick11And15_APeerThatOnlyReplaysTheFrameLandsOnTheSameMesh()
        {
            // Two situations share one mechanism, so they share one test: a peer that never saw the
            // command — a joiner given the state, a spectator, a replay — installs the same mesh
            // purely by running the invariant. Nothing hooks the swap for it.
            Sim placer = CreateSim();
            placer.StepTo(5);
            placer.Step(Place(0, 0, 0));
            placer.StepTo(placer.Tick + PlatformerCommandSystem.BuildDelayTicks + 4);

            Sim observer = CreateSim();
            observer.StepTo(placer.Tick - 1);
            CopyBuildings(placer.Simulation.Frame, observer.Simulation.Frame);

            // One tick is all the invariant needs: it reads the frame it was handed.
            observer.Step();

            Assert.AreEqual(placer.InstalledFingerprint, observer.InstalledFingerprint,
                "a peer that only has the components does not reach the same mesh — that is the "
                + "join, spectate and replay paths all at once");
        }

        [Test]
        public void Tick11_AJoinerInsideTheDelayWindowAgreesOnceTheBoundaryPasses()
        {
            // Joining while a placement is pending. The component carries its own effective tick, so
            // the joiner swaps on the same tick the incumbent does without being told when.
            Sim placer = CreateSim();
            placer.StepTo(5);
            placer.Step(Place(0, 0, 0));
            int boundary = placer.Tick + PlatformerCommandSystem.BuildDelayTicks;

            placer.StepTo(boundary - 10);
            Sim joiner = CreateSim();
            joiner.StepTo(placer.Tick - 1);
            CopyBuildings(placer.Simulation.Frame, joiner.Simulation.Frame);
            joiner.Step();

            Assert.AreEqual(placer.InstalledFingerprint, joiner.InstalledFingerprint,
                "the joiner installed the pending building early");

            while (placer.Tick < boundary + 3)
            {
                placer.Step();
                joiner.Step();
                Assert.AreEqual(placer.InstalledFingerprint, joiner.InstalledFingerprint,
                    $"tick {placer.LastExecutedTick}: the joiner and the incumbent disagree about the mesh");
            }
        }

        /// <summary>
        /// Stands in for a FullState apply: the destination gets the source's building components
        /// and nothing else — no swap call, no notification of when the boundary is.
        /// </summary>
        private static void CopyBuildings(Frame source, Frame destination)
        {
            var filter = source.Filter<BuildingComponent>();
            while (filter.Next(out var entity))
            {
                BuildingComponent b = source.GetReadOnly<BuildingComponent>(entity);
                EntityRef created = destination.CreateEntity();
                destination.Add(created, b);
            }
        }

        // ------------------------------------------- Sequence uniqueness and the cap

        [Test]
        public void Tick13_SequencesStayUniqueAcrossTombstones()
        {
            // Sequence orders the rebake input, so a duplicate is a mesh that depends on
            // storage iteration order. The risk is specific: the counter is derived from the whole
            // component set, and a tombstone is still in it.
            Sim sim = CreateSim();
            sim.StepTo(5);

            sim.Step(Place(0, -4, -4));
            sim.Step(Place(0, 4, 4));
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 2);

            EntityRef first = FirstBuilding(sim.Simulation.Frame);
            sim.Step(new RemoveBuildingCommand { PlayerId = 0, TargetEntityId = first.ToId() });
            sim.Step(Place(0, 0, 0));   // placed while a tombstone still occupies a slot

            var seen = new HashSet<int>();
            var filter = sim.Simulation.Frame.Filter<BuildingComponent>();
            while (filter.Next(out var entity))
            {
                int sequence = sim.Simulation.Frame.GetReadOnly<BuildingComponent>(entity).Sequence;
                Assert.IsTrue(seen.Add(sequence),
                    $"duplicate Sequence {sequence} — the rebake input order stops being defined, "
                    + "and two peers iterating differently produce different meshes");
            }
        }

        [Test]
        public void Tick14_ADemolitionDoesNotStrandASlot()
        {
            // The cap is enforced on standing buildings while the array also holds tombstones,
            // so a leak shows up as a placement refused with fewer than MaxBuildings standing.
            Sim sim = CreateSim();
            sim.StepTo(5);
            sim.Step(Place(0, -4, -4));
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 2);

            EntityRef target = FirstBuilding(sim.Simulation.Frame);
            sim.Step(new RemoveBuildingCommand { PlayerId = 0, TargetEntityId = target.ToId() });
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 2);

            Assert.AreEqual(0, LiveCount(sim.Simulation.Frame),
                "the demolished building still occupies a slot after its removal tick");

            sim.Step(Place(0, -4, -4));   // the same spot the demolished one held
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 2);

            Assert.AreEqual(1, LiveCount(sim.Simulation.Frame),
                "rebuilding on the cleared spot was refused — the old placement is still occupying "
                + "the collected set even though its component is gone");
            Assert.AreEqual(ExpectedFingerprint(sim.Simulation.Frame, sim.LastExecutedTick),
                sim.InstalledFingerprint);
        }

        [Test]
        public void Tick14_TwoDemolitionsDueOnTheSameTickBothLand()
        {
            // Issued in one tick, so both fall due on one tick. Destroying inside the filter loop
            // forces a stop after the first, and the leftover is invisible from then on: the active
            // set already excludes it, so the mesh is right and only the slot is gone — for the
            // rest of the match, because the due predicate never matches that tick again.
            Sim sim = CreateSim();
            sim.StepTo(5);
            sim.Step(Place(0, -6, 0));
            sim.Step(Place(1, 6, 0));
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 2);
            Assert.AreEqual(2, LiveCount(sim.Simulation.Frame), "both placements should have landed");

            var targets = new List<EntityRef>();
            var filter = sim.Simulation.Frame.Filter<BuildingComponent>();
            while (filter.Next(out var entity)) targets.Add(entity);

            sim.Step(
                new RemoveBuildingCommand { PlayerId = 0, TargetEntityId = targets[0].ToId() },
                new RemoveBuildingCommand { PlayerId = 1, TargetEntityId = targets[1].ToId() });
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 3);

            Assert.AreEqual(0, LiveCount(sim.Simulation.Frame),
                "a tombstone survived its removal tick — two came due together and only one was "
                + "destroyed, which leaks the slot permanently");
            Assert.AreEqual(ExpectedFingerprint(sim.Simulation.Frame, sim.LastExecutedTick),
                sim.InstalledFingerprint);
        }

        [Test]
        public void Tick14_ASecondRemoveOnTheSameBuildingIsRefused()
        {
            // Two removes would otherwise leave the second one's tick standing, and a player
            // double-clicking is the ordinary way to send them.
            Sim sim = CreateSim();
            sim.StepTo(5);
            sim.Step(Place(0, 0, 0));
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 2);

            EntityRef target = FirstBuilding(sim.Simulation.Frame);
            sim.Step(new RemoveBuildingCommand { PlayerId = 0, TargetEntityId = target.ToId() });
            int firstRemoval = sim.Simulation.Frame
                .GetReadOnly<BuildingComponent>(target).RemovalEffectiveTick;

            sim.Step(new RemoveBuildingCommand { PlayerId = 0, TargetEntityId = target.ToId() });

            Assert.AreEqual(firstRemoval,
                sim.Simulation.Frame.GetReadOnly<BuildingComponent>(target).RemovalEffectiveTick,
                "the second remove moved the removal tick, which changes when the mesh swaps");
        }

        // -------------------------------------------- the boundary cache

        /// <summary>
        /// What the mesh must be at a tick, as a MESH rather than as a digest — so the comparison
        /// can be element by element.
        /// </summary>
        private FPNavMesh ExpectedMesh(Frame frame, int atTick)
        {
            var live = new List<BuildingComponent>();
            var filter = frame.Filter<BuildingComponent>();
            while (filter.Next(out var entity))
            {
                BuildingComponent b = frame.GetReadOnly<BuildingComponent>(entity);
                if (atTick >= b.EffectiveTick && atTick < b.RemovalEffectiveTick)
                    live.Add(b);
            }
            live.Sort((l, r) => l.Sequence.CompareTo(r.Sequence));

            var placements = new FPBuildingPlacement[live.Count];
            for (int i = 0; i < live.Count; i++)
                placements[i] = new FPBuildingPlacement(
                    live[i].ShapeId, live[i].Orientation,
                    live[i].Centre.x, live[i].Centre.z, live[i].Centre.y);

            Assert.IsTrue(FPNavMeshRebaker.TryRebakePlacements(
                _oracleContext, placements, out FPNavMesh mesh, out _,
                null, PlatformerCommandSystem.PlacementRules, live.Count));
            _oracleContext.DiscardProduced();
            return mesh;
        }

        [Test]
        public void Cache_ServesTheBoundaryBounce_AndEveryMeshItServesIsElementForElementRight()
        {
            // The measurement this whole change rests on: a client crosses a boundary, rolls back
            // across it, and crosses it again, wanting one of two meshes each time. Two slots turn
            // that into two rebakes; one slot turned it into one per crossing.
            //
            // Compared with DescribeFirstDifference rather than the fingerprint. A cache that hands
            // back a mesh built for a NEIGHBOURING set could still agree on the fingerprint's
            // inputs; this one compares neighbours, portals and the spatial grid, which is what
            // decides the triangle index an agent is reseeded to.
            Sim sim = CreateSim();
            sim.StepTo(5);
            sim.Step(Place(0, 0, 0));
            int boundary = 5 + PlatformerCommandSystem.BuildDelayTicks;

            sim.StepTo(boundary + 3);
            int rebakesAfterFirstCrossing = sim.Pump.RebuildInstalls + sim.Pump.TaskInstalls;

            void AssertInstalledMatchesOracle()
            {
                FPNavMesh expected = ExpectedMesh(sim.Simulation.Frame, sim.LastExecutedTick);
                string diff = FPNavMeshBuildPipeline.DescribeFirstDifference(
                    sim.Agents.CurrentMesh, expected);
                Assert.IsNull(diff, $"the installed mesh differs from a fresh bake: {diff}");
            }

            for (int i = 0; i < 6; i++)
            {
                sim.Simulation.Rollback(boundary - 2);      // back to the set WITHOUT the building
                sim.StepTo(boundary - 1);
                AssertInstalledMatchesOracle();

                sim.StepTo(boundary + 2);                   // forward across it again
                AssertInstalledMatchesOracle();
            }

            // Twelve more crossings, and not one of them may build: both sets are in slots.
            Assert.AreEqual(rebakesAfterFirstCrossing,
                sim.Pump.RebuildInstalls + sim.Pump.TaskInstalls,
                "a bounce across the boundary built a mesh the pump had already built. Two slots "
                + "cover a two-cycle exactly; if this rises with the number of crossings, either "
                + "the cache is not being consulted or the pair is being evicted");
            Assert.Greater(sim.Pump.CacheHits, 0, "nothing was served from cache at all");
        }

        [Test]
        public void Cache_NeverHandsBackAMeshItsOwnCommitRetired()
        {
            // R-5, and the reason the slot's mesh and its context's commit are written in one
            // statement. CommitSwap RETIRES the previous mesh of the context it is called on, and
            // that mesh is exactly what the slot was caching — so a version that updates the entry
            // anywhere else leaves a window where the cache points at storage the pool has given
            // away. In DEBUG the retired mesh throws on the first read, which is what makes this
            // reachable at all; in Release it is silent until the next rebake overwrites it.
            //
            // Three sets and only two slots, so slot reuse — and therefore retirement — is forced
            // rather than hoped for. Then every set is asked for again, including the ones whose
            // meshes were retired: a correct pump rebuilds those, a broken one returns the corpse.
            Sim sim = CreateSim();
            sim.StepTo(5);
            sim.Step(Place(0, 0, 0));
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 2);
            sim.Step(Place(1, -6, 0));
            sim.StepTo(sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 2);
            sim.Step(Place(1, 6, 0));
            int last = sim.Tick + PlatformerCommandSystem.BuildDelayTicks + 2;
            sim.StepTo(last);

            Assert.AreEqual(3, LiveCount(sim.Simulation.Frame), "the fixture did not get three sets");

            // Walk back through all four sets and forward again, twice. Every step reads the
            // installed mesh through the oracle comparison, so a retired one is read, not just held.
            for (int round = 0; round < 2; round++)
            {
                foreach (int target in new[] { 8, 70, 130, last - 1, 70, 8, last - 1, 130 })
                {
                    sim.Simulation.Rollback(target);
                    sim.Step();
                    FPNavMesh expected = ExpectedMesh(sim.Simulation.Frame, sim.LastExecutedTick);
                    string diff = FPNavMeshBuildPipeline.DescribeFirstDifference(
                        sim.Agents.CurrentMesh, expected);
                    Assert.IsNull(diff,
                        $"round {round}, tick {target}: the installed mesh is not the one this "
                        + $"frame describes — a cache entry outlived the commit that retired it: {diff}");
                }
            }
        }

    }
}
