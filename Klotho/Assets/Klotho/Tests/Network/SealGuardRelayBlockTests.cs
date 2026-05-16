using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ZLogger.Unity;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Relay seal-block path lock-in for KlothoNetworkService.HandleCommandMessage.
    ///   (a) sealed (tick, playerId) from guest -> RelaySealDropCount++ + RelayMessage suppressed
    ///   (b) unsealed (tick, playerId) from guest -> RelayMessage forwarded (positive control)
    ///   (c) local-origin (fromPeerId = -1) on sealed slot -> guard bypassed (no drop, no relay)
    ///   (d) non-host peer receives sealed message -> guard bypassed (no drop, no relay)
    /// Companion to InputBufferTests F-1/F-2/F-3 (InputBuffer-level seal contract).
    /// </summary>
    [TestFixture]
    public class SealGuardRelayBlockTests
    {
        // ── Reflection handles ───────────────────────────────────────────────

        private static readonly MethodInfo _handleCommandMessageMethod = typeof(KlothoNetworkService)
            .GetMethod("HandleCommandMessage", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _inputBufferField = typeof(KlothoEngine)
            .GetField("_inputBuffer", BindingFlags.NonPublic | BindingFlags.Instance);

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void InvokeHandleCommandMessage(KlothoNetworkService service, CommandMessage msg, int fromPeerId)
            => _handleCommandMessageMethod.Invoke(service, new object[] { msg, fromPeerId });

        private static InputBuffer ReadInputBuffer(KlothoEngine engine)
            => (InputBuffer)_inputBufferField.GetValue(engine);

        private static void SealCommandAt(KlothoEngine engine, int tick, int playerId)
            => ReadInputBuffer(engine).SealEmpty(tick, playerId);

        // CommandDataSpan length must be >= 4 to pass the malformed-length guard at the top of
        // HandleCommandMessage. The first 4 bytes are read as commandType by DeserializeCommandRaw;
        // we encode 0xFFFFFFFF (int.MinValue path) — an unlikely commandType — so the factory falls
        // through to the null-return path instead of attempting to deserialize beyond the 4 bytes.
        private static CommandMessage MakeCommandMessage(int tick, int playerId)
        {
            var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
            return new CommandMessage
            {
                Tick = tick,
                PlayerId = playerId,
                SenderTick = tick,
                CommandData = data,
            };
        }

        // ── Fixture state ────────────────────────────────────────────────────

        private LogCapture _log;
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
            _logger = loggerFactory.CreateLogger("Tests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _log = new LogCapture();
            _harness = new KlothoTestHarness(_log);
            _harness.CreateHost(4);
            // Two guests required so that RelayMessage has at least one forward target
            // after excluding the sender peer (positive control in test (b)).
            _harness.AddGuest();
            _harness.AddGuest();
            _harness.StartPlaying();
            _log.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        // ── (a) Sealed (tick, playerId) from guest -> RelaySealDropCount++ + relay suppressed ──

        [Test]
        public void HandleCommandMessage_FromGuest_OnSealedTickPlayer_BlocksRelayAndIncrementsDropCount()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            int guestPlayerId = guest.LocalPlayerId;
            int guestPeerId = guest.Transport.LocalPeerId;

            SealCommandAt(host.Engine, tick: 30, playerId: guestPlayerId);
            Assert.IsTrue(host.Engine.IsCommandSealed(30, guestPlayerId),
                "Setup precondition — (30, guestPlayerId) must be sealed on host engine");

            int dropCountBefore = host.NetworkService.RelaySealDropCount;
            int sendCountBefore = host.Transport.SendCallCount;

            var msg = MakeCommandMessage(tick: 30, playerId: guestPlayerId);
            InvokeHandleCommandMessage(host.NetworkService, msg, fromPeerId: guestPeerId);

            Assert.AreEqual(1, host.NetworkService.RelaySealDropCount - dropCountBefore,
                "Sealed slot must increment _relaySealDropCount by exactly 1");
            Assert.AreEqual(0, host.Transport.SendCallCount - sendCountBefore,
                "Sealed slot must suppress RelayMessage (no Send call on host transport)");
        }

        // ── (b) Unsealed (tick, playerId) from guest -> RelayMessage forwarded (positive control) ──

        [Test]
        public void HandleCommandMessage_FromGuest_OnUnsealedTickPlayer_RelaysNormallyWithoutDropCount()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            int guestPlayerId = guest.LocalPlayerId;
            int guestPeerId = guest.Transport.LocalPeerId;

            Assert.IsFalse(host.Engine.IsCommandSealed(30, guestPlayerId),
                "Setup precondition — (30, guestPlayerId) must NOT be sealed");

            int dropCountBefore = host.NetworkService.RelaySealDropCount;
            int sendCountBefore = host.Transport.SendCallCount;

            var msg = MakeCommandMessage(tick: 30, playerId: guestPlayerId);
            InvokeHandleCommandMessage(host.NetworkService, msg, fromPeerId: guestPeerId);

            Assert.AreEqual(0, host.NetworkService.RelaySealDropCount - dropCountBefore,
                "Unsealed slot must NOT increment _relaySealDropCount");
            Assert.GreaterOrEqual(host.Transport.SendCallCount - sendCountBefore, 1,
                "Unsealed slot must invoke RelayMessage (at least one Send call on host transport)");
        }

        // ── (c) Local-origin (fromPeerId = -1) on sealed slot -> guard bypassed ──

        [Test]
        public void HandleCommandMessage_LocalOrigin_OnSealedTickPlayer_BypassesSealGuardWithoutDrop()
        {
            var host = _harness.Host;
            int hostPlayerId = host.LocalPlayerId;

            SealCommandAt(host.Engine, tick: 30, playerId: hostPlayerId);
            Assert.IsTrue(host.Engine.IsCommandSealed(30, hostPlayerId),
                "Setup precondition — (30, hostPlayerId) must be sealed on host engine");

            int dropCountBefore = host.NetworkService.RelaySealDropCount;
            int sendCountBefore = host.Transport.SendCallCount;

            var msg = MakeCommandMessage(tick: 30, playerId: hostPlayerId);
            InvokeHandleCommandMessage(host.NetworkService, msg, fromPeerId: -1);

            Assert.AreEqual(0, host.NetworkService.RelaySealDropCount - dropCountBefore,
                "Local-origin path must skip seal-check — no drop even though slot is sealed");
            Assert.AreEqual(0, host.Transport.SendCallCount - sendCountBefore,
                "Local-origin path must not invoke RelayMessage (guard `IsHost && fromPeerId != -1` is false)");
        }

        // ── (d) Non-host peer receives sealed message -> guard bypassed ──

        [Test]
        public void HandleCommandMessage_OnGuestPeer_OnSealedTickPlayer_BypassesSealGuardWithoutDrop()
        {
            var host = _harness.Host;
            var guest = _harness.Guests[0];
            int sealedPlayerId = host.LocalPlayerId;

            SealCommandAt(guest.Engine, tick: 30, playerId: sealedPlayerId);
            Assert.IsTrue(guest.Engine.IsCommandSealed(30, sealedPlayerId),
                "Setup precondition — (30, sealedPlayerId) must be sealed on guest engine");

            int dropCountBefore = guest.NetworkService.RelaySealDropCount;
            int sendCountBefore = guest.Transport.SendCallCount;

            var msg = MakeCommandMessage(tick: 30, playerId: sealedPlayerId);
            // Simulate guest receiving a message from host (peerId = 0).
            InvokeHandleCommandMessage(guest.NetworkService, msg, fromPeerId: 0);

            Assert.AreEqual(0, guest.NetworkService.RelaySealDropCount - dropCountBefore,
                "Non-host peer must skip seal-check — no drop even though slot is sealed");
            Assert.AreEqual(0, guest.Transport.SendCallCount - sendCountBefore,
                "Non-host peer must not invoke RelayMessage (guard `IsHost && fromPeerId != -1` is false)");
        }
    }
}
