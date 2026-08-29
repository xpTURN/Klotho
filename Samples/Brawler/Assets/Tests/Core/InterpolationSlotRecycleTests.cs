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
    /// Entity-slot recycling across the interpolation window (IMP103 V-6).
    ///
    /// `Frame.Has` and `GetReadOnly` forward `entity.Index` alone, so a handle whose slot was recycled
    /// answers about the NEW occupant and reports true. `Frame.TryRead`'s remarks state the rule that
    /// follows — call `Entities.IsAlive` first for any handle that did not come from the current tick —
    /// and the view layer has two places that break it, in opposite directions:
    ///
    ///   - <see cref="VerifiedFrameInterpolator"/> reads frames from the PAST with a handle taken at
    ///     spawn, so a NEW view renders the OLD occupant.
    ///   - <see cref="EntityView"/>'s live-frame gate reads the CURRENT frame with a handle from spawn,
    ///     so a STALE view renders the NEW occupant.
    ///
    /// Both are silent: the frame exists, the component is there, and the motion is smooth.
    /// </summary>
    [TestFixture]
    public class InterpolationSlotRecycleTests
    {
        private const int MaxEntities = 16;
        private const int TickIntervalMs = 50;

        private static readonly FieldInfo RenderTimeMsField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeInitField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);

        // The two occupants of the recycled slot, far enough apart that confusing them is unmistakable.
        private static readonly Vector3 OldOccupantPos = new Vector3(-50f, 0f, -50f);
        private static readonly Vector3 NewOccupantPos = new Vector3(50f, 0f, 50f);
        private static readonly Vector3 Fallback       = new Vector3(7f, 7f, 7f);

        // Tick dereferences the list, so an empty one rather than null.
        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        private EcsSimulation _sim;
        private KlothoEngine _engine;
        private IKLogger _logger;

        private EntityRef _old;   // index i, version 1 — destroyed
        private EntityRef _new;   // index i, version 2 — recycled into the same slot

        [SetUp]
        public void SetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("InterpolationSlotRecycleTests");

            _sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: TickIntervalMs);
            _engine = new KlothoEngine(
                new SimulationConfig
                {
                    TickIntervalMs = TickIntervalMs,
                    InterpolationDelayTicks = 2,
                    MaxRollbackTicks = 8,
                    MaxEntities = MaxEntities,
                },
                new SessionConfig());
            _engine.Initialize(_sim, _logger);

            BuildRecycleHistory();
        }

        /// <summary>
        /// Ring history: tick 0 holds the old occupant, tick 1 holds the freed slot, tick 2 holds the new
        /// one. That is the shape the interpolation window walks through after a short-lived entity dies
        /// and its slot is handed to the next spawn.
        /// </summary>
        private void BuildRecycleHistory()
        {
            _old = _sim.Frame.CreateEntity();
            _sim.Frame.Add(_old, new TransformComponent
            {
                Position = new FPVector3(
                    FP64.FromFloat(OldOccupantPos.x), FP64.FromFloat(OldOccupantPos.y), FP64.FromFloat(OldOccupantPos.z)),
            });
            _sim.SaveSnapshot();            // tick 0 — old occupant alive
            _sim.Tick(NoCommands);

            _sim.Frame.DestroyEntity(_old);
            _sim.SaveSnapshot();            // tick 1 — slot free
            _sim.Tick(NoCommands);

            _new = _sim.Frame.CreateEntity();
            _sim.Frame.Add(_new, new TransformComponent
            {
                Position = new FPVector3(
                    FP64.FromFloat(NewOccupantPos.x), FP64.FromFloat(NewOccupantPos.y), FP64.FromFloat(NewOccupantPos.z)),
            });
            _sim.SaveSnapshot();            // tick 2 — new occupant alive
            _sim.Tick(NoCommands);

            Assert.That(_new.Index, Is.EqualTo(_old.Index),
                "precondition: the slot must actually be recycled, otherwise this fixture proves nothing");
            Assert.That(_new.Version, Is.Not.EqualTo(_old.Version),
                "precondition: recycling must bump the version — that is the only thing distinguishing them");
        }

        // Pins the verified render clock so VerifiedBaseTick lands on the given tick with alpha 0.
        private void SeedVerifiedBase(int baseTick)
        {
            RenderTimeMsField.SetValue(_engine, (double)baseTick * TickIntervalMs);
            RenderTimeInitField.SetValue(_engine, true);
            LastVerifiedTickField.SetValue(_engine, baseTick + 1);
        }

        /// <summary>
        /// The V-6 case. The new occupant's view asks for a frame from before it existed; the slot there
        /// belongs to the old occupant. Version-blind, that returns the old occupant's position — this
        /// test fails on the pre-fix code with exactly that value.
        /// </summary>
        [Test]
        public void PastFrameWithOldOccupant_DoesNotRenderTheStranger()
        {
            SeedVerifiedBase(0);

            Vector3 got = VerifiedFrameInterpolator.InterpolatePosition(_new, _engine, Fallback);

            Assert.That(Vector3.Distance(got, OldOccupantPos), Is.GreaterThan(1f),
                "the new entity's view must never render the position of whatever previously held its slot");
            Assert.That(Vector3.Distance(got, Fallback), Is.LessThan(1e-3f),
                "with no frame legitimately holding this entity, the interpolator must fall back");
        }

        /// <summary>Rotation takes the same path and must reject the same frame.</summary>
        [Test]
        public void PastFrameWithOldOccupant_RotationAlsoFallsBack()
        {
            SeedVerifiedBase(0);
            var fallbackRot = Quaternion.Euler(0f, 123f, 0f);

            Quaternion got = VerifiedFrameInterpolator.InterpolateRotation(_new, _engine, fallbackRot);

            Assert.That(Quaternion.Angle(got, fallbackRot), Is.LessThan(0.1f));
        }

        /// <summary>
        /// The guard must not over-reject: once the window reaches the tick where the entity really
        /// exists, its own position comes back. Without this the fix could "pass" by refusing everything.
        /// </summary>
        [Test]
        public void FrameWhereTheEntityActuallyLives_StillInterpolates()
        {
            SeedVerifiedBase(2);

            Vector3 got = VerifiedFrameInterpolator.InterpolatePosition(_new, _engine, Fallback);

            Assert.That(Vector3.Distance(got, NewOccupantPos), Is.LessThan(1e-3f),
                "the guard rejects other versions, not the entity itself");
        }

        /// <summary>
        /// The live-frame mirror. A stale view — one whose entity died and whose slot the next spawn took
        /// — must stop updating rather than follow the new occupant around.
        /// </summary>
        [Test]
        public void StaleViewOnRecycledSlot_DoesNotFollowTheNewOccupant()
        {
            var go = new GameObject("StaleView");
            try
            {
                var view = go.AddComponent<ProbeEntityView>();
                view.Engine = _engine;
                view.EntityRef = _old;                  // the dead handle; its slot now holds _new
                go.transform.position = Fallback;       // sentinel: an early return leaves this alone

                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, NewOccupantPos), Is.GreaterThan(1f),
                    "a stale view must not render the transform of whatever took over its slot");
                Assert.That(Vector3.Distance(go.transform.position, Fallback), Is.LessThan(1e-3f),
                    "the live-frame gate must return before touching the transform");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>A live view on its own entity still updates — the gate is not a blanket refusal.</summary>
        [Test]
        public void LiveViewOnItsOwnEntity_StillUpdates()
        {
            var go = new GameObject("LiveView");
            try
            {
                var view = go.AddComponent<ProbeEntityView>();
                view.Engine = _engine;
                view.EntityRef = _new;
                go.transform.position = Fallback;

                view.InternalLateUpdateView();

                Assert.That(Vector3.Distance(go.transform.position, Fallback), Is.GreaterThan(1f),
                    "a live view must have been moved off the sentinel");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── Test-only concrete EntityView subclass ──
        private class ProbeEntityView : EntityView
        {
            // The test GameObject has no EntityViewComponent children; skip the base walk.
            public override void OnInitialize() { }
            public override void OnActivate(FrameRef frame) { }
            public override void OnLateUpdateView() { }
        }
    }
}
