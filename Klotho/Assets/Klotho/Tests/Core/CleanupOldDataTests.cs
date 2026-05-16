using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Input;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// KlothoEngine.CleanupOldData transient cap unit tests.
    ///   F-2: Initial state (`_lastVerifiedTick = -1`) — `cleanupTick > 0` 가드로 skip,
    ///        InputBuffer / EventBuffer / `_localHashes` 변동 0
    ///   F-3: Boundary (`cleanupTick == _lastVerifiedTick`) — `ClearBefore(< cleanupTick)`
    ///        시맨틱으로 tick `_lastVerifiedTick` 자체는 보존 (1 tick 보수적)
    /// Companion to InputBufferTests `ClearBefore_NonPositiveOrFirstTick_PreservesAllEntries`
    /// — that test locks in InputBuffer's own boundary semantics, this fixture verifies the
    /// KlothoEngine integration (cap + guard + downstream cascade).
    /// </summary>
    [TestFixture]
    public class CleanupOldDataTests
    {
        private static readonly Type _engineType = typeof(KlothoEngine);

        private static readonly MethodInfo _cleanupOldDataMethod =
            _engineType.GetMethod("CleanupOldData", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _lastVerifiedTickField =
            _engineType.GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _inputBufferField =
            _engineType.GetField("_inputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _localHashesField =
            _engineType.GetField("_localHashes", BindingFlags.NonPublic | BindingFlags.Instance);

        // CurrentTick has a private setter; PropertyInfo.SetValue accesses it via reflection.
        private static readonly PropertyInfo _currentTickPropInfo =
            _engineType.GetProperty("CurrentTick");

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
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        private static void InvokeCleanupOldData(KlothoEngine engine)
            => _cleanupOldDataMethod.Invoke(engine, Array.Empty<object>());

        private static void SetLastVerifiedTick(KlothoEngine engine, int value)
            => _lastVerifiedTickField.SetValue(engine, value);

        private static void SetCurrentTick(KlothoEngine engine, int value)
            => _currentTickPropInfo.SetValue(engine, value);

        private static InputBuffer ReadInputBuffer(KlothoEngine engine)
            => (InputBuffer)_inputBufferField.GetValue(engine);

        private static Dictionary<int, long> ReadLocalHashes(KlothoEngine engine)
            => (Dictionary<int, long>)_localHashesField.GetValue(engine);

        // ── F-2: Initial state — cap=-1 skips cleanup entirely ─────────────

        [Test]
        public void CleanupOldData_InitialState_LastVerifiedNegative_SkipsCleanup()
        {
            var engine = _harness.Host.Engine;

            // Force the engine back into the transient state right after Initialize:
            //   _lastVerifiedTick = -1, CurrentTick = 0. cleanupTick = min(-MaxRollback-Margin, -1) = -60.
            //   `if (cleanupTick > 0)` gate must skip — no InputBuffer / _localHashes mutation.
            SetLastVerifiedTick(engine, -1);
            SetCurrentTick(engine, 0);

            var buffer = ReadInputBuffer(engine);
            var localHashes = ReadLocalHashes(engine);
            localHashes[0] = 0xAAAAL;

            // Harness StartPlaying pre-fills InputBuffer with the input-delay window
            // (KlothoEngine.cs:603-613). Use the post-setup count as the unchanged-baseline
            // rather than seeding a fixed value — the invariant under test is that cleanup
            // makes zero modifications when cap = -1.
            int bufferCountBefore = buffer.Count;
            int hashesCountBefore = localHashes.Count;
            Assert.Greater(bufferCountBefore, 0, "Setup precondition — buffer has pre-filled entries from StartPlaying");
            Assert.AreEqual(1, hashesCountBefore, "Setup precondition — _localHashes seeded");

            InvokeCleanupOldData(engine);

            Assert.AreEqual(bufferCountBefore, buffer.Count,
                "Initial state (cap=-1) must skip InputBuffer.ClearBefore");
            Assert.AreEqual(hashesCountBefore, localHashes.Count,
                "Initial state (cap=-1) must skip _localHashes pruning");
        }

        // ── F-3: Boundary — cleanupTick == _lastVerifiedTick preserves tick itself ──

        [Test]
        public void CleanupOldData_Boundary_CleanupTickEqualsLastVerified_PreservesBoundaryTick()
        {
            var engine = _harness.Host.Engine;

            // Drive cleanupTick == _lastVerifiedTick: rawCleanupTick (CurrentTick - 60) must be
            // >= _lastVerifiedTick so Math.Min picks _lastVerifiedTick.
            //   CurrentTick = 100, MaxRollbackTicks=50 (default), CLEANUP_MARGIN_TICKS=10
            //   → rawCleanupTick = 40, _lastVerifiedTick = 10 → cleanupTick = min(40, 10) = 10.
            const int lastVerified = 10;
            SetLastVerifiedTick(engine, lastVerified);
            SetCurrentTick(engine, 100);

            var buffer = ReadInputBuffer(engine);
            // Replace any harness-injected entries with deterministic boundary fixtures.
            buffer.Clear();
            buffer.AddCommand(new EmptyCommand(playerId: 0, tick: lastVerified - 2)); // wiped
            buffer.AddCommand(new EmptyCommand(playerId: 0, tick: lastVerified - 1)); // wiped
            buffer.AddCommand(new EmptyCommand(playerId: 0, tick: lastVerified));     // preserved — boundary
            buffer.AddCommand(new EmptyCommand(playerId: 0, tick: lastVerified + 5)); // preserved
            Assert.AreEqual(4, buffer.Count, "Setup precondition — 4 entries injected");

            InvokeCleanupOldData(engine);

            Assert.IsFalse(buffer.HasCommandForTick(lastVerified - 2),
                $"Tick {lastVerified - 2} (< cleanupTick) must be wiped");
            Assert.IsFalse(buffer.HasCommandForTick(lastVerified - 1),
                $"Tick {lastVerified - 1} (< cleanupTick) must be wiped");
            Assert.IsTrue(buffer.HasCommandForTick(lastVerified),
                $"Tick {lastVerified} (== cleanupTick) must be preserved — ClearBefore is `< cleanupTick`");
            Assert.IsTrue(buffer.HasCommandForTick(lastVerified + 5),
                $"Tick {lastVerified + 5} (> cleanupTick) must be preserved");
        }
    }
}
