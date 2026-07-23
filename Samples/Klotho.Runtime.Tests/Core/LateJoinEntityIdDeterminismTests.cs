using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Late-join SessionParticipantComponent slot-creation determinism.
    ///
    /// A late-join guest's _activePlayerIds already contains its own id (Initialize seeds it
    /// from the live roster), so the old `!_activePlayerIds.Contains` slot gate evaluated false
    /// on the joiner and true on peers — the joiner SKIPPED its slot CreateEntity, an off-by-one
    /// entity cursor vs peers → permanent hash desync (3-player P2P, observed in Brawler logs:
    /// p2 spawned entity 12 while host/peer spawned 13). The fix gates the slot CreateEntity on
    /// the hash-locked FRAME (does a SessionParticipantComponent for this player already exist?),
    /// so every node creates it exactly once regardless of _activePlayerIds skew.
    ///
    /// These drive HandlePlayerJoinedNotification on a real EcsSimulation-backed engine via
    /// reflection (the KlothoTestHarness uses a mock TestSimulation that never exercises the
    /// ECS slot path — same KlothoEngine+EcsSimulation pattern as CommandOwnershipTests).
    /// </summary>
    [TestFixture]
    public class LateJoinEntityIdDeterminismTests
    {
        private static readonly System.Type _engineType = typeof(KlothoEngine);

        private static readonly MethodInfo _handlePlayerJoinedMethod =
            _engineType.GetMethod("HandlePlayerJoinedNotification",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _activePlayerIdsField =
            _engineType.GetField("_activePlayerIds", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            _logger = factory.CreateLogger("LateJoinEntityIdDeterminismTests");
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private KlothoEngine MakeEngine()
        {
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(new EcsSimulation(maxEntities: 16, maxRollbackTicks: 8, deltaTimeMs: 50), _logger);
            return engine;
        }

        private static void PlayerJoined(KlothoEngine engine, int playerId)
            => _handlePlayerJoinedMethod.Invoke(engine, new object[] { playerId });

        private static List<int> ActivePlayerIds(KlothoEngine engine)
            => (List<int>)_activePlayerIdsField.GetValue(engine);

        // Count live SessionParticipantComponent slots for a player (dense ComponentsSpan[0..Count)).
        private static int SlotCount(KlothoEngine engine, int playerId)
        {
            var sim = (EcsSimulation)engine.Simulation;
            var storage = sim.Frame.GetStorage<SessionParticipantComponent>();
            var comps = storage.ComponentsSpan;
            int n = 0;
            for (int i = 0; i < storage.Count; i++)
                if (comps[i].PlayerId == playerId) n++;
            return n;
        }

        private static int TotalSlots(KlothoEngine engine)
            => ((EcsSimulation)engine.Simulation).Frame.GetStorage<SessionParticipantComponent>().Count;

        // ── (1) Slot created once + gate idempotent ──────────────────────

        [Test]
        public void PlayerJoined_CreatesSlotExactlyOnce()
        {
            var engine = MakeEngine();

            PlayerJoined(engine, 5);
            Assert.AreEqual(1, SlotCount(engine, 5), "join must create exactly one slot for the player");
            Assert.AreEqual(1, TotalSlots(engine), "no extra slots");

            // Re-applying the same join (e.g. rollback re-sim) must NOT create a second slot —
            // the frame-gate sees the existing SessionParticipantComponent and skips.
            PlayerJoined(engine, 5);
            Assert.AreEqual(1, SlotCount(engine, 5), "frame-gate idempotency — no duplicate slot on re-apply");
            Assert.AreEqual(1, TotalSlots(engine), "still exactly one slot");
        }

        // ── (2) THE regression gate — roster already contains the joiner ──

        [Test]
        public void PlayerJoined_WhenRosterAlreadyContainsSelf_StillCreatesSlot()
        {
            var engine = MakeEngine();

            // Reproduce the late-joiner shape: _activePlayerIds already contains the joining id
            // (Initialize seeded it from the live roster) before its PlayerJoinCommand is processed.
            ActivePlayerIds(engine).Add(2);

            PlayerJoined(engine, 2);

            // Fix: the slot is gated on the FRAME (no slot yet) → created. Pre-fix the old
            // `!_activePlayerIds.Contains(2)` gate was false here → slot SKIPPED → off-by-one
            // entity cursor vs peers (the desync).
            Assert.AreEqual(1, SlotCount(engine, 2),
                "joiner must create its slot even when _activePlayerIds already contains it " +
                "(pre-fix the engine-roster gate short-circuited → off-by-one entity cursor)");
        }

        // ── (3) Distinct players → distinct slots, sequential ────────────

        [Test]
        public void PlayerJoined_DistinctPlayers_OneSlotEach()
        {
            var engine = MakeEngine();

            PlayerJoined(engine, 0);
            PlayerJoined(engine, 1);
            PlayerJoined(engine, 2);

            Assert.AreEqual(3, TotalSlots(engine), "three distinct players → three slots");
            Assert.AreEqual(1, SlotCount(engine, 0));
            Assert.AreEqual(1, SlotCount(engine, 1));
            Assert.AreEqual(1, SlotCount(engine, 2));
        }
    }
}
