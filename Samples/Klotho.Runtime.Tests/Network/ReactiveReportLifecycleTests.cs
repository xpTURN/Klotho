using System.Collections.Generic;
using System.Reflection;

using NUnit.Framework;

using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// The host's per-peer reactive report store (`_reportedEffective`) is
    /// dropped on disconnect, so a stale-high reactive report from a departed/reconnecting guest cannot
    /// pin the broadcast baseline (a seed re-establishes it on rejoin).
    /// </summary>
    [TestFixture]
    internal class ReactiveReportLifecycleTests
    {
        private static readonly FieldInfo _reportedEffectiveField = typeof(KlothoNetworkService)
            .GetField("_reportedEffective", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Warning);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("ReactiveReportLifecycleTests");
        }

        [Test]
        public void Disconnect_ResetsReportedEffective()
        {
            var harness = new KlothoTestHarness(_logger);
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();

                int peerId = guest.Transport.LocalPeerId;
                var reported = (Dictionary<int, int>)_reportedEffectiveField.GetValue(harness.Host.NetworkService);

                reported[peerId] = 20; // simulate an absorbed reactive report from this guest
                Assert.IsTrue(reported.ContainsKey(peerId), "precondition: report seeded for the guest peer");

                harness.DisconnectPeer(guest);
                harness.PumpMessages();

                Assert.IsFalse(reported.ContainsKey(peerId),
                    "R2: host must drop _reportedEffective[peerId] on disconnect (no stale-high baseline pin)");
            }
            finally
            {
                harness.Reset();
            }
        }
    }
}
