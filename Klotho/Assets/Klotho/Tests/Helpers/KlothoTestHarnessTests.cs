using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Helper.Tests
{
    [TestFixture]
    public class KlothoTestHarnessTests
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
            _logger = loggerFactory.CreateLogger("KlothoTestHarnessTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _harness = new KlothoTestHarness(_logger);
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        [Test]
        public void AdvanceWithStalledPeer_LastVerifiedTickStays_CurrentTickAdvances()
        {
            _harness.CreateHost(4);
            var guest = _harness.AddGuest();
            _harness.StartPlaying();

            int stallPlayerId = guest.LocalPlayerId;
            int inputDelay = _harness.Host.Engine.InputDelay;
            int currentTickBefore = _harness.Host.CurrentTick;

            const int targetTick = 10;
            _harness.AdvanceWithStalledPeer(currentTickBefore + targetTick, stallPlayerId);

            // Pre-inserted empty commands cover ticks [0, InputDelay), so the verified chain
            // may advance up to InputDelay ticks even with a stalled peer. Beyond that it must freeze.
            Assert.GreaterOrEqual(_harness.Host.CurrentTick, currentTickBefore + targetTick,
                "CurrentTick should reach the requested target via prediction");
            Assert.LessOrEqual(_harness.Host.Engine.LastVerifiedTick, currentTickBefore + inputDelay,
                "LastVerifiedTick must not advance past the pre-inserted window — stalled peer's input missing → quorum fails");
        }
    }
}
