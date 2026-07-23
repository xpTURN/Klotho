using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Verifies that HandleGameStartMessage applies the 17 SessionConfig fields in place
    /// without replacing the SessionConfig instance, preserving the engine reference.
    /// </summary>
    [TestFixture]
    public class SessionConfigPropagationTests
    {
        private TestTransport _transport;
        private CommandFactory _commandFactory;
        private LogCapture _logger;

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            _transport = new TestTransport();
            _commandFactory = new CommandFactory();
            _logger = new LogCapture();
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        [Test]
        public void HandleGameStartMessage_AppliesAllFieldsInPlace_PreservesReference()
        {
            var svc = new ServerDrivenClientService();
            svc.Initialize(_transport, _commandFactory, _logger);

            ISessionConfig beforeCfg = GetSessionConfig(svc);
            Assert.IsNotNull(beforeCfg, "Initialize should seed a default SessionConfig");

            var msg = new GameStartMessage
            {
                StartTime = 0,
                RandomSeed = 12345,
                MaxPlayers = 6,
                MinPlayers = 3,
                MaxSpectators = 9,
                AllowLateJoin = false,
                LateJoinDelayTicks = 20,
                ReconnectTimeoutMs = 30000,
                ReconnectMaxRetries = 5,
                LateJoinDelaySafety = 4,
                RttSanityMaxMs = 200,
                MinStallAbortTicks = 500,
                CountdownDurationMs = 2000,
                AbortGraceMs = 2500,
                EndGracePolicy = (int)EndGracePolicy.Pause,
                EndGraceMs = 6000,
                ClientShutdownGraceMs = 5500,
            };

            InvokePrivate(svc, "HandleGameStartMessage", new object[] { msg });

            ISessionConfig afterCfg = GetSessionConfig(svc);

            Assert.AreSame(beforeCfg, afterCfg,
                "mutation-in-place must not replace the SessionConfig instance");

            Assert.AreEqual(12345, afterCfg.RandomSeed);
            Assert.AreEqual(6, afterCfg.MaxPlayers);
            Assert.AreEqual(3, afterCfg.MinPlayers);
            Assert.AreEqual(9, afterCfg.MaxSpectators);
            Assert.AreEqual(false, afterCfg.AllowLateJoin);
            Assert.AreEqual(20, afterCfg.LateJoinDelayTicks);
            Assert.AreEqual(30000, afterCfg.ReconnectTimeoutMs);
            Assert.AreEqual(5, afterCfg.ReconnectMaxRetries);
            Assert.AreEqual(4, afterCfg.LateJoinDelaySafety);
            Assert.AreEqual(200, afterCfg.RttSanityMaxMs);
            Assert.AreEqual(500, afterCfg.MinStallAbortTicks);
            Assert.AreEqual(2000, afterCfg.CountdownDurationMs);
            Assert.AreEqual(2500, afterCfg.AbortGraceMs);
            Assert.AreEqual(EndGracePolicy.Pause, afterCfg.EndGracePolicy);
            Assert.AreEqual(6000, afterCfg.EndGraceMs);
            Assert.AreEqual(5500, afterCfg.ClientShutdownGraceMs);
        }

        [Test]
        public void HandleGameStartMessage_EndGracePolicyContinue_RoundtripsCorrectly()
        {
            var svc = new ServerDrivenClientService();
            svc.Initialize(_transport, _commandFactory, _logger);

            var msg = new GameStartMessage
            {
                EndGracePolicy = (int)EndGracePolicy.Continue,
            };

            InvokePrivate(svc, "HandleGameStartMessage", new object[] { msg });

            Assert.AreEqual(EndGracePolicy.Continue, GetSessionConfig(svc).EndGracePolicy);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static ISessionConfig GetSessionConfig(ServerDrivenClientService svc)
        {
            var field = typeof(ServerDrivenClientService).GetField(
                "_sessionConfig",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "reflection: _sessionConfig");
            return (ISessionConfig)field.GetValue(svc);
        }

        private static void InvokePrivate(object target, string methodName, object[] args)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(method, $"reflection: {methodName}");
            method.Invoke(target, args);
        }
    }
}
