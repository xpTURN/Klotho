using System.Collections.Generic;

namespace xpTURN.Klotho.Generator.Model
{
    internal sealed class ComponentTypeInfo
    {
        public string Namespace { get; set; }
        public string TypeName { get; set; }
        public string FullTypeName { get; set; }
        public int ComponentTypeId { get; set; }
        public bool IsSingleton { get; set; }
        public bool IsCore { get; set; }
        public int MaxCount { get; set; }

        /// <summary>[KlothoCleanup] mode as the enum MEMBER NAME (e.g. "RemoveComponent"), or null
        /// when unmarked. A name, not an int, because the emitted registrar has to read as source.</summary>
        public string Cleanup { get; set; }
        public List<ComponentFieldInfo> Fields { get; set; } = new List<ComponentFieldInfo>();
    }

    internal sealed class ComponentFieldInfo
    {
        public string Name { get; set; }
        public string TypeFullName { get; set; }
        public bool IsFixed { get; set; }
        public int FixedSize { get; set; }
        public string ElementType { get; set; }

        /// <summary>Field type is a [KlothoSerializableStruct] — serialize by delegating to its
        /// generated Serialize/Deserialize/GetSerializedSize/GetHash (size is non-constant).</summary>
        public bool IsNestedSerializable { get; set; }
    }
}
