using System;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Runtime.Tests.Core
{
    /// <summary>
    /// <c>KlothoEngine.OnFrameBoundary</c> — the seam a sliced rebake advances on.
    ///
    /// <para>Two properties matter and neither is obvious from the declaration. It must fire once
    /// per FRAME and not once per tick: a server-driven client re-executes about a dozen ticks per
    /// frame, so a per-tick hook would spend a slicing budget that many times over and make the
    /// spike it was meant to remove larger. And a throwing subscriber must not take the session
    /// down, because this point sits ahead of every per-mode early return — the one path every
    /// mode shares.</para>
    ///
    /// <para>The event form is itself the finding: a hook on <c>IViewCallbacks</c> would be null on
    /// the dedicated server, and one on <c>ISimulationCallbacks</c> would be null in most test
    /// harnesses, which construct the engine through the three-argument <c>Initialize</c>. Both
    /// would have passed a naive test while covering nothing.</para>
    /// </summary>
    [TestFixture]
    public class FrameBoundaryHookTests
    {
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = KLoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(KLogLevel.Warning);
                logging.AddUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("FrameBoundaryHookTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
        }

        [Test]
        public void FiresOncePerUpdate_NotOncePerTick()
        {
            var harness = new KlothoTestHarness(_logger);
            harness.CreateHost(2);
            harness.AddGuest();
            harness.StartPlaying();

            int frames = 0;
            int ticksSeen = 0;
            harness.Host.Engine.OnFrameBoundary += _ => frames++;
            harness.Host.Engine.OnTickExecuted += _ => ticksSeen++;

            const int updates = 12;
            for (int i = 0; i < updates; i++)
                harness.Tick();

            Assert.AreEqual(updates, frames,
                "the hook must fire exactly once per Update — that is the whole reason it exists");
            Assert.GreaterOrEqual(ticksSeen, 1, "the fixture executed no ticks, so the comparison is vacuous");
        }

        [Test]
        public void ReceivesTheUpdateDelta()
        {
            var harness = new KlothoTestHarness(_logger);
            harness.CreateHost(2);
            harness.AddGuest();
            harness.StartPlaying();

            float seen = -1f;
            harness.Host.Engine.OnFrameBoundary += dt => seen = dt;
            harness.Tick(0.025f);

            Assert.AreEqual(0.025f, seen, 1e-6f);
        }

        [Test]
        public void AThrowingSubscriber_IsContained()
        {
            var harness = new KlothoTestHarness(_logger);
            harness.CreateHost(2);
            harness.AddGuest();
            harness.StartPlaying();

            int before = 0;
            harness.Host.Engine.OnFrameBoundary += _ => before++;
            harness.Host.Engine.OnFrameBoundary += _ => throw new InvalidOperationException("boom");

            Assert.DoesNotThrow(() => harness.Tick());
            Assert.DoesNotThrow(() => harness.Tick());

            // The session survives and keeps calling the hook every frame. Note what is NOT
            // claimed: the guard wraps the whole invocation, so a throw skips the subscribers
            // registered after it for that frame. Catching per subscriber would mean walking
            // GetInvocationList every frame, which allocates — the wrong trade for a hook whose
            // point is to be cheap.
            Assert.AreEqual(2, before);
        }

        [Test]
        public void NoSubscribers_IsANullCheck()
        {
            var harness = new KlothoTestHarness(_logger);
            harness.CreateHost(2);
            harness.AddGuest();
            harness.StartPlaying();
            Assert.DoesNotThrow(() => harness.Tick());
        }
    }
}
