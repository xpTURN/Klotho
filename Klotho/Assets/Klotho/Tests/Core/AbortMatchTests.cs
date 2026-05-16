using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Validates IKlothoEngine.AbortMatch — the sole entry point for KlothoState.Aborted.
    /// Covers Gate 4.1 / Gate A of IMP38 Plan-P2PChainStallWatchdog.
    /// </summary>
    [TestFixture]
    public class AbortMatchTests
    {
        private KlothoTestHarness _harness;
        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("AbortMatchTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _harness = new KlothoTestHarness(_logger);
            _harness.CreateHost(4);
            _harness.AddGuest();
            _harness.StartPlaying();
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        [Test]
        public void AbortMatch_TransitionsToAbortedAndEmitsEvent()
        {
            var engine = _harness.Host.Engine;
            AbortReason capturedReason = AbortReason.Unknown;
            int eventFireCount = 0;
            engine.OnMatchAborted += r => { capturedReason = r; eventFireCount++; };

            Assert.AreEqual(KlothoState.Running, engine.State, "Pre-condition: engine should be Running");

            engine.AbortMatch(AbortReason.ChainStallTimeout);

            Assert.AreEqual(KlothoState.Aborted, engine.State, "State should transition to Aborted");
            Assert.AreEqual(1, eventFireCount, "OnMatchAborted should fire exactly once");
            Assert.AreEqual(AbortReason.ChainStallTimeout, capturedReason, "Event should carry the reason");
        }

        [Test]
        public void AbortMatch_IsIdempotent_OnSecondCall()
        {
            var engine = _harness.Host.Engine;
            int eventFireCount = 0;
            engine.OnMatchAborted += _ => eventFireCount++;

            engine.AbortMatch(AbortReason.ChainStallTimeout);
            engine.AbortMatch(AbortReason.StateDivergence);

            Assert.AreEqual(KlothoState.Aborted, engine.State, "State should remain Aborted");
            Assert.AreEqual(1, eventFireCount, "Second AbortMatch must not re-emit OnMatchAborted");
        }

        [Test]
        public void AbortMatch_AfterFinished_DoesNothing()
        {
            var engine = _harness.Host.Engine;
            int eventFireCount = 0;
            engine.OnMatchAborted += _ => eventFireCount++;

            engine.Stop();
            Assert.AreEqual(KlothoState.Finished, engine.State, "Pre-condition: Stop() transitions to Finished");

            engine.AbortMatch(AbortReason.ChainStallTimeout);

            Assert.AreEqual(KlothoState.Finished, engine.State, "AbortMatch must not overwrite Finished");
            Assert.AreEqual(0, eventFireCount, "OnMatchAborted must not fire when already Finished");
        }

        [Test]
        public void AbortMatch_DoesNotCallStop_StateMachinePreserved()
        {
            var engine = _harness.Host.Engine;

            int tickBeforeAbort = engine.CurrentTick;
            int verifiedTickBeforeAbort = engine.LastVerifiedTick;

            engine.AbortMatch(AbortReason.ChainStallTimeout);

            Assert.AreEqual(KlothoState.Aborted, engine.State,
                "State must be Aborted (not Finished) — AbortMatch must not call Stop()");
            Assert.AreEqual(tickBeforeAbort, engine.CurrentTick,
                "CurrentTick must not be reset by AbortMatch");
            Assert.AreEqual(verifiedTickBeforeAbort, engine.LastVerifiedTick,
                "LastVerifiedTick must not be reset by AbortMatch");
        }
    }
}
