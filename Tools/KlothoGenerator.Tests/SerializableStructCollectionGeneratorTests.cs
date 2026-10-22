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
    /// Tier 1 (generator-driver) coverage for List&lt;[KlothoSerializableStruct]&gt; / T[] support.
    /// Runs <see cref="KlothoSerializationGenerator"/> via CSharpGeneratorDriver and asserts on the
    /// generated source TEXT + diagnostics — no runtime (SpanWriter/SpanReader) execution. Runtime
    /// round-trip + size equality is covered separately at Tier 2 (Unity EditMode, headless-incapable).
    /// </summary>
    [TestFixture]
    public class SerializableStructCollectionGeneratorTests
    {
        // Minimal stand-ins so the generator's category/attribute resolution succeeds. Only the
        // generated text is inspected, so the runtime codec surface (SpanWriter, CommandRegistry…)
        // need not exist for these assertions.
        private const string Stub = @"
using System;
namespace xpTURN.Klotho.Serialization
{
    [AttributeUsage(AttributeTargets.Class)] public class KlothoSerializableAttribute : Attribute { public KlothoSerializableAttribute(int typeId = -1) {} }
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)] public class KlothoOrderAttribute : Attribute { public KlothoOrderAttribute(int order = -1) {} }
    [AttributeUsage(AttributeTargets.Struct)] public class KlothoSerializableStructAttribute : Attribute { }
}
namespace xpTURN.Klotho.Core { public abstract class CommandBase { } }
";

        private sealed class GenOutput
        {
            public List<Diagnostic> Diagnostics;
            public string CommandSource;   // generated source containing the host command
        }

        private static GenOutput Run(string source, string hostName)
        {
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

            var commandSrc = result.GeneratedTrees
                .Select(t => t.GetText().ToString())
                .FirstOrDefault(t => t.Contains($"struct {hostName}") || t.Contains($"class {hostName}")
                                     || t.Contains($"partial class {hostName}") || t.Contains(hostName + ".TYPE_ID")
                                     || t.Contains(hostName));

            return new GenOutput
            {
                Diagnostics = result.Diagnostics.ToList(),
                CommandSource = commandSrc ?? string.Empty,
            };
        }

        private const string NestedStruct = @"
    [xpTURN.Klotho.Serialization.KlothoSerializableStruct]
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    public partial struct Item { public int Id; public int Qty; }";

        private static string Host(string fields) => $@"
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Core;
namespace Demo
{{
{NestedStruct}

    [KlothoSerializable(42)]
    public partial class MyCommand : CommandBase
    {{
{fields}
    }}
}}";

        [Test]
        public void ListOfNestedStruct_NoKLSG005()
        {
            var o = Run(Host("        [KlothoOrder] public List<Item> Items = new List<Item>();"), "MyCommand");
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG005"));
        }

        [Test]
        public void ArrayOfNestedStruct_NoKLSG005()
        {
            var o = Run(Host("        [KlothoOrder] public Item[] Items;"), "MyCommand");
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG005"));
        }

        [Test]
        public void UnsupportedElement_StillKLSG005()
        {
            var o = Run(Host("        [KlothoOrder] public List<System.Text.StringBuilder> Bad = new List<System.Text.StringBuilder>();"), "MyCommand");
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Contain("KLSG005"));
        }

        [Test]
        public void ListOfNestedStruct_DelegatesSerialize()
        {
            var o = Run(Host("        [KlothoOrder] public List<Item> Items = new List<Item>();"), "MyCommand");
            Assert.That(o.CommandSource, Does.Contain("this.Items[__i].Serialize(ref writer);"));
        }

        [Test]
        public void ListOfNestedStruct_DeserializeUsesScopedTempAndAdd()
        {
            var o = Run(Host("        [KlothoOrder] public List<Item> Items = new List<Item>();"), "MyCommand");
            Assert.That(o.CommandSource, Does.Contain("var __e_Items = default(Demo.Item);"));
            Assert.That(o.CommandSource, Does.Contain("__e_Items.Deserialize(ref reader);"));
            Assert.That(o.CommandSource, Does.Contain("this.Items.Add(__e_Items);"));
        }

        [Test]
        public void ArrayOfNestedStruct_DeserializeAddressable()
        {
            var o = Run(Host("        [KlothoOrder] public Item[] Items;"), "MyCommand");
            Assert.That(o.CommandSource, Does.Contain("this.Items = __count_Items == 0 ? System.Array.Empty<Demo.Item>() : new Demo.Item[__count_Items];"));
            Assert.That(o.CommandSource, Does.Contain("this.Items[__i].Deserialize(ref reader);"));
        }

        [Test]
        public void Size_ListUsesCount_ArrayUsesLength()
        {
            var o = Run(Host(
                "        [KlothoOrder] public List<Item> Items = new List<Item>();\n" +
                "        [KlothoOrder] public Item[] Arr;"), "MyCommand");
            Assert.That(o.CommandSource, Does.Contain("(this.Items?.Count ?? 0) * default(Demo.Item).GetSerializedSize()"));
            Assert.That(o.CommandSource, Does.Contain("(this.Arr?.Length ?? 0) * default(Demo.Item).GetSerializedSize()"));
        }

        [Test]
        public void Regression_ListOfPrimitiveUnchanged()
        {
            var o = Run(Host("        [KlothoOrder] public List<int> Numbers = new List<int>();"), "MyCommand");
            // existing primitive-element path: WriteInt32 loop + Count*4 size, no nested delegation
            Assert.That(o.CommandSource, Does.Contain("writer.WriteInt32(this.Numbers[__i]);"));
            Assert.That(o.CommandSource, Does.Contain("(this.Numbers?.Count ?? 0) * 4"));
            Assert.That(o.CommandSource, Does.Not.Contain("default(int).GetSerializedSize()"));
        }
    }
}
