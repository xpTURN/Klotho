using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using xpTURN.Klotho.Generator.Model;
using xpTURN.Klotho.Generator.Utils;

namespace xpTURN.Klotho.Generator.Analyzers
{
    internal sealed class AnalyzeResult
    {
        public SerializableTypeInfo TypeInfo { get; set; }
        public List<Diagnostic> Diagnostics { get; set; } = new List<Diagnostic>();
    }

    internal static class TypeAnalyzer
    {
        private const string KlothoOrderAttributeName = "xpTURN.Klotho.Serialization.KlothoOrderAttribute";
        private const string KlothoIgnoreAttributeName = "xpTURN.Klotho.Serialization.KlothoIgnoreAttribute";
        private const string KlothoHashIgnoreAttributeName = "xpTURN.Klotho.Serialization.KlothoHashIgnoreAttribute";

        public static AnalyzeResult Analyze(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            var result = new AnalyzeResult();

            var symbol = ctx.TargetSymbol as INamedTypeSymbol;
            if (symbol == null) return result;

            var category = DetermineCategory(symbol);
            if (category == null) return result;

            var info = new SerializableTypeInfo
            {
                Namespace = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
                TypeName = symbol.Name,
                FullTypeName = symbol.ToDisplayString(),
                Category = category.Value,
            };

            // Extract TYPE_ID or MessageType
            ExtractTypeId(ctx, info);

            // Detect manual serialization overrides (skip code generation if present)
            info.HasManualSerialization = HasMethodDeclaredInType(symbol, "SerializeData")
                                      || HasMethodDeclaredInType(symbol, "GetSerializedSize");

            // Collect fields with [KlothoOrder]
            var members = symbol.GetMembers();
            int declarationIndex = 0;
            foreach (var member in members)
            {
                ct.ThrowIfCancellationRequested();

                if (member is not IFieldSymbol && member is not IPropertySymbol)
                    continue;

                // Skip if [KlothoIgnore]
                if (HasAttribute(member, KlothoIgnoreAttributeName))
                    continue;

                // Only include if [KlothoOrder] is present (opt-in)
                var orderAttr = GetAttribute(member, KlothoOrderAttributeName);
                if (orderAttr == null)
                    continue;

                int order = -1;
                if (orderAttr.ConstructorArguments.Length > 0)
                    order = (int)orderAttr.ConstructorArguments[0].Value;

                // -1 means use declaration order
                if (order == -1)
                {
                    order = declarationIndex;
                    declarationIndex++;
                }

                bool hashIgnore = HasAttribute(member, KlothoHashIgnoreAttributeName);

                var fieldType = member is IFieldSymbol fs ? fs.Type : ((IPropertySymbol)member).Type;
                var fieldInfo = new SerializableFieldInfo
                {
                    Name = member.Name,
                    TypeFullName = fieldType.ToDisplayString(),
                    Order = order,
                    IsProperty = member is IPropertySymbol,
                    IncludeInHash = !hashIgnore,
                };

                ClassifyFieldType(fieldInfo, fieldType);
                info.Fields.Add(fieldInfo);
            }

            info.Fields.Sort((a, b) => a.Order.CompareTo(b.Order));

            // --- Validation ---
            ValidateFields(info, symbol, result.Diagnostics);

            result.TypeInfo = result.Diagnostics.Count == 0 ? info : null;
            return result;
        }

        private static void ValidateFields(SerializableTypeInfo info, INamedTypeSymbol symbol, List<Diagnostic> diagnostics)
        {
            // KLSG002: Duplicate order values
            var orderGroups = info.Fields.GroupBy(f => f.Order).Where(g => g.Count() > 1);
            foreach (var group in orderGroups)
            {
                var location = GetMemberLocation(symbol, group.First().Name);
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.DuplicateOrder,
                    location,
                    group.Key,
                    symbol.Name));
            }

            // Event types do not require serialization — skip unsupported type validation (GetContentHash falls back to GetHashCode)
            if (info.Category == TypeCategory.Event)
                return;

