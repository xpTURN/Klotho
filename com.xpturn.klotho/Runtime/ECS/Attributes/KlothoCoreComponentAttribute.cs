using System;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Marks a component type as engine-core — always reserved regardless of a session's
    /// active-set (component-reservation pruning). The generated [ModuleInitializer]
    /// registrar passes <c>core: true</c> to <see cref="ComponentStorageRegistry.Register{T}"/>,
    /// and the registry force-includes these typeIds in the layout's active-set union so a game
    /// allowlist can never accidentally prune an engine-required component (e.g. Transform,
    /// RandomSeed, MatchEndState). Self-declaring: the authoritative core base-set is the set of
    /// components carrying this marker, not a hand-maintained list.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class KlothoCoreComponentAttribute : Attribute { }
}
