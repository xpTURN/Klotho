namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// How the engine disposes of a <see cref="KlothoCleanupAttribute"/> component at the end of
    /// every tick. A single value, not a flag set: "remove the component" and "destroy the entity"
    /// are alternatives, and destruction subsumes removal.
    /// </summary>
    /// <remarks>
    /// This is a determinism input: two builds that disagree on a component's mode produce different
    /// state from tick 0, so the value is folded into
    /// <see cref="ComponentStorageRegistry.LayoutFingerprint"/> and therefore compared at the Ready
    /// exchange. Adding or reordering members changes that fingerprint for every peer.
    /// </remarks>
    public enum CleanupMode
    {
        /// <summary>Normal lifetime — the game owns add and remove. Default.</summary>
        None = 0,

        /// <summary>The component is removed from every carrier at the end of the tick it was added.</summary>
        RemoveComponent = 1,

        /// <summary>The carrier entity is destroyed at the end of the tick the component was added.</summary>
        DestroyEntity = 2,
    }
}
