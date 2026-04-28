using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using xpTURN.Klotho.Generator.Analyzers;
using xpTURN.Klotho.Generator.Emitters;
using xpTURN.Klotho.Generator.Model;

namespace xpTURN.Klotho.Generator
{
    [Generator]
    public class KlothoSerializationGenerator : IIncrementalGenerator
    {
        private const string KlothoSerializableAttribute =
            "xpTURN.Klotho.Serialization.KlothoSerializableAttribute";

        private const string KlothoComponentAttribute =
            "xpTURN.Klotho.ECS.KlothoComponentAttribute";

        private const string KlothoDataAssetAttribute =
            "xpTURN.Klotho.ECS.KlothoDataAssetAttribute";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // === [KlothoComponent] pipeline ===
            InitializeComponentPipeline(context);

            // === [KlothoDataAsset] pipeline ===
            InitializeDataAssetPipeline(context);


            // 1. Collect [KlothoSerializable] partial classes
            var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
                KlothoSerializableAttribute,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) =>
                {
                    var classDecl = (ClassDeclarationSyntax)ctx.TargetNode;

                    // Diagnostic: check partial keyword
                    bool isPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword);
                    if (!isPartial)
                    {
                        return new GeneratorResult
                        {
                            TypeInfo = null,
                            Diagnostics = new List<Diagnostic>
                            {
                                Diagnostic.Create(
                                    DiagnosticDescriptors.MissingPartial,
                                    classDecl.Identifier.GetLocation(),
                                    classDecl.Identifier.Text)
                            }
                        };
                    }

                    var analyzeResult = TypeAnalyzer.Analyze(ctx, ct);

                    // Could not determine category (no base class match)
                    if (analyzeResult.TypeInfo == null && analyzeResult.Diagnostics.Count == 0)
                    {
                        var symbol = ctx.TargetSymbol;
                        analyzeResult.Diagnostics.Add(Diagnostic.Create(
                            DiagnosticDescriptors.InvalidBaseClass,
                            classDecl.Identifier.GetLocation(),
                            symbol.Name));
                    }