            foreach (var field in info.Fields)
            {
                var location = GetMemberLocation(symbol, field.Name);

                // KLSG003: Unsupported direct type
                if (field.ElementTypeName == null && field.KeyTypeName == null)
                {
                    if (!TypeMappings.TryGetMapping(field.TypeFullName, out _))
                    {
                        field.IsUnsupported = true;
                        diagnostics.Add(Diagnostic.Create(
                            DiagnosticDescriptors.UnsupportedFieldType,
                            location,
                            field.Name,
                            field.TypeFullName));
                    }
                }

                // KLSG005: Unsupported collection element type (Array or List)
                if (field.ElementTypeName != null && field.KeyTypeName == null)
                {
                    if (!TypeMappings.TryGetMapping(field.ElementTypeName, out _))
                    {
                        field.IsUnsupported = true;
                        diagnostics.Add(Diagnostic.Create(
                            DiagnosticDescriptors.UnsupportedCollectionElement,
                            location,
                            field.Name,
                            field.ElementTypeName));
                    }
                }

                // KLSG006: Unsupported dictionary key or value type
                if (field.KeyTypeName != null && field.ValueTypeName != null)
                {
                    bool keyOk = TypeMappings.TryGetMapping(field.KeyTypeName, out _);
                    bool valOk = TypeMappings.TryGetMapping(field.ValueTypeName, out _);
                    if (!keyOk || !valOk)
                    {
                        field.IsUnsupported = true;
                        diagnostics.Add(Diagnostic.Create(
                            DiagnosticDescriptors.UnsupportedDictionaryType,
                            location,
                            field.Name,
                            field.KeyTypeName,
                            field.ValueTypeName));
                    }
                }
            }
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
            // Fallback to type location
            var typeRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
            return typeRef?.GetSyntax().GetLocation() ?? Location.None;
        }

        private static TypeCategory? DetermineCategory(INamedTypeSymbol symbol)
        {
            var current = symbol.BaseType;
            while (current != null)
            {
                var name = current.ToDisplayString();
                if (name == "xpTURN.Klotho.State.EntityBase") return TypeCategory.Entity;
                if (name == "xpTURN.Klotho.Core.CommandBase") return TypeCategory.Command;
                if (name == "xpTURN.Klotho.Network.NetworkMessageBase") return TypeCategory.Message;
                if (name == "xpTURN.Klotho.Core.SimulationEvent") return TypeCategory.Event;
                current = current.BaseType;
            }
            return null;
        }

        private static void ExtractTypeId(GeneratorAttributeSyntaxContext ctx, SerializableTypeInfo info)
        {
            // Read typeId from [KlothoSerializable(typeId)] attribute constructor argument
            var attrData = ctx.Attributes.FirstOrDefault();
            if (attrData != null && attrData.ConstructorArguments.Length > 0
                && attrData.ConstructorArguments[0].Value is int attrTypeId
                && attrTypeId >= 0)
            {
                info.TypeId = attrTypeId;
            }

            // For NetworkMessage, read MessageType enum from attribute named property
            if (info.Category == TypeCategory.Message && attrData != null)
            {
                foreach (var namedArg in attrData.NamedArguments)
                {
                    if (namedArg.Key == "MessageTypeId")
                    {
                        var tc = namedArg.Value;
                        if (tc.Type is INamedTypeSymbol enumType && tc.Value != null)
                        {
                            var enumName = FindEnumMemberName(enumType, tc.Value);
                            if (enumName != null)
                                info.MessageTypeEnum = enumName;
                        }
                        break;
                    }
                }
            }
        }

        private static void ClassifyFieldType(SerializableFieldInfo field, ITypeSymbol typeSymbol)
        {
            var fullName = typeSymbol.ToDisplayString();

            // Check direct mapping
            if (TypeMappings.TryGetMapping(fullName, out var mapping))
            {
                field.SizeKind = mapping.Size > 0 ? FieldSizeKind.Fixed : FieldSizeKind.Variable;
                return;
            }

            // Check byte[]
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

            // Check generic collections
            if (typeSymbol is INamedTypeSymbol namedType && namedType.IsGenericType)
            {
                var originalDef = namedType.OriginalDefinition.ToDisplayString();

                // List<T>
                if (originalDef == "System.Collections.Generic.List<T>")
                {
                    field.ElementTypeName = namedType.TypeArguments[0].ToDisplayString();
                    field.SizeKind = FieldSizeKind.Variable;
                    return;
                }

                // Dictionary<TK, TV>
                if (originalDef == "System.Collections.Generic.Dictionary<TKey, TValue>")
                {
                    field.KeyTypeName = namedType.TypeArguments[0].ToDisplayString();
                    field.ValueTypeName = namedType.TypeArguments[1].ToDisplayString();
                    field.SizeKind = FieldSizeKind.Variable;
                    return;
                }
            }

            // Unknown type — will be caught as KLSG003
            field.SizeKind = FieldSizeKind.Fixed;
        }

        private static bool HasMethodDeclaredInType(INamedTypeSymbol symbol, string methodName)
        {
            return symbol.GetMembers(methodName).OfType<IMethodSymbol>().Any();
        }

        private static bool HasAttribute(ISymbol symbol, string attributeFullName)
        {
            return symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attributeFullName);
        }

        private static AttributeData GetAttribute(ISymbol symbol, string attributeFullName)
        {
            return symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attributeFullName);
        }

        private static string FindEnumMemberName(INamedTypeSymbol enumType, object value)
        {
            foreach (var member in enumType.GetMembers())
            {
                if (member is IFieldSymbol fs && fs.HasConstantValue && fs.ConstantValue.Equals(value))
                    return fs.Name;
            }
            return null;
        }
    }
}
