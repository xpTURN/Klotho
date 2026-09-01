using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using xpTURN.Klotho;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.View.Tests
{
    /// <summary>
    /// The two defects left on the CSP lerp's two endpoints. Both are silent: the
    /// motion is smooth and the frames exist, so nothing about the picture says it is wrong.
    ///
    ///   Teleport detection — teleports were only recognised through <c>HasEntityTeleported</c>, whose
    ///         set is filled by ComputeErrorDeltas alone. That runs only when error correction is on AND
    ///         a rollback happened in the same frame, and even then it answers "re-simulation introduced
    ///         a teleport", not "the game teleported this tick". So an ordinary respawn was lerped
    ///         across — for the whole tick interval, in every configuration, EnableErrorCorrection or not.
    ///
    ///   The prev endpoint — it was read with <c>Has</c> alone. Index-only, so a slot recycled inside one
    ///         tick answers about the previous occupant, and the new view lerps out of a stranger's
    ///         position. The version check had already reached the live frame and the two verified
    ///         frames; this endpoint was missed.
    ///
    /// The recycled-slot fixture deliberately leaves TeleportTick equal on both occupants, so the
    /// teleport guard cannot mask it by snapping.
    /// </summary>
    [TestFixture]
    public class CspPathTeleportAndPrevGuardTests
    {
        private const int MaxEntities = 16;
        private const int TickIntervalMs = 50;
        private const float Tolerance = 1e-3f;

        private static readonly FieldInfo CurrentTickProp = typeof(KlothoEngine)
            .GetField("<CurrentTick>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo AccumulatorField = typeof(KlothoEngine)
            .GetField("_accumulator", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        private EcsSimulation _sim;
        private KlothoEngine _engine;

        [SetUp]
        public void SetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            var logger = factory.CreateLogger("CspPathTeleportAndPrevGuardTests");

            _sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: TickIntervalMs);
            _engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = TickIntervalMs,
                    MaxRollbackTicks = 8,
                    MaxEntities = MaxEntities,
                    // Left at its default (false) on purpose: the guard must hold for games that never enable it.
                    EnableErrorCorrection = false,
                },
                new SessionConfig());
            _engine.Initialize(_sim, logger);
        }

        private static FPVector3 Fp(Vector3 v)
            => new FPVector3(FP64.FromFloat(v.x), FP64.FromFloat(v.y), FP64.FromFloat(v.z));

        /// <summary>
        /// Places the engine's tick cursor and render alpha. CurrentTick decides which snapshot
        /// PredictedPreviousFrame resolves to (CurrentTick - 1), so it is what makes the CSP lerp read the
        /// frame this fixture built. Alpha at 0.5 puts a lerp result unmistakably between the endpoints.
        /// </summary>
        private void SeedRenderCursor(int currentTick, float alpha = 0.5f)
        {
            CurrentTickProp.SetValue(_engine, currentTick);
            AccumulatorField.SetValue(_engine, alpha * TickIntervalMs);
        }

        private GameObject NewCspView(EntityRef entity, Vector3 sentinel, out ProbeEntityView view)
        {
            var go = new GameObject("CspView");
            view = go.AddComponent<ProbeEntityView>();
            view.Engine = _engine;
            view.EntityRef = entity;
            view.SetFlags(ViewFlags.None);      // CSP path
            go.transform.position = sentinel;
            return go;
        }

        // ── An ordinary teleport must not be lerped, with error correction off ───────

        private static readonly Vector3 BeforeTeleport = new Vector3(-40f, 0f, 0f);
        private static readonly Vector3 AfterTeleport  = new Vector3(40f, 0f, 0f);

        /// <summary>
        /// Builds what a respawn looks like: tick 1 runs, the game stamps TeleportTick with its own tick
        /// and moves the transform. Snapshot 1 is the pre-teleport pose (snapshots are taken on entering a
        /// tick), the live frame is the post-teleport one.
        /// </summary>
        private EntityRef BuildTeleportHistory()
        {
            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent { Position = Fp(BeforeTeleport) });
            _sim.SaveSnapshot();                    // tick 0
            _sim.Tick(NoCommands);

            _sim.SaveSnapshot();                    // tick 1 — still pre-teleport on entry
            ref var t = ref _sim.Frame.Get<TransformComponent>(entity);
            t.Position = Fp(AfterTeleport);
            t.TeleportTick = 1;                     // teleported DURING tick 1
            _sim.Tick(NoCommands);

            return entity;
        }

        /// <summary>
        /// Error correction is off, so the old flag could never fire; the view must still
        /// refuse to walk between the two poses. Pre-fix this lerps and lands 40 units away from both.
        /// </summary>
        [Test]
        public void CspTeleport_WithErrorCorrectionOff_SnapsInsteadOfLerping()
        {
            var entity = BuildTeleportHistory();
            SeedRenderCursor(currentTick: 2);       // TeleportTick 1 == CurrentTick - 1
            var sentinel = new Vector3(0f, 99f, 0f);
            var go = NewCspView(entity, sentinel, out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, AfterTeleport), Is.LessThan(Tolerance),
                    "a teleport is instantaneous — the CSP view must render the post-teleport pose, not a "
                    + "point between the two, and that must not depend on EnableErrorCorrection");
                Assert.That(Vector3.Distance(go.transform.position, Vector3.Lerp(BeforeTeleport, AfterTeleport, 0.5f)),
                    Is.GreaterThan(1f), "...and specifically not the midpoint the old code produced");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The guard must key on the teleport tick, not on distance: once the window has moved past the
        /// teleport, the ordinary lerp comes back. Without this the fix could "pass" by never lerping.
        /// </summary>
        [Test]
        public void CspOrdinaryMotion_StillInterpolates()
        {
            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent { Position = Fp(BeforeTeleport) });
            _sim.SaveSnapshot();                    // tick 0
            _sim.Tick(NoCommands);

            _sim.SaveSnapshot();                    // tick 1
            ref var t = ref _sim.Frame.Get<TransformComponent>(entity);
            t.Position = Fp(AfterTeleport);         // moved, but no teleport stamp
            _sim.Tick(NoCommands);

            SeedRenderCursor(currentTick: 2);
            var go = NewCspView(entity, new Vector3(0f, 99f, 0f), out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, Vector3.Lerp(BeforeTeleport, AfterTeleport, 0.5f)),
                    Is.LessThan(Tolerance), "the gate keys on TeleportTick, not on how far the entity moved");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// A stale teleport stamp must not keep snapping forever. One tick later the render window is
        /// entirely post-teleport, so both endpoints agree and interpolating is correct.
        /// </summary>
        [Test]
        public void CspTeleport_OneTickLater_NoLongerSnaps()
        {
            var entity = BuildTeleportHistory();

            _sim.SaveSnapshot();                    // tick 2 — post-teleport on entry
            ref var t = ref _sim.Frame.Get<TransformComponent>(entity);
            t.Position = Fp(AfterTeleport + new Vector3(10f, 0f, 0f));
            _sim.Tick(NoCommands);

            SeedRenderCursor(currentTick: 3);       // TeleportTick 1 != CurrentTick - 1 == 2
            var go = NewCspView(entity, new Vector3(0f, 99f, 0f), out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position,
                        Vector3.Lerp(AfterTeleport, AfterTeleport + new Vector3(10f, 0f, 0f), 0.5f)),
                    Is.LessThan(Tolerance), "the stamp is one tick old — ordinary interpolation resumes");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// <c>TeleportTick</c> defaults to 0, and <c>0 == CurrentTick - 1</c> holds at <c>CurrentTick == 1</c>
        /// — so before the guard every entity that had never teleported took the teleport branch on the
        /// session's first rendered tick, and kept taking it for as long as the tick cursor sat there
        /// (a stall right after tick 0 widens that window). Pre-fix this snaps to the live pose instead of
        /// the midpoint.
        /// </summary>
        [Test]
        public void CspUnstampedEntity_AtTickOne_StillInterpolates()
        {
            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent { Position = Fp(BeforeTeleport) });
            _sim.SaveSnapshot();                    // tick 0 — the prev endpoint
            ref var t = ref _sim.Frame.Get<TransformComponent>(entity);
            t.Position = Fp(AfterTeleport);         // ordinary motion; TeleportTick left at its default 0
            _sim.Tick(NoCommands);

            SeedRenderCursor(currentTick: 1);       // 0 == CurrentTick - 1 — the ambiguity this pins
            var go = NewCspView(entity, new Vector3(0f, 99f, 0f), out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, Vector3.Lerp(BeforeTeleport, AfterTeleport, 0.5f)),
                    Is.LessThan(Tolerance),
                    "the default TeleportTick 0 means 'never teleported', not 'teleported during tick 0' — "
                    + "an entity that never teleported must still interpolate at CurrentTick == 1");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── The prev endpoint must reject a stranger in a recycled slot ───────

        private static readonly Vector3 OldOccupantPos = new Vector3(-50f, 0f, -50f);
        private static readonly Vector3 NewOccupantPos = new Vector3(50f, 0f, 50f);

        /// <summary>
        /// Same-tick recycle, which is what makes this reachable: EntityManager's free list is LIFO with
        /// immediate reuse, so the slot freed inside tick 1 is the first one the next Create takes. The
        /// snapshot for tick 1 was taken on entry, so it still holds the old occupant — alive, with a
        /// transform — under the index the new entity now owns.
        ///
        /// Both occupants keep TeleportTick 0 so the teleport guard sees no discontinuity and cannot mask this.
        /// </summary>
        private EntityRef BuildSameTickRecycleHistory()
        {
            var old = _sim.Frame.CreateEntity();
            _sim.Frame.Add(old, new TransformComponent { Position = Fp(OldOccupantPos) });
            _sim.SaveSnapshot();                    // tick 0
            _sim.Tick(NoCommands);

            _sim.SaveSnapshot();                    // tick 1 — old occupant alive on entry
            _sim.Frame.DestroyEntity(old);          // ...destroyed and replaced within tick 1
            var fresh = _sim.Frame.CreateEntity();
            _sim.Frame.Add(fresh, new TransformComponent { Position = Fp(NewOccupantPos) });
            _sim.Tick(NoCommands);

            Assert.That(fresh.Index, Is.EqualTo(old.Index),
                "precondition: the slot must actually be recycled, otherwise this fixture proves nothing");
            Assert.That(fresh.Version, Is.Not.EqualTo(old.Version),
                "precondition: recycling must bump the version — that is the only thing telling them apart");
            return fresh;
        }

        /// <summary>
        /// Pre-fix, `prev.Has` answers about the old occupant and the new view lerps out of
        /// its position — a full tick of travel across the map.
        /// </summary>
        [Test]
        public void CspPrevEndpoint_WithRecycledSlot_DoesNotLerpFromTheStranger()
        {
            var fresh = BuildSameTickRecycleHistory();
            SeedRenderCursor(currentTick: 2);
            var go = NewCspView(fresh, new Vector3(0f, 99f, 0f), out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, NewOccupantPos), Is.LessThan(Tolerance),
                    "with no valid prev endpoint the view renders its own live pose, uninterpolated");
                Assert.That(Vector3.Distance(go.transform.position,
                        Vector3.Lerp(OldOccupantPos, NewOccupantPos, 0.5f)), Is.GreaterThan(1f),
                    "...and never the midpoint between a stranger and itself");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The prev guard must not over-reject: an entity that legitimately existed in the previous
        /// snapshot still interpolates from it.
        /// </summary>
        [Test]
        public void CspPrevEndpoint_WithItsOwnHistory_StillInterpolates()
        {
            var entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(entity, new TransformComponent { Position = Fp(OldOccupantPos) });
            _sim.SaveSnapshot();                    // tick 0
            _sim.Tick(NoCommands);

            _sim.SaveSnapshot();                    // tick 1 — same entity, alive
            ref var t = ref _sim.Frame.Get<TransformComponent>(entity);
            t.Position = Fp(NewOccupantPos);
            _sim.Tick(NoCommands);

            SeedRenderCursor(currentTick: 2);
            var go = NewCspView(entity, new Vector3(0f, 99f, 0f), out var view);
            try
            {
                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position,
                        Vector3.Lerp(OldOccupantPos, NewOccupantPos, 0.5f)), Is.LessThan(Tolerance),
                    "the guard rejects other versions, not the entity's own history");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── Test-only concrete EntityView subclass ──
        private class ProbeEntityView : EntityView
        {
            public void SetFlags(ViewFlags flags) => _viewFlags = flags;

            // The test GameObject has no EntityViewComponent children; skip the base walk.
            public override void OnInitialize() { }
            public override void OnActivate(FrameRef frame) { }
            public override void OnLateUpdateView() { }
        }
    }
}