                    return new GeneratorResult
                    {
                        TypeInfo = analyzeResult.TypeInfo,
                        Diagnostics = analyzeResult.Diagnostics
                    };
                });

            // 2. Report diagnostics
            var diagnosticTargets = targets.Where(static r => r.Diagnostics.Count > 0);

            context.RegisterSourceOutput(diagnosticTargets, static (spc, result) =>
            {
                foreach (var diag in result.Diagnostics)
                    spc.ReportDiagnostic(diag);
            });

            // 3. Generate per-type serialization code
            var validTargets = targets.Where(static r => r.TypeInfo != null);

            // Combine with compilation to resolve project root from syntax tree paths
            var validWithCompilation = validTargets.Combine(context.CompilationProvider);

            context.RegisterSourceOutput(validWithCompilation, static (spc, pair) =>
            {
                var (result, compilation) = pair;
                var info = result.TypeInfo;
                string code = info.Category switch
                {
                    TypeCategory.Entity => EntityEmitter.Emit(info),
                    TypeCategory.Command => CommandEmitter.Emit(info),
                    TypeCategory.Message => MessageEmitter.Emit(info),
                    TypeCategory.Event => EventEmitter.Emit(info),
                    _ => null
                };

                if (code != null)
                {
                    // Use safe file name
                    var fileName = info.FullTypeName.Replace('.', '_').Replace('<', '_').Replace('>', '_');
                    spc.AddSource($"{fileName}.g.cs", code);

                    EmitToFile(compilation, fileName + ".g.cs", code);
                }
            });

            // 4. Generate factory registration code (collect all types)
            // Combine with compilation to check if Factory classes are defined in current assembly
            var allTypes = validTargets.Select(static (r, _) => r.TypeInfo).Collect();
            var typesWithCompilation = allTypes.Combine(context.CompilationProvider);
            context.RegisterSourceOutput(typesWithCompilation, static (spc, pair) =>
            {
                var (types, compilation) = pair;

                // KLSG007: Duplicate TYPE_ID check per category
                ReportDuplicateTypeIds(spc, types, compilation);

                var hasEntityFactory = HasSourceDefinedType(compilation, "xpTURN.Klotho.State.EntityFactory");
                var hasCommandFactory = HasSourceDefinedType(compilation, "xpTURN.Klotho.Core.CommandFactory");
                var hasMessageSerializer = HasSourceDefinedType(compilation, "xpTURN.Klotho.Network.MessageSerializer");

                var factoryCode = FactoryEmitter.Emit(types, hasEntityFactory, hasCommandFactory, hasMessageSerializer);
                if (factoryCode != null)
                {
                    spc.AddSource("KlothoFactoryRegistration.g.cs", factoryCode);

                    EmitToFile(compilation, "KlothoFactoryRegistration.g.cs", factoryCode);
                }

                var warmupCode = WarmupEmitter.Emit(types);
                if (warmupCode != null)
                {
                    spc.AddSource("KlothoWarmupRegistration.g.cs", warmupCode);
                    EmitToFile(compilation, "KlothoWarmupRegistration.g.cs", warmupCode);
                }
            });
        }

        private static void ReportDuplicateTypeIds(
            SourceProductionContext spc,
            ImmutableArray<SerializableTypeInfo> types,
            Compilation compilation)
        {
            // Group by (Category, TypeId) and report duplicates
            var withId = types.Where(t => t.TypeId.HasValue);
            var groups = withId.GroupBy(t => (t.Category, t.TypeId.Value));

            foreach (var group in groups)
            {
                var list = group.ToList();
                if (list.Count <= 1) continue;

                var categoryName = group.Key.Category.ToString();
                var first = list[0];

                for (int i = 1; i < list.Count; i++)
                {
                    var dup = list[i];
                    var location = GetTypeLocation(compilation, dup.FullTypeName);
                    spc.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.DuplicateTypeId,
                        location,
                        group.Key.Value,
                        first.FullTypeName,
                        dup.FullTypeName,
                        categoryName));
                }
            }
        }

        private static Location GetTypeLocation(Compilation compilation, string fullTypeName)
        {
            var symbol = compilation.GetTypeByMetadataName(fullTypeName);
            if (symbol != null)
            {
                foreach (var loc in symbol.Locations)
                {
                    if (loc.IsInSource) return loc;
                }
            }
            return Location.None;
        }

        private static void EmitToFile(Compilation compilation, string fileName, string code)
        {
            try
            {
                var projectRoot = ResolveProjectRoot(compilation);
                if (projectRoot == null) return;

                var dir = Path.Combine(projectRoot, "Tools");
                dir = Path.Combine(dir, "Generated", compilation.AssemblyName ?? "Unknown");
#pragma warning disable RS1035 // File IO is intentional for debugging generated code
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, fileName), code);
#pragma warning restore RS1035
            }
            catch
            {
                // Silently ignore — file output is best-effort for debugging
            }
        }

        private static string ResolveProjectRoot(Compilation compilation)
        {
            // Find project root by looking for "Assets/" in syntax tree file paths
            foreach (var tree in compilation.SyntaxTrees)
            {
                var filePath = tree.FilePath;
                if (string.IsNullOrEmpty(filePath)) continue;

                var idx = filePath.IndexOf(Path.DirectorySeparatorChar + "Assets" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (idx < 0)
                    idx = filePath.IndexOf("/Assets/", StringComparison.Ordinal);
                if (idx >= 0)
                    return filePath.Substring(0, idx);
            }
            return null;
        }

        /// <summary>
        /// Check if a type is defined in source (not from a referenced assembly)
        /// </summary>
        private static bool HasSourceDefinedType(Compilation compilation, string fullyQualifiedName)
        {
            var symbol = compilation.GetTypeByMetadataName(fullyQualifiedName);
            if (symbol == null) return false;
            return SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly);
        }

        private void InitializeComponentPipeline(IncrementalGeneratorInitializationContext context)
        {
            // 1. Collect [KlothoComponent] structs
            var componentTargets = context.SyntaxProvider.ForAttributeWithMetadataName(
                KlothoComponentAttribute,
                predicate: static (node, _) => node is StructDeclarationSyntax,
                transform: static (ctx, ct) => ComponentAnalyzer.Analyze(ctx, ct));

            // 2. Report diagnostics
            var componentDiagnostics = componentTargets.Where(static r => r.Diagnostics.Count > 0);
            context.RegisterSourceOutput(componentDiagnostics, static (spc, result) =>
            {
                foreach (var diag in result.Diagnostics)
                    spc.ReportDiagnostic(diag);
            });

            // 3. Per-component code generation (Serialize/Deserialize/GetSerializedSize/GetHash)
            var validComponents = componentTargets.Where(static r => r.TypeInfo != null);
            var validComponentsWithCompilation = validComponents.Combine(context.CompilationProvider);

            context.RegisterSourceOutput(validComponentsWithCompilation, static (spc, pair) =>
            {
                var (result, compilation) = pair;
                var info = result.TypeInfo;
                var code = ComponentEmitter.Emit(info);

                var fileName = info.FullTypeName.Replace('.', '_').Replace('<', '_').Replace('>', '_');
                spc.AddSource($"{fileName}.g.cs", code);

                EmitToFile(compilation, fileName + ".g.cs", code);
            });

            // 4. Aggregate: duplicate ID check only (Frame registration is now per-component via ModuleInitializer)
            var allComponents = validComponents.Select(static (r, _) => r.TypeInfo).Collect();
            var componentsWithCompilation = allComponents.Combine(context.CompilationProvider);

            context.RegisterSourceOutput(componentsWithCompilation, static (spc, pair) =>
            {
                var (components, compilation) = pair;

                // KLSG_ECS001: Duplicate ComponentTypeId check
                ReportDuplicateComponentTypeIds(spc, components, compilation);
            });
        }

        private static void ReportDuplicateComponentTypeIds(
            SourceProductionContext spc,
            ImmutableArray<ComponentTypeInfo> components,
            Compilation compilation)
        {
            var groups = components.GroupBy(c => c.ComponentTypeId);
            foreach (var group in groups)
            {
                var list = group.ToList();
                if (list.Count <= 1) continue;

                var first = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var dup = list[i];
                    var location = GetTypeLocation(compilation, dup.FullTypeName);
                    spc.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.DuplicateComponentTypeId,
                        location,
                        group.Key,
                        first.FullTypeName,
                        dup.FullTypeName));
                }
            }
        }

        // === [KlothoDataAsset] pipeline ===

        private void InitializeDataAssetPipeline(IncrementalGeneratorInitializationContext context)
        {
            // 1. Collect [KlothoDataAsset] classes
            var dataAssetTargets = context.SyntaxProvider.ForAttributeWithMetadataName(
                KlothoDataAssetAttribute,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => DataAssetAnalyzer.Analyze(ctx, ct));

            // 2. Report diagnostics
            var daDiagnostics = dataAssetTargets.Where(static r => r.Diagnostics.Count > 0);
            context.RegisterSourceOutput(daDiagnostics, static (spc, result) =>
            {
                foreach (var diag in result.Diagnostics)
                    spc.ReportDiagnostic(diag);
            });

            // 3. Per-type code generation (Serialize/Deserialize/GetSerializedSize + Registrar)
            var validDataAssets = dataAssetTargets.Where(static r => r.TypeInfo != null);
            var daWithCompilation = validDataAssets.Combine(context.CompilationProvider);

            context.RegisterSourceOutput(daWithCompilation, static (spc, pair) =>
            {
                var (result, compilation) = pair;
                var info = result.TypeInfo;
                var code = DataAssetEmitter.Emit(info);

                var fileName = info.FullTypeName.Replace('.', '_').Replace('<', '_').Replace('>', '_');
                spc.AddSource($"{fileName}.g.cs", code);

                EmitToFile(compilation, fileName + ".g.cs", code);
            });

            // 4. Aggregate: duplicate TypeId check
            var allDataAssets = validDataAssets.Select(static (r, _) => r.TypeInfo).Collect();
            var daAllWithCompilation = allDataAssets.Combine(context.CompilationProvider);

            context.RegisterSourceOutput(daAllWithCompilation, static (spc, pair) =>
            {
                var (dataAssets, compilation) = pair;

                // KLSG_DA005: Duplicate DataAsset TypeId check
                var groups = dataAssets.GroupBy(d => d.TypeId);
                foreach (var group in groups)
                {
                    var list = group.ToList();
                    if (list.Count <= 1) continue;

                    var first = list[0];
                    for (int i = 1; i < list.Count; i++)
                    {
                        var dup = list[i];
                        var location = GetTypeLocation(compilation, dup.FullTypeName);
                        spc.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.DuplicateDataAssetTypeId,
                            location,
                            group.Key,
                            first.FullTypeName,
                            dup.FullTypeName));
                    }
                }
            });
        }

        private sealed class GeneratorResult
        {
            public SerializableTypeInfo TypeInfo { get; set; }
            public List<Diagnostic> Diagnostics { get; set; } = new List<Diagnostic>();
        }
    }
}
