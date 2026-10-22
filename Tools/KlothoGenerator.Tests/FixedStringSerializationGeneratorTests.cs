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
    /// Tier 1 (generator-driver) coverage for FixedString32/64 first-class mapping.
    /// Asserts generated source TEXT + diagnostics only — runtime round-trip / size equality / hash
    /// stability is covered separately at Tier 2 (Unity EditMode).
    /// </summary>
    [TestFixture]
    public class FixedStringSerializationGeneratorTests
    {
        // Stub: trigger attrs + CommandBase + minimal FixedString stand-ins in the real namespace.
        // The generator resolves these by display name (xpTURN.Klotho.ECS.FixedString32/64) for the
        // TypeMappings lookup; the fixed buffer / unmanaged-ness are irrelevant to text assertions.
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
    public struct FixedString32 { public short Length; }
    public struct FixedString64 { public short Length; }
}
";

        private sealed class GenOutput
        {
            public List<Diagnostic> Diagnostics;
            public string CommandSource;
        }

        private static GenOutput Run(string fields)
        {
            string source = @"
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
namespace Demo
{
    [KlothoSerializable(42)]
    public partial class MyCommand : CommandBase
    {
" + fields + @"
    }
}";

            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Where(p => p.Length > 0)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            var compilation = CSharpCompilation.Create(
                "BrawlerSim",
                new[] { CSharpSyntaxTree.ParseText(Stub), CSharpSyntaxTree.ParseText(source) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new KlothoSerializationGenerator());
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
            var result = driver.GetRunResult();

            var src = result.GeneratedTrees
                .Select(t => t.GetText().ToString())
                .FirstOrDefault(t => t.Contains("MyCommand"));

            return new GenOutput { Diagnostics = result.Diagnostics.ToList(), CommandSource = src ?? string.Empty };
        }

        [Test]
        public void DirectField_NoKLSG003_DelegatesWrite()
        {
            var o = Run("        [KlothoOrder] public FixedString32 Name;");
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG003"));
            Assert.That(o.CommandSource, Does.Contain("writer.WriteFixedString32(this.Name);"));
            Assert.That(o.CommandSource, Does.Contain("this.Name = reader.ReadFixedString32();"));
        }

        [Test]
        public void List_NoKLSG005_SizeUsesCount32()
        {
            var o = Run("        [KlothoOrder] public List<FixedString32> Names = new List<FixedString32>();");
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG005"));
            Assert.That(o.CommandSource, Does.Contain("writer.WriteFixedString32(this.Names[__i]);"));
            Assert.That(o.CommandSource, Does.Contain("(this.Names?.Count ?? 0) * 32"));
        }

        [Test]
        public void Array_SizeUsesLength32()
        {
            var o = Run("        [KlothoOrder] public FixedString32[] Names;");
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG005"));
            Assert.That(o.CommandSource, Does.Contain("(this.Names?.Length ?? 0) * 32"));
        }

        [Test]
        public void FixedString64_DirectField_Size64()
        {
            var o = Run("        [KlothoOrder] public FixedString64 Name;");
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG003"));
            Assert.That(o.CommandSource, Does.Contain("writer.WriteFixedString64(this.Name);"));
            // base(Command=12) + 64 = 76; assert the constant size method contains 64-derived total
            Assert.That(o.CommandSource, Does.Contain("GetSerializedSize() => 76;"));
        }

        [Test]
        public void Dictionary_NoKLSG006()
        {
            var o = Run("        [KlothoOrder] public Dictionary<int, FixedString32> Map = new Dictionary<int, FixedString32>();");
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG006"));
            Assert.That(o.CommandSource, Does.Contain("WriteFixedString32"));
        }
    }
}
