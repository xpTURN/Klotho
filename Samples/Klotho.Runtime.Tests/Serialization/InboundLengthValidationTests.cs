using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Replay;

namespace xpTURN.Klotho.Serialization.Tests
{
    // Codecs generated in this assembly by the shipped analyzer. TypeId 9401 ~ 9404 (command plane);
    // 9300 ~ 9304 belong to StringCollectionSizeTests and NullCollectionTests.

    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct BoundaryItem
    {
        public int A;
        public int B;
    }

    // Each probe ends with its collection so a full read leaves exactly count * minBytes remaining.
    [KlothoSerializable(9401)]
    public partial class BoundaryIntListCommand : CommandBase
    {
        [KlothoOrder] public List<int> Ids = new List<int>();
    }

    [KlothoSerializable(9402)]
    public partial class BoundaryStringListCommand : CommandBase
    {
        [KlothoOrder] public List<string> Names = new List<string>();
    }

    [KlothoSerializable(9403)]
    public partial class BoundaryStructArrayCommand : CommandBase
    {
        [KlothoOrder] public BoundaryItem[] Items = Array.Empty<BoundaryItem>();
    }

    [KlothoSerializable(9404)]
    public partial class BoundaryMapCommand : CommandBase
    {
        [KlothoOrder] public Dictionary<int, int> Map = new Dictionary<int, int>();
    }

    /// <summary>
    /// Wire lengths and counts are untrusted: a declared length past the end, a negative one, or a count
    /// whose elements cannot fit in the bytes left is refused before anything is allocated, and the
    /// normal path neither rejects its own bytes nor allocates.
    /// </summary>
    [TestFixture]
    public class InboundLengthValidationTests
    {
        private const int Bomb = 5_000_000;
        private const long AllocBudget = 1L << 20;

        private MessageSerializer _ser;

        [SetUp]
        public void SetUp()
        {
            StreamPool.Clear();
            _ser = new MessageSerializer();
        }

        // ── helpers ─────────────────────────────────────────────────────────

        // A pooled 4096-byte bucket that a previous "packet" filled with 0xAB, handed out again.
        private static byte[] PooledBufferWithResidue()
        {
            var prev = StreamPool.GetBuffer(100);
            for (int i = 0; i < prev.Length; i++) prev[i] = 0xAB;
            StreamPool.ReturnBuffer(prev);
            var buf = StreamPool.GetBuffer(32);
            Assert.AreSame(prev, buf, "repro condition: the pool hands the same array back");
            return buf;
        }

        private static long Allocated(Action action)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            action();
            return GC.GetTotalAllocatedBytes(true) - before;
        }

        // Header bytes before the int32 length field (excluding the type byte), length field last.
        private static readonly object[] ZeroCopyCases =
        {
            new object[] { NetworkMessageType.ClientInput, 8 },
            new object[] { NetworkMessageType.ReliableCommandSubmit, 4 },
            new object[] { NetworkMessageType.Command, 17 },
            new object[] { NetworkMessageType.VerifiedState, 12 },
        };

        // ── zero-copy declared length ─────────────────────────────

        [TestCaseSource(nameof(ZeroCopyCases))]
        public void ZeroCopy_DeclaredLengthPastEnd_IsRejected(NetworkMessageType type, int headerBytes)
        {
            var buf = PooledBufferWithResidue();
            int total = 1 + headerBytes + 4;
            Array.Clear(buf, 0, total);
            buf[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1 + headerBytes), 200);

