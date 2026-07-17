using xpTURN.Klotho.ECS;   // ComponentStorageRegistry, MovementComponent

namespace Brawler
{
    /// <summary>
    /// Reservation-pruning denylist for Brawler — the component types this game skips reserving.
    ///
    /// A denylist lists ONLY the types to prune; everything else is reserved by default (fail-safe: forgetting
    /// to list a type just reserves it, never crashes). A type belongs here ONLY when NO system Brawler
    /// registers touches it — a <c>Filter</c>/<c>Has</c> opens the storage even at 0 live instances, so pruning
    /// a touched type throws at <c>GetStorage</c>. Engine <c>[KlothoCoreComponent]</c> types are force-excluded
    /// by the registry, so never list them. Feed to <c>ISimulationConfig.SetRuntimePrunedComponentTypeIds</c>
    /// (server + P2P host); an empty list = no pruning (safe, byte-identical).
    ///
    /// Measured (dedicated server <c>--memreport</c>, no pruning): of Brawler's 22 registered types, the only
    /// one no registered system touches is <see cref="MovementComponent"/> — everything else is either an
    /// engine <c>[KlothoCoreComponent]</c> or scanned by a registered system (PhysicsSystem / CombatSystem /
    /// NavigationSystem / HFSM), so it cannot be pruned even at peak=0. So the denylist is a single entry.
    /// Pruning it reclaims its full per-frame reservation (≈ 11.3KB/frame, incl. the sparse floor that
    /// maxCount cannot), i.e. ≈ 0.5MB across the rollback ring — small, as expected for a game that registers
    /// the physics/combat/nav/AI system stack (the big savings come from maxCount, not pruning).
    ///
    /// Declared type-based (typeIds resolved via the registry) so no magic ints leak into config.
    /// </summary>
    public static class BrawlerPrunedComponents
    {
        /// <summary>
        /// The denylist as resolved typeIds (single source of truth). Feed to
        /// <c>ISimulationConfig.SetRuntimePrunedComponentTypeIds</c> on any config (dedicated server,
        /// Unity/Godot P2P host) — a uniform, non-persisting runtime injection.
        /// </summary>
        public static int[] ResolveTypeIds() => new[]
        {
            // Engine movement component — registered (came along with the engine package) but touched by no
            // system Brawler registers (verified: the prior full SD+P2P e2e pruned exactly this with 0 guard
            // throws). The one genuinely-unused registered type in Brawler.
            ComponentStorageRegistry.GetTypeId<MovementComponent>(),
        };
    }
}
