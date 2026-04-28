using System;

namespace xpTURN.Klotho.ECS
{
    [AttributeUsage(AttributeTargets.Struct)]
    public class KlothoComponentAttribute : Attribute
    {
        public const int UserMinId = 100;

        public int ComponentTypeId { get; }
        public KlothoComponentAttribute(int componentTypeId) => ComponentTypeId = componentTypeId;
    }
}
