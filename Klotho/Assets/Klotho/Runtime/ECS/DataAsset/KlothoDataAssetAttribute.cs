using System;

namespace xpTURN.Klotho.ECS
{
    [AttributeUsage(AttributeTargets.Class)]
    public class KlothoDataAssetAttribute : Attribute
    {
        public new int TypeId { get; }
        public KlothoDataAssetAttribute(int typeId) => TypeId = typeId;
    }
}
