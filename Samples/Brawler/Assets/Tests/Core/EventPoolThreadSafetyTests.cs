using System;
using System.Collections.Generic;
using System.Threading;
#if DEBUG || UNITY_EDITOR
using System.Text.RegularExpressions;
#endif
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// EventPool thread-safety + ownership diagnostics.
    ///
    /// Pins:
    ///   (1) shared lock-guarded pool — concurrent Get/Return from many threads (the SD dedicated
    ///       server's ThreadPool-worker room loop with MaxRooms>1) must not corrupt the shared
    ///       Dictionary/Stack. Under the old unlocked pool this races (InvalidOperationException /
    ///       corruption).
    ///   (2) DEBUG-only ownership diagnostic — non-pool-origin (game-side `new`) and double-Return
    ///       are detected + skipped; a legitimate cross-thread Get/Return is coherent (no false
    ///       positive, since the pool is shared + locked — unlike CommandPool's ThreadStatic diagnostic).
    /// </summary>
    [TestFixture]
    public class EventPoolThreadSafetyTests
    {
        private sealed class PoolTestEventA : SimulationEvent
        {
            public override int EventTypeId => 9_900_101;
        }

        private sealed class PoolTestEventB : SimulationEvent
        {
            public override int EventTypeId => 9_900_102;
        }

#if DEBUG || UNITY_EDITOR
        private static readonly Regex s_diagnosticPattern =
            new Regex(@"\[EventPool\] Return called on non-pool instance");

        private static IKLogger MakeLogger()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            return factory.CreateLogger("EventPool");
        }
#endif

        [SetUp]
        public void SetUp() => EventPool.ClearAll();

        // ── 1. Concurrent Get/Return stress — no corruption (the multi-room race) ──

        [Test]
        public void Concurrent_GetReturn_DoesNotCorruptPool()
        {
            const int threadCount = 8;
            const int iterations = 5000;
            var errors = new List<Exception>();
            var errLock = new object();

            void Worker(int seed)
            {
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        // Two event types → exercises both the shared Dictionary (multiple keys) and
                        // per-type Stack push/pop under contention.
                        if (((i + seed) & 1) == 0)
                            EventPool.Return(EventPool.Get<PoolTestEventA>());
                        else
                            EventPool.Return(EventPool.Get<PoolTestEventB>());
                    }
                }
                catch (Exception ex)
                {
                    lock (errLock) errors.Add(ex);
                }
            }

            var threads = new Thread[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int seed = t;
                threads[t] = new Thread(() => Worker(seed));
                threads[t].Start();
            }
            for (int t = 0; t < threadCount; t++)
                threads[t].Join();

            Assert.IsEmpty(errors,
                "concurrent Get/Return must not throw — the shared pool must be lock-guarded");
            // Every Get is paired with a Return on the same thread → no outstanding leak,
            // pool bounded by per-type cap (64) across the two types.
            Assert.LessOrEqual(EventPool.GetTotalPooledCount(), 64 * 2,
                "pooled count must stay within the per-type cap");
        }

#if DEBUG || UNITY_EDITOR
        // ── 2. Non-pool instance Return → diagnostic + pool unchanged ──

        [Test]
        public void Return_NonPoolInstance_LogsDiagnostic_AndSkipsPoolInsert()
        {
            EventPool.SetDiagnosticLogger(MakeLogger());
            try
            {
                int before = EventPool.GetTotalPooledCount();
                var rogue = new PoolTestEventA(); // game-side `new` — never tracked by Get<T>

                LogAssert.Expect(LogType.Error, s_diagnosticPattern);
                EventPool.Return(rogue);

                Assert.AreEqual(before, EventPool.GetTotalPooledCount(),
                    "non-pool Return must NOT enter the pool stack");
            }
            finally { EventPool.SetDiagnosticLogger(null); }
        }

        // ── 3. Normal Get → Return cycle is silent ──

        [Test]
        public void Get_Then_Return_NormalCycle_LogsNothing()
        {
            EventPool.SetDiagnosticLogger(MakeLogger());
            try
            {
                var pooled = EventPool.Get<PoolTestEventA>();
                EventPool.Return(pooled);

                Assert.AreEqual(0, EventPool.GetOutstandingCount(),
                    "outstanding set must be empty after balanced Get/Return");
                Assert.AreEqual(1, EventPool.GetTotalPooledCount(),
                    "returned instance must enter the pool stack");
            }
            finally { EventPool.SetDiagnosticLogger(null); }
        }

        // ── 4. Double-Return is caught as a non-pool case ──

        [Test]
        public void Return_SameInstanceTwice_DiagnosesSecondAsNonPool()
        {
            EventPool.SetDiagnosticLogger(MakeLogger());
            try
            {
                var pooled = EventPool.Get<PoolTestEventA>();
                EventPool.Return(pooled);

                LogAssert.Expect(LogType.Error, s_diagnosticPattern);
                EventPool.Return(pooled);

                Assert.AreEqual(1, EventPool.GetTotalPooledCount(),
                    "double-Return must not push the same instance twice (no pool aliasing)");
            }
            finally { EventPool.SetDiagnosticLogger(null); }
        }

        // ── 5. ClearAll lockstep with _outstanding ──

        [Test]
        public void ClearAll_ClearsOutstandingInLockstep()
        {
            EventPool.SetDiagnosticLogger(MakeLogger());
            try
            {
                var pooled = EventPool.Get<PoolTestEventA>();
                Assert.AreEqual(1, EventPool.GetOutstandingCount());

                EventPool.ClearAll();

                Assert.AreEqual(0, EventPool.GetOutstandingCount(),
                    "ClearAll must clear the outstanding set so subsequent Returns don't dangle");
                Assert.AreEqual(0, EventPool.GetTotalPooledCount());

                // A Return on the pre-ClearAll rental now looks like a non-pool case (expected).
                LogAssert.Expect(LogType.Error, s_diagnosticPattern);
                EventPool.Return(pooled);
            }
            finally { EventPool.SetDiagnosticLogger(null); }
        }

        // ── 6. Cross-thread Get→Return is coherent (shared + locked → no false positive) ──

        [Test]
        public void Get_OnOneThread_Return_OnAnother_IsPooled_NoDiagnostic()
        {
            EventPool.SetDiagnosticLogger(MakeLogger());
            try
            {
                int before = EventPool.GetTotalPooledCount();

                // Rent on one thread, return on another — the SD dedicated server's ThreadPool-worker
                // room loop does exactly this across ticks. The shared lock-guarded pool tracks it in
                // the shared _outstanding (no false "wrong thread" diagnostic). Count assertions are the
                // robust check: a false-positive skip would leave the count at `before`.
                SimulationEvent rented = null;
                var rent = new Thread(() => rented = EventPool.Get<PoolTestEventA>());
                rent.Start();
                rent.Join();

                var ret = new Thread(() => EventPool.Return(rented));
                ret.Start();
                ret.Join();

                Assert.AreEqual(0, EventPool.GetOutstandingCount(),
                    "cross-thread Return must clear the shared outstanding set");
                Assert.AreEqual(before + 1, EventPool.GetTotalPooledCount(),
                    "cross-thread Return must enter the shared pool (not skipped as a violation)");
            }
            finally { EventPool.SetDiagnosticLogger(null); }
        }
#endif
    }
}
