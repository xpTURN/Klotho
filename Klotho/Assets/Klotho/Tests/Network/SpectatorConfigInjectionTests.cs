using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Regression tests for SimulationConfig / SessionConfig injection into the spectator engine.
    /// </summary>
    /// <remarks>
    /// Guarantees both the mechanism by which stateHash diverges when the server and spectator simulate
    /// with different <c>deltaTimeMs</c>, and the spec that stateHash converges when they simulate with the same value.
    /// </remarks>
    [TestFixture]
    public class SpectatorConfigInjectionTests
    {
        private const int MaxEntities = 32;
        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        // ── Helpers ──────────────────────────────────────────

        private EcsSimulation CreateSimulationWithMover(int deltaTimeMs)
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: deltaTimeMs);
            sim.Initialize();

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new MovementComponent
            {
                MoveSpeed = FP64.FromInt(5),
                TargetPosition = new FPVector3(FP64.FromInt(100), FP64.Zero, FP64.Zero),
                IsMoving = true
            });
            sim.Frame.Add(entity, new TransformComponent
            {
                Position = FPVector3.Zero,
                Scale = FPVector3.One
            });
            sim.Frame.Add(entity, new VelocityComponent());
            sim.AddSystem(new MovementSystem(), SystemPhase.Update);

            return sim;
        }

        // ── deltaTimeMs mismatch -> stateHash diverges ──

        /// <summary>
        /// Guarantees that stateHash does not match when the server and spectator run the same number
        /// of ticks with different <c>deltaTimeMs</c>.
        /// </summary>
        /// <remarks>
        /// If this test fails, the determinism model of EcsSimulation has been changed to no longer
        /// depend on deltaTime, in which case the related analysis and diagnostics should be revisited.
        /// </remarks>
        [Test]
        public void DeltaTimeMismatch_StateHashDiverges()
        {
            const int serverDeltaMs = 50;
            const int spectatorDeltaMs_BUGGY = 25;

            var serverSim = CreateSimulationWithMover(serverDeltaMs);
            var spectatorSim = CreateSimulationWithMover(spectatorDeltaMs_BUGGY);

            for (int i = 0; i < 10; i++)
            {
                serverSim.Tick(NoCommands);
                spectatorSim.Tick(NoCommands);
            }

            Assert.AreNotEqual(
                serverSim.GetStateHash(), spectatorSim.GetStateHash(),
                "Different deltaTimeMs values must produce different stateHash values.");
        }

        /// <summary>
        /// Guarantees that stateHash matches when the server and spectator run the same number of ticks
        /// with the same <c>deltaTimeMs</c>.
        /// </summary>
        [Test]
        public void DeltaTimeMatched_StateHashConverges()
        {
            const int serverDeltaMs = 50;
            const int spectatorDeltaMs_FIXED = 50;

            var serverSim = CreateSimulationWithMover(serverDeltaMs);
            var spectatorSim = CreateSimulationWithMover(spectatorDeltaMs_FIXED);

            for (int i = 0; i < 10; i++)
            {
                serverSim.Tick(NoCommands);
                spectatorSim.Tick(NoCommands);
            }

            Assert.AreEqual(
                serverSim.GetStateHash(), spectatorSim.GetStateHash(),
                "The same deltaTimeMs must always produce the same stateHash.");
        }

        // ── SpectatorAcceptMessage serialization round-trip ──

        /// <summary>
        /// Verifies that the 5 top-level + 13 SimulationConfig fields of <see cref="SpectatorAcceptMessage"/>
        /// are all preserved across a serialization round-trip.
        /// </summary>
        [Test]
        public void SpectatorAcceptMessage_SimulationConfigRoundTrip_PreservesAllFields()
        {
            var simConfig = new SimulationConfig
            {
                TickIntervalMs = 50,
                InputDelayTicks = 4,
                MaxRollbackTicks = 50,
                SyncCheckInterval = 30,
                UsePrediction = true,
                MaxEntities = 256,
                Mode = NetworkMode.ServerDriven,
                HardToleranceMs = 200,
                InputResendIntervalMs = 150,
                MaxUnackedInputs = 30,
                ServerSnapshotRetentionTicks = 0,
                EventDispatchWarnMs = 5,
                TickDriftWarnMultiplier = 2,
            };

            var original = new SpectatorAcceptMessage
            {
                SpectatorId = -7,
                RandomSeed = 12345,
                CurrentTick = 100,
                LastVerifiedTick = 99,
            };
            original.PlayerIds.Add(0);
            original.PlayerIds.Add(1);
            original.CopySimulationConfigFrom(simConfig);

            var restored = RoundTrip(original);

            // 5 top-level
            Assert.AreEqual(original.SpectatorId, restored.SpectatorId);
            Assert.AreEqual(original.RandomSeed, restored.RandomSeed);
            Assert.AreEqual(original.CurrentTick, restored.CurrentTick);
            Assert.AreEqual(original.LastVerifiedTick, restored.LastVerifiedTick);
            CollectionAssert.AreEqual(original.PlayerIds, restored.PlayerIds);

            // 13 SimulationConfig
            Assert.AreEqual(original.TickIntervalMs, restored.TickIntervalMs);
            Assert.AreEqual(original.InputDelayTicks, restored.InputDelayTicks);
            Assert.AreEqual(original.MaxRollbackTicks, restored.MaxRollbackTicks);
            Assert.AreEqual(original.SyncCheckInterval, restored.SyncCheckInterval);
            Assert.AreEqual(original.UsePrediction, restored.UsePrediction);
            Assert.AreEqual(original.MaxEntities, restored.MaxEntities);
            Assert.AreEqual(original.Mode, restored.Mode);
            Assert.AreEqual(original.HardToleranceMs, restored.HardToleranceMs);
            Assert.AreEqual(original.InputResendIntervalMs, restored.InputResendIntervalMs);
            Assert.AreEqual(original.MaxUnackedInputs, restored.MaxUnackedInputs);
            Assert.AreEqual(original.ServerSnapshotRetentionTicks, restored.ServerSnapshotRetentionTicks);
            Assert.AreEqual(original.EventDispatchWarnMs, restored.EventDispatchWarnMs);
            Assert.AreEqual(original.TickDriftWarnMultiplier, restored.TickDriftWarnMultiplier);
        }

        // TODO: Add round-trip coverage for the 9 SessionConfig fields (MaxPlayers, AllowLateJoin, ReconnectTimeoutMs,
        //   ReconnectMaxRetries, LateJoinDelayTicks, ResyncMaxRetries,
        //   DesyncThresholdForResync, CountdownDurationMs, CatchupMaxTicksPerFrame)
        //   plus ToSessionConfig().RandomSeed == top-level.RandomSeed synchronization verification.

        private static T RoundTrip<T>(T message) where T : NetworkMessageBase, new()
        {
            int size = message.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            message.Serialize(ref writer);

            var restored = new T();
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            restored.Deserialize(ref reader);
            return restored;
        }
    }
}
