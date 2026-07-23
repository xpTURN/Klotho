using System;
using System.Collections.Generic;
using System.Reflection;
using xpTURN.Klotho.Logging;
using NUnit.Framework;

using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// P2P caller post-processing cascade lock-in (a/b/c/d).
    ///   (a) HandleFullStateReceived Resync matched — buffers clear + verified = tick-1
    ///   (b) HandleFullStateReceived Resync mismatched — buffers clear + mid-match desync state preserved
    ///   (c) HandleFullStateReceived unexpected drop — entry guard skips cascade
    ///   (d) ApplyP2PLateJoinFullState — buffers clear + verified = tick + StartCatchingUp
    /// Sibling: SDFullStateCallerCascadeTests for SD (e)/(f) paths. ApplyFullState internal
    /// cascade is covered separately — this fixture focuses on caller-side cascade only.
    /// </summary>
    [TestFixture]
    public class P2PFullStateCallerCascadeTests
    {
        // ── Reflection handles ───────────────────────────────────────────────

        private static readonly MethodInfo _handleFullStateReceivedMethod = typeof(KlothoEngine)
            .GetMethod("HandleFullStateReceived", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _applyP2PLateJoinFullStateMethod = typeof(KlothoEngine)
            .GetMethod("ApplyP2PLateJoinFullState", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _inputBufferField = typeof(KlothoEngine)
            .GetField("_inputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _pendingCommandsField = typeof(KlothoEngine)
            .GetField("_pendingCommands", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _resyncStateField = typeof(KlothoEngine)
            .GetField("_resyncState", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _expectingFullStateField = typeof(KlothoEngine)
            .GetField("_expectingFullState", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _unexpectedFullStateDropCountField = typeof(KlothoEngine)
            .GetField("_unexpectedFullStateDropCount", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _lastMatchedSyncTickField = typeof(KlothoEngine)
            .GetField("_lastMatchedSyncTick", BindingFlags.NonPublic | BindingFlags.Instance);

        // _consecutiveDesyncCount was replaced by per-peer _desyncCountByPeer. The
        // post-apply self-state-mismatch bridge accounts under SelfDesyncPeerKey (-1).
        private static readonly FieldInfo _desyncCountByPeerField = typeof(KlothoEngine)
            .GetField("_desyncCountByPeer", BindingFlags.NonPublic | BindingFlags.Instance);
        private const int SelfDesyncPeerKey = -1;

        private static readonly FieldInfo _resyncRetryCountField = typeof(KlothoEngine)
            .GetField("_resyncRetryCount", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _hasPendingRollbackField = typeof(KlothoEngine)
            .GetField("_hasPendingRollback", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly Type _resyncStateEnum = typeof(KlothoEngine)
            .GetNestedType("ResyncState", BindingFlags.NonPublic);

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void InvokeHandleFullStateReceived(
            KlothoEngine engine, int tick, byte[] data, long hash, FullStateKind kind)
            => _handleFullStateReceivedMethod.Invoke(engine, new object[] { tick, data, hash, kind });

        private static void InvokeApplyP2PLateJoinFullState(
            KlothoEngine engine, int tick, byte[] data, long hash)
            => _applyP2PLateJoinFullStateMethod.Invoke(engine, new object[] { tick, data, hash });

        private static InputBuffer ReadInputBuffer(KlothoEngine engine)
            => (InputBuffer)_inputBufferField.GetValue(engine);

        private static int ReadInputBufferCount(KlothoEngine engine)
            => ReadInputBuffer(engine).Count;

        private static List<ICommand> ReadPendingCommands(KlothoEngine engine)
            => (List<ICommand>)_pendingCommandsField.GetValue(engine);

        private static int ReadPendingCommandsCount(KlothoEngine engine)
            => ReadPendingCommands(engine).Count;

        private static int ReadUnexpectedDropCount(KlothoEngine engine)
            => (int)_unexpectedFullStateDropCountField.GetValue(engine);

        private static int ReadLastMatchedSyncTick(KlothoEngine engine)
            => (int)_lastMatchedSyncTickField.GetValue(engine);

        private static int ReadConsecutiveDesyncCount(KlothoEngine engine)
        {
            var dict = (Dictionary<int, int>)_desyncCountByPeerField.GetValue(engine);
            return dict.TryGetValue(SelfDesyncPeerKey, out int v) ? v : 0;
        }

        private static void SetSelfDesyncCount(KlothoEngine engine, int count)
        {
            var dict = (Dictionary<int, int>)_desyncCountByPeerField.GetValue(engine);
            dict[SelfDesyncPeerKey] = count;
        }

        private static int ReadResyncRetryCount(KlothoEngine engine)
            => (int)_resyncRetryCountField.GetValue(engine);

        private static bool ReadHasPendingRollback(KlothoEngine engine)
            => (bool)_hasPendingRollbackField.GetValue(engine);

        // ResyncState is a private nested enum (None=0, Requested=1, Applying=2).
        private static void SetResyncState(KlothoEngine engine, int value)
            => _resyncStateField.SetValue(engine, Enum.ToObject(_resyncStateEnum, value));

        private static int ReadResyncState(KlothoEngine engine)
            => Convert.ToInt32(_resyncStateField.GetValue(engine));

        private static void SetExpectingFullState(KlothoEngine engine, bool value)
            => _expectingFullStateField.SetValue(engine, value);

        private static void PopulatePendingCommands(KlothoEngine engine, int count)
        {
            // Clear residue first: non-ECS rollback resolves against
            // TestSimulation's own history and succeeds — harness advancement can leave
            // re-simulation predictions in _pendingCommands. Tests assert exact counts.
            var pending = ReadPendingCommands(engine);
            pending.Clear();
            for (int i = 0; i < count; i++)
                pending.Add(new EmptyCommand(playerId: 1, tick: 100 + i));
        }

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

        // ── (a) Resync matched — caller cascade clears buffers + verified = tick-1 ──

        [Test]
        public void HandleFullStateReceived_ResyncMatched_ClearsBuffersAndAdvancesVerified()
        {
            var guest = _harness.Guests[0]; // FullState receiver (non-host) — avoids the corrective reset cascade

            PopulatePendingCommands(guest.Engine, count: 5);
            int inputBufferBefore = ReadInputBufferCount(guest.Engine);
            int pendingBefore = ReadPendingCommandsCount(guest.Engine);
            Assert.Greater(inputBufferBefore, 0,
                "Setup precondition — input buffer must have entries after AdvanceAllToTick(50)");
            Assert.AreEqual(5, pendingBefore, "Setup precondition — _pendingCommands populated");

            SetResyncState(guest.Engine, value: 1); // ResyncState.Requested
            int dropCountBefore = ReadUnexpectedDropCount(guest.Engine);

            const int applyTick = 60;
            byte[] stateData = guest.Simulation.SerializeFullState();
            long matchingHash = guest.Simulation.GetStateHash();

            InvokeHandleFullStateReceived(guest.Engine, applyTick, stateData, matchingHash, FullStateKind.Unicast);

            Assert.AreEqual(0, ReadInputBufferCount(guest.Engine), "_inputBuffer.Clear() must execute");
            Assert.AreEqual(0, ReadPendingCommandsCount(guest.Engine), "_pendingCommands.Clear() must execute");
            Assert.AreEqual(applyTick - 1, guest.Engine.LastVerifiedTick,
                "P2P HandleFullStateReceived must set _lastVerifiedTick = tick - 1");
            Assert.AreEqual(0, ReadUnexpectedDropCount(guest.Engine) - dropCountBefore,
                "Drop counter must not increment on Resync path");
            Assert.AreEqual(applyTick, ReadLastMatchedSyncTick(guest.Engine),
                "Matched hash must update _lastMatchedSyncTick");
            Assert.AreEqual(0, ReadConsecutiveDesyncCount(guest.Engine),
                "Matched hash must reset _consecutiveDesyncCount");
            Assert.AreEqual(0, ReadResyncRetryCount(guest.Engine),
                "Matched hash must reset _resyncRetryCount");
            Assert.IsFalse(ReadHasPendingRollback(guest.Engine),
                "Caller cascade must clear _hasPendingRollback");
            Assert.IsTrue(_log.Contains(KLogLevel.Warning, "[FullStateResync] complete"),
                "Match path must emit success log");
        }

        // ── (b) Resync mismatched — silent-accept cascade + desync escalation accounting ──
        //    (A post-apply hash mismatch now feeds RegisterDesyncForEscalation —
        //     the old "bridge" fired an external event and never re-entered the pipeline.)

        [Test]
        public void HandleFullStateReceived_ResyncMismatched_BelowThreshold_AccumulatesDesyncCount()
        {
            var guest = _harness.Guests[0]; // FullState receiver (non-host) — avoids the corrective reset cascade

            PopulatePendingCommands(guest.Engine, count: 5);
            SetResyncState(guest.Engine, value: 1); // Requested

            _lastMatchedSyncTickField.SetValue(guest.Engine, 42);
            SetSelfDesyncCount(guest.Engine, 1); // below threshold(3) after +1
            _resyncRetryCountField.SetValue(guest.Engine, 2);

            int onResyncCompletedFireCount = 0;
            guest.Engine.OnResyncCompleted += _ => onResyncCompletedFireCount++;

            const int applyTick = 60;
            byte[] stateData = guest.Simulation.SerializeFullState();
            long wrongHash = unchecked((long)0xDEAD_BEEF_DEAD_BEEFUL);

            InvokeHandleFullStateReceived(guest.Engine, applyTick, stateData, wrongHash, FullStateKind.Unicast);

            Assert.AreEqual(0, ReadInputBufferCount(guest.Engine), "_inputBuffer.Clear() must execute on mismatch too");
            Assert.AreEqual(0, ReadPendingCommandsCount(guest.Engine), "_pendingCommands.Clear() must execute on mismatch too");
            Assert.AreEqual(applyTick - 1, guest.Engine.LastVerifiedTick,
                "_lastVerifiedTick = tick - 1 on mismatch path too");

            Assert.AreEqual(42, ReadLastMatchedSyncTick(guest.Engine),
                "Mismatch path must preserve _lastMatchedSyncTick (mid-match desync recovery)");
            Assert.AreEqual(2, ReadConsecutiveDesyncCount(guest.Engine),
                "Post-apply mismatch counts toward escalation: 1 + 1 = 2");
            Assert.AreEqual(2, ReadResyncRetryCount(guest.Engine),
                "Below threshold — no re-request, _resyncRetryCount preserved");
            Assert.AreEqual(0, ReadResyncState(guest.Engine),
                "Below threshold — resync state returns to None");

            Assert.IsTrue(_log.Contains(KLogLevel.Error, "hash mismatch"),
                "Mismatch must emit diagnostic error log");
            Assert.AreEqual(0, onResyncCompletedFireCount,
                "OnResyncCompleted must NOT fire on mismatch path");
        }

        [Test]
        public void HandleFullStateReceived_ResyncMismatched_AtThreshold_EscalatesToRetry()
        {
            var guest = _harness.Guests[0];

            PopulatePendingCommands(guest.Engine, count: 5);
            SetResyncState(guest.Engine, value: 1); // Requested

            _lastMatchedSyncTickField.SetValue(guest.Engine, 42);
            SetSelfDesyncCount(guest.Engine, 2); // reaches threshold(3) after +1
            _resyncRetryCountField.SetValue(guest.Engine, 1);

            int onResyncCompletedFireCount = 0;
            guest.Engine.OnResyncCompleted += _ => onResyncCompletedFireCount++;

            const int applyTick = 60;
            byte[] stateData = guest.Simulation.SerializeFullState();
            long wrongHash = unchecked((long)0xDEAD_BEEF_DEAD_BEEFUL);

            InvokeHandleFullStateReceived(guest.Engine, applyTick, stateData, wrongHash, FullStateKind.Unicast);

            Assert.AreEqual(0, ReadConsecutiveDesyncCount(guest.Engine),
                "Escalation resets the consecutive counter");
            Assert.AreEqual(2, ReadResyncRetryCount(guest.Engine),
                "Escalation issues a new resync request — retry count increments");
            Assert.AreEqual(1, ReadResyncState(guest.Engine),
                "Escalation re-enters Requested state");
            Assert.AreEqual(0, onResyncCompletedFireCount,
                "OnResyncCompleted must NOT fire on mismatch path");
        }

        // ── (c) Unexpected drop — entry guard skips cascade ──

        [Test]
        public void HandleFullStateReceived_UnexpectedDrop_DoesNotExecuteCascade()
        {
            var guest = _harness.Guests[0]; // FullState receiver (non-host) — avoids the corrective reset cascade

            PopulatePendingCommands(guest.Engine, count: 5);
            SetResyncState(guest.Engine, value: 0); // ResyncState.None
            SetExpectingFullState(guest.Engine, false);

            int inputBufferBefore = ReadInputBufferCount(guest.Engine);
            int pendingBefore = ReadPendingCommandsCount(guest.Engine);
            int verifiedBefore = guest.Engine.LastVerifiedTick;
            int currentTickBefore = guest.Engine.CurrentTick;
            int dropCountBefore = ReadUnexpectedDropCount(guest.Engine);
            int lastMatchedBefore = ReadLastMatchedSyncTick(guest.Engine);

            Assert.Greater(inputBufferBefore, 0, "Setup precondition — buffer populated");
            Assert.AreEqual(5, pendingBefore, "Setup precondition — pending populated");

            const int applyTick = 60;
            InvokeHandleFullStateReceived(
                guest.Engine, applyTick, BitConverter.GetBytes(0L), 0L, FullStateKind.Unicast);

            Assert.AreEqual(inputBufferBefore, ReadInputBufferCount(guest.Engine),
                "Entry guard must NOT clear _inputBuffer");
            Assert.AreEqual(pendingBefore, ReadPendingCommandsCount(guest.Engine),
                "Entry guard must NOT clear _pendingCommands");
            Assert.AreEqual(verifiedBefore, guest.Engine.LastVerifiedTick,
                "Entry guard must NOT modify _lastVerifiedTick");
            Assert.AreEqual(currentTickBefore, guest.Engine.CurrentTick,
                "Entry guard must NOT modify CurrentTick");
            Assert.AreEqual(lastMatchedBefore, ReadLastMatchedSyncTick(guest.Engine),
                "Entry guard must NOT modify _lastMatchedSyncTick");
            Assert.AreEqual(1, ReadUnexpectedDropCount(guest.Engine) - dropCountBefore,
                "Entry guard must increment _unexpectedFullStateDropCount");
            Assert.IsTrue(_log.Contains(KLogLevel.Warning, "not in Requested state, ignoring"),
                "Entry guard must emit drop log");
        }

        // ── (d) P2P Late Join — buffers clear + verified = tick + StartCatchingUp ──

        [Test]
        public void ApplyP2PLateJoinFullState_ClearsBuffersAndStartsCatchup()
        {
            var guest = _harness.Guests[0]; // FullState receiver (non-host) — avoids the corrective reset cascade

            PopulatePendingCommands(guest.Engine, count: 5);
            SetExpectingFullState(guest.Engine, true);

            Assert.IsFalse(_harness.IsCatchingUp(guest),
                "Setup precondition — engine must not be catching up yet");
            Assert.Greater(ReadInputBufferCount(guest.Engine), 0,
                "Setup precondition — _inputBuffer populated");
            Assert.AreEqual(5, ReadPendingCommandsCount(guest.Engine),
                "Setup precondition — _pendingCommands populated");

            const int applyTick = 60;
            byte[] stateData = guest.Simulation.SerializeFullState();
            long hash = guest.Simulation.GetStateHash();

            InvokeApplyP2PLateJoinFullState(guest.Engine, applyTick, stateData, hash);

            Assert.AreEqual(0, ReadInputBufferCount(guest.Engine),
                "_inputBuffer.Clear() must execute");
            Assert.AreEqual(0, ReadPendingCommandsCount(guest.Engine),
                "_pendingCommands.Clear() must execute");
            Assert.AreEqual(applyTick, guest.Engine.LastVerifiedTick,
                "P2P LateJoin sets _lastVerifiedTick = tick (not tick - 1, distinct from HandleFullStateReceived)");
            Assert.AreEqual(applyTick, ReadLastMatchedSyncTick(guest.Engine),
                "LateJoin auto-sets _lastMatchedSyncTick");
            Assert.AreEqual(0, ReadConsecutiveDesyncCount(guest.Engine),
                "LateJoin resets _consecutiveDesyncCount");
            Assert.AreEqual(0, ReadResyncRetryCount(guest.Engine),
                "LateJoin resets _resyncRetryCount");
            Assert.IsFalse(ReadHasPendingRollback(guest.Engine),
                "LateJoin clears _hasPendingRollback");
            Assert.IsTrue(_harness.IsCatchingUp(guest),
                "ApplyP2PLateJoinFullState must call StartCatchingUp");
            Assert.IsTrue(_log.Contains(KLogLevel.Information, "Late Join FullState received"),
                "LateJoin must emit info log");
        }
    }
}
