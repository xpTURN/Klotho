using System;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Marks a component that lives for exactly one tick — a hit mark, a frame flag, a "destroy me"
    /// tag. The engine clears it (or destroys its carrier) in a built-in pass at the end of every
    /// tick, so the game never writes a cleanup system and never risks the mutate-during-iteration
    /// trap that hand-rolled removal invites (see Docs/ECS.md §5).
    /// </summary>
    /// <remarks>
    /// The generated <c>[ModuleInitializer]</c> registrar forwards the mode to
    /// <see cref="ComponentStorageRegistry.Register{T}"/>; nothing else reads the attribute. The pass
    /// runs after all systems and before <c>Tick++</c>, so the post-cleanup state is what gets
    /// hashed and snapshotted — rollback and resim reproduce it exactly.
    /// <para>
    /// Two combinations are rejected by the analyzer: a <c>[KlothoCoreComponent]</c> carrying any
    /// mode (KLSG_ECS007, warning — the engine's own components are persistent state) and a
    /// <c>[KlothoSingletonComponent]</c> with <see cref="CleanupMode.DestroyEntity"/>
    /// (KLSG_ECS008, error — the carrier may hold other components). A cleaned-up singleton must be
    /// read with <c>TryGetSingleton</c>: <c>GetSingleton</c> throws once the carrier is gone.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class KlothoCleanupAttribute : Attribute
    {
        /// <summary>The disposal mode applied at the end of every tick.</summary>
        public CleanupMode Mode { get; }

        public KlothoCleanupAttribute(CleanupMode mode)
        {
            Mode = mode;
        }
    }
}
