using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// How often a stalled input chain is allowed to say so (IMP103).
    ///
    /// The throttle claimed "at most once per 1s" but keyed that on the stalled tick, and the dominant
    /// case — a peer's input arriving one tick late — advances the stalled tick every tick, so the guard
    /// never held and every tick produced a WARN. A 2s dynamic-delay warmup at a 25ms tick printed ~80
    /// lines. Keying on WHICH PLAYERS are missing fixes that while keeping the three things a reader
    /// needs: the first occurrence, any change in the cause, and the size of what was swallowed.
    ///
    /// These tests only exist in a dev build. Until this change the guard was
    /// `DEVELOPMENT_BUILD || UNITY_EDITOR`, which a `dotnet` build defines under neither configuration —
    /// the code was unreachable from here at all, and Unity plus a live session were the only way to see
    /// it. `DEBUG` was added to that guard (46 sites) precisely so this file could exist.
    /// </summary>
    [TestFixture]
    public class ChainBreakThrottleTests
    {
        private const string BreakTag = "[ChainBreak]";

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        [Test]
        public void FirstBreak_IsNotHeldBehindTheCounter()
        {
#if !DEBUG
            Assert.Ignore("LogChainAdvanceBreak is dev-build-only");
#else
            var log = new LogCapture();
            using (var stall = new StalledChain(log, stalledTicks: 30))
            {
                Assert.That(Count(log, BreakTag), Is.GreaterThanOrEqualTo(1),
                    "a stalled chain has to say so — the opening line is the one that reports a stall started");

                // The distinguishing property of the opening line: nothing has been swallowed before it,
                // so it carries no suppression suffix. A throttle that delayed the first report would put
                // a count on the line that finally appeared.
                Assert.That(FirstMatching(log, BreakTag), Does.Not.Contain("suppressed since tick="),
                    "the first line of a burst is due immediately, with nothing swallowed ahead of it");
            }
#endif
        }

        [Test]
        public void RepeatedBreaks_SameCause_AreThrottled()
        {
#if !DEBUG
            Assert.Ignore("LogChainAdvanceBreak is dev-build-only");
#else
            var log = new LogCapture();
            using (var stall = new StalledChain(log, stalledTicks: 30))
            {
                var engine = stall.Harness.Host.Engine;
                int before = Count(log, BreakTag);

                // The frontier has to ADVANCE for this to test anything. A frozen chain reports the same
                // stalled tick every time, which the old throttle already suppressed — the defect was the
                // other case: an input arriving one tick late moves the stalled tick every tick, so
                // `tick == _lastLoggedTick` never held and every tick produced a line. Driving the log
                // with a rising tick and an unchanged missing set is that case, and the harness has no
                // way to produce a one-tick-late peer.
                int from = engine.LastVerifiedTick + 1;
                for (int i = 0; i < 40; i++) LogBreak(engine, from + i);

                int added = Count(log, BreakTag) - before;
                Assert.That(added, Is.LessThanOrEqualTo(2),
                    $"40 advancing stalled ticks produced {added} lines — before the fix this was one per tick");
                Assert.That(added, Is.GreaterThanOrEqualTo(1), "and it must not go silent either");
            }
#endif
        }

        [Test]
        public void CauseChange_LogsImmediately_AndCarriesTheSuppressedCount()
        {
#if !DEBUG
            Assert.Ignore("LogChainAdvanceBreak is dev-build-only");
#else
            var log = new LogCapture();
            using (var stall = new StalledChain(log, stalledTicks: 40))
            {
                var engine = stall.Harness.Host.Engine;
                int before = Count(log, BreakTag);
                Assert.That(Suppressed(engine), Is.GreaterThan(0),
                    "precondition: the stall must have swallowed at least one break");

                // Flip the remembered cause. A real cause change is another player going missing; doing
                // it through the field is what keeps this test off the wall clock, which is the throttle's
                // other input and the one a test cannot advance.
                int swallowed = Suppressed(engine);
                MissingMaskField.SetValue(engine, ~(ulong)MissingMaskField.GetValue(engine));
                LogBreak(engine, engine.LastVerifiedTick + 1);

                Assert.That(Count(log, BreakTag), Is.EqualTo(before + 1),
                    "a changed cause is reported at once, not on the next heartbeat");
                Assert.That(LastMatching(log, BreakTag), Does.Contain($"(+{swallowed} suppressed since tick="),
                    "and the burst size rides along, so 'one late packet' stays distinguishable from 'stalled for two seconds'");
                Assert.That(Suppressed(engine), Is.Zero, "the counter resets once reported");
            }
#endif
        }

#if DEBUG
        // ── Harness ─────────────────────────────────────────────────────────────────────────────

        private static readonly FieldInfo MissingMaskField = typeof(KlothoEngine)
            .GetField("_lastChainBreakMissingMask", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo SuppressedField = typeof(KlothoEngine)
            .GetField("_chainBreakSuppressed", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo LogBreakMethod = typeof(KlothoEngine)
            .GetMethod("LogChainAdvanceBreak", BindingFlags.NonPublic | BindingFlags.Instance);

        private static int Suppressed(KlothoEngine e) => (int)SuppressedField.GetValue(e);
        private static void LogBreak(KlothoEngine e, int tick) => LogBreakMethod.Invoke(e, new object[] { tick });

        /// <summary>
        /// A host whose guest has gone quiet without disconnecting, so the verified chain genuinely
        /// stalls while CurrentTick advances by prediction. The two preconditions are the harness's, not
        /// this test's: QuorumMissDropTicks disables the auto-seal watchdog, and time sync would
        /// otherwise throttle the host below the helper's safety limit.
        /// </summary>
        private sealed class StalledChain : System.IDisposable
        {
            public readonly KlothoTestHarness Harness;

            public StalledChain(IKLogger logger, int stalledTicks)
            {
                Harness = new KlothoTestHarness(logger).WithSimulationConfig(new SimulationConfig
                {
                    TickIntervalMs      = 50,
                    QuorumMissDropTicks = int.MaxValue,
                    MaxRollbackTicks    = 50,
                });
                Harness.CreateHost(2);
                var guest = Harness.AddGuest();
                Harness.StartPlaying();
                Harness.Host.Engine.DisableTimeSync();

                Harness.AdvanceAllToTick(20);
                Harness.AdvanceWithFrozenVerifiedTick(Harness.Host.CurrentTick + stalledTicks, guest.LocalPlayerId);
            }

            public void Dispose() { }
        }

        private static int Count(LogCapture log, string needle)
        {
            int n = 0;
            foreach (var e in log.Entries)
                if (e.Message.Contains(needle)) n++;
            return n;
        }

        private static string FirstMatching(LogCapture log, string needle)
        {
            foreach (var e in log.Entries)
                if (e.Message.Contains(needle)) return e.Message;
            return string.Empty;
        }

        private static string LastMatching(LogCapture log, string needle)
        {
            string last = null;
            foreach (var e in log.Entries)
                if (e.Message.Contains(needle)) last = e.Message;
            return last ?? string.Empty;
        }
#endif
    }
}
