using System;
using System.Reflection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    [TestFixture]
    public class ApplyFullStateHashGateTests
    {
        private LogCapture _log;
        private KlothoTestHarness _harness;

        private static readonly MethodInfo _applyFullStateMethod = typeof(KlothoEngine)
            .GetMethod("ApplyFullState", BindingFlags.NonPublic | BindingFlags.Instance);

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

            _log.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        private void InvokeApplyFullState(int tick, byte[] stateData, long stateHash, ApplyReason reason = ApplyReason.LateJoin)
        {
            _applyFullStateMethod.Invoke(_harness.Host.Engine, new object[] { tick, stateData, stateHash, reason });
        }

        [Test]
        public void ApplyFullState_EmitsHashCheckLog_WhenHashMatches()
        {
            const long state = 0x1234_5678_9ABC_DEF0L;
            InvokeApplyFullState(5, BitConverter.GetBytes(state), state);

            Assert.IsTrue(_log.Contains(LogLevel.Information, "[FullStateResync] hash check"),
                "Hash check log line must be emitted on every ApplyFullState invocation");
            Assert.IsTrue(_log.Contains(LogLevel.Information, "match=True"),
                "Matching hashes must produce match=True");
            Assert.IsFalse(_log.Contains(LogLevel.Error, "hash mismatch"),
                "Matching hashes must not emit the mismatch error line");
        }

        [Test]
        public void ApplyFullState_EmitsHashCheckLog_WhenHashMismatches()
        {
            const long restoredState = 0x0AAA_AAAA_AAAA_AAAAL;
            const long advertisedHash = 0x0BBB_BBBB_BBBB_BBBBL;
            InvokeApplyFullState(7, BitConverter.GetBytes(restoredState), advertisedHash);

            Assert.IsTrue(_log.Contains(LogLevel.Information, "[FullStateResync] hash check"),
                "Hash check log line must be emitted on every ApplyFullState invocation");
            Assert.IsTrue(_log.Contains(LogLevel.Information, "match=False"),
                "Mismatched hashes must produce match=False");
            Assert.IsTrue(_log.Contains(LogLevel.Error, "hash mismatch"),
                "Mismatched hashes must also emit the diagnostic error line");
        }

        [Test]
        public void ApplyFullState_OnMismatch_FiresOnHashMismatchEvent()
        {
            const int targetTick = 11;
            const long restoredState = 0x0AAA_AAAA_AAAA_AAAAL;
            const long advertisedHash = 0x0BBB_BBBB_BBBB_BBBBL;

            int capturedTick = -1;
            long capturedLocal = 0;
            long capturedRemote = 0;
            int fireCount = 0;
            _harness.Host.Engine.OnHashMismatch += (tick, local, remote) =>
            {
                capturedTick = tick;
                capturedLocal = local;
                capturedRemote = remote;
                fireCount++;
            };

            InvokeApplyFullState(targetTick, BitConverter.GetBytes(restoredState), advertisedHash);

            Assert.AreEqual(1, fireCount, "OnHashMismatch must fire exactly once on a mismatch");
            Assert.AreEqual(targetTick, capturedTick);
            Assert.AreEqual(restoredState, capturedLocal);
            Assert.AreEqual(advertisedHash, capturedRemote);
        }

        [Test]
        public void ApplyFullState_OnMatchingHash_DoesNotFireOnHashMismatchEvent()
        {
            const long state = 0x0C0D_E0C0_DE0C_0DE0L;

            int fireCount = 0;
            _harness.Host.Engine.OnHashMismatch += (_, _, _) => fireCount++;

            InvokeApplyFullState(13, BitConverter.GetBytes(state), state);

            Assert.AreEqual(0, fireCount, "OnHashMismatch must not fire when hashes agree");
        }

        [Test]
        public void ApplyFullState_OnMismatch_AlsoFiresOnDesyncDetected()
        {
            const long restoredState = 0x0AAA_AAAA_AAAA_AAAAL;
            const long advertisedHash = 0x0BBB_BBBB_BBBB_BBBBL;

            long capturedLocal = 0;
            long capturedRemote = 0;
            int fireCount = 0;
            _harness.Host.Engine.OnDesyncDetected += (local, remote) =>
            {
                capturedLocal = local;
                capturedRemote = remote;
                fireCount++;
            };

            InvokeApplyFullState(17, BitConverter.GetBytes(restoredState), advertisedHash);

            Assert.AreEqual(1, fireCount, "OnDesyncDetected must fire on ApplyFullState mismatch so the mid-match desync pipeline can react");
            Assert.AreEqual(restoredState, capturedLocal);
            Assert.AreEqual(advertisedHash, capturedRemote);
        }

        [Test]
        public void ApplyFullState_OnMatchingHash_DoesNotFireOnDesyncDetected()
        {
            const long state = 0x0FEE_DBEE_FACE_F00DL;

            int fireCount = 0;
            _harness.Host.Engine.OnDesyncDetected += (_, _) => fireCount++;

            InvokeApplyFullState(19, BitConverter.GetBytes(state), state);

            Assert.AreEqual(0, fireCount, "OnDesyncDetected must not fire when hashes agree");
        }
    }
}
