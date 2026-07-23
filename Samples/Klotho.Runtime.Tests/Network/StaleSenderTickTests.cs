using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Stale SenderTick decontamination after FullState restore.
    ///   (a) restore sync: ApplyFullState seeds the service's _localTick with the restore
    ///       tick, so prefill/commands sent before the first post-restore tick carry a
    ///       truthful SenderTick (pre-fix: pre-disconnect value on reconnect, 0 on late join)
    ///   (a′) hoist: _localTick keeps tracking CurrentTick even when timesync is disabled —
    ///       the LATE-JOIN case is the direct regression gate for this: a late-join guest is
    ///       a fresh engine that never passes HandleGameStart, so _timeSyncEnabled stays
    ///       false and only the hoisted SetLocalTick keeps the piggyback honest. (The
    ///       harness reconnect path reuses the engine, so the flag survives and cannot
    ///       exercise the hoist.)
    ///   (b) receiver side: the host's _remoteTicks entry for a reconnected guest must hold
    ///       a near-restore tick, not the pre-disconnect relic that made the host throttle
    ///       to the cap right in the catchup window (Sweep RelaySealDropCount failure).
    /// </summary>
    [TestFixture]
    public class StaleSenderTickTests
    {
        private static readonly FieldInfo _localTickField = typeof(KlothoNetworkService)
            .GetField("_localTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _remoteTicksField = typeof(KlothoEngine)
            .GetField("_remoteTicks", BindingFlags.NonPublic | BindingFlags.Instance);

        private LogCapture _log;
        private KlothoTestHarness _harness;
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Warning);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("Tests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _log = new LogCapture();
            _harness = new KlothoTestHarness(_log);
            _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.StartPlaying();
            _log.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        private static int ReadLocalTick(TestPeer peer)
            => (int)_localTickField.GetValue(peer.NetworkService);

        // _localTick is written inside tick execution BEFORE CurrentTick++ — in steady state
        // it trails CurrentTick by exactly 1. Allow 2 for loop-boundary slack.
        private static void AssertPiggybackTracksEngine(TestPeer peer, string context)
        {
            int engineTick = peer.CurrentTick;
            int localTick = ReadLocalTick(peer);
            Assert.GreaterOrEqual(localTick, engineTick - 2,
                $"{context}: service _localTick ({localTick}) must track engine CurrentTick ({engineTick}) " +
                "— a frozen/stale piggyback tick re-creates the advantage spike");
        }

        // ── (a′) Late join — fresh engine, _timeSyncEnabled false: hoist regression ──

        [Test]
        public void LateJoinGuest_PiggybackTickTracksEngine()
        {
            _harness.AdvanceAllToTick(30);

            var lateJoin = _harness.AddLateJoinGuest();
            _harness.PumpMessages(20);
            _harness.AdvanceAllToTick(60);

            Assert.AreEqual(SessionPhase.Playing, lateJoin.Phase, "Setup — late joiner must reach Playing");
            Assert.IsFalse(_harness.IsCatchingUp(lateJoin), "Setup — catchup must be complete");

            // Pre-fix: 0 (fresh service) at join, then frozen at the restore tick without the
            // hoist (this guest's _timeSyncEnabled is false — the in-guard SetLocalTick never ran).
            AssertPiggybackTracksEngine(lateJoin, "Late-join guest after advancing past the join");
        }

        // ── (a) Reconnect — restore-point sync + continued tracking ───────────

        [Test]
        public void ReconnectedGuest_PiggybackTickSyncedOnRestoreAndTracks()
        {
            var guest = _harness.Guests[0];

            _harness.AdvanceAllToTick(30);
            _harness.DisconnectPeer(guest);
            _harness.AdvanceWithStalledPeer(60, guest.LocalPlayerId);

            _harness.ReconnectPeer(guest);

            // Step 1: ApplyFullState seeded _localTick with the restore tick (pre-fix: ≈29,
            // the pre-disconnect value retained by the reused service instance).
            AssertPiggybackTracksEngine(guest, "Reconnected guest immediately after restore");

            _harness.AdvanceAllToTick(_harness.Host.CurrentTick + 15);
            AssertPiggybackTracksEngine(guest, "Reconnected guest after advancing");
        }

        // ── (b) Receiver side — host's view of the reconnected guest ──────────

        [Test]
        public void Reconnect_HostViewOfGuestIsNotStale()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];

            _harness.AdvanceAllToTick(30);
            _harness.DisconnectPeer(guest);
            _harness.AdvanceWithStalledPeer(60, guest.LocalPlayerId);

            _harness.ReconnectPeer(guest);
            int restoreTick = guest.CurrentTick;

            // Let post-restore guest sends (prefill + real commands) reach the host.
            _harness.AdvanceAllToTick(host.CurrentTick + 10);

            var remoteTicks = (System.Collections.IDictionary)_remoteTicksField.GetValue(host.Engine);
            Assert.IsTrue(remoteTicks.Contains(guest.LocalPlayerId),
                "Setup — host must hold a timing entry for the reconnected guest");

            object entry = remoteTicks[guest.LocalPlayerId];
            int remoteTick = (int)entry.GetType().GetField("Item1").GetValue(entry);

            // ε = InputDelay + RecommendedExtraDelay (catchup piggyback latency allowance) —
            // the actual value should sit at/above the restore tick. Pre-fix this held the
            // pre-disconnect relic (≈29 vs restore ≈60): the host computed a huge advantage
            // and throttled to the cap right in the catchup window (Sweep seal-drop failure).
            int epsilon = host.Engine.InputDelay + host.Engine.RecommendedExtraDelay + 2;
            Assert.GreaterOrEqual(remoteTick, restoreTick - epsilon,
                $"Host's view of the reconnected guest ({remoteTick}) must be near the restore tick " +
                $"({restoreTick}, ε={epsilon}) — a stale SenderTick re-creates the catchup-window over-throttle");
        }
    }
}
