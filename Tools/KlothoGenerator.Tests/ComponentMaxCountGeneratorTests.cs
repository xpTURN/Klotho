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
    /// Generator-driver coverage for the [KlothoComponent(id, MaxCount=N)] emit — asserts the generated
    /// StorageRegistrar Register&lt;T&gt; call + diagnostics only. Runtime layout / overflow / wire behavior
    /// is exercised by the EditMode component tests.
    /// </summary>
    [TestFixture]
    public class ComponentMaxCountGeneratorTests
    {
        // Minimal ECS surface the ComponentAnalyzer resolves against (IComponent + the two attributes,
        // with MaxCount as a named-arg field mirroring the real KlothoComponentAttribute).
        private const string Stub = @"
using System;
namespace xpTURN.Klotho.ECS
{
    [AttributeUsage(AttributeTargets.Struct)]
    public class KlothoComponentAttribute : Attribute
    {
        public int ComponentTypeId { get; }
        public int MaxCount;
        public KlothoComponentAttribute(int componentTypeId) { ComponentTypeId = componentTypeId; }
    }
    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class KlothoSingletonComponentAttribute : Attribute { }
    public interface IComponent { }
}
";

        private sealed class GenOutput
        {
            public List<Diagnostic> Diagnostics;
            public string Registrar;
        }

        private static GenOutput Run(string componentDecl)
        {
            string source = $@"
using System.Runtime.InteropServices;
using xpTURN.Klotho.ECS;
namespace Demo
{{
{componentDecl}
}}";

            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Where(p => p.Length > 0)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            var compilation = CSharpCompilation.Create(
                "MaxCountGen",
                new[] { CSharpSyntaxTree.ParseText(Stub), CSharpSyntaxTree.ParseText(source) },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new KlothoSerializationGenerator());
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
            var result = driver.GetRunResult();

            var trees = result.GeneratedTrees.Select(t => t.GetText().ToString()).ToList();

            return new GenOutput
            {
                Diagnostics = result.Diagnostics.ToList(),
                Registrar = trees.FirstOrDefault(t => t.Contains("StorageRegistrar")) ?? string.Empty,
            };
        }

        private const string Layout = "    [StructLayout(LayoutKind.Sequential, Pack = 4)]";

        [Test]
        public void MaxCount_Specified_EmitsMaxCountArg()
        {
            var o = Run($@"
    [KlothoComponent(9101, MaxCount = 8)]
{Layout}
    public partial struct FooComponent : IComponent {{ public int Value; }}");

            Assert.That(o.Registrar, Does.Contain(
                "ComponentStorageRegistry.Register<Demo.FooComponent>(Demo.FooComponent.TYPE_ID, maxCount: 8);"));
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG_ECS006"));
        }

        [Test]
        public void MaxCount_Unspecified_NoMaxCountArg()
        {
            var o = Run($@"
    [KlothoComponent(9102)]
{Layout}
    public partial struct BarComponent : IComponent {{ public int Value; }}");

            // Unspecified MaxCount emits the plain 1-arg Register call — no maxCount argument.
            Assert.That(o.Registrar, Does.Contain(
                "ComponentStorageRegistry.Register<Demo.BarComponent>(Demo.BarComponent.TYPE_ID);"));
            Assert.That(o.Registrar, Does.Not.Contain("maxCount:"));
        }

        [Test]
        public void Singleton_WithMaxCount_SuppressesArg_AndWarns()
        {
            var o = Run($@"
    [KlothoComponent(9103, MaxCount = 8)]
    [KlothoSingletonComponent]
{Layout}
    public partial struct BazComponent : IComponent {{ public int Value; }}");

            // Singleton wins → isSingleton emit, no maxCount arg, plus the KLSG_ECS006 warning.
            Assert.That(o.Registrar, Does.Contain(
                "ComponentStorageRegistry.Register<Demo.BazComponent>(Demo.BazComponent.TYPE_ID, isSingleton: true);"));
            Assert.That(o.Registrar, Does.Not.Contain("maxCount:"));
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Contain("KLSG_ECS006"));
        }
    }
}
