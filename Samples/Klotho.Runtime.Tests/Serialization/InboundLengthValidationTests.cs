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
using xpTURN.Klotho.ECS;
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

        // A reader over a bare span has no source buffer, so the message takes its copying branch.
        [TestCaseSource(nameof(ZeroCopyCases))]
        public void ZeroCopy_NegativeLength_FromASpan_IsRejected(NetworkMessageType type, int headerBytes)
        {
            int total = 1 + headerBytes + 4;
            var buf = new byte[total];
            buf[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1 + headerBytes), -8);
            var msg = Creators(_ser)[type]();

            Assert.Throws<InvalidDataException>(() =>
            {
                var reader = new SpanReader(buf.AsSpan());
                msg.Deserialize(ref reader);
            }, "a negative length must not be accepted on either branch");
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

        // type(1) + PlayerId(4) + Count(4) = 1 + Tick(4) + len(4)
        private static byte[] OneEntryBundle(int entryLength)
        {
            var b = new byte[17];
            b[0] = (byte)NetworkMessageType.ClientInputBundle;
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), 1);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), 500);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(13), entryLength);
            return b;
        }

        [Test]
        public void ClientInputBundle_NegativeEntryLength_IsRejected()
        {
            var b = OneEntryBundle(-1);
            Assert.IsNull(_ser.Deserialize(b, b.Length), "a negative entry length must not be accepted");
        }

        // A short entry is the handler's to drop (Q1), not malformed wire.
        [Test]
        public void ClientInputBundle_ZeroLengthEntry_StillDecodes()
        {
            var b = OneEntryBundle(0);
            var bundle = _ser.Deserialize(b, b.Length) as ClientInputBundleMessage;
            Assert.IsNotNull(bundle);
            Assert.AreEqual(1, bundle.Count);
            Assert.AreEqual(0, bundle.Entries[0].CommandDataLength);
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
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return (MemberInfo)type.GetField(name, all) ?? type.GetProperty(name, all);
        }

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

        private static bool IsCountedCollection(Type t)
            => IsList(t) || (t.IsArray && t != typeof(byte[])) || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>));

        // The collection members a generated codec writes with a wire count — the generator's own selection:
        // [KlothoOrder] opts a field or property in at any accessibility, [KlothoIgnore] opts it out.
        private static IEnumerable<string> CountedCollectionMembers(Type type)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var m in type.GetFields(all).Cast<MemberInfo>().Concat(type.GetProperties(all)))
                if (m.IsDefined(typeof(KlothoOrderAttribute)) && !m.IsDefined(typeof(KlothoIgnoreAttribute))
                    && IsCountedCollection(m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType))
                    yield return m.Name;
        }

        // Never registered and never [KlothoSerializable], so the generator emits nothing for it.
        private class CollectionMemberProbe
        {
            [KlothoOrder] public List<int> PublicField = new List<int>();
            [KlothoOrder] private List<int> _privateField = new List<int>();
            [KlothoOrder] public List<int> Property { get; set; } = new List<int>();
            [KlothoOrder] public int[] Array = new int[0];
            [KlothoOrder] public Dictionary<int, int> Map = new Dictionary<int, int>();
            [KlothoOrder] public byte[] Blob = new byte[0];
            [KlothoOrder, KlothoIgnore] public List<int> Ignored = new List<int>();
            public List<int> NotOnTheWire = new List<int>();

            public int PrivateCount => _privateField.Count;
        }

        // The generator takes every [KlothoOrder] field or property, at any accessibility, unless [KlothoIgnore].
        [Test]
        public void CountedCollectionMembers_FollowTheGeneratorsRule()
        {
            CollectionAssert.AreEquivalent(
                new[] { "PublicField", "_privateField", "Property", "Array", "Map" },
                CountedCollectionMembers(typeof(CollectionMemberProbe)));
            Assert.IsNotNull(Member(typeof(CollectionMemberProbe), "_privateField"), "the table lookup must find a non-public member too");
        }

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
                foreach (var name in CountedCollectionMembers(type))
                    found.Add((type, name));
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

        // ── replay tick table: every (offset, length) lies inside the stream ──

        // Two recorded ticks; the offset table is the last 24 bytes (tick, offset, length per entry), and the
        // tick-1 blob ends exactly where the stream does.
        private static byte[] TwoTickReplay(out int bufferSize, out int tick0Length)
        {
            var factory = new CommandFactory();
            var data = new ReplayData(factory);
            data.RecordCommands(0, new List<ICommand> { new StopCommand { PlayerId = 1, Tick = 0 } }, factory);
            data.RecordCommands(1, new List<ICommand> { new StopCommand { PlayerId = 2, Tick = 1 } }, factory);
            var bytes = data.Serialize();
            bufferSize = 0;
            tick0Length = 0;
            for (int p = bytes.Length - 24; p < bytes.Length; p += 12)
            {
                int end = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4)) + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8));
                bufferSize = Math.Max(bufferSize, end);
                if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p)) == 0)
                    tick0Length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8));
            }
            Assert.AreEqual(bufferSize, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bytes.Length - 24 - bufferSize - 4)),
                "fixture: the last blob ends at the stream size the file records");
            return bytes;
        }

        private static byte[] WithTick0Entry(byte[] replay, int offset, int length)
        {
            var b = (byte[])replay.Clone();
            for (int p = b.Length - 24; p < b.Length; p += 12)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)) != 0) continue;
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p + 4), offset);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p + 8), length);
                return b;
            }
            throw new InvalidOperationException("fixture: no tick-0 entry");
        }

        [Test]
        public void Replay_NegativeTickOffset_IsRefused()
        {
            var replay = TwoTickReplay(out _, out int len);
            Assert.Throws<InvalidDataException>(() => new ReplayData().Deserialize(WithTick0Entry(replay, -1, len)));
        }

        [TestCase(-1)]
        [TestCase(2)]
        public void Replay_TickLengthShorterThanTheCountField_IsRefused(int length)
        {
            var replay = TwoTickReplay(out _, out _);
            Assert.Throws<InvalidDataException>(() => new ReplayData().Deserialize(WithTick0Entry(replay, 0, length)));
        }

        [Test]
        public void Replay_TickBlobPastTheStreamEnd_IsRefused()
        {
            var replay = TwoTickReplay(out int size, out int len);
            Assert.Throws<InvalidDataException>(() => new ReplayData().Deserialize(WithTick0Entry(replay, size, len)));
            Assert.Throws<InvalidDataException>(() => new ReplayData().Deserialize(WithTick0Entry(replay, size - len + 1, len)));
        }

        [Test]
        public void Replay_TickOffsetThatOverflowsAnAddedEnd_IsRefused()
        {
            var replay = TwoTickReplay(out _, out _);
            Assert.Throws<InvalidDataException>(() => new ReplayData().Deserialize(WithTick0Entry(replay, int.MaxValue - 1, 4)));
        }

        [Test]
        public void Replay_RecordedTicks_RoundTrip()
        {
            var target = new ReplayData();
            target.Deserialize(TwoTickReplay(out _, out _));

            var t0 = target.GetCommandsForTick(0);
            Assert.AreEqual(1, t0.Count);
            Assert.AreEqual(1, t0[0].PlayerId);
            var t1 = target.GetCommandsForTick(1);
            Assert.AreEqual(1, t1.Count);
            Assert.AreEqual(2, t1[0].PlayerId, "the blob ending at the stream end is read");
        }

        // ── a command array that fails part-way gives back what it already decoded ──

        private static int PooledStopCommands()
        {
            var pools = (Dictionary<int, Stack<ICommand>>)typeof(CommandPool)
                .GetField("_pools", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            return pools.TryGetValue(StopCommand.TYPE_ID, out var s) ? s.Count : 0;
        }

        [Test]
        public void DeserializeCommands_FailingPartWay_ReturnsTheDecodedCommandsToThePool()
        {
            var factory = new CommandFactory();
            // Two whole StopCommands, then a third whose payload ends after its type id.
            var two = new List<ICommand> { new StopCommand { PlayerId = 1, Tick = 5 }, new StopCommand { PlayerId = 2, Tick = 5 } };
            var blob = new byte[factory.GetSerializedCommandsSize(two) + 8];
            int written = factory.SerializeCommandsTo(blob);
            BinaryPrimitives.WriteInt32LittleEndian(blob, 3);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(written), 4);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(written + 4), StopCommand.TYPE_ID);

            var rented = new[] { CommandPool.Get<StopCommand>(), CommandPool.Get<StopCommand>(), CommandPool.Get<StopCommand>() };
            foreach (var c in rented) CommandPool.Return(c);
            int seeded = PooledStopCommands();

            Assert.Catch(() => factory.DeserializeCommands(blob));

            Assert.AreEqual(seeded, PooledStopCommands(), "the two decoded commands and the half-filled one must all go back");
        }

        // ── the four-argument TryDeserializeCommandRaw hands back what the decode threw ──

        [Test]
        public void TryDeserializeCommandRaw_HandsBackTheDecodeException()
        {
            var factory = new CommandFactory();

            var whole = new StopCommand { PlayerId = 1, Tick = 5 };
            var bytes = new byte[whole.GetSerializedSize()];
            var writer = new SpanWriter(bytes);
            whole.Serialize(ref writer);
            var reader = new SpanReader(bytes);
            Assert.IsTrue(factory.TryDeserializeCommandRaw(ref reader, out var decoded, out var error));
            Assert.IsInstanceOf<StopCommand>(decoded);
            Assert.IsNull(error, "a decode that succeeds hands back no exception");
            CommandPool.Return(decoded);

            var unknown = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(unknown, int.MaxValue);
            reader = new SpanReader(unknown);
            Assert.IsTrue(factory.TryDeserializeCommandRaw(ref reader, out decoded, out error));
            Assert.IsNull(decoded, "an unknown type is a null command");
            Assert.IsNull(error, "an unknown type is not a throw");

            var truncated = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(truncated, StopCommand.TYPE_ID);
            reader = new SpanReader(truncated);
            Assert.IsFalse(factory.TryDeserializeCommandRaw(ref reader, out decoded, out error));
            Assert.IsNull(decoded);
            Assert.IsInstanceOf<ArgumentOutOfRangeException>(error, "the decode's own exception comes back");
        }

        // ── DataAsset collections: the entry count is checked against the bytes left ──

        // A writer's empty collection (header + count 0) with its count field overwritten.
        private static byte[] WithCount(byte[] emptyCollection, int count)
        {
            BinaryPrimitives.WriteInt32LittleEndian(emptyCollection.AsSpan(emptyCollection.Length - 4), count);
            return emptyCollection;
        }

        private static Exception LoadThrows(Action load, out long alloc)
        {
            Exception thrown = null;
            alloc = Allocated(() => { try { load(); } catch (Exception e) { thrown = e; } });
            return thrown;
        }

        [TestCase(-1)]
        [TestCase(Bomb)]
        public void DataAssetMixedCollection_CountPastTheBytesLeft_IsRefusedWithoutAllocating(int count)
        {
            var bytes = WithCount(DataAssetWriter.SerializeMixedCollectionToBytes(new List<IDataAssetSerializable>()), count);

            var thrown = LoadThrows(() => DataAssetReader.LoadMixedCollectionFromBytes(bytes), out long alloc);

            Assert.IsInstanceOf<InvalidDataException>(thrown);
            Assert.Less(alloc, AllocBudget, $"allocated {alloc} bytes for a {bytes.Length}-byte file");
        }

        [TestCase(-1)]
        [TestCase(Bomb)]
        public void DataAssetCollection_CountPastTheBytesLeft_IsRefusedWithoutAllocating(int count)
        {
            var bytes = WithCount(DataAssetWriter.SerializeCollectionToBytes(new List<StringCollectionProbeAsset>()), count);

            var thrown = LoadThrows(() => DataAssetReader.LoadCollectionFromBytes<StringCollectionProbeAsset>(bytes), out long alloc);

            Assert.IsInstanceOf<InvalidDataException>(thrown);
            Assert.Less(alloc, AllocBudget, $"allocated {alloc} bytes for a {bytes.Length}-byte file");
        }

        // A mixed entry is at least a type id and a size: n entries of an unregistered type with size 0
        // fill exactly 8n bytes and read back as nothing.
        [Test]
        public void DataAssetMixedCollection_CountAtTheBound_StillLoads()
        {
            const int n = 3;
            var empty = DataAssetWriter.SerializeMixedCollectionToBytes(new List<IDataAssetSerializable>());
            var bytes = new byte[empty.Length + 8 * n];
            empty.CopyTo(bytes, 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(empty.Length - 4), n);
            for (int i = 0; i < n; i++)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(empty.Length + 8 * i), int.MaxValue);

            Assert.AreEqual(0, DataAssetReader.LoadMixedCollectionFromBytes(bytes).Count);
        }

        [Test]
        public void DataAssetCollection_RoundTrips()
        {
            var assets = new List<StringCollectionProbeAsset> { new StringCollectionProbeAsset(), new StringCollectionProbeAsset { Description = "b" } };

            var back = DataAssetReader.LoadCollectionFromBytes<StringCollectionProbeAsset>(DataAssetWriter.SerializeCollectionToBytes(assets));

            Assert.AreEqual(2, back.Count);
            Assert.AreEqual("b", back[1].Description);
        }
    }
}
