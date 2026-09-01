using System.Collections.Generic;

using NUnit.Framework;

using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// The join tick on the replay path.
    ///
    /// <para><b>What was broken.</b> A replay session is built through the callbacks overload of
    /// <c>Initialize</c>, which delegated to the bare two-argument body — and both join subscriptions
    /// (<c>OnPlayerJoinedNotification</c> → participant slot + <c>OnPlayerJoinedWorld</c>, and
    /// <c>OnPlayerJoinedEntitlement</c> → the replay entitlement table) lived only in the NETWORK overload.
    /// So on playback a late joiner got no participant slot, the game's join-time seed never ran, and the
    /// bytes the file carried for that joiner were read by nobody. The recording was correct throughout;
    /// only the reader was missing.</para>
    ///
    /// <para><b>Why these gates use the callbacks overload.</b> The engine has three Initialize shapes and
    /// the subscription lands in exactly one of them. Existing replay tests build engines with the bare
    /// two-argument overload (it is shorter and no callbacks are needed), and a gate written that way is
    /// red before the fix and red after it — for the wrong reason. Every gate below goes through the same
    /// overload <c>KlothoSession</c> uses for replay.</para>
    ///
    /// <para><b>What they do NOT cover.</b> Whether a GAME seeds the right world state from those bytes —
    /// that needs a game, and lives in the Brawler server suite. These say the engine hands the join to
    /// the game at all.</para>
    /// </summary>
    [TestFixture]
    public class ReplayJoinWiringTests
    {
        /// <summary>Records what the engine handed the game at a join tick.</summary>
        private sealed class JoinRecordingCallbacks : ISimulationCallbacks
        {
            public readonly List<int> JoinedPlayers = new List<int>();
            public readonly List<byte[]> EntitlementsSeenByGame = new List<byte[]>();
            public IKlothoEngine LastEngine;

            public void RegisterSystems(EcsSimulation simulation) { }
            public void OnInitializeWorld(IKlothoEngine engine) { }
            public void OnPollInput(int playerId, int tick, ICommandSender sender) { }

            public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId)
            {
                LastEngine = engine;
                JoinedPlayers.Add(playerId);
                // Read it here, not after the tick: this is the moment the game seeds from, and the
                // whole point of the join-tick carrier is that the value is available AT this instant.
                EntitlementsSeenByGame.Add(engine.GetPlayerEntitlement(playerId));
            }
        }

        // maxEntities must match every other fixture in this assembly: the component layout freezes
        // process-wide on first use, and a different number throws instead of failing a gate.
        private const int MaxEntities = 64;

        private static EcsSimulation NewSim() => new EcsSimulation(MaxEntities, maxRollbackTicks: 4, deltaTimeMs: 50);

        /// <summary>An engine wired the way a replay session wires one: callbacks, no network service.</summary>
        private static KlothoEngine NewReplayShapedEngine(EcsSimulation sim, ISimulationCallbacks callbacks)
        {
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, new LogCapture(), callbacks);
            engine.SetCommandFactory(new CommandFactory());
            return engine;
        }

        /// <summary>
        /// Executes one join through the simulation, exactly as playback does: the recorded command is
        /// re-executed inside <c>EcsSimulation.Tick</c>, which raises the entitlement adoption and then the
        /// join notification. No replay file is involved — the file's only role is delivering this command.
        /// </summary>
        private static void TickJoin(EcsSimulation sim, int joinedPlayerId, byte[] entitlement)
        {
            var cmd = new PlayerJoinCommand { JoinedPlayerId = joinedPlayerId, Entitlement = entitlement };
            sim.Tick(new List<ICommand> { cmd });
        }

        private static int CountParticipantSlots(Frame frame, int playerId)
        {
            int n = 0;
            var filter = frame.Filter<SessionParticipantComponent>();
            while (filter.Next(out var e))
                if (frame.GetReadOnly<SessionParticipantComponent>(e).PlayerId == playerId)
                    n++;
            return n;
        }

        // ── J-1 ────────────────────────────────────────────────────────────

        [Test] // Without the slot the joiner is not a participant: every per-participant pass skips it and
               // the entity cursor drifts from the live match, which is a divergence no verdict reports.
        public void ReplayShapedEngine_CreatesTheParticipantSlotAtTheJoinTick()
        {
            var sim = NewSim();
            var callbacks = new JoinRecordingCallbacks();
            NewReplayShapedEngine(sim, callbacks);

            TickJoin(sim, joinedPlayerId: 3, entitlement: null);

            Assert.AreEqual(1, CountParticipantSlots(sim.Frame, 3),
                "the join notification did not reach the engine on a replay-shaped session");
        }

        // ── J-2 ────────────────────────────────────────────────────────────

        [Test] // The frame is the gate (create-iff-no-slot), so a re-executed join must not double up.
               // Seeking a replay across the join tick re-runs that tick for real, so this is not academic.
        public void RepeatedJoinExecution_SeedsTheWorldExactlyOnce()
        {
            var sim = NewSim();
            var callbacks = new JoinRecordingCallbacks();
            NewReplayShapedEngine(sim, callbacks);

            TickJoin(sim, joinedPlayerId: 3, entitlement: null);
            TickJoin(sim, joinedPlayerId: 3, entitlement: null);

            Assert.AreEqual(1, CountParticipantSlots(sim.Frame, 3), "duplicate participant slot");
            Assert.AreEqual(1, callbacks.JoinedPlayers.Count,
                $"OnPlayerJoinedWorld fired {callbacks.JoinedPlayers.Count} times for one joiner");
        }

        // ── J-3 ────────────────────────────────────────────────────────────

        [Test] // The bytes must be adopted BEFORE the world seed runs — that ordering is the contract
               // (EcsSimulation raises the entitlement event ahead of the join notification).
        public void JoinCommandEntitlement_IsReadableByTheGameAtTheJoinTick()
        {
            var sim = NewSim();
            var callbacks = new JoinRecordingCallbacks();
            var engine = NewReplayShapedEngine(sim, callbacks);

            var issued = new byte[] { 0x0f, 0, 0, 0, 0xff, 0, 0, 0, 0x01, 0, 0, 0 };
            TickJoin(sim, joinedPlayerId: 3, entitlement: issued);

            Assert.AreEqual(1, callbacks.EntitlementsSeenByGame.Count, "the game was never handed the join");
            Assert.AreEqual(issued, callbacks.EntitlementsSeenByGame[0],
                "the game read different bytes than the join command carried");
            Assert.AreEqual(issued, engine.GetPlayerEntitlement(3),
                "the joiner's verified data is not readable after the join tick");
        }

        [Test] // "Nothing was issued" is a real answer, not a missing one: it must not resurrect a stale
               // table entry for the same id from an earlier session of this engine.
        public void JoinWithoutEntitlement_LeavesTheJoinerWithNone()
        {
            var sim = NewSim();
            var callbacks = new JoinRecordingCallbacks();
            var engine = NewReplayShapedEngine(sim, callbacks);

            TickJoin(sim, joinedPlayerId: 3, entitlement: null);

            Assert.IsNull(engine.GetPlayerEntitlement(3));
            Assert.AreEqual(1, callbacks.JoinedPlayers.Count);
        }

        // ── J-5 ────────────────────────────────────────────────────────────

        [Test] // Scope guard, not a correctness claim. The spectator path builds its engine through the
               // bare overload and takes its world from full states, never from a command stream — so the
               // join wiring deliberately stops at the callbacks overload. If someone moves the
               // subscription down into the bare body, this turns red and that is the conversation.
        public void BareOverload_StaysOutOfTheJoinPath()
        {
            var sim = NewSim();
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, new LogCapture());
            engine.SetCommandFactory(new CommandFactory());

            TickJoin(sim, joinedPlayerId: 3, entitlement: new byte[] { 1, 2, 3, 4 });

            Assert.AreEqual(0, CountParticipantSlots(sim.Frame, 3),
                "the bare overload subscribed to the join path — see D-1, this changes the spectator too");
            Assert.IsNull(engine.GetPlayerEntitlement(3));
        }
    }
}
