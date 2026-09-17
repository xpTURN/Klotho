using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// CommandPool ownership-violation diagnostic.
    ///
    /// Pins the DEBUG-only diagnostic added to CommandPool: Return must detect non-pool-origin
    /// instances (and double-Return) and skip the pool insert, preventing the pool-poisoning
    /// surface that the ownership contract is designed to close.
    ///
    /// Test 6 pins the follow-up: the pool is now a shared lock-guarded pool (not ThreadStatic),
    /// so a legitimate cross-thread Get/Return (the SD dedicated server's ThreadPool-worker room loop)
    /// is coherent — it must NOT be misdiagnosed as a violation.
    ///
    /// Tests are DEBUG-only — the diagnostic is compiled out in release.
    /// </summary>
#if DEBUG || UNITY_EDITOR
    [TestFixture]
    public class CommandPoolOwnershipDiagnosticTests
    {
        private static readonly Regex s_diagnosticPattern =
            new Regex(@"\[CommandPool\] Return called on non-pool instance");

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(KLogLevel.Warning);
                b.AddUnityDebug();
            });
            CommandPool.SetDiagnosticLogger(factory.CreateLogger("CommandPool"));
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            CommandPool.SetDiagnosticLogger(null);
        }

        [SetUp]
        public void SetUp()
        {
            // Start each test from a clean pool/outstanding state.
            CommandPool.ClearAll();
        }

        // ── 1. Non-pool instance Return → diagnostic logged + pool unchanged ──────

        [Test]
        public void Return_NonPoolInstance_LogsDiagnostic_AndSkipsPoolInsert()
        {
            int before = CommandPool.GetTotalPooledCount();

            // Game-side `new` — bypasses Get<T>, so _outstanding has no record of it.
            var rogue = new EmptyCommand();

            LogAssert.Expect(LogType.Error, s_diagnosticPattern);
            CommandPool.Return(rogue);

            Assert.AreEqual(before, CommandPool.GetTotalPooledCount(),
                "non-pool Return must NOT enter the pool stack");
        }

        // ── 2. Normal Get → Return cycle is silent ────────────────────────────────

        [Test]
        public void Get_Then_Return_NormalCycle_LogsNothing()
        {
            var pooled = CommandPool.Get<EmptyCommand>();
            CommandPool.Return(pooled);

            // No LogAssert.Expect issued — TestRunner fails this test if any unexpected
            // LogType.Error was emitted during the run.
            Assert.AreEqual(0, CommandPool.GetOutstandingCount(),
                "outstanding set must be empty after balanced Get/Return");
            Assert.AreEqual(1, CommandPool.GetTotalPooledCount(),
                "returned instance must enter the pool stack");
        }

        // ── 3. Double-Return is caught as a non-pool case ────────────────────────

        [Test]
        public void Return_SameInstanceTwice_DiagnosesSecondAsNonPool()
        {
            var pooled = CommandPool.Get<EmptyCommand>();
            CommandPool.Return(pooled);

            // The second Return has no _outstanding entry — it looks identical to a
            // game-side rogue Return and is rejected by the same guard.
            LogAssert.Expect(LogType.Error, s_diagnosticPattern);
            CommandPool.Return(pooled);

            // Pool count still 1 (the second Return short-circuits before stack.Push).
            Assert.AreEqual(1, CommandPool.GetTotalPooledCount(),
                "double-Return must not push the same instance twice");
        }

        // ── 4. Pool cap overflow leaves no dangling _outstanding entries ──────────

        [Test]
        public void ReturnOverflow_DoesNotLeakOutstanding()
        {
            const int OverCap = 70; // MAX_POOL_SIZE = 64

            var rented = new EmptyCommand[OverCap];
            for (int i = 0; i < OverCap; i++)
                rented[i] = CommandPool.Get<EmptyCommand>();

            Assert.AreEqual(OverCap, CommandPool.GetOutstandingCount(),
                "all rentals must be tracked in _outstanding");

            for (int i = 0; i < OverCap; i++)
                CommandPool.Return(rented[i]);

            Assert.AreEqual(0, CommandPool.GetOutstandingCount(),
                "every Return — including the cap-overflow GC drops — must clear _outstanding");
            Assert.LessOrEqual(CommandPool.GetTotalPooledCount(), 64,
                "pool must respect MAX_POOL_SIZE cap");
        }

        // ── 5. ClearAll lockstep with _outstanding ────────────────────────────────

        [Test]
        public void ClearAll_ClearsOutstandingInLockstep()
        {
            var pooled = CommandPool.Get<EmptyCommand>();
            Assert.AreEqual(1, CommandPool.GetOutstandingCount());

            CommandPool.ClearAll();

            Assert.AreEqual(0, CommandPool.GetOutstandingCount(),
                "ClearAll must clear the outstanding set so subsequent Returns don't dangle");
            Assert.AreEqual(0, CommandPool.GetTotalPooledCount());

            // A Return on the pre-ClearAll rental now looks like a non-pool case (expected).
            LogAssert.Expect(LogType.Error, s_diagnosticPattern);
            CommandPool.Return(pooled);
        }

        // ── 6. Cross-thread Get/Return is coherent (shared lock-guarded pool) ─────

        [Test]
        public void Get_OnOneThread_Return_OnAnother_IsPooled_NoDiagnostic()
        {
            int before = CommandPool.GetTotalPooledCount();

            // Rent on one worker thread, return on another — the SD dedicated server's
            // ThreadPool-worker room loop does exactly this across ticks. The shared lock-guarded
            // pool must track it across the thread hop (no false "wrong thread" diagnostic).
            ICommand rented = null;
            var rent = new System.Threading.Thread(() => rented = CommandPool.Get<EmptyCommand>());
            rent.Start();
            rent.Join();

            var ret = new System.Threading.Thread(() => CommandPool.Return(rented));
            ret.Start();
            ret.Join();

            // No LogAssert.Expect — any LogType.Error fails the test. The count assertions are the
            // robust check: under the old ThreadStatic pool the cross-thread Return would be skipped
            // as a violation and the count would stay at `before`.
            Assert.AreEqual(0, CommandPool.GetOutstandingCount(),
                "cross-thread Return must clear the shared outstanding set");
            Assert.AreEqual(before + 1, CommandPool.GetTotalPooledCount(),
                "cross-thread Return must enter the shared pool (not skipped as a wrong-thread violation)");
        }
    }
#endif
}
