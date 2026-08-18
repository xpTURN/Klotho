using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Helper.Tests
{
    /// <summary>
    /// A real <see cref="EcsSimulation"/> with real systems, driven forward and rolled back.
    ///
    /// <para>The existing rollback harnesses cannot serve this: <c>KlothoTestHarness</c>,
    /// <c>ReplayFidelityHarness</c> and <c>SdReplayHarness</c> all drive an <c>ISimulation</c>
    /// STUB, so no <c>ISystem</c> runs inside them and nothing that lives in a system can be
    /// tested. Anything asserting "the systems agree across a rollback" needs the real
    /// simulation, and that is what this is.</para>
    ///
    /// <para>No network and no engine. The properties under test — a per-tick invariant holding,
    /// and a re-executed tick landing on the same state as the first execution — are properties of
    /// the simulation, and driving them directly makes the failure legible: the harness rolls back
    /// and replays explicitly rather than provoking it through a delivery schedule.</para>
    ///
    /// <para><b>Snapshots.</b> <see cref="Tick"/> saves one before each tick, which is what a
    /// rollback restores to. That mirrors the engine, whose ring buffer is keyed by
    /// <c>frame.Tick</c> itself — so a restore to T leaves the simulation about to execute T.</para>
    /// </summary>
    internal sealed class EcsRollbackHarness
    {
        private readonly List<ICommand> _noCommands = new List<ICommand>();

        public EcsSimulation Simulation { get; }

        public int CurrentTick => Simulation.CurrentTick;

        public EcsRollbackHarness(int maxEntities = 64, int maxRollbackTicks = 16, int deltaTimeMs = 25)
        {
            Simulation = new EcsSimulation(maxEntities, maxRollbackTicks, deltaTimeMs);
        }

        /// <summary>Registers a system. Must happen before <see cref="Initialize"/>.</summary>
        public EcsRollbackHarness AddSystem(object system, SystemPhase phase = SystemPhase.Update)
        {
            Simulation.AddSystem(system, phase);
            return this;
        }

        public EcsRollbackHarness Initialize()
        {
            Simulation.Initialize();
            return this;
        }

        /// <summary>Saves a snapshot of the pre-tick state, then executes one tick.</summary>
        public void Tick(params ICommand[] commands)
        {
            Simulation.SaveSnapshot();
            Simulation.Tick(commands == null || commands.Length == 0
                ? _noCommands : new List<ICommand>(commands));
        }

        public void TickTo(int targetTick)
        {
            while (CurrentTick < targetTick)
                Tick();
        }

        public long Hash() => Simulation.GetStateHash();

        /// <summary>
        /// Runs <paramref name="ticks"/> ticks and returns the post-tick hash of each, indexed
        /// from the tick the run started on.
        /// </summary>
        public long[] RunAndRecord(int ticks)
        {
            var hashes = new long[ticks];
            for (int i = 0; i < ticks; i++)
            {
                Tick();
                hashes[i] = Hash();
            }
            return hashes;
        }

        /// <summary>
        /// Rolls back to <paramref name="toTick"/> and re-executes to <paramref name="head"/>,
        /// returning the post-tick hash of each re-executed tick. Comparing these against the
        /// original run is the "a replay lands where the first run did" assertion — the one a
        /// derived-state bug breaks, because the derived state does not roll back with the frame.
        /// </summary>
        public long[] RollbackAndReplay(int toTick, int head)
        {
            int nearest = Simulation.GetNearestRollbackTick(toTick);
            Assert.AreEqual(toTick, nearest,
                $"no snapshot at tick {toTick} (nearest {nearest}) — widen maxRollbackTicks or roll back less far");

            Simulation.Rollback(toTick);
            Assert.AreEqual(toTick, CurrentTick, "rollback did not restore frame.Tick");

            var hashes = new long[head - toTick];
            for (int i = 0; i < hashes.Length; i++)
            {
                Tick();
                hashes[i] = Hash();
            }
            return hashes;
        }

        /// <summary>
        /// Forward run, then a rollback that re-executes the tail, asserting the two agree tick by
        /// tick. Reports the first tick that differs rather than just "hashes differ".
        /// </summary>
        public void AssertReplayMatchesForward(int forwardTicks, int rollbackTo)
        {
            long[] forward = RunAndRecord(forwardTicks);
            int head = CurrentTick;
            long[] replay = RollbackAndReplay(rollbackTo, head);

            int offset = rollbackTo - (head - forwardTicks);
            for (int i = 0; i < replay.Length; i++)
            {
                Assert.AreEqual(forward[offset + i], replay[i],
                    $"tick {rollbackTo + i + 1} differs between the first execution and the replay — "
                    + "state that does not roll back with the frame is the usual cause");
            }
        }
    }
}
