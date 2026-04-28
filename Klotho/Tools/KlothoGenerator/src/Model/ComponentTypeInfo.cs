using System.Collections.Generic;

namespace xpTURN.Klotho.Generator.Model
{
    internal sealed class ComponentTypeInfo
    {
        public string Namespace { get; set; }
        public string TypeName { get; set; }
        public string FullTypeName { get; set; }
        public int ComponentTypeId { get; set; }
        public List<ComponentFieldInfo> Fields { get; set; } = new List<ComponentFieldInfo>();
    }

    internal sealed class ComponentFieldInfo
    {
        public string Name { get; set; }
        public string TypeFullName { get; set; }
        public bool IsFixed { get; set; }
        public int FixedSize { get; set; }
        public string ElementType { get; set; }
    }
}
