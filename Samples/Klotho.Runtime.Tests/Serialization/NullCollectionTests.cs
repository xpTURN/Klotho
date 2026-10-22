// The probe types leave their collections without initializers on purpose — that is the case under test.
#pragma warning disable KLSG008

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Core.Tests;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Serialization.Tests
{
    // Codecs generated in this assembly by the shipped analyzer. TypeId 9302 (DataAsset plane) and
    // 9303 / 9304 (command plane); 9300 / 9301 belong to StringCollectionSizeTests.

    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct NullProbeItem
    {
        public int A;
        public int B;
    }

    [KlothoDataAsset(9302, AssetId = 9302)]
    public partial class NullProbeAsset : IDataAsset
    {
        [KlothoOrder(0)] public string[] Strings;
        [KlothoOrder(1)] public List<string> StringList;
        [KlothoOrder(2)] public int[] Ints;
        [KlothoOrder(3)] public List<int> IntList;
        [KlothoOrder(4)] public FP64[] Fixed;
        [KlothoOrder(5)] public Dictionary<int, int> Map;
        [KlothoOrder(6)] public List<NullProbeItem> ItemList;
        [KlothoOrder(7)] public NullProbeItem[] Items;
        [KlothoOrder(8)] public FP64 Tail;
    }

    [KlothoSerializable(9303)]
    public partial class NullProbeCommand : CommandBase
    {
        [KlothoOrder] public string[] Strings;
        [KlothoOrder] public List<string> StringList;
        [KlothoOrder] public int[] Ints;
        [KlothoOrder] public List<int> IntList;
        [KlothoOrder] public Dictionary<int, int> Map;
        [KlothoOrder] public List<NullProbeItem> ItemList;
        [KlothoOrder] public NullProbeItem[] Items;
        [KlothoOrder] public FP64 Tail;
    }

    // Members the generated Deserialize cannot assign to — it must reuse them instead.
    [KlothoSerializable(9304)]
    public partial class UnassignableProbeCommand : CommandBase
    {
        [KlothoOrder] public readonly List<int> ReadonlyList = new List<int>();
        [KlothoOrder] public readonly Dictionary<int, int> ReadonlyMap = new Dictionary<int, int>();
        [KlothoOrder] public List<int> GetOnlyList { get; } = new List<int>();
    }

    /// <summary>
    /// Collection fields of generated codecs tolerate null and a missing initializer.
    ///
    /// <para>A null <c>T[]</c> / <c>List&lt;T&gt;</c> / <c>Dictionary&lt;K,V&gt;</c> is written as empty (count 0) and
    /// reads back as an empty instance, never null — the same direction as <c>string</c>, and the wire format is
    /// unchanged because count 0 was already a valid encoding. A <c>List</c> / <c>Dictionary</c> left null by a
    /// missing initializer is created on read; an existing one is cleared and reused, so a pooled command
    /// allocates only on its first read. A zero-length array reads back as <c>Array.Empty&lt;T&gt;()</c>.</para>
    ///
    /// <para>The codecs come from the analyzer DLL the package ships, so these tests also fail when that DLL
    /// was not regenerated after a generator change.</para>
    /// </summary>
    [TestFixture]
    public class NullCollectionTests
    {
        private static byte[] Write(IDataAssetSerializable asset)
        {
            var buffer = new byte[asset.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            asset.Serialize(ref writer);
            Assert.AreEqual(buffer.Length, writer.Position, "bytes written vs GetSerializedSize()");
            return buffer;
        }

        private static byte[] Write(CommandBase command)
        {
            var buffer = new byte[command.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            command.Serialize(ref writer);
            Assert.AreEqual(buffer.Length, writer.Position, "bytes written vs GetSerializedSize()");
            return buffer;
        }

        internal static NullProbeAsset FilledAsset() => new NullProbeAsset
        {
            Strings = new[] { "a", "한글" },
            StringList = new List<string> { "x" },
            Ints = new[] { 1, 2 },
            IntList = new List<int> { 3 },
            Fixed = new[] { FP64.One },
            Map = new Dictionary<int, int> { { 2, 20 }, { 1, 10 } },
            ItemList = new List<NullProbeItem> { new NullProbeItem { A = 1, B = 2 } },
            Items = new[] { new NullProbeItem { A = 3, B = 4 } },
            Tail = FP64.FromInt(7),
        };

        internal static NullProbeCommand FilledCommand() => new NullProbeCommand
        {
            Strings = new[] { "a", "한글" },
            StringList = new List<string> { "x" },
            Ints = new[] { 1, 2 },
            IntList = new List<int> { 3 },
            Map = new Dictionary<int, int> { { 2, 20 }, { 1, 10 } },
            ItemList = new List<NullProbeItem> { new NullProbeItem { A = 1, B = 2 } },
            Items = new[] { new NullProbeItem { A = 3, B = 4 } },
            Tail = FP64.FromInt(7),
        };

        // ── null on the write side ──

        [Test]
        public void NullCollections_DataAsset_WritesExactlyGetSerializedSize()
        {
            Write(new NullProbeAsset { Tail = FP64.One });
        }

        [Test]
        public void NullCollections_Command_WritesExactlyGetSerializedSize()
        {
            Write(new NullProbeCommand { Tail = FP64.One });
        }

        [Test]
        public void NullCollections_ReadBackEmptyNotNull()
        {
            var bytes = Write(new NullProbeAsset { Tail = FP64.One });

            var reader = new SpanReader(bytes);
            var back = NullProbeAsset.Deserialize(ref reader);

            Assert.AreEqual(0, back.Strings.Length);
            Assert.AreEqual(0, back.StringList.Count);
            Assert.AreEqual(0, back.Ints.Length);
            Assert.AreEqual(0, back.IntList.Count);
            Assert.AreEqual(0, back.Fixed.Length);
            Assert.AreEqual(0, back.Map.Count);
            Assert.AreEqual(0, back.ItemList.Count);
            Assert.AreEqual(0, back.Items.Length);
            Assert.AreSame(Array.Empty<int>(), back.Ints, "a zero-length array reads back as Array.Empty<T>()");
            Assert.AreEqual(FP64.One, back.Tail);
            Assert.AreEqual(bytes.Length, reader.Position);
        }

        // ── no initializer on the read side ──

        [Test]
        public void UninitializedFields_DataAsset_RoundTrip()
        {
            var asset = FilledAsset();
            var bytes = Write(asset);

            var reader = new SpanReader(bytes);
            var back = NullProbeAsset.Deserialize(ref reader);

            Assert.AreEqual(asset.Strings, back.Strings);
            Assert.AreEqual(asset.StringList, back.StringList);
            Assert.AreEqual(asset.Ints, back.Ints);
            Assert.AreEqual(asset.IntList, back.IntList);
            Assert.AreEqual(asset.Fixed, back.Fixed);
            Assert.AreEqual(asset.Map, back.Map);
            Assert.AreEqual(asset.ItemList, back.ItemList);
            Assert.AreEqual(asset.Items, back.Items);
            Assert.AreEqual(bytes.Length, reader.Position);
        }

        [Test]
        public void UninitializedFields_Command_RoundTripIntoFreshInstance()
        {
            var command = FilledCommand();
            var bytes = Write(command);

            var fresh = new NullProbeCommand();
            var reader = new SpanReader(bytes);
            fresh.Deserialize(ref reader);

            Assert.AreEqual(command.Strings, fresh.Strings);
            Assert.AreEqual(command.StringList, fresh.StringList);
            Assert.AreEqual(command.IntList, fresh.IntList);
            Assert.AreEqual(command.Map, fresh.Map);
            Assert.AreEqual(command.ItemList, fresh.ItemList);
            Assert.AreEqual(command.Items, fresh.Items);
            Assert.AreEqual(bytes.Length, reader.Position);
        }

        // The DataAssetWriter → DataAssetReader path from the issue that prompted this: one buffer sized from
        // every asset's GetSerializedSize, read back asset after asset.
        [Test]
        public void MixedCollection_WithNullCollections_WritesAndReadsBack()
        {
            var assets = new List<IDataAssetSerializable>
            {
                new NullProbeAsset { Tail = FP64.One },
                FilledAsset(),
                new NullProbeAsset { IntList = new List<int> { 9 } },
            };

            var bytes = DataAssetWriter.SerializeMixedCollectionToBytes(assets);
            var back = DataAssetReader.LoadMixedCollectionFromBytes(bytes);

            Assert.AreEqual(3, back.Count);
            Assert.AreEqual(0, ((NullProbeAsset)back[0]).IntList.Count);
            Assert.AreEqual(FilledAsset().Map, ((NullProbeAsset)back[1]).Map);
            Assert.AreEqual(new List<int> { 9 }, ((NullProbeAsset)back[2]).IntList);
        }

        // ── reuse and allocation ──

        [Test]
        public void SecondRead_ReusesTheCollectionsTheFirstReadCreated()
        {
            var bytes = Write(FilledCommand());
            var target = new NullProbeCommand();

            var first = new SpanReader(bytes);
            target.Deserialize(ref first);
            var list = target.StringList;
            var map = target.Map;

            var second = new SpanReader(bytes);
            target.Deserialize(ref second);

            Assert.AreSame(list, target.StringList);
            Assert.AreSame(map, target.Map);
            Assert.AreEqual(FilledCommand().StringList, target.StringList);
        }

        // Dictionary is left out: its serialize allocates a sorted key list by design (editor / conversion path).
        [Test]
        public void Serialize_AllocatesNothing_NullOrFilled()
        {
            var empty = new NullProbeCommand { Tail = FP64.One };
            var filled = FilledCommand();
            filled.Map = null;
            var buffer = new byte[4096];

            long control = AllocProbe.SmallestWindow(() => GC.KeepAlive(new int[8]));
            long nullCase = AllocProbe.SmallestWindow(() =>
            {
                for (int i = 0; i < 1000; i++) { var w = new SpanWriter(buffer); empty.Serialize(ref w); }
            });
            long filledCase = AllocProbe.SmallestWindow(() =>
            {
                for (int i = 0; i < 1000; i++) { var w = new SpanWriter(buffer); filled.Serialize(ref w); }
            });

            Assert.That(control, Is.GreaterThan(0), "positive control — the probe must see a known allocation");
            Assert.That(nullCase, Is.Zero, "null collections");
            Assert.That(filledCase, Is.Zero, "filled collections");
        }

        // ── members the codec cannot assign ──

        // If the generated Deserialize assigned to these members this file would not compile (CS0191 for the
        // readonly fields, CS0200 for the get-only property) — that compile failure is this test's red.
        [Test]
        public void UnassignableMembers_RoundTrip()
        {
            var command = new UnassignableProbeCommand();
            command.ReadonlyList.Add(5);
            command.ReadonlyMap[1] = 2;
            command.GetOnlyList.Add(6);
            var bytes = Write(command);

            var back = new UnassignableProbeCommand();
            var reader = new SpanReader(bytes);
            back.Deserialize(ref reader);

            Assert.AreEqual(new List<int> { 5 }, back.ReadonlyList);
            Assert.AreEqual(2, back.ReadonlyMap[1]);
            Assert.AreEqual(new List<int> { 6 }, back.GetOnlyList);
        }
    }
}
