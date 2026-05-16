using System;
using System.Reflection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// ApplyFullState ClearAll 3-path cascade lock-in.
    ///   (a) Hash matched         — ClearAll executes + watermark cascade 3 branches
    ///   (b) Hash mismatched      — ClearAll still executes (silent-accept) + emit cascade
    ///   (c) Retreat early return — ClearAll skipped + all internal state unchanged
    /// Companion to ApplyFullStateHashGateTests (F-1 / F-1.5) — sibling fixture, hash check
    /// and event emission concerns covered there; this fixture focuses on the ClearAll-side
    /// observable effects (event buffer wipe, watermark reset, retreat guard).
    /// </summary>
    [TestFixture]
    public class ApplyFullStateClearAllTests
    {
        private sealed class TestSyncedEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_800;
            public override EventMode Mode => EventMode.Synced;
        }

        private static readonly MethodInfo _applyFullStateMethod = typeof(KlothoEngine)
            .GetMethod("ApplyFullState", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _eventBufferField = typeof(KlothoEngine)
            .GetField("_eventBuffer", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _watermarkField = typeof(KlothoEngine)
            .GetField("_syncedDispatchHighWaterMark", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _resyncCountField = typeof(KlothoEngine)
            .GetField("_resyncHashMismatchCount", BindingFlags.NonPublic | BindingFlags.Instance);

        private LogCapture _log;
        private KlothoTestHarness _harness;

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

            var sim = (TestSimulation)_harness.Host.Simulation;
            sim.UseDeterministicHash = true;

            _harness.AdvanceAllToTick(50);
            _log.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        private bool InvokeApplyFullState(int tick, byte[] stateData, long stateHash, ApplyReason reason)
            => (bool)_applyFullStateMethod.Invoke(_harness.Host.Engine, new object[] { tick, stateData, stateHash, reason });

        private static EventBuffer ReadEventBuffer(KlothoEngine engine)
            => (EventBuffer)_eventBufferField.GetValue(engine);

        private static int ReadWatermark(KlothoEngine engine)
            => (int)_watermarkField.GetValue(engine);

        private static void SetWatermark(KlothoEngine engine, int value)
            => _watermarkField.SetValue(engine, value);

        private static int ReadResyncHashMismatchCount(KlothoEngine engine)
            => (int)_resyncCountField.GetValue(engine);

        private static void InjectSyncedEventAt(KlothoEngine engine, int tick, int payload)
        {
            var buffer = ReadEventBuffer(engine);
            buffer.AddEvent(tick, new TestSyncedEvent { Payload = payload });
        }

        private static void AssertAllSlotsEmpty(EventBuffer buffer)
        {
            // Ring capacity == MaxRollbackTicks + 2 (default 50 + 2 = 52). Buffer has no public
            // capacity accessor; probe a tick range that comfortably exceeds production capacity
            // — GetEvents(t) wraps via modulo so every physical slot is hit.
            const int probeRange = 256;
            for (int t = 0; t < probeRange; t++)
            {
                var slot = buffer.GetEvents(t);
                Assert.AreEqual(0, slot.Count,
                    $"After ClearAll, buffer slot at tick {t} must be empty but has {slot.Count} event(s)");
            }
        }

        // ── (a) Hash matched — ClearAll executes + watermark cascade 3 branches ──

        [TestCase(45, 10, 9, TestName = "HashMatched_ResetHigh")]
        [TestCase(10, 10, 9, TestName = "HashMatched_ResetEdgeEqual")]
        [TestCase(-1, 10, -1, TestName = "HashMatched_NoReset")]
        public void ApplyFullState_HashMatched_ClearsEventBufferAndAppliesWatermarkCascade(
            int initialWatermark, int applyTick, int expectedWatermarkAfter)
        {
            var host = _harness.Host;
            int verifiedBefore = host.Engine.LastVerifiedTick;

            InjectSyncedEventAt(host.Engine, tick: 30, payload: 1);
            InjectSyncedEventAt(host.Engine, tick: 40, payload: 2);
            SetWatermark(host.Engine, initialWatermark);

            int hashMismatchFireCount = 0;
            int desyncDetectedFireCount = 0;
            host.Engine.OnHashMismatch += (_, _, _) => hashMismatchFireCount++;
            host.Engine.OnDesyncDetected += (_, _) => desyncDetectedFireCount++;

            int mismatchCountBefore = ReadResyncHashMismatchCount(host.Engine);

            byte[] stateData = host.Simulation.SerializeFullState();
            long matchingHash = host.Simulation.GetStateHash();
            bool result = InvokeApplyFullState(applyTick, stateData, matchingHash, ApplyReason.LateJoin);

            Assert.IsTrue(result, "Hash matched path must return true");
            Assert.AreEqual(applyTick, host.Engine.CurrentTick, "CurrentTick must equal applyTick after restore");
            Assert.AreEqual(verifiedBefore, host.Engine.LastVerifiedTick,
                "ApplyFullState internal must not modify _lastVerifiedTick (caller post-processing is §F-9 territory)");

            AssertAllSlotsEmpty(ReadEventBuffer(host.Engine));

            Assert.AreEqual(expectedWatermarkAfter, ReadWatermark(host.Engine),
                $"Watermark cascade: initial={initialWatermark}, applyTick={applyTick}, expected={expectedWatermarkAfter}");

            Assert.IsTrue(_log.Contains(LogLevel.Information, "[FullStateResync] hash check"),
                "Hash check log must be emitted");
            Assert.IsTrue(_log.Contains(LogLevel.Information, "match=True"),
                "Matching hash must produce match=True");

            Assert.AreEqual(0, hashMismatchFireCount, "OnHashMismatch must not fire on matched path");
            Assert.AreEqual(0, desyncDetectedFireCount, "OnDesyncDetected must not fire on matched path");
            Assert.AreEqual(0, ReadResyncHashMismatchCount(host.Engine) - mismatchCountBefore,
                "_resyncHashMismatchCount must be unchanged on matched path");
        }

        // ── (b) Hash mismatched — silent-accept cascade (ClearAll still executes) ──

        [Test]
        public void ApplyFullState_HashMismatched_ClearsEventBufferAndEmitsCascade()
        {
            var host = _harness.Host;
            int verifiedBefore = host.Engine.LastVerifiedTick;

            InjectSyncedEventAt(host.Engine, tick: 30, payload: 1);
            InjectSyncedEventAt(host.Engine, tick: 40, payload: 2);
            SetWatermark(host.Engine, 45);

            int hashMismatchFireCount = 0;
            int desyncDetectedFireCount = 0;
            host.Engine.OnHashMismatch += (_, _, _) => hashMismatchFireCount++;
            host.Engine.OnDesyncDetected += (_, _) => desyncDetectedFireCount++;

            int mismatchCountBefore = ReadResyncHashMismatchCount(host.Engine);

            byte[] stateData = host.Simulation.SerializeFullState();
            long wrongHash = unchecked((long)0xDEAD_BEEF_DEAD_BEEFUL);
            bool result = InvokeApplyFullState(10, stateData, wrongHash, ApplyReason.LateJoin);

            Assert.IsFalse(result, "Hash mismatched path must return false");
            Assert.AreEqual(10, host.Engine.CurrentTick, "State application still happens on mismatch (silent-accept)");
            Assert.AreEqual(verifiedBefore, host.Engine.LastVerifiedTick,
                "ApplyFullState internal must not modify _lastVerifiedTick (mismatch path too)");

            AssertAllSlotsEmpty(ReadEventBuffer(host.Engine));

            Assert.AreEqual(9, ReadWatermark(host.Engine),
                "Watermark cascade fires on mismatch path too — 45 >= 10 → reset to 9");

            Assert.IsTrue(_log.Contains(LogLevel.Information, "match=False"),
                "Mismatched hash must produce match=False");
            Assert.IsTrue(_log.Contains(LogLevel.Error, "hash mismatch"),
                "Mismatched hash must emit the diagnostic error line");

            Assert.AreEqual(1, hashMismatchFireCount, "OnHashMismatch must fire exactly once on mismatch");
            Assert.AreEqual(1, desyncDetectedFireCount, "OnDesyncDetected must fire exactly once on mismatch");
            Assert.AreEqual(1, ReadResyncHashMismatchCount(host.Engine) - mismatchCountBefore,
                "_resyncHashMismatchCount must increment by 1 on mismatch");
        }

        // ── (c) Retreat guard early return — ClearAll skipped, all internal state unchanged ──

        [TestCase(ApplyReason.ResyncRequest)]
        [TestCase(ApplyReason.Reconnect)]
        public void ApplyFullState_RetreatGuard_SkipsCascadeWhenNotAllowed(ApplyReason reason)
        {
            var host = _harness.Host;
            const int applyTick = 5;

            Assert.Greater(host.Engine.LastVerifiedTick, applyTick,
                "Setup precondition — verified must exceed applyTick for retreat guard to fire");

            int verifiedBefore = host.Engine.LastVerifiedTick;
            int currentTickBefore = host.Engine.CurrentTick;

            InjectSyncedEventAt(host.Engine, tick: 30, payload: 1);
            InjectSyncedEventAt(host.Engine, tick: 40, payload: 2);
            SetWatermark(host.Engine, 45);

            int hashMismatchFireCount = 0;
            int desyncDetectedFireCount = 0;
            host.Engine.OnHashMismatch += (_, _, _) => hashMismatchFireCount++;
            host.Engine.OnDesyncDetected += (_, _) => desyncDetectedFireCount++;

            int mismatchCountBefore = ReadResyncHashMismatchCount(host.Engine);

            bool result = InvokeApplyFullState(applyTick, BitConverter.GetBytes(0L), 0L, reason);

            Assert.IsTrue(result, "Retreat guard early-return path returns true");
            Assert.AreEqual(currentTickBefore, host.Engine.CurrentTick,
                "Early-return path must not modify CurrentTick");
            Assert.AreEqual(verifiedBefore, host.Engine.LastVerifiedTick,
                "Early-return path must not modify _lastVerifiedTick");

            var buffer = ReadEventBuffer(host.Engine);
            Assert.AreEqual(1, buffer.GetEvents(30).Count,
                "Pre-call event at slot 30 must persist (ClearAll not executed)");
            Assert.AreEqual(1, buffer.GetEvents(40).Count,
                "Pre-call event at slot 40 must persist (ClearAll not executed)");

            Assert.AreEqual(45, ReadWatermark(host.Engine),
                "Watermark unchanged on early-return (cascade not reached)");

            Assert.IsTrue(_log.Contains(LogLevel.Warning, "skip retreat"),
                "Skip-retreat log must be emitted on early-return");

            Assert.AreEqual(0, hashMismatchFireCount, "OnHashMismatch must not fire on early-return");
            Assert.AreEqual(0, desyncDetectedFireCount, "OnDesyncDetected must not fire on early-return");
            Assert.AreEqual(0, ReadResyncHashMismatchCount(host.Engine) - mismatchCountBefore,
                "_resyncHashMismatchCount must be unchanged on early-return");
        }
    }
}
