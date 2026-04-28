using System;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Attribute applied to structs that should be treated as primitive types by the source generator.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public class PrimitiveAttribute : Attribute { }
}
