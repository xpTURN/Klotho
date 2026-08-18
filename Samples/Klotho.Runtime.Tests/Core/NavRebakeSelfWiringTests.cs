using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Runtime.Tests.Core
{
    /// <summary>
    /// The engine wiring itself to a registered rebake driver.
    ///
    /// <para><b>Why these exist.</b> The heartbeat used to be subscribed by game code, from three
    /// different callbacks that did not know about each other, and the server-driven client reached
    /// none of them — so slicing was off on that host for an entire release and nothing said so.
    /// What guarded the arrangement was a regex over the sample's source. These are the behavioural
    /// nets that replace it, and the reason the subscription moved into the engine at all.</para>
    ///
    /// <para>A real <c>EcsSimulation</c> is required: the engine finds the driver through
    /// <c>GetSystem</c>, and the shared <c>KlothoTestHarness</c> runs on a <c>TestSimulation</c> that
    /// is not one. Registration happens before <c>Initialize</c> here for the same reason the
    /// production paths do it — the engine does not call <c>RegisterSystems</c>; the session and the
    /// room manager do.</para>
    /// </summary>
    [TestFixture]
    public class NavRebakeSelfWiringTests
    {
        private const long Unit = FPGeoPredicates.SNAP_UNITS_PER_WORLD;

        #region Fixture

        /// <summary>A placement table the test writes directly.</summary>
        private sealed class FakeSource : IFPNavMeshPlacementSource
        {
            public readonly List<FPNavMeshTimedPlacement> Entries = new List<FPNavMeshTimedPlacement>();
            public int Capacity => 8;

            public int Collect(ref Frame frame, FPNavMeshTimedPlacement[] buffer, out int eligible)
            {
                int n = 0;
                for (int i = 0; i < Entries.Count && n < buffer.Length; i++)
                    buffer[n++] = Entries[i];
                eligible = Entries.Count;
                return n;
            }

            public void DestroyDue(ref Frame frame, int tick) { }
        }

        private sealed class CountingInstaller : IFPNavMeshInstaller
        {
            public int Installs;
            public int Reseeds;
            public FPNavMesh Last;
            public void Install(ref Frame frame, FPNavMesh mesh) { Installs++; Last = mesh; }
            public void Reseed(ref Frame frame) { Reseeds++; }
        }

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

        private static FPNavMeshRebakeSnapshot SnapshotWithSquare(out int shapeId)
        {
            var catalog = new FPBuildingShapeCatalogBuilder();
            shapeId = catalog.Add(
                new[] { -Unit, Unit, Unit, -Unit }, new[] { -Unit, -Unit, Unit, Unit });
            return FPNavMeshRebaker.CreateSnapshot(
                Slab(), null, prewarm: false, shapeCatalog: catalog.Build());
        }

        /// <summary>A driver holding one placement that lands far enough ahead to leave a task in
        /// flight — without one, <c>AdvanceSlice</c> is a no-op and every assertion below is
        /// vacuous.</summary>
        private static FPNavMeshRebakeDriver DriverWithPendingTask(out CountingInstaller installer)
        {
            var source = new FakeSource();
            installer = new CountingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer);
            driver.SetSnapshot(SnapshotWithSquare(out int square));
            source.Entries.Add(new FPNavMeshTimedPlacement
            {
                Sequence = 1,
                Placement = new FPBuildingPlacement(square, FP64.FromInt(4), FP64.FromInt(4), FP64.Zero),
                EffectiveTick = 50,
                RemovalEffectiveTick = int.MaxValue,
            });

            var frame = new Frame(64, null) { Tick = 0 };
            driver.Update(ref frame);
            Assert.IsTrue(driver.HasPendingRebake, "no task in flight — the fixture proves nothing");
            return driver;
        }

        /// <summary>Registers several systems — the fingerprint-source tests need a driver AND the
        /// sources beside it, and the population gate needs a simulation with neither.</summary>
        private static (KlothoEngine engine, EcsSimulation sim, LogCapture log) NewEngineWith(
            params object[] systems)
        {
            var engine = new KlothoEngine(
                new SimulationConfig { TickIntervalMs = 25, MaxRollbackTicks = 50 }, new SessionConfig());
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            var log = new LogCapture();
            foreach (object system in systems)
                if (system != null)
                    sim.AddSystem(system, SystemPhase.PreUpdate);
            engine.Initialize(sim, log);
            return (engine, sim, log);
        }

        private static (KlothoEngine engine, EcsSimulation sim, LogCapture log) NewEngine(
            object systemToRegister)
        {
            var engine = new KlothoEngine(
                new SimulationConfig { TickIntervalMs = 25, MaxRollbackTicks = 50 }, new SessionConfig());
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            var log = new LogCapture();
            if (systemToRegister != null)
                sim.AddSystem(systemToRegister, SystemPhase.PreUpdate);
            engine.Initialize(sim, log);
            return (engine, sim, log);
        }

        /// <summary>
        /// An installer that writes into the SAME log the engine uses, so the ORDER of its install
        /// against the engine's own lines is readable. That is the only way to assert this property:
        /// the driver logs nothing on a successful install, so there is no line of its own to order.
        /// </summary>
        private sealed class LoggingInstaller : IFPNavMeshInstaller
        {
            private readonly LogCapture _log;
            public int Installs;
            public LoggingInstaller(LogCapture log) { _log = log; }

            public void Install(ref Frame frame, FPNavMesh mesh)
            {
                Installs++;
                _log.Log(KLogLevel.Information, "TEST-INSTALL", null);
            }

            public void Reseed(ref Frame frame) { }
        }

        /// <summary>
        /// Makes the static-geometry fingerprint line actually appear. Without a registered service
        /// <c>LogStaticFingerprint</c> returns silently, and then the only lines containing "boot" are
        /// the component-registry one that comes LATER — an anchor that reads as passing no matter
        /// where the correction sits. That is not a hypothetical: the first draft of this test used it
        /// and the mutation that moves the correction after the fingerprint stayed green.
        /// </summary>
        private sealed class FakeStaticColliders : xpTURN.Klotho.Deterministic.Physics.IStaticColliderService
        {
            public void LoadStaticColliders(string sceneKey, List<FPStaticCollider> colliders) { }
            public void UnloadStaticColliders(string sceneKey) { }
            public void GetStaticColliders(out FPStaticCollider[] colliders, out int count)
            {
                colliders = System.Array.Empty<FPStaticCollider>();
                count = 0;
            }
            public long GetStaticFingerprint() => 0x1234L;
        }

        /// <summary>Folds something — which is all the engine can check. What it folds is the
        /// game's business; that it exists at all is not.</summary>
        private sealed class FoldsNavAndGame
            : xpTURN.Klotho.Deterministic.Navigation.INavFingerprintSource, IGameFingerprintSource
        {
            public long GetNavFingerprint() => 0x11L;
            public long GetGameFingerprint() => 0x22L;
        }

        private const string NavWarning = "no INavFingerprintSource";
        private const string GameWarning = "no IGameFingerprintSource";

        private static int WarningsMatching(LogCapture log, string needle)
        {
            int n = 0;
            foreach ((KLogLevel level, string message) in log.Entries)
                if (level == KLogLevel.Warning && message.Contains(needle)) n++;
            return n;
        }

        /// <summary>
        /// Drives the engine far enough to run world init. <c>Start</c> refuses unless the state is
        /// <c>WaitingForPlayers</c>, which only the network overload of <c>Initialize</c> sets — and
        /// <c>Start</c> never touches the network service before world init, so the state is the only
        /// thing actually required. Set it directly rather than standing up a mock of an interface
        /// with forty members (reflection into the engine is already how the full-state tests reach
        /// their entry point).
        /// </summary>
        private static void ForceWaitingForPlayers(KlothoEngine engine)
        {
            System.Reflection.FieldInfo state = typeof(KlothoEngine).GetField(
                "_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(state, "KlothoEngine._state is gone — this harness needs rewriting");
            state.SetValue(engine, KlothoState.WaitingForPlayers);
        }

        #endregion

        // ─────────────────────────────────────────────── b1 · the heartbeat

        [Test]
        public void EngineAdvancesTheDriverExactlyOncePerUpdate()
        {
            FPNavMeshRebakeDriver driver = DriverWithPendingTask(out _);
            (KlothoEngine engine, _, _) = NewEngine(driver);

            int before = driver.SlicedFrames;
            engine.Update(0.016f);
            Assert.AreEqual(before + 1, driver.SlicedFrames,
                "one Update did not advance exactly one slice. Frames and ticks are different clocks "
                + "and the budget is denominated in frames");

            // At most one per Update, and at least one overall — NOT a fixed total. A total would
            // pin how many steps this fixture's rebake happens to take, which is not a property of
            // the pacing; the first draft of this test asserted +3 and failed because the task
            // finished in two.
            int last = driver.SlicedFrames;
            int advanced = 0;
            for (int i = 0; i < 5; i++)
            {
                engine.Update(0.016f);
                int now = driver.SlicedFrames;
                Assert.LessOrEqual(now - last, 1, "one Update advanced more than one slice");
                advanced += now - last;
                last = now;
            }
            Assert.Greater(advanced, 0, "later Updates advanced nothing at all");
        }

        [Test]
        public void WithNoDriverRegistered_UpdateIsANullCheck()
        {
            (KlothoEngine engine, _, LogCapture log) = NewEngine(null);

            Assert.DoesNotThrow(() => engine.Update(0.016f),
                "a game that does not use runtime rebake must pay nothing and risk nothing");
            Assert.IsFalse(log.Contains(KLogLevel.Information, "self-wired"),
                "the engine announced self-wiring with no driver registered");
        }

        // ─────────────────────────────────────────────── b2 · the claim

        [Test]
        public void TheEngineClaimsAtInitialize_BeforeAnyGameDoorCouldAsk()
        {
            FPNavMeshRebakeDriver driver = DriverWithPendingTask(out _);
            (KlothoEngine engine, _, LogCapture log) = NewEngine(driver);

            Assert.AreEqual(1, driver.HeartbeatClaimAttempts, "the engine did not claim at Initialize");
            Assert.IsTrue(driver.SliceHeartbeatWired, "the claim did not take");
            Assert.IsTrue(log.Contains(KLogLevel.Information, "self-wired"),
                "self-wiring is silent — a live match then cannot tell it happened");

            // The order that makes double-advance impossible. Initialize runs before Start, so a
            // game wiring from OnInitializeWorld arrives second and is refused — which is how a
            // legacy manual subscription declines to subscribe rather than doubling the budget.
            Assert.IsFalse(driver.TryClaimSliceHeartbeat(),
                "a game door claiming after Initialize was granted the heartbeat. The engine must "
                + "already hold it, or both advance and the frame budget is spent twice");
        }

        [Test]
        public void AClaimTakenBeforeInitialize_DoesNotStopTheEngineAdvancing()
        {
            // The state that made the claim useless as a gate: today's sample consumes the claim and
            // then fails the cast to the concrete engine, so it never subscribes. If the engine
            // treated a lost claim as "someone else is pacing", nobody would pace at all — which is
            // exactly the outage this whole change exists to prevent.
            FPNavMeshRebakeDriver driver = DriverWithPendingTask(out _);
            Assert.IsTrue(driver.TryClaimSliceHeartbeat(), "fixture: the pre-claim did not take");

            (KlothoEngine engine, _, _) = NewEngine(driver);

            int before = driver.SlicedFrames;
            engine.Update(0.016f);
            Assert.AreEqual(before + 1, driver.SlicedFrames,
                "the engine stood down because the claim was taken. Advancing is not gated on the "
                + "claim — the claim only tells a GAME that the engine owns pacing");
        }

        // ─────────────────────────────────────────────── b4 · the precondition

        [Test]
        public void ADriverRegisteredAfterInitialize_IsNotSelfWired()
        {
            // Pins the one precondition the design has instead of a runtime defence. Every shipping
            // path registers before Initialize (KlothoSession, RoomManager), so this is a contract
            // for future wiring rather than a live hazard.
            (KlothoEngine engine, EcsSimulation sim, _) = NewEngine(null);

            FPNavMeshRebakeDriver driver = DriverWithPendingTask(out _);
            sim.AddSystem(driver, SystemPhase.PreUpdate);

            int before = driver.SlicedFrames;
            engine.Update(0.016f);
            Assert.AreEqual(before, driver.SlicedFrames,
                "a driver registered after Initialize was picked up. That is not the contract — if "
                + "it is ever made to work, this test should be deleted deliberately, not by drift");
            Assert.AreEqual(0, driver.HeartbeatClaimAttempts, "it was claimed, too");
        }

        [Test]
        public void ReInitializingTheSameSimulation_DoesNotClaimTwice()
        {
            // Stop clears the re-entry guard and the reconnect path uses that. Claiming again would
            // not change behaviour, but it WOULD change the reading: the claim count is how a live
            // match separates "the engine owns pacing" (1) from "a game is still wiring it" (2), and
            // a reconnect must not turn a healthy peer into the second.
            FPNavMeshRebakeDriver driver = DriverWithPendingTask(out _);
            (KlothoEngine engine, EcsSimulation sim, LogCapture log) = NewEngine(driver);
            Assert.AreEqual(1, driver.HeartbeatClaimAttempts, "fixture: the first claim did not happen");

            engine.Stop();
            engine.Initialize(sim, log);

            Assert.AreEqual(1, driver.HeartbeatClaimAttempts,
                "a reconnect claimed the heartbeat a second time — claims == 2 is the signal for an "
                + "unmigrated game still pacing by hand, and now a healthy peer reports it");

            int before = driver.SlicedFrames;
            engine.Update(0.016f);
            Assert.AreEqual(before + 1, driver.SlicedFrames, "pacing stopped after the reconnect");
        }

        // ─────────────────────────────────────────────── b3 · the world-init invariant

        [Test]
        public void WorldInit_EstablishesTheMeshBeforeTheStaticFingerprintIsSampled()
        {
            // The failure this prevents took a live match to find and blamed the wrong peer: the
            // initial FullState — and the fingerprint it carries — goes out after world init and
            // before tick 0, so without a correction here the server describes the LOADED mesh while
            // every client corrects to Bake({}). Same walkable region, different triangulation, and
            // the client gets reported for a static-environment mismatch for being right.
            //
            // ⚠ The assertion is ORDER, not count. Corrections == 1 holds whether the call sits
            // before or after the fingerprint, so a count would pass a mutation that moves it — which
            // is exactly the mutation that reintroduces the bug.
            var log = new LogCapture();
            var source = new FakeSource();
            var installer = new LoggingInstaller(log);
            var driver = new FPNavMeshRebakeDriver(source, installer);
            driver.SetSnapshot(SnapshotWithSquare(out _));

            var engine = new KlothoEngine(
                new SimulationConfig { TickIntervalMs = 25, MaxRollbackTicks = 50 }, new SessionConfig());
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(driver, SystemPhase.PreUpdate);
            sim.AddSystem(new FakeStaticColliders(), SystemPhase.PreUpdate);
            engine.Initialize(sim, log);
            ForceWaitingForPlayers(engine);

            engine.Start(enableRecording: false);

            Assert.AreEqual(1, installer.Installs,
                "world init did not establish the mesh at all — the invariant starts one tick late "
                + "and the initial FullState describes a derivative nothing agrees with");

            int installLine = IndexOf(log, "TEST-INSTALL");
            int bootLine = IndexOf(log, "[Physics][StaticGeometry] boot");
            Assert.Greater(installLine, -1, "fixture: the install was not logged");
            Assert.Greater(bootLine, -1,
                "the static fingerprint line is gone — it is the reference point this order is "
                + "measured against, so its absence makes the assertion meaningless rather than true");
            Assert.Less(installLine, bootLine,
                "the mesh was installed AFTER the static fingerprint was sampled. The fingerprint "
                + "then describes the loaded mesh while every peer corrects to Bake({}), and the "
                + "peer that corrected is the one that gets blamed");
        }

        private static int IndexOf(LogCapture log, string substring)
        {
            for (int i = 0; i < log.Entries.Count; i++)
                if (log.Entries[i].Message.Contains(substring)) return i;
            return -1;
        }

        // ─────────────────────────────────────────────── b5 · the full-state apply

        private static readonly System.Reflection.MethodInfo _applyFullState = typeof(KlothoEngine)
            .GetMethod("ApplyFullState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private static object Apply(KlothoEngine engine, EcsSimulation sim, long hashOverride = 0)
        {
            (byte[] data, long hash) = sim.SerializeFullStateWithHash();
            System.Type reasonType = _applyFullState.GetParameters()[3].ParameterType;
            return _applyFullState.Invoke(engine, new object[]
            {
                engine.CurrentTick + 10, data, hashOverride != 0 ? hashOverride : hash,
                System.Enum.Parse(reasonType, "ResyncRequest"),
            });
        }

        [Test]
        public void AFullStateApply_CorrectsTheMesh_WithNoGameCallbacksAtAll()
        {
            // ⚠ THE HARNESS IS PART OF THE ASSERTION. This engine is initialized WITHOUT callbacks,
            // which is how every test harness in this repo does it — and it is the reason the
            // correction must not live inside the `_simulationCallbacks != null` block that the game
            // hook sits in. Placed there, this test can never reach it and reads green forever. Move
            // the production code into that block and this test is what goes red.
            var log = new LogCapture();
            var source = new FakeSource();
            var installer = new CountingInstaller();
            var driver = new FPNavMeshRebakeDriver(source, installer);
            driver.SetSnapshot(SnapshotWithSquare(out _));

            var engine = new KlothoEngine(
                new SimulationConfig { TickIntervalMs = 25, MaxRollbackTicks = 50 }, new SessionConfig());
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(driver, SystemPhase.PreUpdate);
            engine.Initialize(sim, log);        // no ISimulationCallbacks — deliberately

            object result = Apply(engine, sim);

            Assert.AreEqual("Applied", result.ToString(), "the apply itself did not succeed");
            Assert.AreEqual(1, installer.Installs,
                "the mesh was not corrected on the apply. The receive path compares this peer's nav "
                + "fingerprint against the sender's immediately after, so 'the next tick will fix it' "
                + "is too late — and that report is one-shot");
            Assert.AreEqual(0, installer.Reseeds,
                "the correction reseeded. Only an executed boundary tick may write hashed agent "
                + "state; the restored state already carries what the sender's own reseed wrote");
        }

        [Test]
        public void AFullStateApply_CorrectsOnTheHashMismatchBranchToo()
        {
            // The local simulation keeps ticking on the restored state whether the hash matched or
            // not, so its derivatives have to track it either way. It also means an UNTRUSTED
            // building set reaches the rebaker, which is why the rebaker keeps re-validating.
            var log = new LogCapture();
            var driver = new FPNavMeshRebakeDriver(new FakeSource(), new CountingInstaller());
            driver.SetSnapshot(SnapshotWithSquare(out _));

            var engine = new KlothoEngine(
                new SimulationConfig { TickIntervalMs = 25, MaxRollbackTicks = 50 }, new SessionConfig());
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(driver, SystemPhase.PreUpdate);
            engine.Initialize(sim, log);

            int before = driver.Corrections;
            object result = Apply(engine, sim, hashOverride: 0x5A5A_5A5A_5A5A_5A5AL);

            Assert.AreEqual("HashMismatch", result.ToString(), "the worse signal must still win");
            Assert.Greater(driver.Corrections, before,
                "a mismatched apply skipped the correction. The peer goes on ticking on that state, "
                + "so leaving the mesh behind guarantees the NEXT comparison blames this peer");
        }

        [Test]
        public void ACorrectionThatThrowsOnApply_SurfacesAsDerivativeRebuildFailed()
        {
            var log = new LogCapture();
            var driver = new FPNavMeshRebakeDriver(new FakeSource(), new ThrowingInstaller());
            driver.SetSnapshot(SnapshotWithSquare(out _));

            var engine = new KlothoEngine(
                new SimulationConfig { TickIntervalMs = 25, MaxRollbackTicks = 50 }, new SessionConfig());
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(driver, SystemPhase.PreUpdate);
            engine.Initialize(sim, log);

            object result = Apply(engine, sim);

            Assert.AreEqual("DerivativeRebuildFailed", result.ToString(),
                "a failed navmesh correction must reach the caller as its own outcome — the state "
                + "stays applied, so callers need to know the derivative did not follow it");
            Assert.IsTrue(log.Contains(KLogLevel.Error, "navmesh correction threw"),
                "the failure was not logged");
        }

        private sealed class ThrowingInstaller : IFPNavMeshInstaller
        {
            public void Install(ref Frame frame, FPNavMesh mesh)
                => throw new InvalidOperationException("install boom");
            public void Reseed(ref Frame frame) { }
        }

        // ─────────────────────────────────────────────── b6 · the SD-client symmetry

        [Test]
        public void AServerDrivenClient_SkipsWorldInit_AndIsCorrectedByTheApplyInstead()
        {
            // The whole ClientSlicing lineage rests on this asymmetry, so it is pinned rather than
            // left to the reader. An SD client never runs world init — the engine gates that block on
            // !isSdClient because its world arrives as a full state — so the invariant there is
            // established by the apply path and nowhere else. If the apply ever stopped correcting,
            // this host would be the only one running on a stale mesh, and the peer that noticed
            // would be the one reported.
            var log = new LogCapture();
            var installer = new CountingInstaller();
            var driver = new FPNavMeshRebakeDriver(new FakeSource(), installer);
            driver.SetSnapshot(SnapshotWithSquare(out _));

            var engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = 25, MaxRollbackTicks = 50,
                    Mode = NetworkMode.ServerDriven,     // and not the server → isSdClient
                },
                new SessionConfig());
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            sim.AddSystem(driver, SystemPhase.PreUpdate);
            sim.AddSystem(new FakeStaticColliders(), SystemPhase.PreUpdate);
            engine.Initialize(sim, log);
            ForceWaitingForPlayers(engine);

            engine.Start(enableRecording: false);

            Assert.IsFalse(engine.IsServer, "fixture: this peer must be the CLIENT of a server-driven session");
            Assert.AreEqual(0, installer.Installs,
                "world init corrected on a server-driven client. That block is gated on !isSdClient "
                + "because the client has no world yet — correcting here would describe a state that "
                + "has not arrived");
            Assert.IsFalse(log.Contains(KLogLevel.Information, "[Physics][StaticGeometry] boot"),
                "fixture: the static fingerprint was logged, so this peer did NOT take the SD-client "
                + "path and the assertion above proves nothing");

            // The window the client DOES have is the apply, and the engine closes it there.
            object result = Apply(engine, sim);

            Assert.AreEqual("Applied", result.ToString());
            Assert.AreEqual(1, installer.Installs,
                "the apply did not correct the mesh — then an SD client has no window at all and "
                + "runs on whatever mesh it loaded");
        }

        // ─────────────────────────────────────────────── t15 · slice faults

        [Test]
        public void AThrowingSlice_IsContainedByDroppingTheTask_NotByLeavingItMidPhase()
        {
            // Leaving it is what the caller-side catch used to do, and it is the worse of the two
            // failures: the task stays mid-phase with its pool held and `_taskDone` false, so the
            // boundary re-drives it — throwing again out of the tick (which stops Tick++ forever) or
            // resuming over corrupted state into a mesh no other peer has.
            FPNavMeshRebakeDriver driver = DriverWithPendingTask(out CountingInstaller installer);
            (KlothoEngine engine, _, _) = NewEngine(driver);

            driver.StepFaultForTests = () => throw new InvalidOperationException("slice boom");

            Assert.DoesNotThrow(() => engine.Update(0.016f),
                "the slice fault escaped into the frame loop");
            Assert.AreEqual(1, driver.SliceFaults, "the fault was not counted");
            Assert.IsFalse(driver.HasPendingRebake,
                "the task survived the throw. Mid-phase is exactly the state the boundary must not "
                + "inherit");

            // And the boundary still produces the mesh — consistency does not depend on slicing.
            driver.StepFaultForTests = null;
            var boundary = new Frame(64, null) { Tick = 50 };
            int installsBefore = installer.Installs;
            driver.Update(ref boundary);
            Assert.Greater(installer.Installs, installsBefore,
                "the boundary installed nothing after a dropped slice — the synchronous rebuild is "
                + "what makes dropping safe, so if it does not happen, dropping is not safe");
            Assert.AreEqual(1, installer.Reseeds, "the boundary did not reseed");
        }

        [Test]
        public void ADroppedSlice_IsReportedOnTheNextTick_BecauseTheFrameBoundaryHasNoLogger()
        {
            FPNavMeshRebakeDriver driver = DriverWithPendingTask(out _);
            (KlothoEngine engine, _, _) = NewEngine(driver);
            driver.StepFaultForTests = () => throw new InvalidOperationException("slice boom");
            engine.Update(0.016f);
            driver.StepFaultForTests = null;

            var log = new LogCapture();
            var frame = new Frame(64, log) { Tick = 1 };
            driver.Update(ref frame);

            Assert.IsTrue(log.Contains(KLogLevel.Error, "sliced rebake threw"),
                "a dropped slice went unreported. AdvanceSlice runs on the frame boundary and has no "
                + "frame, so the next tick is the only place this can surface — and it must, because "
                + "the condition is a core defect even though consistency survives it");

            var second = new Frame(64, log) { Tick = 2 };
            driver.Update(ref second);
            int reports = 0;
            foreach ((KLogLevel level, string message) in log.Entries)
                if (level == KLogLevel.Error && message.Contains("sliced rebake threw")) reports++;
            Assert.AreEqual(1, reports,
                "the fault was reported again on a later tick — it is one event, not a state");
        }

        // ────────────────────────────── f1 · the net a game can forget

        /// <summary>
        /// The last thing a game can still forget. Registering the driver is the whole wiring now,
        /// but the two fingerprint hooks stay optional and a missing one folds 0 — indistinguishable
        /// from a game with nothing to fold. So the mesh, and the table it was carved from, would sit
        /// outside every hash with nothing to say so on either peer.
        /// </summary>
        [Test]
        public void ARebakingGameMissingBothFingerprintSources_IsToldOnce()
        {
            FPNavMeshRebakeDriver driver = DriverWithPendingTask(out _);
            (KlothoEngine engine, EcsSimulation sim, LogCapture log) = NewEngineWith(driver);

            Assert.AreEqual(1, WarningsMatching(log, NavWarning),
                "a rebaking game with no INavFingerprintSource was not told. Nothing else reports it: "
                + "the static fingerprint folds 0 for a missing source, so the omission and 'this game "
                + "has no navmesh' produce the same value");
            Assert.AreEqual(1, WarningsMatching(log, GameWarning),
                "a rebaking game with no IGameFingerprintSource was not told. The shape catalog is a "
                + "determinism input that no state hash covers");

            // A reconnect resolves the same driver again. Saying it twice trains the reader to
            // scroll past it, which is the only way a warning like this actually fails.
            engine.Stop();
            engine.Initialize(sim, log);
            Assert.AreEqual(1, WarningsMatching(log, NavWarning), "warned again on reconnect");
            Assert.AreEqual(1, WarningsMatching(log, GameWarning), "warned again on reconnect");
        }

        [Test]
        public void ARebakingGameThatFoldsBoth_IsNotWarned()
        {
            FPNavMeshRebakeDriver driver = DriverWithPendingTask(out _);
            (_, _, LogCapture log) = NewEngineWith(driver, new FoldsNavAndGame());

            Assert.AreEqual(0, WarningsMatching(log, NavWarning) + WarningsMatching(log, GameWarning),
                "a game that folds both was warned anyway — a warning that fires on correct wiring is "
                + "one the reader learns to ignore, and then it protects nobody");
        }

        /// <summary>
        /// The population gate. A game with a static navmesh cannot drift from a shape table it never
        /// reads, so it must not be told to fold one — every unactionable line spends the credit the
        /// actionable ones need.
        /// </summary>
        [Test]
        public void AGameThatDoesNotRebake_IsNotWarned()
        {
            (_, _, LogCapture log) = NewEngineWith(new CountingInstaller());

            Assert.AreEqual(0, WarningsMatching(log, NavWarning) + WarningsMatching(log, GameWarning),
                "a game with no rebake driver was told to fold fingerprints it has no reason to");
        }
    }
}
