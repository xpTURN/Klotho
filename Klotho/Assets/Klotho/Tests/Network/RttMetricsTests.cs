using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Unit tests for IMP38 RTT metrics — EmitRttMatchAggregate computes
    /// min/max/mean/percentile statistics and emits a culture-invariant JSON line.
    /// MatchRttAccumulator is private nested type; accessed via reflection.
    /// </summary>
    [TestFixture]
    public class RttMetricsTests
    {
        private ServerNetworkService _svc;
        private TestTransport _transport;
        private LogCapture _logger;

        private FieldInfo _matchRttAccField;
        private MethodInfo _emitMethod;
        private Type _accumulatorType;
        private FieldInfo _samplesField;
        private FieldInfo _spikeCountField;
        private FieldInfo _thresholdExceedCountField;
        private FieldInfo _roomIdField;
        private FieldInfo _matchIdField;
        private FieldInfo _playerIdField;
        private FieldInfo _peerIdField;
        private FieldInfo _startTimeMsField;

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            _transport = new TestTransport();
            _transport.Listen("localhost", 0, 4);

            _logger = new LogCapture();
            _svc = new ServerNetworkService();
            _svc.Initialize(_transport, null, _logger);
            _svc.CreateRoom("test", 4);

            _matchRttAccField = typeof(ServerNetworkService).GetField(
                "_matchRttAcc", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(_matchRttAccField, "reflection: _matchRttAcc");

            _accumulatorType = typeof(ServerNetworkService).GetNestedType(
                "MatchRttAccumulator", BindingFlags.NonPublic);
            Assert.IsNotNull(_accumulatorType, "reflection: MatchRttAccumulator");

            _emitMethod = typeof(ServerNetworkService).GetMethod(
                "EmitRttMatchAggregate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(_emitMethod, "reflection: EmitRttMatchAggregate");

            _samplesField = _accumulatorType.GetField("Samples");
            _spikeCountField = _accumulatorType.GetField("SpikeCount");
            _thresholdExceedCountField = _accumulatorType.GetField("ThresholdExceedCount");
            _roomIdField = _accumulatorType.GetField("RoomId");
            _matchIdField = _accumulatorType.GetField("MatchId");
            _playerIdField = _accumulatorType.GetField("PlayerId");
            _peerIdField = _accumulatorType.GetField("PeerId");
            _startTimeMsField = _accumulatorType.GetField("StartTimeMs");
        }

        [TearDown]
        public void TearDown()
        {
            TestTransport.Reset();
        }

        private object NewAccumulator(int roomId, long matchId, int playerId, int peerId, long startTimeMs)
        {
            var acc = Activator.CreateInstance(_accumulatorType, nonPublic: true);
            _roomIdField.SetValue(acc, roomId);
            _matchIdField.SetValue(acc, matchId);
            _playerIdField.SetValue(acc, playerId);
            _peerIdField.SetValue(acc, peerId);
            _startTimeMsField.SetValue(acc, startTimeMs);
            return acc;
        }

        private void SetSamples(object acc, IEnumerable<int> samples)
        {
            var list = (List<int>)_samplesField.GetValue(acc);
            list.Clear();
            list.AddRange(samples);
        }

        private string LastMetricsLine()
        {
            for (int i = _logger.Entries.Count - 1; i >= 0; i--)
                if (_logger.Entries[i].Message.StartsWith("[Metrics][RttMatch]"))
                    return _logger.Entries[i].Message;
            return null;
        }

        [Test]
        public void EmitRttMatchAggregate_KnownSamples_ComputesCorrectStats()
        {
            // Sorted: 100, 120, 150, 170, 180, 200, 220, 250, 280, 300
            // min=100, max=300, mean=197 (1970/10), p50=sorted[5]=200,
            // p95=sorted[9]=300, p99=sorted[9]=300
            var acc = NewAccumulator(roomId: 5, matchId: 1715328000123L, playerId: 1, peerId: 100,
                startTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000);
            SetSamples(acc, new[] { 100, 200, 150, 300, 250, 180, 120, 170, 220, 280 });
            _spikeCountField.SetValue(acc, 2);
            _thresholdExceedCountField.SetValue(acc, 2); // 280, 300 > 250

            _emitMethod.Invoke(_svc, new[] { acc });

            string line = LastMetricsLine();
            Assert.IsNotNull(line, "RttMatch line emitted");
            StringAssert.Contains("\"v\":1", line);
            StringAssert.Contains("\"roomId\":5", line);
            StringAssert.Contains("\"playerId\":1", line);
            StringAssert.Contains("\"matchId\":1715328000123", line);
            StringAssert.Contains("\"sampleCount\":10", line);
            StringAssert.Contains("\"min\":100", line);
            StringAssert.Contains("\"max\":300", line);
            StringAssert.Contains("\"mean\":197", line);
            StringAssert.Contains("\"p50\":200", line);
            StringAssert.Contains("\"p95\":300", line);
            StringAssert.Contains("\"p99\":300", line);
            StringAssert.Contains("\"spikeCount\":2", line);
            StringAssert.Contains("\"thresholdExceedFrac\":0.2000", line);
        }

        [Test]
        public void EmitRttMatchAggregate_EmptySamples_DoesNotEmit()
        {
            var acc = NewAccumulator(0, 0, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            SetSamples(acc, Array.Empty<int>());

            _emitMethod.Invoke(_svc, new[] { acc });

            Assert.IsNull(LastMetricsLine(), "no RttMatch line for empty samples");
        }

        [Test]
        public void EmitRttMatchAggregate_DecimalCommaCulture_StillEmitsInvariantDecimalDot()
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                // de-DE uses "," as decimal separator; without InvariantCulture the fraction
                // would render as "0,7500" and break JSON parsing downstream.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var acc = NewAccumulator(1, 100L, 1, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000);
                SetSamples(acc, new[] { 300, 300, 300, 100 });
                _thresholdExceedCountField.SetValue(acc, 3);

                _emitMethod.Invoke(_svc, new[] { acc });

                string line = LastMetricsLine();
                Assert.IsNotNull(line);
                StringAssert.Contains("\"thresholdExceedFrac\":0.7500", line);
                Assert.IsFalse(line.Contains("0,7500"), "must not use culture decimal comma");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Test]
        public void EmitRttMatchAggregate_RoomIdSentinel_NegativeOnePropagatesToOutput()
        {
            var acc = NewAccumulator(roomId: -1, matchId: 0, playerId: 1, peerId: 1,
                startTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            SetSamples(acc, new[] { 50, 60 });

            _emitMethod.Invoke(_svc, new[] { acc });

            StringAssert.Contains("\"roomId\":-1", LastMetricsLine());
        }

        [Test]
        public void EmitRttMatchAggregate_ClearsSamplesAfterEmit()
        {
            var acc = NewAccumulator(0, 0, 0, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            SetSamples(acc, new[] { 50, 60, 70 });

            _emitMethod.Invoke(_svc, new[] { acc });

            var list = (List<int>)_samplesField.GetValue(acc);
            Assert.AreEqual(0, list.Count, "Samples should be cleared after emit");
        }
    }
}
