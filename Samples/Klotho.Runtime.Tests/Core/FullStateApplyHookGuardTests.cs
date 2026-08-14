using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// D4 join-rebake guard (Plan-D4JoinRebakeGuard §3-⑴/⑵/⑵'). The engine calls
    /// ISimulationCallbacks.OnFullStateApplied as the last statement of ApplyFullState, and it owes
    /// its callers a return value it cannot recompute. A throwing implementation used to unwind
    /// past that return AND past every caller's post-apply bookkeeping, so these pin that:
    ///   ⑴  ApplyFullState returns rather than unwinding, logs exactly one KError, and leaves the
    ///       state applied — including when the throw comes from a real rebaker rejection.
    ///   ⑵  hash mismatch + hook failure still surfaces as HashMismatch (the worse signal wins).
    ///   ⑵' hash MATCH + hook failure does NOT surface as Applied — the failure rides the return
    ///       value, because a KError alone leaves nothing able to tell this peer is degraded.
    ///
    /// Two harness notes worth keeping:
    ///   - The hook is guarded by `_simulation is EcsSimulation`, so a TestSimulation-backed engine
    ///     never reaches it and would pass every assertion here vacuously. These build a real
    ///     EcsSimulation through the network-less Initialize overload.
    ///   - ApplyFullState is private, so these drive it by reflection (same seam as
    ///     MatchEndDivergenceBackstopTests). That observes the RETURN VALUE only and deliberately
    ///     exercises no caller, so the caller-side post-processing criterion (§3-⑵'') needs the
    ///     receive path instead and is not covered here.
    /// </summary>
    [TestFixture]
    public class FullStateApplyHookGuardTests
    {
        private static readonly MethodInfo _applyFullStateMethod = typeof(KlothoEngine)
            .GetMethod("ApplyFullState", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Type _applyReasonType =
            _applyFullStateMethod.GetParameters()[3].ParameterType;

        #region Harness

        private static object ApplyReason(string name) => Enum.Parse(_applyReasonType, name);

        private static object InvokeApplyFullState(
            KlothoEngine engine, int tick, byte[] data, long hash, string reason)
            => _applyFullStateMethod.Invoke(engine, new object[] { tick, data, hash, ApplyReason(reason) });

        private static (KlothoEngine engine, EcsSimulation sim, LogCapture log, HookCallbacks hook)
            NewEngine(Action onFullStateApplied, object systemToRegister = null)
        {
            var config = new SimulationConfig { TickIntervalMs = 25, MaxRollbackTicks = 50 };
            var engine = new KlothoEngine(config, new SessionConfig());
            var sim = new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 25);
            var log = new LogCapture();
            var hook = new HookCallbacks(onFullStateApplied);
            // Registered here rather than through ISimulationCallbacks.RegisterSystems: the engine
            // does not call that — KlothoSession / RoomManager do — so a hook-side registration
            // would silently never run and the fingerprint would look immovable.
            if (systemToRegister != null)
                sim.AddSystem(systemToRegister, SystemPhase.Update);
            engine.Initialize(sim, log, hook);
            return (engine, sim, log, hook);
        }

        /// <summary>A nav fingerprint the test can move, standing in for a rebake changing the mesh.</summary>
        private sealed class MovableNavFingerprint : xpTURN.Klotho.Deterministic.Navigation.INavFingerprintSource
        {
            public long Value = 0x1111_1111_1111_1111L;
            public long GetNavFingerprint() => Value;
        }

        /// <summary>Counts hook invocations and runs whatever the test wants it to do (usually throw).</summary>
        private sealed class HookCallbacks : ISimulationCallbacks
        {
            private readonly Action _onApplied;
            public int Calls;

            public HookCallbacks(Action onApplied) { _onApplied = onApplied; }

            public void RegisterSystems(EcsSimulation simulation) { }
            public void OnInitializeWorld(IKlothoEngine engine) { }
            public void OnPollInput(int playerId, int tick, ICommandSender sender) { }
            public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId) { }

            public void OnFullStateApplied(IKlothoEngine engine, Frame frame)
            {
                Calls++;
                _onApplied();
            }
        }

        // A real rebaker rejection, not a synthetic throw: 20x20 walkable square, then a building
        // whose radius-expanded footprint swallows all of it. Models the shape that actually reaches
        // this hook in production — the authority accepted a placement against ITS base mesh and
        // catalog, and this peer's disagree (a build mismatch), so the same set is refused here.
        private static void RebakeRejectionThrow()
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
            FPNavMesh baseMesh = FPNavMeshBuildPipeline.Build(
                vertices, tris, new int[tris.Length / 3], 1.0, null, bakeAgentRadius: 0.5);

            var swallowsEverything = new FPBuildingRect(
                FP64.FromInt(-12), FP64.FromInt(-12), FP64.FromInt(12), FP64.FromInt(12), FP64.Zero);
            FPNavMeshRebaker.Rebake(baseMesh, new[] { swallowsEverything });
        }

        #endregion

        [Test]
        public void HookThrows_ApplyFullStateReturns_LogsOnce_AndKeepsTheStateApplied()
        {
            var (engine, sim, log, hook) = NewEngine(() => throw new InvalidOperationException("hook boom"));

            // Snapshot an empty world, then move the simulation off it so "restored" is observable
            // rather than a coincidence.
            var (data, hash) = sim.SerializeFullStateWithHash();
            sim.Frame.CreateEntity();
            Assert.AreNotEqual(hash, sim.GetStateHash(),
                "fixture: the simulation must actually differ from the snapshot, or the restore assertion is vacuous");

            object result = InvokeApplyFullState(engine, engine.CurrentTick + 10, data, hash, "ResyncRequest");

            Assert.AreEqual(1, hook.Calls, "the hook must actually have run — otherwise nothing is being guarded");
            Assert.IsNotNull(result, "ApplyFullState must return a result instead of unwinding");
            Assert.AreNotEqual("Skipped", result.ToString(), "the retreat guard must not have rejected this fixture");
            Assert.AreEqual(hash, sim.GetStateHash(),
                "the state stays applied — a hook failure must not roll the restore back");
            Assert.AreEqual(1, log.CountAt(KLogLevel.Error),
                "exactly one KError: silence is the failure mode this guard exists to prevent");
            Assert.IsTrue(log.Contains(KLogLevel.Error, "OnFullStateApplied threw"),
                "the KError must name the hook, so the reader is not sent hunting");
        }

        [Test]
        public void HookThrows_FromARealRebakerRejection_IsAlsoContained()
        {
            // The synthetic throw above pins the contract's SHAPE. This pins that the shape the
            // production callee actually produces flows through it too.
            Assert.Throws<InvalidOperationException>(() => RebakeRejectionThrow(),
                "fixture: the rebaker must genuinely reject this placement, or this test proves nothing");

            var (engine, sim, log, hook) = NewEngine(RebakeRejectionThrow);
            var (data, hash) = sim.SerializeFullStateWithHash();

            object result = InvokeApplyFullState(engine, engine.CurrentTick + 10, data, hash, "ResyncRequest");

            Assert.AreEqual(1, hook.Calls);
            Assert.AreEqual("DerivativeRebuildFailed", result.ToString());
            Assert.AreEqual(1, log.CountAt(KLogLevel.Error));
        }

        [Test]
        public void HashMismatch_PlusHookFailure_StillSurfacesAsHashMismatch()
        {
            // §3-⑵. The worse signal wins: a state whose hash disagrees is untrustworthy no matter
            // what the derivative rebuild did, and callers key their recovery off that. On SD that
            // branch ends the match, which is exactly why the derivative value must not reach it.
            var (engine, sim, _, hook) = NewEngine(() => throw new InvalidOperationException("hook boom"));
            var (data, hash) = sim.SerializeFullStateWithHash();

            object result = InvokeApplyFullState(
                engine, engine.CurrentTick + 10, data, hash ^ 0x5A5A5A5AL, "ResyncRequest");

            Assert.AreEqual(1, hook.Calls, "the hook runs on the mismatch branch too — that is what makes it untrusted input");
            Assert.AreEqual("HashMismatch", result.ToString(),
                "a mismatched hash must not be masked by the derivative-failure value");
        }

        [Test]
        public void HashMatch_PlusHookFailure_DoesNotSurfaceAsApplied()
        {
            // §3-⑵'. This is the axis §3-⑵ does NOT cover, and the one a KError alone leaves
            // undetectable: the state is sound, so nothing downstream would ever ask why this
            // peer's navmesh disagrees with everyone else's.
            var (engine, sim, _, _) = NewEngine(() => throw new InvalidOperationException("hook boom"));
            var (data, hash) = sim.SerializeFullStateWithHash();

            object result = InvokeApplyFullState(engine, engine.CurrentTick + 10, data, hash, "ResyncRequest");

            Assert.AreNotEqual("Applied", result.ToString(),
                "a failed derivative rebuild must not be reported as a clean apply");
            Assert.AreEqual("DerivativeRebuildFailed", result.ToString());
        }

        [Test]
        public void EnvironmentFingerprint_MovesAcrossTheApply_SoTheCheckMustRunAfterIt()
        {
            // Why both FullState receive sites call CheckStaticGeometryFingerprint AFTER raising
            // the apply (ServerDrivenClientService.HandleFullStateResponse ·
            // KlothoNetworkService.HandleFullStateResponse). The apply runs OnFullStateApplied,
            // which rebuilds peer-local derivatives — the navmesh — and the SENDER's fingerprint
            // was taken from its own post-rebake state. So the identical comparison gives opposite
            // answers on the two sides of the apply, and the pre-apply side is wrong by
            // construction on any state carrying runtime rebakes.
            //
            // The ordering is otherwise a silent convention two call sites have to keep, and the
            // cost of dropping it is not just a stray log: the mismatch report is one-shot
            // (_staticMismatchLogged re-arms only on a later MATCHING check), so a false positive
            // eats the report a real divergence in the same window would have needed.
            var nav = new MovableNavFingerprint();
            var (engine, sim, log, hook) = NewEngine(() => nav.Value = 0x2222_2222_2222_2222L, nav);
            var (data, hash) = sim.SerializeFullStateWithHash();

            long beforeApply = engine.GetLocalStaticFingerprint();
            InvokeApplyFullState(engine, engine.CurrentTick + 10, data, hash, "ResyncRequest");
            long afterApply = engine.GetLocalStaticFingerprint();

            Assert.AreEqual(1, hook.Calls, "fixture: the hook must have run, or nothing moved");
            Assert.AreNotEqual(beforeApply, afterApply,
                "fixture: the apply must actually move the environment fingerprint, or this proves nothing");

            // Post-apply comparison — what the fixed ordering does. The sender's value is the
            // post-rebake one, so it matches and stays silent.
            log.Clear();
            engine.CheckStaticGeometryFingerprint(afterApply);
            Assert.AreEqual(0, log.CountAt(KLogLevel.Error),
                "comparing after the apply must agree with a sender whose fingerprint is also post-rebake");

            // Pre-apply comparison — what the old ordering did. Same inputs, mismatch.
            log.Clear();
            engine.CheckStaticGeometryFingerprint(beforeApply);
            Assert.AreEqual(1, log.CountAt(KLogLevel.Error),
                "comparing against the pre-apply value must mismatch — that is the false positive being fixed");
            Assert.IsTrue(log.Contains(KLogLevel.Error, "Static environment mismatch"));
        }

        [Test]
        public void HookDoesNotThrow_ReturnsApplied_AndLogsNothing()
        {
            // The negative control. Without it, an implementation that always reports
            // DerivativeRebuildFailed would pass every assertion above.
            var (engine, sim, log, hook) = NewEngine(() => { });
            var (data, hash) = sim.SerializeFullStateWithHash();

            object result = InvokeApplyFullState(engine, engine.CurrentTick + 10, data, hash, "ResyncRequest");

            Assert.AreEqual(1, hook.Calls);
            Assert.AreEqual("Applied", result.ToString());
            Assert.AreEqual(0, log.CountAt(KLogLevel.Error),
                "a healthy apply must stay silent — otherwise the KError above proves nothing");
        }

        // ── The game's own slot in the environment fingerprint ───────────────

        private sealed class GameFingerprint : xpTURN.Klotho.ECS.IGameFingerprintSource
        {
            public long Value;
            public long GetGameFingerprint() => Value;
        }

        [Test]
        public void GameFingerprint_IsFoldedIn_AndReportedSeparately()
        {
            // What the slot is for: data that must match across builds but is deliberately
            // outside the state hash — a shape catalog, a tuning table. Before it existed a game
            // had nowhere to put such a value except inside one of the engine's own terms, which
            // then reported its divergence as a navmesh problem.
            var game = new GameFingerprint { Value = 0x0BADC0DE_0BADC0DEL };
            var (engine, _, log, _) = NewEngine(() => { }, game);

            long withValue = engine.GetLocalStaticFingerprint();
            game.Value = 0x0BADC0DE_0BADC0DFL;                 // one bit of the game's data differs
            long withOther = engine.GetLocalStaticFingerprint();

            Assert.AreNotEqual(withValue, withOther,
                "a game-side difference must move the fingerprint — otherwise peers cannot see it");

            // And it is named as itself in the breakdown, which is the whole reason it is a
            // separate source rather than something folded into the nav term.
            engine.CheckStaticGeometryFingerprint(withValue);
            Assert.AreEqual(1, log.CountAt(KLogLevel.Error));
            Assert.IsTrue(log.Contains(KLogLevel.Error, "game=0x0BADC0DE0BADC0DF"),
                "the mismatch report must attribute it to the game, not to the mesh");
        }

        [Test]
        public void NoGameFingerprint_ChangesNothing()
        {
            // The control. Not implementing the interface has to be free, or every existing game
            // pays for a slot it never asked for.
            var (withoutSource, _, _, _) = NewEngine(() => { });
            var zero = new GameFingerprint { Value = 0 };
            var (withZeroSource, _, _, _) = NewEngine(() => { }, zero);

            Assert.AreEqual(
                withoutSource.GetLocalStaticFingerprint(),
                withZeroSource.GetLocalStaticFingerprint(),
                "an absent source and a source returning 0 must be indistinguishable");
        }
    }
}
