using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Serialization.Tests
{
    // Codecs generated in this assembly by the shipped analyzer (com.xpturn.klotho/Plugins/Analyzers).
    // TypeId 9300 (DataAsset plane) / 9301 (command plane); a trailing fixed field shows an under-budgeted
    // collection as an overflow on the write that follows it.

    [KlothoDataAsset(9300, AssetId = 9300)]
    public partial class StringCollectionProbeAsset : IDataAsset
    {
        [KlothoOrder(0)] public string Description;
        [KlothoOrder(1)] public string[] Stats = Array.Empty<string>();
        [KlothoOrder(2)] public List<string> Tags = new List<string>();
        [KlothoOrder(3)] public FP64 Tail;
    }

    [KlothoSerializable(9301)]
    public partial class StringCollectionProbeCommand : CommandBase
    {
        [KlothoOrder(0)] public string[] Stats = Array.Empty<string>();
        [KlothoOrder(1)] public List<string> Tags = new List<string>();
        [KlothoOrder(2)] public FP64 Tail;
    }

    /// <summary>
    /// <c>string[]</c> and <c>List&lt;string&gt;</c> fields serialize into exactly the bytes
    /// <c>GetSerializedSize()</c> promises.
    ///
    /// <para>Callers size the buffer from <c>GetSerializedSize()</c> — <c>DataAssetWriter</c> and the command
    /// send path both do — so a size that leaves these fields out makes <c>Serialize</c> run off the end
    /// ("SpanWriter buffer overflow … Check GetSerializedSize()"). The codecs here come from the analyzer DLL
    /// the package ships, not from the generator source, so a DLL built before the size term existed fails
    /// here even while the generator's own tests (<c>StringCollectionSizeGeneratorTests</c>) pass.</para>
    /// </summary>
    [TestFixture]
    public class StringCollectionSizeTests
    {
        private static IEnumerable<TestCaseData> Contents()
        {
            yield return new TestCaseData(new[] { "Armor", "MagicResist" }, new[] { "a", "bb" }).SetName("{m}(ascii)");
            yield return new TestCaseData(new[] { "한글", "ü" }, new[] { "日本語" }).SetName("{m}(multibyte)");
            yield return new TestCaseData(new[] { null, "x" }, new string[] { null }).SetName("{m}(null elements)");
            yield return new TestCaseData(new string[0], new string[0]).SetName("{m}(empty)");
        }

        private static StringCollectionProbeAsset Asset(string[] stats, string[] tags) => new StringCollectionProbeAsset
        {
            Description = "desc",
            Stats = stats,
            Tags = new List<string>(tags),
            Tail = FP64.FromInt(7),
        };

        [TestCaseSource(nameof(Contents))]
        public void DataAsset_WritesExactlyGetSerializedSize(string[] stats, string[] tags)
        {
            var asset = Asset(stats, tags);
            var buffer = new byte[asset.GetSerializedSize()];
            var writer = new SpanWriter(buffer);

            asset.Serialize(ref writer);

            Assert.AreEqual(buffer.Length, writer.Position);
        }

        [TestCaseSource(nameof(Contents))]
        public void Command_WritesExactlyGetSerializedSize(string[] stats, string[] tags)
        {
            var command = new StringCollectionProbeCommand { Stats = stats, Tags = new List<string>(tags), Tail = FP64.FromInt(7) };
            var buffer = new byte[command.GetSerializedSize()];
            var writer = new SpanWriter(buffer);

            command.Serialize(ref writer);

            Assert.AreEqual(buffer.Length, writer.Position);
        }

        [TestCaseSource(nameof(Contents))]
        public void DataAsset_RoundTrips(string[] stats, string[] tags)
        {
            var asset = Asset(stats, tags);
            var buffer = new byte[asset.GetSerializedSize()];
            var writer = new SpanWriter(buffer);
            asset.Serialize(ref writer);

            var reader = new SpanReader(buffer);
            var back = StringCollectionProbeAsset.Deserialize(ref reader);

            // A null element is written as the empty string (SpanWriter.WriteString) and reads back as "".
            Assert.AreEqual(stats.Select(s => s ?? "").ToArray(), back.Stats);
            Assert.AreEqual(tags.Select(s => s ?? "").ToList(), back.Tags);
            Assert.AreEqual(asset.Tail, back.Tail);
            Assert.AreEqual(buffer.Length, reader.Position);
        }

        // DataAssetWriter sizes one buffer from every asset's GetSerializedSize and the reader advances by
        // what each Deserialize consumes, so every asset after an under-budgeted one is misread.
        [Test]
        public void MixedCollection_WritesAndReadsBackEveryAsset()
        {
            var assets = new List<IDataAssetSerializable>
            {
                Asset(new[] { "Armor", "MagicResist" }, new[] { "t" }),
                Asset(new[] { "Speed" }, new string[0]),
                Asset(new[] { "한글", "x", "yy" }, new[] { "a", "b", "c" }),
            };

            var bytes = DataAssetWriter.SerializeMixedCollectionToBytes(assets);
            var back = DataAssetReader.LoadMixedCollectionFromBytes(bytes);

            Assert.AreEqual(assets.Count, back.Count);
            for (int i = 0; i < assets.Count; i++)
            {
                var expected = (StringCollectionProbeAsset)assets[i];
                var actual = (StringCollectionProbeAsset)back[i];
                Assert.AreEqual(expected.Stats, actual.Stats, $"asset {i}");
                Assert.AreEqual(expected.Tags, actual.Tags, $"asset {i}");
                Assert.AreEqual(expected.Tail, actual.Tail, $"asset {i}");
            }
        }
    }
}
