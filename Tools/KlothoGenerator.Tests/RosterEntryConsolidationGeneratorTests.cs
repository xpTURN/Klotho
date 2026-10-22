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
    /// Tier 1 (generator-driver) coverage for the consolidated roster struct
    /// (RosterEntry: int + byte + byte + FixedString64 + FixedString64) carried as List&lt;RosterEntry&gt;
    /// on a message. Asserts generated source TEXT + diagnostics only; runtime round-trip / per-path
    /// IsReady / truncation is covered separately at Tier 2 (Unity EditMode).
    /// </summary>
    [TestFixture]
    public class RosterEntryConsolidationGeneratorTests
    {
        // Trigger attrs + CommandBase + minimal FixedString64 stand-in (resolved by display name for
        // the TypeMappings lookup; fixed buffer / unmanaged-ness irrelevant to text assertions).
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
    public struct FixedString64 { public short Length; }
}
";

        // Mirrors the real RosterEntry layout (Runtime/Network/Messages/RosterEntry.cs).
        private const string RosterEntryStruct = @"
    [xpTURN.Klotho.Serialization.KlothoSerializableStruct]
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    public partial struct RosterEntry
    {
        public int PlayerId;
        public byte ConnectionState;
        public byte ReadyState;
        public xpTURN.Klotho.ECS.FixedString64 Account;
        public xpTURN.Klotho.ECS.FixedString64 DisplayName;
    }";

        private sealed class GenOutput
        {
            public List<Diagnostic> Diagnostics;
            public string HostSource;     // generated codec for the host message
            public string StructSource;   // generated codec for RosterEntry
        }

        private static GenOutput Run(string fields)
        {
            string source = $@"
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
namespace Demo
{{
{RosterEntryStruct}

    [KlothoSerializable(42)]
    public partial class MyCommand : CommandBase
    {{
{fields}
    }}
}}";

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

            var trees = result.GeneratedTrees.Select(t => t.GetText().ToString()).ToList();

            return new GenOutput
            {
                Diagnostics = result.Diagnostics.ToList(),
                HostSource = trees.FirstOrDefault(t => t.Contains("class MyCommand")) ?? string.Empty,
                StructSource = trees.FirstOrDefault(t => t.Contains("struct RosterEntry")) ?? string.Empty,
            };
        }

        [Test]
        public void ListOfRosterEntry_NoKLSGDiagnostics()
        {
            var o = Run("        [KlothoOrder] public List<RosterEntry> Roster = new List<RosterEntry>();");
            var ids = o.Diagnostics.Select(d => d.Id).ToList();
            Assert.That(ids, Does.Not.Contain("KLSG003"));
            Assert.That(ids, Does.Not.Contain("KLSG005"));
            Assert.That(ids, Does.Not.Contain("KLSG006"));
        }

        [Test]
        public void RosterEntry_GetSerializedSize_Is134Literal()
        {
            var o = Run("        [KlothoOrder] public List<RosterEntry> Roster = new List<RosterEntry>();");
            // int(4) + byte(1) + byte(1) + FixedString64(64) + FixedString64(64) = 134
            Assert.That(o.StructSource, Does.Contain("public int GetSerializedSize() => 134;"));
        }

        [Test]
        public void RosterEntry_DelegatesFixedString64Codec()
        {
            var o = Run("        [KlothoOrder] public List<RosterEntry> Roster = new List<RosterEntry>();");
            Assert.That(o.StructSource, Does.Contain("writer.WriteFixedString64(this.Account);"));
            Assert.That(o.StructSource, Does.Contain("writer.WriteFixedString64(this.DisplayName);"));
            Assert.That(o.StructSource, Does.Contain("this.Account = reader.ReadFixedString64();"));
            Assert.That(o.StructSource, Does.Contain("this.DisplayName = reader.ReadFixedString64();"));
        }

        [Test]
        public void ListOfRosterEntry_NestedDelegationAndSize()
        {
            var o = Run("        [KlothoOrder] public List<RosterEntry> Roster = new List<RosterEntry>();");
            Assert.That(o.HostSource, Does.Contain("this.Roster[__i].Serialize(ref writer);"));
            Assert.That(o.HostSource, Does.Contain("var __e_Roster = default(Demo.RosterEntry);"));
            Assert.That(o.HostSource, Does.Contain("__e_Roster.Deserialize(ref reader);"));
            Assert.That(o.HostSource, Does.Contain("this.Roster.Add(__e_Roster);"));
            Assert.That(o.HostSource, Does.Contain("(this.Roster?.Count ?? 0) * default(Demo.RosterEntry).GetSerializedSize()"));
        }
    }
}
