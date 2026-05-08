using System;
using System.Reflection;
using NUnit.Framework;
using Microsoft.Extensions.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Client-side ZLogWarning on malformed payloads.
    /// Verifies the trace log fires for the 3 client wire-input sites
    /// (ServerDrivenClientService / SpectatorService / KlothoConnection)
    /// without changing the disconnect policy.
    /// </summary>
    [TestFixture]
    public class ClientServiceMalformedLogTests
    {
        private TestTransport _transport;
        private CommandFactory _commandFactory;
        private MessageSerializer _serializer;
        private LogCapture _logger;

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            _transport = new TestTransport();
            _commandFactory = new CommandFactory();
            _serializer = new MessageSerializer();
            _logger = new LogCapture();
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        // ── L2c-1 ───────────────────────────────────────────────────────────

        [Test]
        public void ServerDrivenClientService_Malformed_LogsWarning()
        {
            var svc = new ServerDrivenClientService();
            svc.Initialize(_transport, _commandFactory, _logger);

            byte[] data = MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin);
            InvokePrivate(svc, "HandleDataReceived", new object[] { 0, data, data.Length });

            Assert.IsTrue(_logger.Contains(LogLevel.Warning, "Malformed payload"),
                "client should log warning on malformed payload");
            Assert.IsTrue(_logger.Contains(LogLevel.Warning, "[ServerDrivenClientService]"),
                "warning should carry the SD client tag");
            Assert.AreEqual(0, _transport.DisconnectPeerCallCount,
                "client log path must not disconnect (policy preserved)");
        }

        // ── L2c-2 ───────────────────────────────────────────────────────────

        [Test]
        public void SpectatorService_Malformed_LogsWarning()
        {
            var svc = new SpectatorService();
            svc.Initialize(_transport, _commandFactory, null /* engine */, _logger);

            byte[] data = MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin);
            InvokePrivate(svc, "HandleDataReceived", new object[] { 0, data, data.Length });

            Assert.IsTrue(_logger.Contains(LogLevel.Warning, "Malformed payload"));
            Assert.IsTrue(_logger.Contains(LogLevel.Warning, "[SpectatorService]"));
            Assert.AreEqual(0, _transport.DisconnectPeerCallCount);
        }

        // ── L2c-3 ───────────────────────────────────────────────────────────

        [Test]
        public void KlothoConnection_Malformed_LogsWarning()
        {
            var conn = ConstructKlothoConnection(_transport, _logger);

            byte[] data = MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin);
            InvokePrivate(conn, "HandleDataReceived", new object[] { 0, data, data.Length });

            Assert.IsTrue(_logger.Contains(LogLevel.Warning, "Malformed payload"));
            Assert.IsTrue(_logger.Contains(LogLevel.Warning, "[KlothoConnection]"));
        }

        // ── L2c-4 ───────────────────────────────────────────────────────────

        [Test]
        public void ServerDrivenClientService_Valid_NoWarning()
        {
            var svc = new ServerDrivenClientService();
            svc.Initialize(_transport, _commandFactory, _logger);

            // A valid PlayerReady message — registered, default-construct OK.
            byte[] data = SerializeMessage(new PlayerReadyMessage());
            InvokePrivate(svc, "HandleDataReceived", new object[] { 0, data, data.Length });

            int malformedWarnings = 0;
            foreach (var entry in _logger.Entries)
                if (entry.Level == LogLevel.Warning && entry.Message.Contains("Malformed payload"))
                    malformedWarnings++;
            Assert.AreEqual(0, malformedWarnings, "valid message must not produce malformed warning");
        }

        // ── L2c-5 ───────────────────────────────────────────────────────────

        [Test]
        public void KlothoConnection_AfterCompleted_Malformed_NoLog()
        {
            var conn = ConstructKlothoConnection(_transport, _logger);
            // Force _completed = true → HandleDataReceived's first line `if (_completed) return;` short-circuits.
            typeof(KlothoConnection)
                .GetField("_completed", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(conn, true);

            byte[] data = MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin);
            InvokePrivate(conn, "HandleDataReceived", new object[] { 0, data, data.Length });

            Assert.IsFalse(_logger.Contains(LogLevel.Warning, "Malformed payload"),
                "completed connection must not log — early return");
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static void InvokePrivate(object target, string methodName, object[] args)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(method, $"reflection: {methodName}");
            try
            {
                method.Invoke(target, args);
            }
            catch (TargetInvocationException)
            {
                // Downstream switch-case may throw if dispatch reaches an internal handler that
                // depends on engine/state. The L2c boundary (warning log + return on null) executes
                // before dispatch, so log assertions remain valid.
            }
        }

        private static KlothoConnection ConstructKlothoConnection(INetworkTransport transport, ILogger logger)
        {
            // KlothoConnection has a private ctor — invoke via reflection.
            var ctor = typeof(KlothoConnection).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(INetworkTransport), typeof(ILogger) },
                modifiers: null);
            Assert.IsNotNull(ctor, "reflection: KlothoConnection ctor");
            return (KlothoConnection)ctor.Invoke(new object[] { transport, logger });
        }

        private byte[] SerializeMessage(NetworkMessageBase msg)
        {
            using (var serialized = _serializer.SerializePooled(msg))
            {
                byte[] buf = new byte[serialized.Length];
                Array.Copy(serialized.Data, 0, buf, 0, serialized.Length);
                return buf;
            }
        }
    }
}
