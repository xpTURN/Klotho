using System;
using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// The late-join WaitingForFullState→CatchingUp transition in
    /// HandleFullStateResponse must NOT fire on a CorrectiveReset broadcast (it would anchor catchup
    /// bookkeeping on the corrective-reset tick). The guard is `!= CorrectiveReset` (blacklist), not
    /// `== Unicast` (whitelist), so a future revived InitialState-seed late-join still completes.
    ///
    /// LATENT / DEFENSIVE: the current KlothoConnection flow seeds via SeedPlayersFromCatchupPayload
    /// and never sets WaitingForFullState (the sole setter HandleLateJoinAccept is dead), so this
    /// branch is unreachable live. The state is therefore force-injected via a reflection seam and
    /// HandleFullStateResponse invoked directly — this unit-verifies the latent branch, not a live
    /// scenario.
    /// </summary>
    [TestFixture]
    public class LateJoinKindGuardTests
    {
        private static readonly FieldInfo LateJoinStateField = typeof(KlothoNetworkService)
            .GetField("_lateJoinState", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo EngineField = typeof(KlothoNetworkService)
            .GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo HandleFullStateResponseMethod = typeof(KlothoNetworkService)
            .GetMethod("HandleFullStateResponse", BindingFlags.NonPublic | BindingFlags.Instance);

        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("LateJoinKindGuardTests");
        }

        // A guest service (IsHost defaults false) with a network-less engine injected so the
        // CatchingUp branch's StartCatchingUp() call is satisfied. OnFullStateReceived is left
        // unsubscribed, so the message's StateData is never deserialized.
        private KlothoNetworkService MakeGuestService()
        {
            var engine = new KlothoEngine(new SimulationConfig { MaxRollbackTicks = 50 }, new SessionConfig());
            engine.Initialize(new TestSimulation(), _logger);

            var service = new KlothoNetworkService();
            EngineField.SetValue(service, engine);
            return service;
        }

        private void SetLateJoinState(KlothoNetworkService service, string name)
            => LateJoinStateField.SetValue(service, Enum.Parse(LateJoinStateField.FieldType, name));

        private string GetLateJoinState(KlothoNetworkService service)
            => LateJoinStateField.GetValue(service).ToString();

        private void InvokeHandle(KlothoNetworkService service, FullStateKind kind)
        {
            var msg = new FullStateResponseMessage
            {
                Tick = 10,
                StateHash = 0,
                StateData = new byte[8],
                StaticFingerprint = 0, // 0 = not provided → receiver skips the fingerprint check
                KindEnum = kind,
            };
            HandleFullStateResponseMethod.Invoke(service, new object[] { msg });
        }

        /// <summary>CorrectiveReset must NOT drive the late-join transition.</summary>
        [Test]
        public void CorrectiveReset_DoesNotCompleteLateJoin()
        {
            var service = MakeGuestService();
            SetLateJoinState(service, "WaitingForFullState");

            InvokeHandle(service, FullStateKind.CorrectiveReset);

            Assert.AreEqual("WaitingForFullState", GetLateJoinState(service),
                "A CorrectiveReset broadcast must not transition WaitingForFullState → CatchingUp (R-11).");
        }

        /// <summary>A Unicast late-join response completes the join (unchanged behavior).</summary>
        [Test]
        public void Unicast_CompletesLateJoin()
        {
            var service = MakeGuestService();
            SetLateJoinState(service, "WaitingForFullState");

            InvokeHandle(service, FullStateKind.Unicast);

            Assert.AreEqual("CatchingUp", GetLateJoinState(service),
                "A Unicast response must complete the join (WaitingForFullState → CatchingUp).");
        }

        /// <summary>
        /// InitialState also completes the join — pins the blacklist (`!= CorrectiveReset`) choice:
        /// a `== Unicast` whitelist would wrongly block this (the future InitialState-seed revival case).
        /// </summary>
        [Test]
        public void InitialState_CompletesLateJoin()
        {
            var service = MakeGuestService();
            SetLateJoinState(service, "WaitingForFullState");

            InvokeHandle(service, FullStateKind.InitialState);

            Assert.AreEqual("CatchingUp", GetLateJoinState(service),
                "InitialState (non-CorrectiveReset) must complete the join — blacklist guard, not Unicast whitelist.");
        }
    }
}
