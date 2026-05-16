using System;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    [TestFixture]
    public class ChainStallWatchdogTests
    {
        private KlothoTestHarness _harness;
        private ILogger _logger;

        private static readonly FieldInfo _lastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _reconnectStateField = typeof(KlothoNetworkService)
            .GetField("_reconnectState", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Type _reconnectStateEnumType = typeof(KlothoNetworkService)
            .GetNestedType("ReconnectState", BindingFlags.NonPublic);

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("ChainStallWatchdogTests");
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

        private void SetLagOnHost(int lag)
        {
            var engine = _harness.Host.Engine;
            int currentTick = engine.CurrentTick;
            _lastVerifiedTickField.SetValue(engine, currentTick - lag);
        }

        private int ComputeThreshold()
        {
            var engine = _harness.Host.Engine;
            int reconnectTimeoutTicks = engine.SessionConfig.ReconnectTimeoutMs / engine.SimulationConfig.TickIntervalMs;
            return Math.Max(reconnectTimeoutTicks + 100, engine.SimulationConfig.MinStallAbortTicks);
        }

        [Test]
        public void Watchdog_DoesNotFire_WhenLagBelowThreshold()
        {
            int threshold = ComputeThreshold();
            SetLagOnHost(threshold - 1);

            int eventFireCount = 0;
            _harness.Host.Engine.OnMatchAborted += _ => eventFireCount++;

            _harness.Host.NetworkService.CheckChainStallTimeout();

            Assert.AreEqual(KlothoState.Running, _harness.Host.Engine.State);
            Assert.AreEqual(0, eventFireCount);
        }

        [Test]
        public void Watchdog_FiresAbort_WhenLagExceedsThreshold()
        {
            int threshold = ComputeThreshold();
            SetLagOnHost(threshold);

            AbortReason captured = AbortReason.Unknown;
            int eventFireCount = 0;
            _harness.Host.Engine.OnMatchAborted += r => { captured = r; eventFireCount++; };

            _harness.Host.NetworkService.CheckChainStallTimeout();

            Assert.AreEqual(KlothoState.Aborted, _harness.Host.Engine.State);
            Assert.AreEqual(1, eventFireCount);
            Assert.AreEqual(AbortReason.ChainStallTimeout, captured);
        }

        [Test]
        public void Watchdog_IsIdempotent_AfterAlreadyAborted()
        {
            _harness.Host.Engine.AbortMatch(AbortReason.StateDivergence);

            int eventFireCount = 0;
            _harness.Host.Engine.OnMatchAborted += _ => eventFireCount++;

            int threshold = ComputeThreshold();
            SetLagOnHost(threshold);

            _harness.Host.NetworkService.CheckChainStallTimeout();

            Assert.AreEqual(KlothoState.Aborted, _harness.Host.Engine.State);
            Assert.AreEqual(0, eventFireCount);
        }

        [Test]
        public void Watchdog_ReturnsEarly_WhenTickIntervalMsZero()
        {
            ((SimulationConfig)_harness.Host.Engine.SimulationConfig).TickIntervalMs = 0;

            SetLagOnHost(100000);

            int eventFireCount = 0;
            _harness.Host.Engine.OnMatchAborted += _ => eventFireCount++;

            Assert.DoesNotThrow(() => _harness.Host.NetworkService.CheckChainStallTimeout());
            Assert.AreEqual(KlothoState.Running, _harness.Host.Engine.State);
            Assert.AreEqual(0, eventFireCount);
        }

        [Test]
        public void Watchdog_OnFire_ReconnectStateBecomesFailed()
        {
            var waitingState = Enum.Parse(_reconnectStateEnumType, "WaitingForTransport");
            _reconnectStateField.SetValue(_harness.Host.NetworkService, waitingState);

            int threshold = ComputeThreshold();
            SetLagOnHost(threshold);

            _harness.Host.NetworkService.CheckChainStallTimeout();

            var failedState = Enum.Parse(_reconnectStateEnumType, "Failed");
            Assert.AreEqual(failedState, _reconnectStateField.GetValue(_harness.Host.NetworkService));
        }

        [Test]
        public void Watchdog_OnFire_PhaseTransitionsToDisconnected()
        {
            int threshold = ComputeThreshold();
            SetLagOnHost(threshold);

            _harness.Host.NetworkService.CheckChainStallTimeout();

            Assert.AreEqual(SessionPhase.Disconnected, _harness.Host.NetworkService.Phase);
        }
    }
}
