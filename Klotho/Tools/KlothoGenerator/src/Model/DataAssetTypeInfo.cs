using System.Collections.Generic;

namespace xpTURN.Klotho.Generator.Model
{
    internal sealed class DataAssetTypeInfo
    {
        public string Namespace { get; set; }
        public string TypeName { get; set; }
        public string FullTypeName { get; set; }
        public int TypeId { get; set; }
        public string ConstructorParamName { get; set; }
        public List<SerializableFieldInfo> Fields { get; set; } = new List<SerializableFieldInfo>();
    }
}
