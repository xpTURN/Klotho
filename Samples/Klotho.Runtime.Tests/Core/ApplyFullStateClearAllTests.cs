using System;
using System.Reflection;
using xpTURN.Klotho.Logging;
using NUnit.Framework;

using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// ApplyFullState ClearAll 3-path cascade lock-in.
    ///   (a) Hash matched         — ClearAll executes + watermark cascade 3 branches
    ///   (b) Hash mismatched      — ClearAll still executes (silent-accept) + emit cascade
    ///   (c) Retreat early return — ClearAll skipped + all internal state unchanged
    /// Companion to ApplyFullStateHashGateTests — sibling fixture, hash check
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

        // ApplyFullState returns the private enum FullStateApplyResult:
        // "Applied" / "Skipped" / "HashMismatch" — compared by name since the type is private.
        private string InvokeApplyFullState(int tick, byte[] stateData, long stateHash, ApplyReason reason)
            => _applyFullStateMethod.Invoke(_harness.Host.Engine, new object[] { tick, stateData, stateHash, reason }).ToString();

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
            string result = InvokeApplyFullState(applyTick, stateData, matchingHash, ApplyReason.LateJoin);

            Assert.AreEqual("Applied", result, "Hash matched path must return Applied");
            Assert.AreEqual(applyTick, host.Engine.CurrentTick, "CurrentTick must equal applyTick after restore");
            Assert.AreEqual(verifiedBefore, host.Engine.LastVerifiedTick,
                "ApplyFullState internal must not modify _lastVerifiedTick (caller post-processing is §F-9 territory)");

            AssertAllSlotsEmpty(ReadEventBuffer(host.Engine));

            Assert.AreEqual(expectedWatermarkAfter, ReadWatermark(host.Engine),
                $"Watermark cascade: initial={initialWatermark}, applyTick={applyTick}, expected={expectedWatermarkAfter}");

            Assert.IsTrue(_log.Contains(KLogLevel.Information, "[FullStateResync] hash check"),
                "Hash check log must be emitted");
            Assert.IsTrue(_log.Contains(KLogLevel.Information, "match=True"),
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
            string result = InvokeApplyFullState(10, stateData, wrongHash, ApplyReason.LateJoin);

            Assert.AreEqual("HashMismatch", result, "Hash mismatched path must return HashMismatch (state still applied)");
            Assert.AreEqual(10, host.Engine.CurrentTick, "State application still happens on mismatch (silent-accept)");
            Assert.AreEqual(verifiedBefore, host.Engine.LastVerifiedTick,
                "ApplyFullState internal must not modify _lastVerifiedTick (mismatch path too)");

            AssertAllSlotsEmpty(ReadEventBuffer(host.Engine));

            Assert.AreEqual(9, ReadWatermark(host.Engine),
                "Watermark cascade fires on mismatch path too — 45 >= 10 → reset to 9");

            Assert.IsTrue(_log.Contains(KLogLevel.Information, "match=False"),
                "Mismatched hash must produce match=False");
            Assert.IsTrue(_log.Contains(KLogLevel.Error, "hash mismatch"),
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

            string result = InvokeApplyFullState(applyTick, BitConverter.GetBytes(0L), 0L, reason);

            Assert.AreEqual("Skipped", result,
                "Retreat guard early-return must report Skipped — no longer disguised as success");
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

            Assert.IsTrue(_log.Contains(KLogLevel.Warning, "skip retreat"),
                "Skip-retreat log must be emitted on early-return");

            Assert.AreEqual(0, hashMismatchFireCount, "OnHashMismatch must not fire on early-return");
            Assert.AreEqual(0, desyncDetectedFireCount, "OnDesyncDetected must not fire on early-return");
            Assert.AreEqual(0, ReadResyncHashMismatchCount(host.Engine) - mismatchCountBefore,
                "_resyncHashMismatchCount must be unchanged on early-return");
        }

        // ── ApplyFullState must not wipe the process-global EventPool ──

        // A pool-rentable test event (parameterless ctor for EventPool.Get<T>).
        private sealed class TestPoolEvent : SimulationEvent
        {
            public override int EventTypeId => 9_999_810;
        }

        // EventPool is a process-global static pool shared across engines. Another
        // engine's pool-rented events ("B") are tracked in EventPool._outstanding. Pre-fix, engine A's
        // ApplyFullState called EventPool.ClearAll() and wiped that tracking, so B's later legitimate
        // Return was flagged as an ownership violation (KError + pool-insert skip = leak). With the
        // fix (ClearAll removed) B's _outstanding survives and its Return is clean.
        [Test]
        public void ApplyFullState_DoesNotWipeOtherEnginesPooledEvents_IMP60_30_E3()
        {
            var host = _harness.Host;

            // "Engine B" holdings — pool-rented, tracked in the shared EventPool._outstanding.
            const int held = 4;
            var rented = new TestPoolEvent[held];
            for (int i = 0; i < held; i++)
                rented[i] = EventPool.Get<TestPoolEvent>();

            // Engine A applies a full state (matched hash → Applied path runs the event-buffer clear).
            byte[] stateData = host.Simulation.SerializeFullState();
            long matchingHash = host.Simulation.GetStateHash();
            string result = InvokeApplyFullState(50, stateData, matchingHash, ApplyReason.LateJoin);
            Assert.AreEqual("Applied", result, "Setup: matched-hash LateJoin must run the apply (event-buffer clear) path");

            // Spy only B's post-apply Returns for ownership violations.
            EventPool.SetDiagnosticLogger(_log);
            _log.Clear();
            for (int i = 0; i < held; i++)
                EventPool.Return(rented[i]);
            EventPool.SetDiagnosticLogger(null);

            Assert.IsFalse(_log.Contains(KLogLevel.Error, "[EventPool] Return called on non-pool"),
                "E-3: ApplyFullState must not wipe another engine's EventPool._outstanding — " +
                "B's legitimate Return must not be flagged as an ownership violation (pre-fix: KError + leak).");
        }

        // removing EventPool.ClearAll() must not surface a masked leak. Repeated
        // ApplyFullState with no ticks between must not accumulate outstanding pool instances
        // (the per-apply _eventBuffer.ClearAll() returns this engine's buffered events each time).
        [Test]
        public void ApplyFullState_RepeatedApply_NoOutstandingGrowth_IMP60_30_E3()
        {
            var host = _harness.Host;
            byte[] stateData = host.Simulation.SerializeFullState();
            long hash = host.Simulation.GetStateHash();

            // Warm up one apply, then measure the outstanding delta across repeated applies.
            InvokeApplyFullState(50, stateData, hash, ApplyReason.LateJoin);
            int baseline = EventPool.GetOutstandingCount();

            for (int i = 0; i < 20; i++)
                InvokeApplyFullState(50, stateData, hash, ApplyReason.LateJoin);

            int delta = EventPool.GetOutstandingCount() - baseline;
            Assert.LessOrEqual(delta, 0,
                $"E-3 NoLeak: repeated ApplyFullState must not accumulate outstanding pool events (delta={delta}). " +
                "A positive delta would indicate a pre-existing leak that EventPool.ClearAll() was masking.");
        }
    }
}
