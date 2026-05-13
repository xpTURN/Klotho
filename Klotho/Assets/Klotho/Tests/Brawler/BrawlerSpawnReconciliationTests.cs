using System.Reflection;
using NUnit.Framework;

using Brawler;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Tests.Brawler
{
    /// <summary>
    /// BrawlerSimulationCallbacks spawn-reconciliation unit tests — verifies that the
    /// state-driven spawn cooldown latch reacts correctly to (a) FullState resync and
    /// (b) server-issued Duplicate rejection feedback. Heavier ECS-frame scenarios
    /// (LateJoin / Reconnect / OnPollInput state-driven query / retry interval) are
    /// exercised by the manual fault-injection scenario matrix and integration tests.
    /// </summary>
    [TestFixture]
    public class BrawlerSpawnReconciliationTests
    {
        private const string LastAttemptField = "_lastSpawnAttemptTick";
        private const int SeedTick = 100;

        private static FieldInfo GetLastAttemptField() =>
            typeof(BrawlerSimulationCallbacks).GetField(
                LastAttemptField, BindingFlags.NonPublic | BindingFlags.Instance);

        private static MethodInfo GetHandleCommandRejected() =>
            typeof(BrawlerSimulationCallbacks).GetMethod(
                "HandleCommandRejected", BindingFlags.NonPublic | BindingFlags.Instance);

        private static BrawlerSimulationCallbacks NewCallbacks() =>
            // Constructor only assigns dependencies to fields. The reconciliation paths under
            // test (OnResyncCompleted / HandleCommandRejected) do not dereference any of them,
            // so nulls are safe and avoid heavy NavMesh / InputCapture scaffolding.
            new BrawlerSimulationCallbacks(
                input: null, logger: null, colliders: null, navMesh: null,
                maxPlayers: 4, botCount: 0, dataAssets: null);

        [Test]
        public void OnResyncCompleted_ResetsLastSpawnAttemptTick()
        {
            var callbacks = NewCallbacks();
            var field = GetLastAttemptField();
            field.SetValue(callbacks, SeedTick);

            callbacks.OnResyncCompleted(0);

            Assert.AreEqual(-1, (int)field.GetValue(callbacks),
                "FullState resync must invalidate the previous spawn-attempt tick");
        }

        [Test]
        public void HandleCommandRejected_DuplicateSpawn_ClearsCooldown()
        {
            var callbacks = NewCallbacks();
            var field = GetLastAttemptField();
            field.SetValue(callbacks, SeedTick);

            GetHandleCommandRejected().Invoke(callbacks, new object[]
            {
                /* tick      */ SeedTick + 1,
                /* cmdTypeId */ SpawnCharacterCommand.TYPE_ID,
                /* reason    */ RejectionReason.Duplicate,
            });

            Assert.AreEqual(-1, (int)field.GetValue(callbacks),
                "Duplicate-rejected spawn must clear cooldown so the state-driven query re-evaluates");
        }

        [Test]
        public void HandleCommandRejected_NonSpawnCommand_PreservesCooldown()
        {
            var callbacks = NewCallbacks();
            var field = GetLastAttemptField();
            field.SetValue(callbacks, SeedTick);

            // Move command rejected as Duplicate (synthetic — Move never produces Duplicate today,
            // but the handler must filter on cmdTypeId regardless to stay forward-compatible with
            // future game-layer reject sites).
            GetHandleCommandRejected().Invoke(callbacks, new object[]
            {
                SeedTick + 1, /* MoveInputCommand */ 100, RejectionReason.Duplicate,
            });

            Assert.AreEqual(SeedTick, (int)field.GetValue(callbacks),
                "Non-spawn cmd rejection must not clear the spawn cooldown");
        }

        [Test]
        public void HandleCommandRejected_SpawnPastTick_ClearsCooldownAndEscalatesLead()
        {
            var callbacks = NewCallbacks();
            var field = GetLastAttemptField();
            field.SetValue(callbacks, SeedTick);

            // ISS-06 policy: PastTick reject for spawn → clear cooldown + escalate _extraSpawnDelay
            // (by SPAWN_DELAY_STEP) so the next OnPollInput re-issues with a larger lead margin.
            // Burst-resend is bounded by the SPAWN_DELAY_MAX cap, not by preserving the cooldown.
            GetHandleCommandRejected().Invoke(callbacks, new object[]
            {
                SeedTick + 1, SpawnCharacterCommand.TYPE_ID, RejectionReason.PastTick,
            });

            Assert.AreEqual(-1, (int)field.GetValue(callbacks),
                "PastTick-rejected spawn must clear cooldown so the next OnPollInput re-issues with the escalated lead");

            var delayField = typeof(BrawlerSimulationCallbacks).GetField(
                "_extraSpawnDelay", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.AreEqual(4 /* SPAWN_DELAY_STEP */, (int)delayField.GetValue(callbacks),
                "PastTick-rejected spawn must escalate _extraSpawnDelay by SPAWN_DELAY_STEP");
        }

        [Test]
        public void HandleCommandRejected_SpawnToleranceExceeded_PreservesCooldown()
        {
            var callbacks = NewCallbacks();
            var field = GetLastAttemptField();
            field.SetValue(callbacks, SeedTick);

            // Non-PastTick transport reasons (e.g. ToleranceExceeded) fall through the handler
            // without touching the cooldown — only PastTick (ISS-06) and Duplicate have explicit
            // policies. Preserving the cooldown here avoids burst-resend on tolerance-window
            // jitter where escalating the lead would not help.
            GetHandleCommandRejected().Invoke(callbacks, new object[]
            {
                SeedTick + 1, SpawnCharacterCommand.TYPE_ID, RejectionReason.ToleranceExceeded,
            });

            Assert.AreEqual(SeedTick, (int)field.GetValue(callbacks),
                "ToleranceExceeded-rejected spawn must keep cooldown armed (no policy branch)");
        }

    }
}
