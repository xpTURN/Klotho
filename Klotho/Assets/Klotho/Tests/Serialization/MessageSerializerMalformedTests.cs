using System;
using NUnit.Framework;

using xpTURN.Klotho.Network;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Serialization.Tests
{
    /// <summary>
    /// Stage 1 (L1) — MessageSerializer.Deserialize boundary + malformed payload coverage.
    /// Verifies the try/catch in NetworkMessages.cs absorbs all malformed wire input
    /// (returns null, no throw) and the cache invalidation policy on catch.
    /// </summary>
    [TestFixture]
    public class MessageSerializerMalformedTests
    {
        private MessageSerializer _serializer;

        [SetUp]
        public void SetUp()
        {
            _serializer = new MessageSerializer();
        }

        // ── Boundary checks (L1-1 ~ L1-5) ───────────────────────────────────

        [Test]
        public void Deserialize_NullData_ReturnsNull()
        {
            Assert.IsNull(_serializer.Deserialize(null));
            Assert.IsNull(_serializer.Deserialize(null, 5, 0));
        }

        [Test]
        public void Deserialize_LengthZero_ReturnsNull()
        {
            byte[] data = new byte[8];
            Assert.IsNull(_serializer.Deserialize(data, 0, 0));
        }

        [Test]
        public void Deserialize_NegativeOffset_ReturnsNull()
        {
            byte[] data = new byte[8];
            Assert.IsNull(_serializer.Deserialize(data, 1, -1));
        }

        [Test]
        public void Deserialize_LengthExceedsBuffer_ReturnsNull()
        {
            byte[] data = new byte[5];
            Assert.IsNull(_serializer.Deserialize(data, 10, 0));
            Assert.IsNull(_serializer.Deserialize(data, 5, 1));
        }

        [Test]
        public void Deserialize_OffsetOverflow_ReturnsNull()
        {
            byte[] data = new byte[5];
            // overflow-safe guard: 'length > data.Length - offset' must reject without int wrap.
            Assert.IsNull(_serializer.Deserialize(data, 1, int.MaxValue));
        }

        // ── Type-id and payload format (L1-6 ~ L1-10) ───────────────────────

        [Test]
        public void Deserialize_UnknownTypeId_ReturnsNull()
        {
            byte[] data = MalformedPayloadFactory.UnknownType(0xFE);
            Assert.IsNull(_serializer.Deserialize(data, data.Length, 0));
        }

        [Test]
        public void Deserialize_TruncatedStringField_ReturnsNull()
        {
            // PlayerJoinMessage's DeviceId — declared length 100, only 5 bytes follow.
            byte[] data = MalformedPayloadFactory.TruncatedString(NetworkMessageType.PlayerJoin, 100, 5);
            Assert.DoesNotThrow(() => _serializer.Deserialize(data, data.Length, 0));
            Assert.IsNull(_serializer.Deserialize(data, data.Length, 0));
        }

        [Test]
        public void Deserialize_NegativeStringLength_ReturnsNull()
        {
            byte[] data = MalformedPayloadFactory.NegativeLength(NetworkMessageType.PlayerJoin);
            Assert.IsNull(_serializer.Deserialize(data, data.Length, 0));
        }

        [Test]
        public void Deserialize_EmptyBodyAfterTypeId_ReturnsNull()
        {
            // PlayerJoinMessage requires a string field; reading the int32 length prefix
            // out of an empty body will throw ArgumentOutOfRangeException, absorbed by L1.
            byte[] data = MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin);
            Assert.IsNull(_serializer.Deserialize(data, data.Length, 0));
        }

        [Test]
        public void Deserialize_RandomGarbage_ReturnsNull()
        {
            // 100 random-seed payloads — most will fail with null (unknown type or malformed body).
            // Some seeds may by chance produce a valid payload of an empty-body type;
            // the assertion is "no throw", not "all null".
            for (int seed = 0; seed < 100; seed++)
            {
                byte[] data = MalformedPayloadFactory.RandomGarbage(seed, 16);
                Assert.DoesNotThrow(() => _serializer.Deserialize(data, data.Length, 0),
                    $"seed={seed} should not throw");
            }
        }

        // ── Valid round-trip + cache invalidation (L1-11 ~ L1-14) ───────────

        [Test]
        public void Deserialize_ValidPayload_ReturnsMessage()
        {
            var src = new PlayerJoinMessage { DeviceId = "test-device-A" };
            byte[] buf = SerializeToArray(src);

            var result = _serializer.Deserialize(buf, buf.Length, 0) as PlayerJoinMessage;

            Assert.IsNotNull(result);
            Assert.AreEqual("test-device-A", result.DeviceId);
        }

        [Test]
        public void Deserialize_MalformedThenValid_CacheInvalidated()
        {
            // Step 1 — valid call: caches PlayerJoinMessage instance A.
            var src1 = new PlayerJoinMessage { DeviceId = "first" };
            byte[] buf1 = SerializeToArray(src1);
            var instanceA = _serializer.Deserialize(buf1, buf1.Length, 0) as PlayerJoinMessage;
            Assert.IsNotNull(instanceA);

            // Step 2 — malformed of the same type: cache invalidated (Remove(type)).
            byte[] malformed = MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin);
            Assert.IsNull(_serializer.Deserialize(malformed, malformed.Length, 0));

            // Step 3 — valid call again: should return a *fresh* instance (cache miss → creator()).
            var src2 = new PlayerJoinMessage { DeviceId = "second" };
            byte[] buf2 = SerializeToArray(src2);
            var instanceB = _serializer.Deserialize(buf2, buf2.Length, 0) as PlayerJoinMessage;

            Assert.IsNotNull(instanceB);
            Assert.AreNotSame(instanceA, instanceB,
                "Cache should be invalidated after malformed — a new instance must be created.");
            Assert.AreEqual("second", instanceB.DeviceId,
                "All fields must reflect the valid payload, with no bleed from the prior partial deserialize.");
        }

        [Test]
        public void Deserialize_MalformedTypeA_DoesNotPolluteTypeB()
        {
            // Malformed PlayerJoin must not affect the cache of ReconnectRequest.
            // First, prime ReconnectRequest cache with a valid call.
            var rrSrc = new ReconnectRequestMessage { SessionMagic = 0xABCDEF, PlayerId = 7, DeviceId = "dev" };
            byte[] rrBuf = SerializeToArray(rrSrc);
            var rrFirst = _serializer.Deserialize(rrBuf, rrBuf.Length, 0) as ReconnectRequestMessage;
            Assert.IsNotNull(rrFirst);

            // Inject malformed PlayerJoin — should invalidate only PlayerJoin's cache entry.
            byte[] malformed = MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin);
            Assert.IsNull(_serializer.Deserialize(malformed, malformed.Length, 0));

            // ReconnectRequest deserialize — should still work and reuse the cached instance.
            var rrSecond = _serializer.Deserialize(rrBuf, rrBuf.Length, 0) as ReconnectRequestMessage;
            Assert.IsNotNull(rrSecond);
            Assert.AreSame(rrFirst, rrSecond,
                "Type B cache should remain intact after type A malformed.");
            Assert.AreEqual(0xABCDEF, rrSecond.SessionMagic);
            Assert.AreEqual(7, rrSecond.PlayerId);
            Assert.AreEqual("dev", rrSecond.DeviceId);
        }

        [Test]
        public void Deserialize_RepeatedMalformedSameType_ReturnsNullEachTime()
        {
            byte[] malformed = MalformedPayloadFactory.EmptyBody(NetworkMessageType.PlayerJoin);
            for (int i = 0; i < 100; i++)
            {
                Assert.DoesNotThrow(() => _serializer.Deserialize(malformed, malformed.Length, 0),
                    $"iteration {i} should not throw");
                Assert.IsNull(_serializer.Deserialize(malformed, malformed.Length, 0),
                    $"iteration {i} should return null");
            }
        }

        // ── Regression guard (L1-15) ────────────────────────────────────────

        [Test]
        public void Deserialize_ValidRoundTrip_RepresentativeTypes()
        {
            // L1 catch is broad — guards against absorbing intentional throws on the deserialize path.
            // MessageRegistry exposes only Action wrappers (no public type list), so we round-trip
            // a representative set of types covering string/int32/int64 fields.
            // If the L1 catch ever swallows a valid deserialize bug, these will fail.

            // PlayerJoinMessage — single string field
            var pj = new PlayerJoinMessage { DeviceId = "valid-pj" };
            byte[] pjBuf = SerializeToArray(pj);
            var pjResult = _serializer.Deserialize(pjBuf, pjBuf.Length, 0) as PlayerJoinMessage;
            Assert.IsNotNull(pjResult);
            Assert.AreEqual("valid-pj", pjResult.DeviceId);

            // ReconnectRequestMessage — long + int + string
            var rr = new ReconnectRequestMessage
            {
                SessionMagic = 0x123456789ABCDEF0L,
                PlayerId = 42,
                DeviceId = "valid-rr",
            };
            byte[] rrBuf = SerializeToArray(rr);
            var rrResult = _serializer.Deserialize(rrBuf, rrBuf.Length, 0) as ReconnectRequestMessage;
            Assert.IsNotNull(rrResult);
            Assert.AreEqual(0x123456789ABCDEF0L, rrResult.SessionMagic);
            Assert.AreEqual(42, rrResult.PlayerId);
            Assert.AreEqual("valid-rr", rrResult.DeviceId);
        }

        // ── Helper ──────────────────────────────────────────────────────────

        private byte[] SerializeToArray(NetworkMessageBase msg)
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
