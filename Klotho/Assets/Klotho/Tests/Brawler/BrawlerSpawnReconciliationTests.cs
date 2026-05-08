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
        public void HandleCommandRejected_SpawnButNotDuplicate_PreservesCooldown()
        {
            var callbacks = NewCallbacks();
            var field = GetLastAttemptField();
            field.SetValue(callbacks, SeedTick);

            // Spawn rejected for a transport-level reason (PastTick / ToleranceExceeded).
            // The cooldown must stay armed because the cmd never reached the dedup site —
            // re-clearing here would burst-resend on every transport reject.
            GetHandleCommandRejected().Invoke(callbacks, new object[]
            {
                SeedTick + 1, SpawnCharacterCommand.TYPE_ID, RejectionReason.PastTick,
            });

            Assert.AreEqual(SeedTick, (int)field.GetValue(callbacks),
                "Spawn rejection that is not a server-side Duplicate must keep cooldown armed");
        }

    }
}
