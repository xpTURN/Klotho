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
    internal sealed class ComponentAnalyzeResult
    {
        public ComponentTypeInfo TypeInfo { get; set; }
        public List<Diagnostic> Diagnostics { get; set; } = new List<Diagnostic>();
    }

    internal static class ComponentAnalyzer
    {
        private const string IComponentInterface = "xpTURN.Klotho.ECS.IComponent";
        private const string StructLayoutAttribute = "System.Runtime.InteropServices.StructLayoutAttribute";
        private const string IntPtrType = "System.IntPtr";
        private const string UIntPtrType = "System.UIntPtr";
        private const string BoolType = "System.Boolean";
        private const string CharType = "System.Char";

        public static ComponentAnalyzeResult Analyze(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            var result = new ComponentAnalyzeResult();

            var symbol = ctx.TargetSymbol as INamedTypeSymbol;
            if (symbol == null) return result;

            var structDecl = ctx.TargetNode as StructDeclarationSyntax;
            if (structDecl == null) return result;

            var location = structDecl.Identifier.GetLocation();

            // Check partial keyword
            if (!structDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.ComponentMissingPartial,
                    location,
                    symbol.Name));
                return result;
            }

            // Check IComponent implementation
            bool implementsIComponent = symbol.AllInterfaces.Any(
                i => i.ToDisplayString() == IComponentInterface);
            if (!implementsIComponent)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.ComponentMissingIComponent,
                    location,
                    symbol.Name));
                return result;
            }

            // Check unmanaged constraint
            if (!symbol.IsUnmanagedType)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.ComponentNotUnmanaged,
                    location,
                    symbol.Name));
                return result;
            }

            // IMP-25 §15 D1 — StructLayout(Sequential, Pack=4) enforcement
            CheckStructLayout(symbol, location, result);

            // Extract ComponentTypeId from attribute
            var attrData = ctx.Attributes.FirstOrDefault();
            if (attrData == null || attrData.ConstructorArguments.Length == 0)
                return result;

            int componentTypeId = (int)attrData.ConstructorArguments[0].Value;

            var info = new ComponentTypeInfo
            {
                Namespace = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
                TypeName = symbol.Name,
                FullTypeName = symbol.ToDisplayString(),
                ComponentTypeId = componentTypeId,
            };

            // Collect fields + IMP-25 §15 D1 field-level cross-runtime checks
            foreach (var member in symbol.GetMembers())
            {
                ct.ThrowIfCancellationRequested();

                if (member is not IFieldSymbol fs) continue;
                if (fs.IsStatic || fs.IsConst) continue;

                if (fs.IsFixedSizeBuffer)
                {
                    var fixedField = fs as IFieldSymbol;
                    var elementType = ((IPointerTypeSymbol)fixedField.Type).PointedAtType.ToDisplayString();
                    info.Fields.Add(new ComponentFieldInfo
                    {
                        Name = fs.Name,
                        TypeFullName = fs.Type.ToDisplayString(),
                        IsFixed = true,
                        FixedSize = fixedField.FixedSize,
                        ElementType = elementType,
                    });
                    continue;
                }

                // IMP-25 §15 D1 field-level guards (non-fixed fields only)
                CheckFieldType(symbol, fs, result);

                info.Fields.Add(new ComponentFieldInfo
                {
                    Name = fs.Name,
                    TypeFullName = fs.Type.ToDisplayString(),
                });
            }

            // IMP-25 §15 D1 — empty tag struct requires Size = 1
            if (info.Fields.Count == 0)
            {
                CheckEmptyStructSize(symbol, location, result);
            }

            // Check oversized (> 128 bytes) — warning only
            int estimatedSize = 0;
            foreach (var field in info.Fields)
            {
                if (field.IsFixed)
                {
                    if (TypeMappings.TryGetMapping(field.ElementType, out var elemMapping) && elemMapping.Size > 0)
                        estimatedSize += elemMapping.Size * field.FixedSize;
                }
                else if (TypeMappings.TryGetMapping(field.TypeFullName, out var mapping) && mapping.Size > 0)
                {
                    estimatedSize += mapping.Size;
                }
            }
            if (estimatedSize > 128)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.ComponentOversized,
                    location,
                    symbol.Name));
            }

            // Check unsupported field types
            foreach (var field in info.Fields)
            {
                if (field.IsFixed)
                {
                    if (!TypeMappings.TryGetMapping(field.ElementType, out _))
                    {
                        result.Diagnostics.Add(Diagnostic.Create(
                            DiagnosticDescriptors.UnsupportedFieldType,
                            GetFieldLocation(symbol, field.Name) ?? location,
                            field.Name,
                            field.ElementType));
                        result.TypeInfo = null;
                        return result;
                    }
                    continue;
                }

                if (!TypeMappings.TryGetMapping(field.TypeFullName, out _))
                {
                    result.Diagnostics.Add(Diagnostic.Create(
                        DiagnosticDescriptors.UnsupportedFieldType,
                        GetFieldLocation(symbol, field.Name) ?? location,
                        field.Name,
                        field.TypeFullName));
                    result.TypeInfo = null;
                    return result;
                }
            }

            result.TypeInfo = info;
            return result;
        }

        private static Location GetFieldLocation(INamedTypeSymbol symbol, string fieldName)
        {
            var member = symbol.GetMembers(fieldName).FirstOrDefault();
            if (member != null)
            {
                var syntaxRef = member.DeclaringSyntaxReferences.FirstOrDefault();
                if (syntaxRef != null)
                    return syntaxRef.GetSyntax().GetLocation();
            }
            return null;
        }

        // IMP-25 §15 D1 / Phase 25-G — helper rules

        private static AttributeData FindStructLayoutAttribute(INamedTypeSymbol symbol)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() == StructLayoutAttribute)
                    return attr;
            }
            return null;
        }

        private static void CheckStructLayout(INamedTypeSymbol symbol, Location location, ComponentAnalyzeResult result)
        {
            var attr = FindStructLayoutAttribute(symbol);
            if (attr == null)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.KlothoStructLayoutMissing,
                    location,
                    symbol.Name));
                return;
            }

            // Verify LayoutKind.Sequential
            if (attr.ConstructorArguments.Length > 0)
            {
                var layoutKind = attr.ConstructorArguments[0].Value;
                // LayoutKind.Sequential == 0
                if (layoutKind is int lk && lk != 0)
                {
                    result.Diagnostics.Add(Diagnostic.Create(
                        DiagnosticDescriptors.KlothoStructLayoutMissing,
                        location,
                        symbol.Name));
                    return;
                }
            }

            // Verify Pack = 4
            bool hasPack4 = false;
            foreach (var na in attr.NamedArguments)
            {
                if (na.Key == "Pack" && na.Value.Value is int pack && pack == 4)
                {
                    hasPack4 = true;
                    break;
                }
            }
            if (!hasPack4)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.KlothoStructLayoutMissing,
                    location,
                    symbol.Name));
            }
        }

        private static void CheckEmptyStructSize(INamedTypeSymbol symbol, Location location, ComponentAnalyzeResult result)
        {
            var attr = FindStructLayoutAttribute(symbol);
            if (attr == null) return;   // Missing StructLayout itself is handled by KlothoStructLayoutMissing

            bool hasSize1 = false;
            foreach (var na in attr.NamedArguments)
            {
                if (na.Key == "Size" && na.Value.Value is int size && size >= 1)
                {
                    hasSize1 = true;
                    break;
                }
            }
            if (!hasSize1)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.KlothoEmptyStructSize,
                    location,
                    symbol.Name));
            }
        }

        private static void CheckFieldType(INamedTypeSymbol symbol, IFieldSymbol field, ComponentAnalyzeResult result)
        {
            var fieldLocation = GetFieldLocation(symbol, field.Name)
                ?? symbol.Locations.FirstOrDefault()
                ?? Location.None;

            var fieldTypeFullName = field.Type.ToDisplayString();

            // IntPtr / UIntPtr — forbidden (platform-variable size)
            if (fieldTypeFullName == IntPtrType || fieldTypeFullName == UIntPtrType)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.KlothoIntPtrForbidden,
                    fieldLocation,
                    symbol.Name,
                    field.Name));
                return;
            }

            // enum — underlying type check (warn if int default implicit)
            if (field.Type.TypeKind == TypeKind.Enum && field.Type is INamedTypeSymbol enumSymbol)
            {
                // Check if enum declaration explicitly specified underlying type via BaseList.
                // Roslyn: enumSymbol.EnumUnderlyingType is always non-null (defaults to Int32).
                // To detect "no explicit declaration", inspect syntax.
                bool explicitUnderlying = false;
                foreach (var syntaxRef in enumSymbol.DeclaringSyntaxReferences)
                {
                    if (syntaxRef.GetSyntax() is EnumDeclarationSyntax eds && eds.BaseList != null)
                    {
                        explicitUnderlying = true;
                        break;
                    }
                }
                if (!explicitUnderlying)
                {
                    result.Diagnostics.Add(Diagnostic.Create(
                        DiagnosticDescriptors.KlothoEnumUnderlying,
                        fieldLocation,
                        symbol.Name,
                        field.Name,
                        enumSymbol.Name));
                }
                return;
            }

            // bool — warn
            if (fieldTypeFullName == BoolType)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.KlothoBoolField,
                    fieldLocation,
                    symbol.Name,
                    field.Name));
                return;
            }

            // char — warn
            if (fieldTypeFullName == CharType)
            {
                result.Diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.KlothoCharField,
                    fieldLocation,
                    symbol.Name,
                    field.Name));
                return;
            }
        }
    }
}
