using Microsoft.CodeAnalysis;

namespace xpTURN.Klotho.Generator.Analyzers
{
    internal static class DiagnosticDescriptors
    {
        public static readonly DiagnosticDescriptor MissingPartial = new DiagnosticDescriptor(
            "KLSG001",
            "Missing partial keyword",
            "[KlothoSerializable] class '{0}' must be declared as partial",
            "KlothoGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateOrder = new DiagnosticDescriptor(
            "KLSG002",
            "Duplicate KlothoOrder value",
            "[KlothoOrder({0})] is used by multiple members in '{1}'",
            "KlothoGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedFieldType = new DiagnosticDescriptor(
            "KLSG003",
            "Unsupported field type",
            "Field '{0}' has unsupported type '{1}' for serialization",
            "KlothoGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvalidBaseClass = new DiagnosticDescriptor(
            "KLSG004",
            "Invalid base class",
            "[KlothoSerializable] class '{0}' must inherit from EntityBase, CommandBase, or NetworkMessageBase",
            "KlothoGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedCollectionElement = new DiagnosticDescriptor(
            "KLSG005",
            "Unsupported collection element type",
            "Collection field '{0}' has unsupported element type '{1}'",
            "KlothoGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedDictionaryType = new DiagnosticDescriptor(
            "KLSG006",
            "Unsupported dictionary key/value type",
            "Dictionary field '{0}' has unsupported key type '{1}' or value type '{2}'",
            "KlothoGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateTypeId = new DiagnosticDescriptor(
            "KLSG007",
            "Duplicate TYPE_ID",
            "TYPE_ID {0} is used by both '{1}' and '{2}' ({3})",
            "KlothoGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        // --- ECS Component diagnostics ---

        public static readonly DiagnosticDescriptor DuplicateComponentTypeId = new DiagnosticDescriptor(
            "KLSG_ECS001",
            "Duplicate Component Type ID",
            "[KlothoComponent({0})] is used by both '{1}' and '{2}'",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ComponentMissingIComponent = new DiagnosticDescriptor(
            "KLSG_ECS002",
            "Missing IComponent",
            "[KlothoComponent] struct '{0}' must implement IComponent",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ComponentNotUnmanaged = new DiagnosticDescriptor(
            "KLSG_ECS003",
            "Component not unmanaged",
            "[KlothoComponent] struct '{0}' must be unmanaged (no managed reference fields)",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ComponentOversized = new DiagnosticDescriptor(
            "KLSG_ECS004",
            "Oversized component",
            "[KlothoComponent] struct '{0}' exceeds 128 bytes",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ComponentMissingPartial = new DiagnosticDescriptor(
            "KLSG_ECS005",
            "Missing partial keyword",
            "[KlothoComponent] struct '{0}' must be declared as partial",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        // --- StructLayout + cross-runtime guard rules ---

        public static readonly DiagnosticDescriptor KlothoStructLayoutMissing = new DiagnosticDescriptor(
            "KLOTHO_STRUCT_LAYOUT_MISSING",
            "Missing StructLayout attribute",
            "[KlothoComponent] struct '{0}' must have [StructLayout(LayoutKind.Sequential, Pack = 4)]",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Frame heap redesign requires Pack=4 Sequential layout on every component struct. Pack=1 risks ARMv7 SIGBUS; Pack=8 is conditional on dropping ARMv7.");

        public static readonly DiagnosticDescriptor KlothoIntPtrForbidden = new DiagnosticDescriptor(
            "KLOTHO_INTPTR_FORBIDDEN",
            "Platform-variable size type forbidden",
            "[KlothoComponent] struct '{0}' contains IntPtr/UIntPtr field '{1}' (forbidden — platform-variable size breaks cross-runtime determinism)",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor KlothoEmptyStructSize = new DiagnosticDescriptor(
            "KLOTHO_EMPTY_STRUCT_SIZE",
            "Empty tag struct requires Size = 1",
            "[KlothoComponent] tag struct '{0}' has no fields — must specify [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 1)] (Mono returns Unsafe.SizeOf<T>()=0 for field-less Sequential structs, causing MemoryMarshal.Cast DivideByZero)",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor KlothoEnumUnderlying = new DiagnosticDescriptor(
            "KLOTHO_ENUM_UNDERLYING",
            "Enum underlying type not specified",
            "[KlothoComponent] struct '{0}' field '{1}' uses enum '{2}' without explicit underlying type (defaults to int 4B — specify 'enum X : byte/short/int/long' for clarity)",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor KlothoBoolField = new DiagnosticDescriptor(
            "KLOTHO_BOOL_FIELD",
            "bool field in component",
            "[KlothoComponent] struct '{0}' field '{1}' is 'bool' — Managed 1B, verify cross-platform size consistency ([MarshalAs] is P/Invoke-only and does not affect Unsafe.SizeOf<T>())",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor KlothoCharField = new DiagnosticDescriptor(
            "KLOTHO_CHAR_FIELD",
            "char field in component",
            "[KlothoComponent] struct '{0}' field '{1}' is 'char' — Managed 2B (UTF-16), easily confused with C++ native char 1B; prefer byte/short for determinism",
            "KlothoGenerator.ECS",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        // --- DataAsset diagnostics ---

        public static readonly DiagnosticDescriptor DataAssetMissingPartial = new DiagnosticDescriptor(
            "KLSG_DA001",
            "Missing partial keyword",
            "[KlothoDataAsset] class '{0}' must be declared as partial",
            "KlothoGenerator.DataAsset",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DataAssetMissingIDataAsset = new DiagnosticDescriptor(
            "KLSG_DA002",
            "Missing IDataAsset",
            "[KlothoDataAsset] class '{0}' must implement IDataAsset",
            "KlothoGenerator.DataAsset",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DataAssetMissingConstructor = new DiagnosticDescriptor(
            "KLSG_DA003",
            "Missing int constructor",
            "[KlothoDataAsset] class '{0}' must have a constructor that accepts a single int parameter (assetId)",
            "KlothoGenerator.DataAsset",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DataAssetAmbiguousConstructor = new DiagnosticDescriptor(
            "KLSG_DA004",
            "Ambiguous constructor",
            "[KlothoDataAsset] class '{0}' has multiple public constructors; Newtonsoft.Json may fail to select the correct one",
            "KlothoGenerator.DataAsset",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateDataAssetTypeId = new DiagnosticDescriptor(
            "KLSG_DA005",
            "Duplicate DataAsset TypeId",
            "[KlothoDataAsset({0})] is used by both '{1}' and '{2}'",
            "KlothoGenerator.DataAsset",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}
