using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace xpTURN.Klotho.Generator.Tests
{
    /// <summary>
    /// Coverage for the debug file dump (<c>&lt;projectRoot&gt;/Tools/Generated/&lt;assembly&gt;/</c>):
    /// the opt-in gate (the root must already exist), the skip-if-identical write, and the paths that
    /// resolve to no project root at all. Every test gets its own temporary root — a file left behind by
    /// an earlier test flips the very decision under test.
    /// </summary>
    [TestFixture]
    public class GeneratorFileDumpTests
    {
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

        /// <summary>Minimal surface for the [KlothoSerializable] command path — the aggregate emit
        /// (KlothoFactoryRegistration.g.cs) only appears for these, not for [KlothoComponent].</summary>
        private const string CommandStub = @"
using System;
namespace xpTURN.Klotho.Serialization
{
    [AttributeUsage(AttributeTargets.Class)]
    public class KlothoSerializableAttribute : Attribute
    {
        public KlothoSerializableAttribute(int typeId) { }
    }
}
namespace xpTURN.Klotho.Core
{
    public abstract class CommandBase { }
}
";

        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "klotho-dump-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best effort */ }
        }

        private static string Component(int id, string name, string field = "public int Value;")
            => $@"
    [KlothoComponent({id})]
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    public partial struct {name} : IComponent {{ {field} }}";

        /// <summary>
        /// Runs the generator over <paramref name="componentDecls"/> with source paths under
        /// <paramref name="sourceDir"/>. The path matters: the dump location is derived from the first
        /// syntax tree whose path contains an /Assets/ or /Packages/ segment.
        /// </summary>
        private static string[] Run(string sourceDir, string assemblyName, params string[] componentDecls)
        {
            string source = $@"
using xpTURN.Klotho.ECS;
namespace Demo
{{
{string.Join("\n", componentDecls)}
}}";

            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Where(p => p.Length > 0)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            var trees = new[]
            {
                CSharpSyntaxTree.ParseText(Stub, path: Path.Combine(sourceDir, "KlothoStub.cs")),
                CSharpSyntaxTree.ParseText(source, path: Path.Combine(sourceDir, "Components.cs")),
            };

            var compilation = CSharpCompilation.Create(
                assemblyName,
                trees,
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new KlothoSerializationGenerator());
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

            return driver.GetRunResult().GeneratedTrees.Select(t => t.GetText().ToString()).ToArray();
        }

        private string AssetsDir()
        {
            var dir = Path.Combine(_tempDir, "Assets", "Game");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private string DumpRoot() => Path.Combine(_tempDir, "Tools", "Generated");

        private string DumpDir(string assemblyName) => Path.Combine(DumpRoot(), assemblyName);

        private string EnableDump()
        {
            var root = DumpRoot();
            Directory.CreateDirectory(root);
            return root;
        }

        // --- Gate closed ------------------------------------------------------------------------

        [Test]
        public void NoOutputRoot_WritesNothing_AndDoesNotCreateIt()
        {
            var generated = Run(AssetsDir(), "DumpGateOff", Component(9401, "GateOffComponent"));

            Assert.That(generated, Is.Not.Empty, "the generator must still emit in-memory sources");
            Assert.That(Directory.Exists(Path.Combine(_tempDir, "Tools")), Is.False,
                "the dump root is opt-in — the generator must never create it");
        }

        // --- Gate open --------------------------------------------------------------------------

        [Test]
        public void OutputRootPresent_WritesTheSameContentAsTheInMemorySource()
        {
            EnableDump();
            var generated = Run(AssetsDir(), "DumpGateOn", Component(9402, "GateOnComponent"));

            var path = Path.Combine(DumpDir("DumpGateOn"), "Demo_GateOnComponent.g.cs");
            Assert.That(File.Exists(path), Is.True, "expected a dump file at " + path);

            var onDisk = File.ReadAllText(path);
            Assert.That(generated, Does.Contain(onDisk),
                "the dumped file must be byte-identical to what AddSource emitted");
        }

        // --- Identical rerun --------------------------------------------------------------------

        [Test]
        public void IdenticalRerun_LeavesTheTimestampUntouched()
        {
            EnableDump();
            var assetsDir = AssetsDir();
            Run(assetsDir, "DumpRerun", Component(9403, "RerunComponent"));

            var path = Path.Combine(DumpDir("DumpRerun"), "Demo_RerunComponent.g.cs");
            var before = File.ReadAllText(path);

            // Age the file: comparing two same-tick writes would pass even without the skip.
            var aged = DateTime.UtcNow.AddDays(-1);
            File.SetLastWriteTimeUtc(path, aged);

            Run(assetsDir, "DumpRerun", Component(9403, "RerunComponent"));

            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(aged).Within(TimeSpan.FromSeconds(1)),
                "an unchanged file must not be rewritten");
            Assert.That(File.ReadAllText(path), Is.EqualTo(before));
        }

        // --- One type added ---------------------------------------------------------------------

        [Test]
        public void AddingAType_LeavesTheUnchangedTypesTimestampAlone()
        {
            EnableDump();
            var assetsDir = AssetsDir();
            Run(assetsDir, "DumpAdd", Component(9404, "KeptComponent"));

            var kept = Path.Combine(DumpDir("DumpAdd"), "Demo_KeptComponent.g.cs");
            var aged = DateTime.UtcNow.AddDays(-1);
            File.SetLastWriteTimeUtc(kept, aged);

            Run(assetsDir, "DumpAdd",
                Component(9404, "KeptComponent"),
                Component(9405, "AddedComponent"));

            Assert.That(File.Exists(Path.Combine(DumpDir("DumpAdd"), "Demo_AddedComponent.g.cs")), Is.True,
                "the new type must be dumped");
            Assert.That(File.GetLastWriteTimeUtc(kept), Is.EqualTo(aged).Within(TimeSpan.FromSeconds(1)),
                "a type that did not change must not be rewritten");
        }

        // --- Content actually changed -----------------------------------------------------------

        [Test]
        public void ChangedContent_IsRewritten()
        {
            EnableDump();
            var assetsDir = AssetsDir();
            Run(assetsDir, "DumpChange", Component(9406, "ChangingComponent"));

            var path = Path.Combine(DumpDir("DumpChange"), "Demo_ChangingComponent.g.cs");
            var aged = DateTime.UtcNow.AddDays(-1);
            File.SetLastWriteTimeUtc(path, aged);
            var before = File.ReadAllText(path);

            Run(assetsDir, "DumpChange",
                Component(9406, "ChangingComponent", "public int Value; public int Added;"));

            Assert.That(File.ReadAllText(path), Is.Not.EqualTo(before), "the emit changed, so the file must");
            Assert.That(File.GetLastWriteTimeUtc(path), Is.GreaterThan(aged));
        }

        // --- Registration order is stable across tree order -------------------------------------

        private static string RunCommands(string sourceDir, string assemblyName, params string[] names)
        {
            var decls = string.Join("\n", names.Select(n =>
                $@"    [xpTURN.Klotho.Serialization.KlothoSerializable({Array.IndexOf(names, n) + 7100})]
    public partial class {n} : xpTURN.Klotho.Core.CommandBase {{ public int Value; }}"));

            string source = $@"
namespace Demo
{{
{decls}
}}";

            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Where(p => p.Length > 0)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            var trees = new[]
            {
                CSharpSyntaxTree.ParseText(CommandStub, path: Path.Combine(sourceDir, "KlothoCommandStub.cs")),
                CSharpSyntaxTree.ParseText(source, path: Path.Combine(sourceDir, "Commands.cs")),
            };

            var compilation = CSharpCompilation.Create(
                assemblyName, trees, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var driver = CSharpGeneratorDriver.Create(new KlothoSerializationGenerator());
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

            return driver.GetRunResult().GeneratedTrees
                .Select(t => t.GetText().ToString())
                .Single(t => t.Contains("CommandRegistrar"));
        }

        [Test]
        public void AggregateEmit_IsIdenticalWhenDeclarationOrderIsReversed()
        {
            EnableDump();
            var assetsDir = AssetsDir();

            var forward = RunCommands(assetsDir, "DumpOrder", "AlphaCommand", "BetaCommand");
            var reversed = RunCommands(assetsDir, "DumpOrder", "BetaCommand", "AlphaCommand");

            Assert.That(reversed, Is.EqualTo(forward),
                "declaration order must not reach the emitted registration order");
            Assert.That(forward.IndexOf("AlphaCommand", StringComparison.Ordinal),
                Is.LessThan(forward.IndexOf("BetaCommand", StringComparison.Ordinal)),
                "registration is sorted ordinal by full type name");

            var dumped = Path.Combine(DumpDir("DumpOrder"), "KlothoFactoryRegistration.g.cs");
            Assert.That(File.Exists(dumped), Is.True, "the aggregate file is dumped too");
            Assert.That(File.ReadAllText(dumped), Is.EqualTo(forward));
        }

        // --- PackageCache and unrooted paths ----------------------------------------------------

        [Test]
        public void PackageCachePath_IsNotAProjectRoot()
        {
            EnableDump();
            var packageCache = Path.Combine(_tempDir, "Library", "PackageCache", "com.xpturn.klotho@abc123", "Runtime");
            Directory.CreateDirectory(packageCache);

            Run(packageCache, "DumpPackageCache", Component(9409, "PackageCacheComponent"));

            Assert.That(Directory.Exists(DumpDir("DumpPackageCache")), Is.False,
                "PackageCache is not /Packages/ — a consumer's package cache must never be written into");
        }

        [Test]
        public void PathWithoutMarker_IsNotAProjectRoot()
        {
            EnableDump();
            var plain = Path.Combine(_tempDir, "src");
            Directory.CreateDirectory(plain);

            Run(plain, "DumpNoMarker", Component(9410, "NoMarkerComponent"));

            Assert.That(Directory.Exists(DumpDir("DumpNoMarker")), Is.False);
        }

        [Test]
        public void PackagesSegment_IsAProjectRoot()
        {
            EnableDump();
            var embedded = Path.Combine(_tempDir, "Packages", "com.example.game", "Runtime");
            Directory.CreateDirectory(embedded);

            Run(embedded, "DumpEmbedded", Component(9411, "EmbeddedComponent"));

            Assert.That(File.Exists(Path.Combine(DumpDir("DumpEmbedded"), "Demo_EmbeddedComponent.g.cs")), Is.True,
                "an embedded package resolves to the project root above /Packages/");
        }

        // --- A write that cannot happen must not escape ------------------------------------------

        [Test]
        public void ReadOnlyTarget_DoesNotThrow_AndOtherFilesStillEmit()
        {
            EnableDump();
            var assetsDir = AssetsDir();
            Run(assetsDir, "DumpReadOnly",
                Component(9412, "LockedComponent"),
                Component(9413, "OtherComponent"));

            var locked = Path.Combine(DumpDir("DumpReadOnly"), "Demo_LockedComponent.g.cs");
            var other = Path.Combine(DumpDir("DumpReadOnly"), "Demo_OtherComponent.g.cs");
            File.SetAttributes(locked, FileAttributes.ReadOnly);

            try
            {
                var agedOther = DateTime.UtcNow.AddDays(-1);
                File.SetLastWriteTimeUtc(other, agedOther);

                Assert.DoesNotThrow(() => Run(assetsDir, "DumpReadOnly",
                    Component(9412, "LockedComponent", "public int Value; public int Added;"),
                    Component(9413, "OtherComponent")));

                Assert.That(File.GetLastWriteTimeUtc(other), Is.EqualTo(agedOther).Within(TimeSpan.FromSeconds(1)),
                    "the unchanged sibling is still skipped, not collateral damage");
            }
            finally
            {
                File.SetAttributes(locked, FileAttributes.Normal);
            }
        }
    }
}
