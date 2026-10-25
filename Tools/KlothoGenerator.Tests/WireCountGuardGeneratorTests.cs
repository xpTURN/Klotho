using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace xpTURN.Klotho.Generator.Tests
{
    /// <summary>
    /// Every wire count the generated Deserialize reads is checked against the bytes left,
    /// after the read and before anything is allocated, with the element's minimum wire size.
    /// </summary>
    [TestFixture]
    public class WireCountGuardGeneratorTests
    {
        private const string Stub = @"
using System;
namespace xpTURN.Klotho.Serialization
{
    [AttributeUsage(AttributeTargets.Class)] public class KlothoSerializableAttribute : Attribute { public KlothoSerializableAttribute(int typeId = -1) {} }
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)] public class KlothoOrderAttribute : Attribute { public KlothoOrderAttribute(int order = -1) {} }
    [AttributeUsage(AttributeTargets.Struct)] public class KlothoSerializableStructAttribute : Attribute { }
}
namespace xpTURN.Klotho.Core { public abstract class CommandBase { } }
namespace xpTURN.Klotho.ECS
{
    public interface IDataAsset { int AssetId { get; } }
    [AttributeUsage(AttributeTargets.Class)] public class KlothoDataAssetAttribute : Attribute
    {
        public KlothoDataAssetAttribute(int typeId) {}
        public int AssetId { get; set; }
        public string Key { get; set; }
    }
}
";

        private const string Source = @"
using System.Collections.Generic;
using System.Runtime.InteropServices;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
namespace Demo
{
    [KlothoSerializableStruct]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public partial struct Item { public int A; }

    [KlothoSerializable(42)]
    public partial class ProbeCommand : CommandBase
    {
        [KlothoOrder] public int[] Ints = System.Array.Empty<int>();
        [KlothoOrder] public List<int> IntList = new List<int>();
        [KlothoOrder] public string[] Strs = System.Array.Empty<string>();
        [KlothoOrder] public List<string> StrList = new List<string>();
        [KlothoOrder] public Item[] Items = System.Array.Empty<Item>();
        [KlothoOrder] public List<Item> ItemList = new List<Item>();
        [KlothoOrder] public Dictionary<int, long> Map = new Dictionary<int, long>();
    }

    [KlothoDataAsset(43, AssetId = 43)]
    public partial class ProbeAsset : IDataAsset
    {
        [KlothoOrder(0)] public List<int> Ids = new List<int>();
    }
}";

        private static List<string> Run()
        {
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator).Where(p => p.Length > 0)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
            var compilation = CSharpCompilation.Create("GuardSim",
                new[] { CSharpSyntaxTree.ParseText(Stub), CSharpSyntaxTree.ParseText(Source) },
                refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var driver = CSharpGeneratorDriver.Create(new KlothoSerializationGenerator());
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
            return driver.GetRunResult().GeneratedTrees.Select(t => t.GetText().ToString()).ToList();
        }

        private const string Nested = "System.Math.Max(1, default(Demo.Item).GetSerializedSize())";

        [TestCase("ProbeCommand", "Ints", "4", "new int[__count_Ints]")]
        [TestCase("ProbeCommand", "IntList", "4", "Capacity = __count_IntList")]
        [TestCase("ProbeCommand", "Strs", "4", "new string[__count_Strs]")]
        [TestCase("ProbeCommand", "StrList", "4", "Capacity = __count_StrList")]
        [TestCase("ProbeCommand", "Items", Nested, "new Demo.Item[__count_Items]")]
        [TestCase("ProbeCommand", "ItemList", Nested, "Capacity = __count_ItemList")]
        [TestCase("ProbeCommand", "Map", "12", "(__count_Map)")]
        [TestCase("ProbeAsset", "Ids", "4", "Capacity = __count_Ids")]
        public void CountIsGuardedBeforeAllocation(string typeName, string field, string minBytes, string allocation)
        {
            var src = Run().FirstOrDefault(s => s.Contains($"partial class {typeName}"));
            Assert.That(src, Is.Not.Null, $"no generated source for {typeName}");

            int read = src.IndexOf($"int __count_{field} = reader.ReadInt32();", StringComparison.Ordinal);
            int guard = src.IndexOf($"if (__count_{field} < 0 || (long)__count_{field} * {minBytes} > reader.Remaining)", StringComparison.Ordinal);
            int alloc = src.IndexOf(allocation, StringComparison.Ordinal);

            Assert.That(read, Is.GreaterThanOrEqualTo(0), "count read");
            Assert.That(guard, Is.GreaterThan(read), $"guard with minBytes {minBytes} after the read");
            Assert.That(alloc, Is.GreaterThan(guard), "allocation after the guard");
        }
    }
}