            Assert.IsNull(_ser.Deserialize(buf, total), "a length past the end must not be accepted");
        }

        [TestCaseSource(nameof(ZeroCopyCases))]
        public void ZeroCopy_NegativeLength_IsRejected(NetworkMessageType type, int headerBytes)
        {
            int total = 1 + headerBytes + 4;
            var buf = new byte[total];
            buf[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1 + headerBytes), -8);

            Assert.IsNull(_ser.Deserialize(buf, total), "a negative length must not be accepted");
        }

        // ── ClientInputBundle count ────────────────────────────────────

        [Test]
        public void ClientInputBundle_CountBomb_IsRejectedWithoutAllocating()
        {
            var b = new byte[9];
            b[0] = (byte)NetworkMessageType.ClientInputBundle;
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), Bomb);

            INetworkMessage result = null;
            long alloc = Allocated(() => result = _ser.Deserialize(b, b.Length));

            Assert.IsNull(result);
            Assert.Less(alloc, AllocBudget, $"allocated {alloc} bytes for a 9-byte packet");
        }

        [Test]
        public void AllocationProbe_SeesAOneMiBArray()
        {
            long alloc = Allocated(() => GC.KeepAlive(new byte[AllocBudget]));
            Assert.GreaterOrEqual(alloc, AllocBudget, "the probe must see a 1 MiB array");
        }

        // ── count bombs on every collection field of the generated engine messages ──

        internal static readonly (Type Type, string Field)[] GeneratedCollectionFields =
        {
            (typeof(GameStartMessage), "PlayerIds"),
            (typeof(LateJoinAcceptMessage), "PlayerConfigLengths"),
            (typeof(LateJoinAcceptMessage), "Roster"),
            (typeof(LateJoinAcceptMessage), "RosterEntitlementLengths"),
            (typeof(LateJoinAcceptMessage), "RosterTickets"),
            (typeof(ReconnectAcceptMessage), "Roster"),
            (typeof(ReconnectAcceptMessage), "RosterEntitlementLengths"),
            (typeof(ReconnectAcceptMessage), "RosterTickets"),
            (typeof(SimulationConfigMessage), "MaxCountOverrideTypeIds"),
            (typeof(SimulationConfigMessage), "MaxCountOverrideValues"),
            (typeof(SimulationConfigMessage), "PrunedComponentTypeIds"),
            (typeof(SpectatorAcceptMessage), "Roster"),
            (typeof(SyncCompleteMessage), "Roster"),
            (typeof(SyncCompleteMessage), "RosterEntitlementLengths"),
            (typeof(SyncCompleteMessage), "RosterTickets"),
        };

        private static IEnumerable<TestCaseData> GeneratedCollectionCases()
            => GeneratedCollectionFields.Select(c => new TestCaseData(c.Type, c.Field).SetName($"GeneratedMessage_CountBomb_IsRejectedWithoutAllocating({c.Type.Name}.{c.Field})"));

        private static object OneElementOf(Type elementType)
            => elementType == typeof(string) ? "" : Activator.CreateInstance(elementType);

        private static void AddOne(object msg, MemberInfo member)
        {
            var value = member is FieldInfo f ? f.GetValue(msg) : ((PropertyInfo)member).GetValue(msg);
            Assert.IsNotNull(value, $"{member.Name} must have an initializer");
            ((IList)value).Add(OneElementOf(value.GetType().GetGenericArguments()[0]));
        }

        private static MemberInfo Member(Type type, string name)
            => (MemberInfo)type.GetField(name) ?? type.GetProperty(name);

        // Offset of the field's int32 count: the first byte where "empty" and "one element" differ.
        private int CountOffset(Type type, string field, out byte[] empty)
        {
            var e = (INetworkMessage)Activator.CreateInstance(type);
            var o = (INetworkMessage)Activator.CreateInstance(type);
            AddOne(o, Member(type, field));
            empty = _ser.Serialize(e);
            var one = _ser.Serialize(o);
            for (int i = 0; i < empty.Length; i++)
                if (empty[i] != one[i]) return i;
            Assert.Fail($"{type.Name}.{field}: adding an element changed no byte");
            return -1;
        }

        [TestCaseSource(nameof(GeneratedCollectionCases))]
        public void GeneratedMessage_CountBomb_IsRejectedWithoutAllocating(Type type, string field)
        {
            int offset = CountOffset(type, field, out var payload);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), Bomb);

            INetworkMessage result = null;
            long alloc = Allocated(() => result = _ser.Deserialize(payload, payload.Length));

            Assert.IsNull(result);
            Assert.Less(alloc, AllocBudget, $"{type.Name}.{field}: allocated {alloc} bytes for a {payload.Length}-byte packet");
        }

        // ── zero-copy offset when the message does not start at index 0 ──

        private static IEnumerable<TestCaseData> OffsetCases()
        {
            var data = new byte[] { 1, 2, 3, 4 };
            yield return new TestCaseData(new ClientInputMessage { Tick = 7, PlayerId = 1, CommandData = data, CommandDataLength = 4 },
                (Func<INetworkMessage, byte[]>)(m => ((ClientInputMessage)m).CommandDataSpan.ToArray())).SetName("ZeroCopy_AtOffset_ReturnsTheMessageBytes(ClientInput)");
            yield return new TestCaseData(new ReliableCommandSubmitMessage { PlayerId = 1, CommandData = data, CommandDataLength = 4 },
                (Func<INetworkMessage, byte[]>)(m => ((ReliableCommandSubmitMessage)m).CommandDataSpan.ToArray())).SetName("ZeroCopy_AtOffset_ReturnsTheMessageBytes(ReliableCommandSubmit)");
            yield return new TestCaseData(new CommandMessage { Tick = 7, PlayerId = 1, CommandData = data, CommandDataLength = 4 },
                (Func<INetworkMessage, byte[]>)(m => ((CommandMessage)m).CommandDataSpan.ToArray())).SetName("ZeroCopy_AtOffset_ReturnsTheMessageBytes(Command)");
            yield return new TestCaseData(new VerifiedStateMessage { Tick = 7, ConfirmedInputsData = data, ConfirmedInputsDataLength = 4 },
                (Func<INetworkMessage, byte[]>)(m => ((VerifiedStateMessage)m).ConfirmedInputsSpan.ToArray())).SetName("ZeroCopy_AtOffset_ReturnsTheMessageBytes(VerifiedState)");
        }

        [TestCaseSource(nameof(OffsetCases))]
        public void ZeroCopy_AtOffset_ReturnsTheMessageBytes(INetworkMessage src, Func<INetworkMessage, byte[]> span)
        {
            var one = _ser.Serialize(src);
            var cat = new byte[10 + one.Length];
            for (int i = 0; i < 10; i++) cat[i] = 0xEE;
            Buffer.BlockCopy(one, 0, cat, 10, one.Length);

            var m = _ser.Deserialize(cat, one.Length, 10);

            Assert.IsNotNull(m);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, span(m));
        }

        // ── no false reject: every registered message round-trips, filled and empty ──

        private static Dictionary<NetworkMessageType, Func<INetworkMessage>> Creators(MessageSerializer ser)
            => (Dictionary<NetworkMessageType, Func<INetworkMessage>>)typeof(MessageSerializer)
                .GetField("_creators", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ser);

        private static IEnumerable<MemberInfo> ListMembers(Type t)
            => t.GetFields(BindingFlags.Public | BindingFlags.Instance).Cast<MemberInfo>()
                .Concat(t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
                .Where(m => IsList(m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType));

        private static bool IsList(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);

        [Test]
        public void EveryRegisteredMessage_RoundTrips_EmptyAndFilled()
        {
            var failures = new List<string>();
            foreach (var kv in Creators(_ser))
            {
                var type = kv.Value().GetType();
                foreach (bool fill in new[] { false, true })
                {
                    var msg = (INetworkMessage)Activator.CreateInstance(type);
                    if (fill)
                        foreach (var m in ListMembers(type))
                            AddOne(msg, m);
                    byte[] bytes;
                    try { bytes = _ser.Serialize(msg); }
                    catch (Exception e) { failures.Add($"{type.Name} fill={fill}: serialize threw {e.GetType().Name}"); continue; }
                    if (_ser.Deserialize(bytes, bytes.Length) == null)
                        failures.Add($"{type.Name} fill={fill}: rejected its own bytes");
                }
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        // ── boundary: count * minBytes == Remaining passes, one byte short throws InvalidDataException ──

        private static IEnumerable<TestCaseData> BoundaryCases()
        {
            var ints = new BoundaryIntListCommand(); ints.Ids.AddRange(new[] { 1, 2, 3 });
            yield return new TestCaseData(ints, (Func<CommandBase>)(() => new BoundaryIntListCommand())).SetName("CountCheck_ExactBytesPass_OneShortIsRefused(ListOfInt)");
            var names = new BoundaryStringListCommand(); names.Names.AddRange(new[] { "", "", "" });
            yield return new TestCaseData(names, (Func<CommandBase>)(() => new BoundaryStringListCommand())).SetName("CountCheck_ExactBytesPass_OneShortIsRefused(ListOfEmptyString)");
            var items = new BoundaryStructArrayCommand { Items = new BoundaryItem[3] };
            yield return new TestCaseData(items, (Func<CommandBase>)(() => new BoundaryStructArrayCommand())).SetName("CountCheck_ExactBytesPass_OneShortIsRefused(ArrayOfNestedStruct)");
            var map = new BoundaryMapCommand(); map.Map[1] = 10; map.Map[2] = 20;
            yield return new TestCaseData(map, (Func<CommandBase>)(() => new BoundaryMapCommand())).SetName("CountCheck_ExactBytesPass_OneShortIsRefused(Dictionary)");
        }

        private static byte[] Bytes(CommandBase c)
        {
            var buf = new byte[c.GetSerializedSize()];
            var w = new SpanWriter(buf);
            c.Serialize(ref w);
            Assert.AreEqual(buf.Length, w.Position);
            return buf;
        }

        [TestCaseSource(nameof(BoundaryCases))]
        public void CountCheck_ExactBytesPass_OneShortIsRefused(CommandBase src, Func<CommandBase> fresh)
        {
            var bytes = Bytes(src);

            Assert.DoesNotThrow(() => { var r = new SpanReader(bytes); fresh().Deserialize(ref r); }, "exactly enough bytes must pass");

            var shortBy1 = bytes.AsSpan(0, bytes.Length - 1).ToArray();
            Assert.Throws<InvalidDataException>(() => { var r = new SpanReader(shortBy1); fresh().Deserialize(ref r); },
                "one byte short must be refused by the count check, before the reads run out");
        }

        // ── the count-bomb table covers every collection field of a registered message ──

        [Test]
        public void CountBombTable_CoversEveryCollectionFieldOfRegisteredMessages()
        {
            // Hand-written codec with a wire count: covered by ClientInputBundle_CountBomb_IsRejectedWithoutAllocating.
            var manual = new HashSet<Type> { typeof(ClientInputBundleMessage) };
            var found = new HashSet<(Type, string)>();
            foreach (var kv in Creators(_ser))
            {
                var type = kv.Value().GetType();
                if (manual.Contains(type)) continue;
                foreach (var m in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    if (!m.IsDefined(typeof(NonSerializedAttribute)) && (IsList(m.FieldType) || (m.FieldType.IsArray && m.FieldType != typeof(byte[]))
                        || (m.FieldType.IsGenericType && m.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>))))
                        found.Add((type, m.Name));
            }
            var table = new HashSet<(Type, string)>(GeneratedCollectionFields);
            CollectionAssert.AreEquivalent(table.Select(x => $"{x.Item1.Name}.{x.Item2}").OrderBy(s => s),
                found.Select(x => $"{x.Item1.Name}.{x.Item2}").OrderBy(s => s));
        }

        // ── the normal path allocates nothing ──

        [Test]
        public void NormalPath_AllocatesNothing()
        {
            var input = _ser.Serialize(new ClientInputMessage { Tick = 1, PlayerId = 1, CommandData = new byte[] { 1, 2, 3, 4 }, CommandDataLength = 4 });
            var gs = new GameStartMessage(); gs.PlayerIds.AddRange(new[] { 1, 2, 3 });
            var start = _ser.Serialize(gs);
            for (int i = 0; i < 3; i++) { _ser.Deserialize(input, input.Length); _ser.Deserialize(start, start.Length); }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { _ser.Deserialize(input, input.Length); _ser.Deserialize(start, start.Length); }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;

            long ctlBefore = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new object[1]);
            long ctl = GC.GetAllocatedBytesForCurrentThread() - ctlBefore;

            Assert.Greater(ctl, 0, "positive control: the counter sees one small array");
            Assert.AreEqual(0, alloc, "zero-copy ClientInput + reused GameStart must not allocate");
        }

        // ── replay stream size ──

        [Test]
        public void Replay_StreamSizeBomb_IsRefusedWithoutAllocating()
        {
            var bad = new ReplayData().Serialize();
            BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(bad.Length - 4), 100_000_000);   // bufferSize: last field of an empty replay
            var target = new ReplayData();

            Exception thrown = null;
            long alloc = Allocated(() => { try { target.Deserialize(bad); } catch (Exception e) { thrown = e; } });

            Assert.IsInstanceOf<InvalidDataException>(thrown);
            Assert.Less(alloc, AllocBudget, $"allocated {alloc} bytes for a {bad.Length}-byte replay");

            var valid = new ReplayData().Serialize();
            Assert.DoesNotThrow(() => new ReplayData().Deserialize(valid));
        }
    }
}
