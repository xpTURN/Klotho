using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using xpTURN.Klotho.Generator.Model;
using xpTURN.Klotho.Generator.Utils;

namespace xpTURN.Klotho.Generator.Analyzers
{
    internal sealed class DataAssetAnalyzeResult
    {
        public DataAssetTypeInfo TypeInfo { get; set; }
        public List<Diagnostic> Diagnostics { get; set; } = new List<Diagnostic>();
    }

    internal static class DataAssetAnalyzer
    {
        private const string IDataAssetInterface = "xpTURN.Klotho.ECS.IDataAsset";
        private const string KlothoOrderAttributeName = "xpTURN.Klotho.Serialization.KlothoOrderAttribute";
        private const string KlothoIgnoreAttributeName = "xpTURN.Klotho.Serialization.KlothoIgnoreAttribute";

        public static DataAssetAnalyzeResult Analyze(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            var result = new DataAssetAnalyzeResult();

            var symbol = ctx.TargetSymbol as INamedTypeSymbol;
            if (symbol == null) return result;

            var classDecl = ctx.TargetNode as ClassDeclarationSyntax;
            if (classDecl == null) return result;

            var location = classDecl.Identifier.GetLocation();

            // 1. Validate partial
            if (!classDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.DataAssetMissingPartial,
                    location, symbol.Name));
                return result;
            }

