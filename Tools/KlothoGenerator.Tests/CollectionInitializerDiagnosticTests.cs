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
    /// KLSG008 — a collection field (<c>T[]</c> / <c>List&lt;T&gt;</c> / <c>Dictionary&lt;K,V&gt;</c>) with no
    /// declaration initializer.
    ///
    /// <para>The codec writes a null collection as empty and reads it back as an empty one, so a null that a game
    /// meant as "absent" is lost on the wire, and a server-driven client predicts with the null its peers never
    /// see. The warning points at the source of that null. It stays silent where the generator writes no codec
    /// (a type that declares <c>SerializeData</c> or <c>GetSerializedSize</c> itself) and on <c>string</c> /
    /// <c>byte[]</c>, which have their own null rules.</para>
    ///
    /// <para>It is a warning and must not cost the type its codec: <c>TypeAnalyzer</c> once withheld the codec
    /// on any diagnostic at all, which would have turned this warning into a missing <c>GetSerializedSize</c>.</para>
    /// </summary>
    [TestFixture]
    public class CollectionInitializerDiagnosticTests
    {
        private const string Stub = @"
using System;
namespace xpTURN.Klotho.Serialization
{
    [AttributeUsage(AttributeTargets.Class)] public class KlothoSerializableAttribute : Attribute { public KlothoSerializableAttribute(int typeId = -1) {} }
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)] public class KlothoOrderAttribute : Attribute { public KlothoOrderAttribute(int order = -1) {} }
    public ref struct SpanWriter { }
}
namespace xpTURN.Klotho.Core { public abstract class CommandBase { } }
namespace xpTURN.Klotho.ECS
{
    public interface IDataAsset { int AssetId { get; } }
    [AttributeUsage(AttributeTargets.Class)] public class KlothoDataAssetAttribute : Attribute
    {
        public KlothoDataAssetAttribute(int typeId) {}
        public int AssetId { get; set; }
    }
}
";

        private static (List<Diagnostic> diagnostics, List<string> sources) Run(string body)
        {
            string source = @"
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
namespace Demo
{
" + body + @"
}";
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Where(p => p.Length > 0)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            var compilation = CSharpCompilation.Create(
                "ProbeSim",
                new[] { CSharpSyntaxTree.ParseText(Stub), CSharpSyntaxTree.ParseText(source) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new KlothoSerializationGenerator());
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
            var result = driver.GetRunResult();
            return (result.Diagnostics.ToList(), result.GeneratedTrees.Select(t => t.GetText().ToString()).ToList());
        }

        private static List<Diagnostic> Klsg008(List<Diagnostic> diagnostics)
            => diagnostics.Where(d => d.Id == "KLSG008").ToList();

        [Test]
        public void Command_EachUninitializedCollectionWarns()
        {
            var (diagnostics, _) = Run(@"
    [KlothoSerializable(42)]
    public partial class Cmd : CommandBase
    {
        [KlothoOrder] public int[] Ids;
        [KlothoOrder] public List<int> List;
        [KlothoOrder] public Dictionary<int, int> Map;
        [KlothoOrder] public List<string> Names { get; set; }
    }");

            var warnings = Klsg008(diagnostics);
            Assert.That(warnings.Select(d => d.GetMessage()), Has.Exactly(4).Items);
            foreach (var name in new[] { "'Ids'", "'List'", "'Map'", "'Names'" })
                Assert.That(warnings.Any(d => d.GetMessage().Contains(name)), name);
            Assert.That(warnings.All(d => d.Severity == DiagnosticSeverity.Warning));
        }

        [Test]
        public void DataAsset_UninitializedCollectionWarns()
        {
            var (diagnostics, _) = Run(@"
    [KlothoDataAsset(43, AssetId = 43)]
    public partial class Asset : IDataAsset
    {
        [KlothoOrder(0)] public string[] Stats;
        [KlothoOrder(1)] public List<int> Initialized = new List<int>();
    }");

            var warnings = Klsg008(diagnostics);
            Assert.That(warnings, Has.Exactly(1).Items);
            Assert.That(warnings[0].GetMessage(), Does.Contain("'Stats'"));
        }

        [Test]
        public void Initialized_FieldsAndProperties_DoNotWarn()
        {
            var (diagnostics, _) = Run(@"
    [KlothoSerializable(44)]
    public partial class Cmd : CommandBase
    {
        [KlothoOrder] public int[] Ids = System.Array.Empty<int>();
        [KlothoOrder] public List<int> List = new List<int>();
        [KlothoOrder] public readonly Dictionary<int, int> Map = new Dictionary<int, int>();
        [KlothoOrder] public List<string> Names { get; } = new List<string>();
    }");

            Assert.That(Klsg008(diagnostics), Is.Empty);
        }

        [Test]
        public void StringAndByteArray_AreNotCollections()
        {
            var (diagnostics, _) = Run(@"
    [KlothoSerializable(45)]
    public partial class Cmd : CommandBase
    {
        [KlothoOrder] public string Name;
        [KlothoOrder] public byte[] Blob;
    }");

            Assert.That(Klsg008(diagnostics), Is.Empty);
        }

        [TestCase("protected void SerializeData(ref SpanWriter writer) { }")]
        [TestCase("public int GetSerializedSize() => 0;")]
        public void ManualSerialization_DoesNotWarn(string member)
        {
            var (diagnostics, _) = Run(@"
    [KlothoSerializable(46)]
    public partial class Cmd : CommandBase
    {
        [KlothoOrder] public int[] Ids;
        " + member + @"
    }");

            Assert.That(Klsg008(diagnostics), Is.Empty);
        }

        [Test]
        public void Warning_DoesNotWithholdTheCodec()
        {
            var (diagnostics, sources) = Run(@"
    [KlothoSerializable(47)]
    public partial class Cmd : CommandBase
    {
        [KlothoOrder] public int[] Ids;
    }");

            Assert.That(Klsg008(diagnostics), Has.Exactly(1).Items);
            Assert.That(sources.Any(s => s.Contains("partial class Cmd") && s.Contains("GetSerializedSize")),
                "the type still gets its generated codec");
        }
    }
}
