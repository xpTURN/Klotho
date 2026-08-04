using NUnit.Framework;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Json;

namespace xpTURN.Klotho.Unity2022.Tests
{
    /// <summary>
    /// Unity 2022.3 LTS compatibility smoke for the JSON authoring path. This is the only part of the
    /// package that depends on a third-party precompiled assembly (Newtonsoft.Json from
    /// com.unity.nuget.newtonsoft-json) and on reflection-driven contract resolution, both of which
    /// break at runtime rather than at compile time when the editor resolves a different copy.
    /// </summary>
    [TestFixture]
    public class DataAssetJsonSmokeTests
    {
        [Test]
        public void DataAsset_JsonRoundTripsThroughCustomConverters()
        {
            var source = new SmokeAsset
            {
                Speed = FP64.FromDouble(4.5),
                Cost = 13,
                Offset = new FPVector3(FP64.FromDouble(-1.25), FP64.Zero, FP64.FromDouble(2.5)),
            };

            string json = DataAssetJsonSerializer.Serialize(source);
            Assert.IsNotNull(json);
            Assert.IsNotEmpty(json);

            var restored = DataAssetJsonSerializer.Deserialize<SmokeAsset>(json);

            Assert.AreEqual(source.Speed, restored.Speed, "FP64 converter");
            Assert.AreEqual(source.Cost, restored.Cost);
            Assert.AreEqual(source.Offset, restored.Offset, "FPVector3 converter");
            Assert.AreEqual(9304, restored.AssetId);
        }

        [Test]
        public void DataAsset_LoadsIntoRegistryAndResolvesByIdAndKey()
        {
            var builder = new DataAssetRegistry();
            string json = DataAssetJsonSerializer.Serialize(new SmokeAsset { Cost = 21 });

            builder.LoadFromJsonAndRegister<SmokeAsset>(json);
            IDataAssetRegistry registry = builder;

            Assert.AreEqual(21, registry.Get<SmokeAsset>(9304).Cost, "lookup by AssetId");
            Assert.AreEqual(21, registry.Get<SmokeAsset>().Cost, "single-instance lookup");
            Assert.AreEqual(21, registry.GetByKey<SmokeAsset>("SmokeAsset").Cost, "lookup by Key");
        }
    }
}
