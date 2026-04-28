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

        // For collection types
        public string ElementTypeName { get; set; }
        public string KeyTypeName { get; set; }
        public string ValueTypeName { get; set; }
    }
}
