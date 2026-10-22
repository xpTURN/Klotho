using System.Collections.Generic;

namespace xpTURN.Klotho.Generator.Model
{
    internal enum TypeCategory
    {
        Entity,
        Command,
        Message,
        Event
    }

    internal enum FieldSizeKind
    {
        Fixed,
        Variable
    }

    internal sealed class SerializableTypeInfo
    {
        public string Namespace { get; set; }
        public string TypeName { get; set; }
        public string FullTypeName { get; set; }
        public TypeCategory Category { get; set; }
        public List<SerializableFieldInfo> Fields { get; set; } = new List<SerializableFieldInfo>();
        public int? TypeId { get; set; }
        public string MessageTypeEnum { get; set; }

        // Raw integer value of [KlothoSerializable(MessageTypeId = (NetworkMessageType)N)] when N
        // falls in the user-defined region (>= UserDefined_Start). Mutually exclusive with
        // MessageTypeEnum — emitters prefer raw value so the generated literal stays aligned
        // with the user's cast expression instead of resolving to a sentinel name.
        public int? MessageTypeRawValue { get; set; }

        public bool HasManualSerialization { get; set; }
    }

    internal sealed class SerializableFieldInfo
    {
        public string Name { get; set; }
        public string TypeFullName { get; set; }
        public int Order { get; set; }
        public bool IsProperty { get; set; }
        public bool IncludeInHash { get; set; } = true;
        public FieldSizeKind SizeKind { get; set; } = FieldSizeKind.Fixed;
        public bool IsUnsupported { get; set; }

        /// <summary>Field type is a [KlothoSerializableStruct] — serialize by delegating to its
        /// generated codec (fields only; size is a runtime expression).</summary>
        public bool IsNestedSerializable { get; set; }

        // For collection types
        public string ElementTypeName { get; set; }
        public string KeyTypeName { get; set; }
        public string ValueTypeName { get; set; }

        /// <summary>Collection element (List&lt;T&gt; / T[]) is a [KlothoSerializableStruct] —
        /// serialize each element by delegating to its generated codec. Orthogonal to
        /// <see cref="IsNestedSerializable"/> (which is for a direct nested struct field).</summary>
        public bool ElementIsNestedSerializable { get; set; }

        /// <summary>The generated Deserialize may assign the member — false for a readonly field, a get-only
        /// property and an init-only setter, which are cleared and refilled in place instead.</summary>
        public bool IsAssignable { get; set; } = true;

        /// <summary>The declaration carries an initializer (<c>= new …</c>) — KLSG008 is reported for a
        /// collection without one.</summary>
        public bool HasInitializer { get; set; }

        public bool IsCollection => ElementTypeName != null || KeyTypeName != null;
    }
}
