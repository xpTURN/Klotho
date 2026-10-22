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
    /// <c>GetSerializedSize</c> budgets <c>string[]</c> and <c>List&lt;string&gt;</c> fields.
    ///
    /// <para>Without the term, <c>Serialize</c> still writes the count prefix and every element, so the
    /// size under-reports by at least 4 bytes per field. Callers that size the buffer from it —
    /// <c>DataAssetWriter</c>, the command send path — run off the end, and in a mixed data-asset collection
    /// the shortfall accumulates until a later asset overflows.</para>
    ///
    /// <para>Both size emitters are covered — <c>EmitHelper.EmitGetSerializedSize</c> (commands, messages,
    /// events, entities) and <c>DataAssetEmitter</c> — since each assembles the expression on its own from
    /// <c>GetSizeExpression</c>. Tier 1 (generated text only); the check that the size equals the bytes
    /// written, against the shipped analyzer DLL, is <c>StringCollectionSizeTests</c> in
    /// Klotho.Runtime.Tests.</para>
    /// </summary>
    [TestFixture]
    public class StringCollectionSizeGeneratorTests
    {
        private const string Stub = @"
using System;
namespace xpTURN.Klotho.Serialization
{
    [AttributeUsage(AttributeTargets.Class)] public class KlothoSerializableAttribute : Attribute { public KlothoSerializableAttribute(int typeId = -1) {} }
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)] public class KlothoOrderAttribute : Attribute { public KlothoOrderAttribute(int order = -1) {} }
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
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
namespace Demo
{
    [KlothoSerializable(42)]
    public partial class ProbeCommand : CommandBase
    {
        [KlothoOrder] public string[] Names = System.Array.Empty<string>();
        [KlothoOrder] public List<string> Tags = new List<string>();
    }

    [KlothoDataAsset(43, AssetId = 43)]
    public partial class ProbeAsset : IDataAsset
    {
        [KlothoOrder(0)] public string[] Names = System.Array.Empty<string>();
        [KlothoOrder(1)] public List<string> Tags = new List<string>();
    }
}";

        private static (List<Diagnostic> diagnostics, List<string> sources) Run()
        {
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Where(p => p.Length > 0)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            var compilation = CSharpCompilation.Create(
                "ProbeSim",
                new[] { CSharpSyntaxTree.ParseText(Stub), CSharpSyntaxTree.ParseText(Source) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new KlothoSerializationGenerator());
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
            var result = driver.GetRunResult();
            return (result.Diagnostics.ToList(), result.GeneratedTrees.Select(t => t.GetText().ToString()).ToList());
        }

        // The GetSerializedSize declaration of the type, up to the ';' that ends its expression body.
        private static string SizeExpression(List<string> sources, string typeName)
        {
            var src = sources.FirstOrDefault(s => s.Contains($"partial class {typeName}"));
            Assert.That(src, Is.Not.Null, $"no generated source for {typeName}");
            int start = src.IndexOf("GetSerializedSize()", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), $"{typeName} has no GetSerializedSize");
            int end = src.IndexOf(';', start);
            return src.Substring(start, end - start);
        }

        [Test]
        public void NoErrorDiagnostics()
        {
            var (diagnostics, _) = Run();
            Assert.That(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()),
                Is.Empty);
        }

        [TestCase("ProbeCommand")]
        [TestCase("ProbeAsset")]
        public void Size_StringArrayHasItsTerm(string typeName)
        {
            var (_, sources) = Run();
            Assert.That(SizeExpression(sources, typeName),
                Does.Contain("xpTURN.Klotho.Serialization.SerializationSize.StringArray(this.Names)"));
        }

        [TestCase("ProbeCommand")]
        [TestCase("ProbeAsset")]
        public void Size_StringListHasItsTerm(string typeName)
        {
            var (_, sources) = Run();
            Assert.That(SizeExpression(sources, typeName),
                Does.Contain("xpTURN.Klotho.Serialization.SerializationSize.StringList(this.Tags)"));
        }
    }
}
