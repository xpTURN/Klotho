using System;
using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// IMP102 — the Ready exchange carries two setup fingerprints (component-registry layout and static
    /// environment) so a cold-start match compares builds before the first tick. These tests pin the
    /// pieces that are easy to break silently: the wire fields, the layout/environment split, the
    /// "0 = not provided" sentinel, the phase gate, and — most importantly — the fact that the host
    /// relays the received ready VERBATIM, which is the only reason guest-to-guest comparison works.
    /// </summary>
    [TestFixture]
    public class ReadySetupFingerprintTests
    {
        // Stands in for a game's own fingerprint slot so the environment half is non-zero without
        // needing collider or navmesh systems.
        private sealed class FakeGameFingerprint : IGameFingerprintSource, ISystem
        {
            private readonly long _fp;
            public FakeGameFingerprint(long fp) { _fp = fp; }
            public long GetGameFingerprint() => _fp;
            public void Update(ref Frame frame) { }
        }

        private static KlothoEngine NewEngine(out EcsSimulation sim, LogCapture logger, long gameFp = 0)
        {
            sim = new EcsSimulation(64, maxRollbackTicks: 4, deltaTimeMs: 50);
            if (gameFp != 0)
                sim.AddSystem(new FakeGameFingerprint(gameFp), SystemPhase.Update);
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            engine.Initialize(sim, logger);
            return engine;
        }

        // ── sentinel / pure comparison ─────────────────────────────────────

        [Test]
        public void FingerprintsDiffer_TreatsZeroAsNotProvided()
        {
            Assert.IsFalse(KlothoEngine.FingerprintsDiffer(0, 0x1234), "remote-only value must not count");
            Assert.IsFalse(KlothoEngine.FingerprintsDiffer(0x1234, 0), "local-only value must not count");
            Assert.IsFalse(KlothoEngine.FingerprintsDiffer(0x1234, 0x1234), "equal is not a difference");
            Assert.IsTrue(KlothoEngine.FingerprintsDiffer(0x1234, 0x5678), "both provided and unequal");
        }

        /// <summary>
        /// Two peers whose navigation plans DIFFERENT corridors from the same content must be
        /// reported as different at Ready. The nav term folds
        /// <c>FPNavAgentSystem.NAV_BEHAVIOUR_REVISION</c> as well as the mesh, so a build-to-build
        /// pathfinding change moves the environment fingerprint — which is what this exchange
        /// compares, before the match starts.
        ///
        /// <para>Two builds are not needed to gate it: the difference reaches the comparison as a
        /// plain value, and that is the whole surface being pinned.</para>
        /// </summary>
        [Test]
        public void PeersWhosNavigationDiffers_AreReportedAsDifferent()
        {
            const long Colliders = 0x0000_1111_2222_3333L;
            const long Game      = 0x0000_7777_8888_9999L;
            const long NavRev1   = 0x0000_4444_5555_6666L;
            long navRev2 = NavRev1 ^ 0x0F0F;          // the same mesh under a bumped revision

            long local  = Colliders ^ NavRev1 ^ Game;
            long remote = Colliders ^ navRev2 ^ Game;

            Assert.IsTrue(KlothoEngine.FingerprintsDiffer(local, remote),
                "a navigation revision difference must survive the XOR fold into the environment "
                + "term — if it cancels, two peers that plan different paths shake hands");
        }

        // ── layout ^ environment split (bit-identity + orthogonality) ──────

        [Test]
        public void StaticFingerprint_EqualsLayoutXorEnvironment()
        {
            var engine = NewEngine(out _, new LogCapture(), gameFp: 0x0BAD_F00D);

            long layout = engine.GetLocalLayoutFingerprint();
            long env = engine.GetLocalEnvironmentFingerprint();
            long expected = layout ^ env;
            if (expected == 0) expected = 1; // wire sentinel, applied by the combined fold only

            Assert.AreEqual(expected, engine.GetLocalStaticFingerprint(),
                "the combined FullState fingerprint must stay bit-identical to layout ^ environment");
            Assert.AreNotEqual(0, layout, "layout is frozen once a simulation exists");
            Assert.AreEqual(0x0BAD_F00D, env,
                "environment half must exclude the registry, otherwise a layout change also moves it");
        }

        // ── verdict ────────────────────────────────────────────────────────

        [Test]
        public void CheckReadyFingerprints_MatchingLayout_IsOk()
        {
            var engine = NewEngine(out _, new LogCapture());
            long layout = engine.GetLocalLayoutFingerprint();

            Assert.AreEqual(ReadyFingerprintVerdict.Ok,
                engine.CheckReadyFingerprints(1, layout, 0, compareEnvironment: true));
        }

        [Test]
        public void CheckReadyFingerprints_DifferentLayout_IsLayoutMismatch()
        {
            var logger = new LogCapture();
            var engine = NewEngine(out _, logger);
            long layout = engine.GetLocalLayoutFingerprint();

            Assert.AreEqual(ReadyFingerprintVerdict.LayoutMismatch,
                engine.CheckReadyFingerprints(7, layout ^ 0xFF, 0, compareEnvironment: true));
            Assert.IsTrue(logger.Contains(Logging.KLogLevel.Error, "SetRuntimePrunedComponentTypeIds"),
                "the error must name the sanctioned fix (prune the difference)");
            Assert.IsTrue(logger.Contains(Logging.KLogLevel.Error, "AUTHORITY-side"),
                "the error must say the fix is an authority-side action");
        }

        [Test]
        public void CheckReadyFingerprints_ZeroRemote_IsOk()
        {
            var logger = new LogCapture();
            var engine = NewEngine(out _, logger);

            Assert.AreEqual(ReadyFingerprintVerdict.Ok,
                engine.CheckReadyFingerprints(3, 0, 0, compareEnvironment: true),
                "an unwired or older peer sends 0 and must not be refused");
            Assert.AreEqual(0, logger.CountAt(Logging.KLogLevel.Error));
        }

        [Test]
        public void CheckReadyFingerprints_PerPeerSuppression_ReportsEveryPeerOnce()
        {
            var logger = new LogCapture();
            var engine = NewEngine(out _, logger);
            long bad = engine.GetLocalLayoutFingerprint() ^ 0xFF;

            engine.CheckReadyFingerprints(1, bad, 0, compareEnvironment: false);
            engine.CheckReadyFingerprints(1, bad, 0, compareEnvironment: false); // same peer: suppressed
            engine.CheckReadyFingerprints(2, bad, 0, compareEnvironment: false); // other peer: must print

            Assert.AreEqual(2, logger.CountAt(Logging.KLogLevel.Error),
                "suppression must be per peer — a single flag would silence every peer after the first");
        }

        // ── phase gate (D-6) ───────────────────────────────────────────────

        [Test]
        public void CheckReadyFingerprints_EnvironmentGate_SkipsWhenDisabled()
        {
            var logger = new LogCapture();
            var engine = NewEngine(out _, logger, gameFp: 0x1111);
            long layout = engine.GetLocalLayoutFingerprint();

            engine.CheckReadyFingerprints(5, layout, 0x2222, compareEnvironment: false);
            Assert.AreEqual(0, logger.CountAt(Logging.KLogLevel.Error),
                "mid-match ready must not compare the environment — runtime rebakes move it legitimately");

            engine.CheckReadyFingerprints(5, layout, 0x2222, compareEnvironment: true);
            Assert.IsTrue(logger.Contains(Logging.KLogLevel.Error, "Static environment mismatch"),
                "pre-match ready compares the environment");
        }

        [Test]
        public void CheckReadyFingerprints_LayoutMismatch_DoesNotAlsoReportEnvironment()
        {
            var logger = new LogCapture();
            var engine = NewEngine(out _, logger, gameFp: 0x1111);

            // Layout differs, environment agrees — exactly one cause, so exactly one report.
            engine.CheckReadyFingerprints(9, engine.GetLocalLayoutFingerprint() ^ 0xFF, 0x1111,
                compareEnvironment: true);

            Assert.AreEqual(1, logger.CountAt(Logging.KLogLevel.Error));
            Assert.IsFalse(logger.Contains(Logging.KLogLevel.Error, "Static environment mismatch"));
        }

        // ── reject reason wiring (D-7) ─────────────────────────────────────

        [Test]
        public void JoinFailReason_LayoutMismatch_WireCodeRoundTrips()
        {
            Assert.AreEqual(12, JoinFailReason.LayoutMismatch.ToWireCode());
            Assert.AreEqual(JoinFailReason.LayoutMismatch, JoinFailReasonExtensions.FromJoinReject(12));
        }

        // ── authority reaction (D-2): refuse the peer, keep the room ───────

        [Test]
        public void Server_RefusesClientWithDifferentLayout()
        {
            TestTransport.Reset();
            var transport = new TestTransport();
            transport.Listen("localhost", 0, 4);   // IsHost — DisconnectPeer is observable
            var clientTransport = new TestTransport();
            clientTransport.Connect("localhost", 0);   // peerId 1 — the reject payload lands here

            var service = new ServerNetworkService();
            service.Initialize(transport, new CommandFactory(), new LogCapture());
            service.CreateRoom("reject-test", 4);

            var engine = NewEngine(out _, new LogCapture());
            service.SubscribeEngine(engine);

            var handler = typeof(ServerNetworkService).GetMethod(
                "HandlePlayerReadyMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handler, "reflection: HandlePlayerReadyMessage");

            var bad = new PlayerReadyMessage
            {
                PlayerId = 4,
                IsReady = true,
                LayoutFingerprint = engine.GetLocalLayoutFingerprint() ^ 0xFF,
            };
            try { handler.Invoke(service, new object[] { bad, 1 }); }
            catch (TargetInvocationException) { /* boundary state is what this asserts */ }

            Assert.AreEqual(1, transport.DisconnectPeerCallCount, "the mismatching client must be dropped");
            Assert.AreEqual(JoinFailReason.LayoutMismatch.ToWireCode(), clientTransport.LastDisconnectPayload,
                "the reject reason must reach the client on the disconnect payload");

            // Same message with a matching layout is accepted (no disconnect).
            var ok = new PlayerReadyMessage
            {
                PlayerId = 5,
                IsReady = true,
                LayoutFingerprint = engine.GetLocalLayoutFingerprint(),
            };
            try { handler.Invoke(service, new object[] { ok, 2 }); }
            catch (TargetInvocationException) { }
            Assert.AreEqual(1, transport.DisconnectPeerCallCount, "a matching client must not be dropped");

            TestTransport.Reset();
        }

        [Test]
        public void Server_AllowLayoutMismatchGate_DowngradesRefusalToLog()
        {
            TestTransport.Reset();
            var transport = new TestTransport();
            transport.Listen("localhost", 0, 4);

            var logger = new LogCapture();
            var service = new ServerNetworkService();
            service.Initialize(transport, new CommandFactory(), logger);
            service.CreateRoom("gate-test", 4);

            var engine = NewEngine(out _, logger);
            engine.AllowLayoutMismatch = true;
            service.SubscribeEngine(engine);

            var handler = typeof(ServerNetworkService).GetMethod(
                "HandlePlayerReadyMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            var bad = new PlayerReadyMessage
            {
                PlayerId = 6,
                IsReady = true,
                LayoutFingerprint = engine.GetLocalLayoutFingerprint() ^ 0xFF,
            };
            try { handler.Invoke(service, new object[] { bad, 1 }); }
            catch (TargetInvocationException) { }

            Assert.AreEqual(0, transport.DisconnectPeerCallCount, "the dev gate must not drop the peer");
            Assert.IsTrue(logger.Contains(Logging.KLogLevel.Error, "AllowLayoutMismatch is on"),
                "the log must say the check was deliberately downgraded");

            TestTransport.Reset();
        }

        // ── the load-bearing one: the host relay must carry the fields ─────

        [Test]
        public void HostRelay_PreservesReadyFingerprints()
        {
            TestTransport.Reset();
            var hostTransport = new TestTransport();
            var guestTransport = new TestTransport();
            hostTransport.Listen("localhost", 0, 4);
            guestTransport.Connect("localhost", 0);

            var hostService = new KlothoNetworkService();
            hostService.Initialize(hostTransport, new CommandFactory(), new LogCapture());
            hostService.CreateRoom("relay-test", 4);   // sets IsHost — the relay branch is host-only

            PlayerReadyMessage relayed = null;
            var serializer = new MessageSerializer();
            guestTransport.OnDataReceived += (peerId, data, len) =>
                relayed = serializer.Deserialize(data, len) as PlayerReadyMessage;

            var incoming = new PlayerReadyMessage
            {
                PlayerId = 2,
                IsReady = true,
                LayoutFingerprint = 0x0102030405060708,
                EnvironmentFingerprint = 0x1112131415161718,
            };

            var handler = typeof(KlothoNetworkService).GetMethod(
                "HandlePlayerReadyMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handler, "reflection: HandlePlayerReadyMessage");
            try
            {
                handler.Invoke(hostService, new object[] { incoming, 1 });
            }
            catch (TargetInvocationException)
            {
                // StartGame downstream needs a session config this slim fixture does not provide.
                // The relay happens before that point, which is what this test asserts.
            }

            guestTransport.PollEvents();

            Assert.IsNotNull(relayed, "host must relay the ready to other peers");
            Assert.AreEqual(incoming.LayoutFingerprint, relayed.LayoutFingerprint,
                "guest-to-guest comparison exists only because the host relays the received instance " +
                "verbatim — rebuilding the message from selected fields would silently drop this");
            Assert.AreEqual(incoming.EnvironmentFingerprint, relayed.EnvironmentFingerprint);
            TestTransport.Reset();
        }
    }
}
