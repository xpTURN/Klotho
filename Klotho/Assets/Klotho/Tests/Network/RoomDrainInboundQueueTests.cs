using System;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Network.Tests
{
    /// <summary>
    /// Stage 2 (L3) — Room.DrainInboundQueue try/catch/finally coverage.
    /// Verifies StreamPool.ReturnBuffer fires unconditionally (defense-in-depth)
    /// and the loop continues past a throwing subscriber.
    /// </summary>
    [TestFixture]
    public class RoomDrainInboundQueueTests
    {
        private const int BUCKET0_SIZE = 4096; // first bucket size — see StreamPool

        private TestTransport _sharedTransport;
        private RoomScopedTransport _roomTransport;
        private LogCapture _logger;
        private Room _room;

        [SetUp]
        public void SetUp()
        {
            StreamPool.Clear();
            _sharedTransport = new TestTransport();
            _roomTransport = new RoomScopedTransport(_sharedTransport);
            _logger = new LogCapture();

            // Room only needs Transport + logger + InboundQueue for DrainInboundQueue.
            // Other deps are unused on this code path; pass null.
            _room = new Room(
                roomId: 0,
                simConfig: null,
                sessionConfig: null,
                simulation: null,
                commandFactory: null,
                transport: _roomTransport,
                networkService: null,
                engine: null,
                callbacks: null,
                logger: _logger);
        }

        [TearDown]
        public void TearDown()
        {
            StreamPool.Clear();
        }

        // ── L3-1 ────────────────────────────────────────────────────────────

        [Test]
        public void DrainInboundQueue_NormalData_BufferReturnedToPool()
        {
            int receivedCount = 0;
            _roomTransport.OnDataReceived += (p, b, l) => receivedCount++;

            byte[] buf = StreamPool.GetBuffer(BUCKET0_SIZE);
            _room.InboundQueue.Enqueue(new InboundEntry
            {
                Type = InboundEventType.Data,
                PeerId = 1,
                Buffer = buf,
                Length = 8,
            });

            int beforeCount = StreamPool.GetPoolCount(0);
            _room.DrainInboundQueue();

            Assert.AreEqual(1, receivedCount, "subscriber should be invoked once");
            Assert.AreEqual(beforeCount + 1, StreamPool.GetPoolCount(0), "buffer must be returned to pool");
        }

        // ── L3-2 ────────────────────────────────────────────────────────────

        [Test]
        public void DrainInboundQueue_SubscriberThrows_BufferStillReturned()
        {
            _roomTransport.OnDataReceived += (p, b, l) =>
                throw new InvalidOperationException("simulated handler throw");

            byte[] buf = StreamPool.GetBuffer(BUCKET0_SIZE);
            _room.InboundQueue.Enqueue(new InboundEntry
            {
                Type = InboundEventType.Data,
                PeerId = 1,
                Buffer = buf,
                Length = 8,
            });

            int beforeCount = StreamPool.GetPoolCount(0);
            Assert.DoesNotThrow(() => _room.DrainInboundQueue());

            Assert.AreEqual(beforeCount + 1, StreamPool.GetPoolCount(0),
                "buffer must be returned via finally even when subscriber throws");
            Assert.IsTrue(_logger.Contains(LogLevel.Error, "RaiseDataReceived exception"),
                "L3 catch should log an error");
        }

        // ── L3-3 ────────────────────────────────────────────────────────────

        [Test]
        public void DrainInboundQueue_ThrowFollowedByValid_LoopContinues()
        {
            int processedPeer2 = 0;
            _roomTransport.OnDataReceived += (peerId, b, l) =>
            {
                if (peerId == 1)
                    throw new InvalidOperationException("entry 1 throws");
                if (peerId == 2)
                    processedPeer2++;
            };

            byte[] buf1 = StreamPool.GetBuffer(BUCKET0_SIZE);
            byte[] buf2 = StreamPool.GetBuffer(BUCKET0_SIZE);
            _room.InboundQueue.Enqueue(new InboundEntry
            {
                Type = InboundEventType.Data, PeerId = 1, Buffer = buf1, Length = 8,
            });
            _room.InboundQueue.Enqueue(new InboundEntry
            {
                Type = InboundEventType.Data, PeerId = 2, Buffer = buf2, Length = 8,
            });

            _room.DrainInboundQueue();

            Assert.AreEqual(1, processedPeer2,
                "loop must continue past the throwing entry and process the next one");
        }

        // ── L3-4 ────────────────────────────────────────────────────────────

        [Test]
        public void DrainInboundQueue_HundredMalformed_NoLeak()
        {
            _roomTransport.OnDataReceived += (p, b, l) =>
                throw new InvalidOperationException("simulated handler throw");

            for (int i = 0; i < 100; i++)
            {
                byte[] buf = StreamPool.GetBuffer(BUCKET0_SIZE);
                _room.InboundQueue.Enqueue(new InboundEntry
                {
                    Type = InboundEventType.Data, PeerId = i, Buffer = buf, Length = 8,
                });
            }

            _room.DrainInboundQueue();

            // The pool's MAX_POOL_SIZE is 16 per bucket; the rest are discarded.
            // What matters: every entry returned its buffer (no leak path), so the pool fills up to its cap.
            int poolCount = StreamPool.GetPoolCount(0);
            Assert.GreaterOrEqual(poolCount, 16,
                $"all 100 buffers should have been returned (pool cap reached); got {poolCount}");
        }

        // ── L3-5 ────────────────────────────────────────────────────────────

        [Test]
        public void DrainInboundQueue_ConnectedDisconnectedNoBuffer_NoReturn()
        {
            int connectCount = 0;
            int disconnectCount = 0;
            _roomTransport.OnPeerConnected += peerId => connectCount++;
            _roomTransport.OnPeerDisconnected += peerId => disconnectCount++;

            _room.InboundQueue.Enqueue(new InboundEntry { Type = InboundEventType.Connected, PeerId = 1 });
            _room.InboundQueue.Enqueue(new InboundEntry { Type = InboundEventType.Disconnected, PeerId = 1 });

            int beforeCount = StreamPool.GetPoolCount(0);
            _room.DrainInboundQueue();

            Assert.AreEqual(1, connectCount);
            Assert.AreEqual(1, disconnectCount);
            Assert.AreEqual(beforeCount, StreamPool.GetPoolCount(0),
                "Connected/Disconnected entries carry no buffer — pool must be unchanged");
        }
    }
}
