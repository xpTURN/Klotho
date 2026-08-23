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
    /// Generator-driver coverage for [KlothoCleanup(mode)] — the emitted Register&lt;T&gt; argument and the
    /// three analyzer rules. Runtime pass behavior lives in the ECS cleanup tests.
    /// </summary>
    [TestFixture]
    public class ComponentCleanupGeneratorTests
    {
        // Minimal ECS surface: the component attribute plus the singleton/core markers and the cleanup
        // attribute with its enum, mirroring the real declarations closely enough for symbol lookup.
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
    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class KlothoCoreComponentAttribute : Attribute { }
    public enum CleanupMode { None = 0, RemoveComponent = 1, DestroyEntity = 2 }
    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class KlothoCleanupAttribute : Attribute
    {
        public CleanupMode Mode { get; }
        public KlothoCleanupAttribute(CleanupMode mode) { Mode = mode; }
    }
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
                "CleanupGen",
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

        // The argument must be the enum MEMBER NAME. A raw integer would compile but read as a magic
        // number, and a raw cast is what KLSG_ECS009 exists to prevent.
        [Test]
        public void Cleanup_RemoveComponent_EmitsEnumMemberName()
        {
            var o = Run($@"
    [KlothoComponent(9401)]
    [KlothoCleanup(CleanupMode.RemoveComponent)]
{Layout}
    public partial struct MarkComponent : IComponent {{ public int Value; }}");

            Assert.That(o.Registrar, Does.Contain(
                "ComponentStorageRegistry.Register<Demo.MarkComponent>(Demo.MarkComponent.TYPE_ID, cleanup: xpTURN.Klotho.ECS.CleanupMode.RemoveComponent);"));
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG_ECS007"));
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG_ECS009"));
        }

        [Test]
        public void Cleanup_DestroyEntity_EmitsEnumMemberName()
        {
            var o = Run($@"
    [KlothoComponent(9402)]
    [KlothoCleanup(CleanupMode.DestroyEntity)]
{Layout}
    public partial struct DoomedComponent : IComponent {{ public int Value; }}");

            Assert.That(o.Registrar, Does.Contain("cleanup: xpTURN.Klotho.ECS.CleanupMode.DestroyEntity"));
        }

        // None is the default, so it must not reach the registrar at all — otherwise every component in
        // the codebase would grow a redundant argument.
        [Test]
        public void Cleanup_None_EmitsNoArg()
        {
            var o = Run($@"
    [KlothoComponent(9403)]
    [KlothoCleanup(CleanupMode.None)]
{Layout}
    public partial struct PlainComponent : IComponent {{ public int Value; }}");

            Assert.That(o.Registrar, Does.Contain(
                "ComponentStorageRegistry.Register<Demo.PlainComponent>(Demo.PlainComponent.TYPE_ID);"));
            Assert.That(o.Registrar, Does.Not.Contain("cleanup:"));
        }

        [Test]
        public void Cleanup_Unmarked_EmitsNoArg()
        {
            var o = Run($@"
    [KlothoComponent(9404)]
{Layout}
    public partial struct UnmarkedComponent : IComponent {{ public int Value; }}");

            Assert.That(o.Registrar, Does.Not.Contain("cleanup:"));
        }

        // A cleaned-up singleton is legal with RemoveComponent — both traits must survive into the call.
        [Test]
        public void Cleanup_SingletonWithRemoveComponent_Allowed_BothArgsEmitted()
        {
            var o = Run($@"
    [KlothoComponent(9405)]
    [KlothoSingletonComponent]
    [KlothoCleanup(CleanupMode.RemoveComponent)]
{Layout}
    public partial struct SingletonMarkComponent : IComponent {{ public int Value; }}");

            Assert.That(o.Registrar, Does.Contain(
                "ComponentStorageRegistry.Register<Demo.SingletonMarkComponent>(Demo.SingletonMarkComponent.TYPE_ID, isSingleton: true, cleanup: xpTURN.Klotho.ECS.CleanupMode.RemoveComponent);"));
            Assert.That(o.Diagnostics.Select(d => d.Id), Does.Not.Contain("KLSG_ECS008"));
        }

        // KLSG_ECS008 — the carrier entity may hold other components.
        [Test]
        public void Cleanup_SingletonWithDestroyEntity_IsError()
        {
            var o = Run($@"
    [KlothoComponent(9406)]
    [KlothoSingletonComponent]
    [KlothoCleanup(CleanupMode.DestroyEntity)]
{Layout}
    public partial struct SingletonDoomedComponent : IComponent {{ public int Value; }}");

            var d = o.Diagnostics.Single(x => x.Id == "KLSG_ECS008");
            Assert.That(d.Severity, Is.EqualTo(DiagnosticSeverity.Error));
        }

        // KLSG_ECS007 — warning, not error: pruning exemption and lifetime are orthogonal axes.
        [Test]
        public void Cleanup_OnCoreComponent_IsWarning()
        {
            var o = Run($@"
    [KlothoComponent(9407)]
    [KlothoCoreComponent]
    [KlothoCleanup(CleanupMode.RemoveComponent)]
{Layout}
    public partial struct CoreMarkComponent : IComponent {{ public int Value; }}");

            var d = o.Diagnostics.Single(x => x.Id == "KLSG_ECS007");
            Assert.That(d.Severity, Is.EqualTo(DiagnosticSeverity.Warning));
            // The mode still flows through — the warning does not disable the feature.
            Assert.That(o.Registrar, Does.Contain("cleanup: xpTURN.Klotho.ECS.CleanupMode.RemoveComponent"));
        }

        // KLSG_ECS009 — an undefined value would emit as a raw cast and be silently ignored at runtime:
        // attribute present, nothing cleaned up, no warning. That is the failure this rule exists for.
        [Test]
        public void Cleanup_UndefinedModeValue_IsError_AndEmitsNoArg()
        {
            var o = Run($@"
    [KlothoComponent(9408)]
    [KlothoCleanup((CleanupMode)7)]
{Layout}
    public partial struct BogusModeComponent : IComponent {{ public int Value; }}");

            var d = o.Diagnostics.Single(x => x.Id == "KLSG_ECS009");
            Assert.That(d.Severity, Is.EqualTo(DiagnosticSeverity.Error));
            Assert.That(o.Registrar, Does.Not.Contain("cleanup:"),
                "an undefined value must not be emitted as a raw cast");
        }
    }
}