            // 2. Validate IDataAsset implementation
            bool implementsIDataAsset = symbol.AllInterfaces.Any(
                i => i.ToDisplayString() == IDataAssetInterface);
            if (!implementsIDataAsset)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.DataAssetMissingIDataAsset,
                    location, symbol.Name));
                return result;
            }

            // 3. Validate constructor — a constructor that takes a single int parameter is required
            string ctorParamName = null;
            int publicCtorCount = 0;
            foreach (var ctor in symbol.Constructors)
            {
                if (ctor.IsStatic) continue;
                if (ctor.DeclaredAccessibility == Accessibility.Public)
                    publicCtorCount++;

                if (ctorParamName == null
                    && ctor.Parameters.Length == 1
                    && ctor.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
                {
                    ctorParamName = ctor.Parameters[0].Name;
                }
            }

            if (ctorParamName == null)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.DataAssetMissingConstructor,
                    location, symbol.Name));
                return result;
            }

            // DA004: Warn about multiple public constructors
            if (publicCtorCount > 1)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.DataAssetAmbiguousConstructor,
                    location, symbol.Name));
            }

            // 4. Extract TypeId from attribute
            var attrData = ctx.Attributes.FirstOrDefault();
            if (attrData == null || attrData.ConstructorArguments.Length == 0)
                return result;

            int typeId = (int)attrData.ConstructorArguments[0].Value;

            var info = new DataAssetTypeInfo
            {
                Namespace = symbol.ContainingNamespace.IsGlobalNamespace
                    ? null
                    : symbol.ContainingNamespace.ToDisplayString(),
                TypeName = symbol.Name,
                FullTypeName = symbol.ToDisplayString(),
                TypeId = typeId,
                ConstructorParamName = ctorParamName,
            };

            // 5. Collect [KlothoOrder] fields
            CollectFields(symbol, info, result.Diagnostics, ct);

            if (result.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error))
                result.TypeInfo = info;

            return result;
        }

        private static void CollectFields(
            INamedTypeSymbol symbol,
            DataAssetTypeInfo info,
            List<Diagnostic> diagnostics,
            CancellationToken ct)
        {
            var members = symbol.GetMembers();
            int declarationIndex = 0;

            foreach (var member in members)
            {
                ct.ThrowIfCancellationRequested();

                if (member is not IFieldSymbol && member is not IPropertySymbol)
                    continue;

                // Skip [KlothoIgnore]
                if (HasAttribute(member, KlothoIgnoreAttributeName))
                    continue;

                // Only include if [KlothoOrder] is present (opt-in)
                var orderAttr = GetAttribute(member, KlothoOrderAttributeName);
                if (orderAttr == null)
                    continue;

                int order = -1;
                if (orderAttr.ConstructorArguments.Length > 0)
                    order = (int)orderAttr.ConstructorArguments[0].Value;

                if (order == -1)
                {
                    order = declarationIndex;
                    declarationIndex++;
                }

                var fieldType = member is IFieldSymbol fs ? fs.Type : ((IPropertySymbol)member).Type;
                var fieldInfo = new SerializableFieldInfo
                {
                    Name = member.Name,
                    TypeFullName = fieldType.ToDisplayString(),
                    Order = order,
                    IsProperty = member is IPropertySymbol,
                };

                ClassifyFieldType(fieldInfo, fieldType);
                info.Fields.Add(fieldInfo);
            }

            info.Fields.Sort((a, b) => a.Order.CompareTo(b.Order));

            // Validate duplicate Order
            var orderGroups = info.Fields.GroupBy(f => f.Order).Where(g => g.Count() > 1);
            foreach (var group in orderGroups)
            {
                var loc = GetMemberLocation(symbol, group.First().Name);
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.DuplicateOrder,
                    loc, group.Key, symbol.Name));
            }

            // Validate field types
            foreach (var field in info.Fields)
            {
                var loc = GetMemberLocation(symbol, field.Name);

                // Validate directly mapped types
                if (field.ElementTypeName == null && field.KeyTypeName == null)
                {
                    if (!TypeMappings.TryGetMapping(field.TypeFullName, out _))
                    {
                        field.IsUnsupported = true;
                        diagnostics.Add(Diagnostic.Create(
                            DiagnosticDescriptors.UnsupportedFieldType,
                            loc, field.Name, field.TypeFullName));
                    }
                }

                // Validate collection element types
                if (field.ElementTypeName != null && field.KeyTypeName == null)
                {
                    if (!TypeMappings.TryGetMapping(field.ElementTypeName, out _))
                    {
                        field.IsUnsupported = true;
                        diagnostics.Add(Diagnostic.Create(
                            DiagnosticDescriptors.UnsupportedCollectionElement,
                            loc, field.Name, field.ElementTypeName));
                    }
                }

                // Validate Dictionary key/value types
                if (field.KeyTypeName != null && field.ValueTypeName != null)
                {
                    bool keyOk = TypeMappings.TryGetMapping(field.KeyTypeName, out _);
                    bool valOk = TypeMappings.TryGetMapping(field.ValueTypeName, out _);
                    if (!keyOk || !valOk)
                    {
                        field.IsUnsupported = true;
                        diagnostics.Add(Diagnostic.Create(
                            DiagnosticDescriptors.UnsupportedDictionaryType,
                            loc, field.Name, field.KeyTypeName, field.ValueTypeName));
                    }
                }
            }
        }

        private static void ClassifyFieldType(SerializableFieldInfo field, ITypeSymbol typeSymbol)
        {
            var fullName = typeSymbol.ToDisplayString();

            if (TypeMappings.TryGetMapping(fullName, out var mapping))
            {
                field.SizeKind = mapping.Size > 0 ? FieldSizeKind.Fixed : FieldSizeKind.Variable;
                return;
            }

            if (typeSymbol is IArrayTypeSymbol arrayType)
            {
                var elemFullName = arrayType.ElementType.ToDisplayString();
                if (elemFullName == "System.Byte" || elemFullName == "byte")
                {
                    field.TypeFullName = "System.Byte[]";
                    field.SizeKind = FieldSizeKind.Variable;
                    return;
                }
                field.ElementTypeName = arrayType.ElementType.ToDisplayString();
                field.SizeKind = FieldSizeKind.Variable;
                return;
            }

            if (typeSymbol is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                var originalDef = namedType.OriginalDefinition.ToDisplayString();

                if (originalDef == "System.Collections.Generic.List<T>")
                {
                    field.ElementTypeName = namedType.TypeArguments[0].ToDisplayString();
                    field.SizeKind = FieldSizeKind.Variable;
                    return;
                }

                if (originalDef == "System.Collections.Generic.Dictionary<TKey, TValue>")
                {
                    field.KeyTypeName = namedType.TypeArguments[0].ToDisplayString();
                    field.ValueTypeName = namedType.TypeArguments[1].ToDisplayString();
                    field.SizeKind = FieldSizeKind.Variable;
                    return;
                }
            }

            field.SizeKind = FieldSizeKind.Fixed;
        }

        private static Location GetMemberLocation(INamedTypeSymbol symbol, string memberName)
        {
            var member = symbol.GetMembers(memberName).FirstOrDefault();
            if (member != null)
            {
                var syntaxRef = member.DeclaringSyntaxReferences.FirstOrDefault();
                if (syntaxRef != null)
                    return syntaxRef.GetSyntax().GetLocation();
            }
            var typeRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
            return typeRef?.GetSyntax().GetLocation() ?? Location.None;
        }

        private static bool HasAttribute(ISymbol symbol, string attributeFullName)
        {
            return symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attributeFullName);
        }

        private static AttributeData GetAttribute(ISymbol symbol, string attributeFullName)
        {
            return symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attributeFullName);
        }
    }
}
