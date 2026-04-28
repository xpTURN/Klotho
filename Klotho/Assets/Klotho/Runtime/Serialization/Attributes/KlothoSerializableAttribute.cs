using System;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Serialization
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class KlothoSerializableAttribute : Attribute
    {
        public new int TypeId { get; }
        public NetworkMessageType MessageTypeId { get; set; }
        public KlothoSerializableAttribute(int typeId = -1) => TypeId = typeId;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class KlothoOrderAttribute : Attribute
    {
        public int Order { get; }
        public KlothoOrderAttribute(int order = -1) => Order = order;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class KlothoIgnoreAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class KlothoHashIgnoreAttribute : Attribute { }
}
