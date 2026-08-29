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
    /// Teleports on the snapshot interpolation path (IMP103 V-5). Two independent defects:
    ///
    ///   1. ApplyTransform's teleport branch snapped to UninterpolatedPosition — the PREDICTED frame.
    ///      The snapshot path renders the verified window, which has not reached the teleport tick when
    ///      the flag fires, so that threw the view forward by the whole render delay and dropped it back
    ///      on the next frame.
    ///   2. The interpolator lerped straight across a teleport, walking the entity through positions it
    ///      never occupied. That one is independent of EnableErrorCorrection: the Teleported flag needs
    ///      error correction to exist at all, but TeleportTick rides on TransformComponent, so the bogus
    ///      interpolation happened even with EC off.
    /// </summary>
    [TestFixture]
    public class TeleportSnapshotInterpolationTests
    {
        private const int MaxEntities = 16;
        private const int TickIntervalMs = 50;
        private const float Tolerance = 1e-3f;

        private static readonly FieldInfo RenderTimeMsField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeMs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RenderTimeInitField = typeof(KlothoEngine)
            .GetField("_verifiedRenderTimeInitialized", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        // Before and after a teleport, far enough apart that any midpoint is unmistakable.
        private static readonly Vector3 BeforeTeleport = new Vector3(-40f, 0f, 0f);
        private static readonly Vector3 AfterTeleport  = new Vector3(40f, 0f, 0f);
        private static readonly Vector3 Fallback       = new Vector3(0f, 99f, 0f);

        private EcsSimulation _sim;
        private KlothoEngine _engine;
        private EntityRef _entity;

        [SetUp]
        public void SetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            var logger = factory.CreateLogger("TeleportSnapshotInterpolationTests");

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
            _engine.Initialize(_sim, logger);

            BuildTeleportHistory();
        }

        /// <summary>
        /// Ring history: tick 0 holds the pre-teleport pose, tick 1 the post-teleport one with TeleportTick
        /// stamped — the shape a game produces when it teleports something (RespawnSystem and
        /// PlatformerCommandSystem both write `transform.TeleportTick = frame.Tick`).
        /// </summary>
        private void BuildTeleportHistory()
        {
            _entity = _sim.Frame.CreateEntity();
            _sim.Frame.Add(_entity, Transform(BeforeTeleport, teleportTick: 0));
            _sim.SaveSnapshot();                    // tick 0 — before
            _sim.Tick(NoCommands);

            ref var t = ref _sim.Frame.Get<TransformComponent>(_entity);
            t.Position = ToFp(AfterTeleport);
            t.TeleportTick = 1;                     // teleported ON tick 1
            _sim.SaveSnapshot();                    // tick 1 — after
            _sim.Tick(NoCommands);
        }

        private static FPVector3 ToFp(Vector3 v)
            => new FPVector3(FP64.FromFloat(v.x), FP64.FromFloat(v.y), FP64.FromFloat(v.z));

        private static TransformComponent Transform(Vector3 pos, int teleportTick)
            => new TransformComponent { Position = ToFp(pos), TeleportTick = teleportTick };

        // Pins the verified render clock at baseTick + alpha.
        private void SeedRenderClock(int baseTick, double alpha)
        {
            RenderTimeMsField.SetValue(_engine, (baseTick + alpha) * TickIntervalMs);
            RenderTimeInitField.SetValue(_engine, true);
            LastVerifiedTickField.SetValue(_engine, baseTick + 2);
        }

        // ── Piece 2: the interpolator must not lerp across a teleport ──

        /// <summary>
        /// The window straddles the teleport at mid-alpha. Lerping would put the entity at the midpoint —
        /// a place it never was. It must hold the pre-teleport pose until the window advances past the
        /// teleport tick, which is where the jump belongs.
        /// </summary>
        [Test]
        public void WindowStraddlingATeleport_HoldsPreTeleportInsteadOfLerping()
        {
            SeedRenderClock(baseTick: 0, alpha: 0.5);

            Vector3 got = VerifiedFrameInterpolator.InterpolatePosition(_entity, _engine, Fallback);

            Assert.That(Vector3.Distance(got, BeforeTeleport), Is.LessThan(Tolerance),
                "a teleport is instantaneous — the render must not visit the midpoint between the two poses");
            Assert.That(Vector3.Distance(got, Vector3.Lerp(BeforeTeleport, AfterTeleport, 0.5f)),
                Is.GreaterThan(1f), "...and specifically not the lerped midpoint the old code produced");
        }

        /// <summary>Even at alpha near 1 the pre-teleport pose holds — the jump is on the tick boundary.</summary>
        [Test]
        public void NearTheEndOfTheWindow_StillHoldsPreTeleport()
        {
            SeedRenderClock(baseTick: 0, alpha: 0.98);

            Vector3 got = VerifiedFrameInterpolator.InterpolatePosition(_entity, _engine, Fallback);

            Assert.That(Vector3.Distance(got, BeforeTeleport), Is.LessThan(Tolerance));
        }

        /// <summary>Rotation takes the same branch and must hold as well.</summary>
        [Test]
        public void WindowStraddlingATeleport_RotationHoldsToo()
        {
            // Give the two frames different yaws so a lerp would be visible.
            ref var t0 = ref FrameAt(0).Get<TransformComponent>(_entity);
            t0.Rotation = FP64.FromFloat(0f);
            ref var t1 = ref FrameAt(1).Get<TransformComponent>(_entity);
            t1.Rotation = FP64.FromFloat(Mathf.PI * 0.5f);   // 90 deg
            SeedRenderClock(baseTick: 0, alpha: 0.5);

            Quaternion got = VerifiedFrameInterpolator.InterpolateRotation(_entity, _engine, Quaternion.identity);

            Assert.That(Quaternion.Angle(got, Quaternion.identity), Is.LessThan(0.5f),
                "holding ta means the pre-teleport yaw, not the 45 deg a lerp would give");
        }

        /// <summary>
        /// The guard must not swallow ordinary motion: with TeleportTick equal on both frames the lerp
        /// runs as before. Without this the fix could "pass" by never interpolating at all.
        /// </summary>
        [Test]
        public void WindowWithoutATeleport_StillInterpolates()
        {
            ref var t1 = ref FrameAt(1).Get<TransformComponent>(_entity);
            t1.TeleportTick = 0;                    // same as frame 0 — ordinary movement
            SeedRenderClock(baseTick: 0, alpha: 0.5);

            Vector3 got = VerifiedFrameInterpolator.InterpolatePosition(_entity, _engine, Fallback);

            Assert.That(Vector3.Distance(got, Vector3.Lerp(BeforeTeleport, AfterTeleport, 0.5f)),
                Is.LessThan(Tolerance), "the gate keys on TeleportTick, not on distance");
        }

        private Frame FrameAt(int tick)
        {
            Assert.IsTrue(_engine.TryGetFrameAtTick(tick, out var f), $"ring must hold tick {tick}");
            return f;
        }

        // ── Piece 1: the teleport branch must not snap a snapshot view to the predicted pose ──

        /// <summary>
        /// The snapshot path renders the verified window; the predicted pose is the render delay ahead of
        /// it. Snapping there on the teleport frame is the forward pop, and it is followed by a backward
        /// one as soon as the flag clears.
        /// </summary>
        [Test]
        public void SnapshotView_TeleportFrame_DoesNotSnapToThePredictedPose()
        {
            var go = new GameObject("SnapshotView");
            try
            {
                var view = go.AddComponent<ProbeEntityView>();
                view.SetFlags(ViewFlags.EnableSnapshotInterpolation);

                var param = new UpdatePositionParameter
                {
                    Teleported             = true,
                    UninterpolatedPosition = AfterTeleport,    // predicted: already teleported
                    UninterpolatedRotation = Quaternion.identity,
                    NewPosition            = BeforeTeleport,   // verified window: not there yet
                    NewRotation            = Quaternion.identity,
                };
                view.Apply(ref param);

                Assert.That(Vector3.Distance(go.transform.position, AfterTeleport), Is.GreaterThan(1f),
                    "snapping a snapshot view to the predicted pose jumps it forward by the render delay");
                Assert.That(Vector3.Distance(go.transform.position, BeforeTeleport), Is.LessThan(Tolerance),
                    "it must keep rendering what its own path renders");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// The CSP path keeps the snap — that is where the flag is meaningful, because there the rendered
        /// basis and the predicted frame are the same tick.
        /// </summary>
        [Test]
        public void CspView_TeleportFrame_StillSnapsToTheUninterpolatedPose()
        {
            var go = new GameObject("CspView");
            try
            {
                var view = go.AddComponent<ProbeEntityView>();
                view.SetFlags(ViewFlags.None);

                var param = new UpdatePositionParameter
                {
                    Teleported             = true,
                    UninterpolatedPosition = AfterTeleport,
                    UninterpolatedRotation = Quaternion.identity,
                    NewPosition            = BeforeTeleport,
                    NewRotation            = Quaternion.identity,
                    ErrorVisualVector      = new Vector3(5f, 0f, 0f),   // must be bypassed
                    ErrorVisualQuaternion  = Quaternion.identity,
                };
                view.Apply(ref param);

                Assert.That(Vector3.Distance(go.transform.position, AfterTeleport), Is.LessThan(Tolerance),
                    "the CSP path must still snap, and must still bypass the error visual");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── Test-only concrete EntityView subclass ──
        private class ProbeEntityView : EntityView
        {
            public void SetFlags(ViewFlags flags) => _viewFlags = flags;
            public void Apply(ref UpdatePositionParameter param) => ApplyTransform(ref param);

            public override void OnInitialize() { }
            public override void OnActivate(FrameRef frame) { }
        }
    }
}
