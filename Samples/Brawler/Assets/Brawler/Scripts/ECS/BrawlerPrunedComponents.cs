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
    /// Declared type-based (typeIds resolved via the registry) so no magic ints leak into config — with one
    /// documented exception, the 9000+ test/verification block below, whose types live in assemblies this one
    /// must not reference.
    /// </summary>
    public static class BrawlerPrunedComponents
    {
        /// <summary>
        /// Test/verification component typeIds (9000+ block convention). These are declared in assemblies that
        /// exist in the Unity Editor but not in a dedicated-server build — `xpTURN.Klotho.Tests`
        /// (includePlatforms: Editor) and the determinism-verification helpers.
        ///
        /// They must be pruned because the registered component SET is state-hash input: Frame.CalculateHash
        /// folds every registered type, including the ones with zero instances. Without this, an Editor client
        /// registers 30 types against the server's 22 and the two can never agree on a state hash — the match
        /// aborts at tick 1 with a divergence that has nothing to do with gameplay. Pruning drops them out of
        /// the layout (and therefore out of the fold), so Editor and server land on the same 22.
        ///
        /// Raw ids, not GetTypeId&lt;T&gt;(): referencing a test-only assembly from game code to prune it would
        /// be worse than the magic numbers. The ids are explicit in each type's [KlothoComponent(id)], so they
        /// are stable by construction.
        ///
        /// Adding a new test component in this range means adding it here. The drift is not silent:
        /// ComponentStorageRegistry.LayoutFingerprint is logged at boot on every peer and compared at join,
        /// so a missing entry shows up immediately as a registry mismatch rather than as a mystery desync.
        ///
        /// Safe to list on peers that do not have these types: the registry only prunes typeIds it actually
        /// registered, so on the dedicated server every entry here is inert.
        /// </summary>
        private static readonly int[] TestVerificationTypeIds =
        {
            9000,   // ArithmeticResultComponent      (determinism verification)
            9001,   // RandomStateComponent           (determinism verification)
            9002,   // EntityLifecycleTagComponent    (tests)
            9003,   // DummySingletonComponent        (tests)
            9004,   // DummyNonSingletonComponent     (tests)
            9100,   // MoveHFSMComponent              (tests)
            9101,   // CombatHFSMComponent            (tests)
            9999,   // TailSingletonComponent         (tests)
        };

        /// <summary>
        /// The denylist as resolved typeIds (single source of truth). Feed to
        /// <c>ISimulationConfig.SetRuntimePrunedComponentTypeIds</c> on any config (dedicated server,
        /// Unity/Godot P2P host) — a uniform, non-persisting runtime injection.
        /// </summary>
        public static int[] ResolveTypeIds()
        {
            var ids = new int[1 + TestVerificationTypeIds.Length];

            // Engine movement component — registered (came along with the engine package) but touched by no
            // system Brawler registers (verified: the prior full SD+P2P e2e pruned exactly this with 0 guard
            // throws). The one genuinely-unused registered GAME type in Brawler.
            ids[0] = ComponentStorageRegistry.GetTypeId<MovementComponent>();

            System.Array.Copy(TestVerificationTypeIds, 0, ids, 1, TestVerificationTypeIds.Length);
            return ids;
        }
    }
}
