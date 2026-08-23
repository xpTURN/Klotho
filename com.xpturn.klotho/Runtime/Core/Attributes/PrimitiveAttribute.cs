using System;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Marks a struct as an atomic value for the ECS inspector: the entity-component window renders it
    /// on ONE line via <c>ToString()</c> instead of recursing into its public fields.
    /// </summary>
    /// <remarks>
    /// The only consumer is <c>ComponentReflectionCache.IsPrimitive</c> in the Unity editor assembly —
    /// despite the historical wording, the source generator never reads this attribute. So adding it is
    /// display-only: serialization, hashing, and the wire are unaffected. Add it to any value type whose
    /// fields are an implementation detail (fixed buffers, packed bits) and whose <c>ToString()</c> is
    /// the readable form.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Struct)]
    public class PrimitiveAttribute : Attribute { }
}
