namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Cross-peer fingerprint source for the peer-local navigation state
    ///
    /// The navmesh — including runtime rebakes (FPNavMeshRebaker) and isBlocked mutations —
    /// is deliberately outside the wire/frame/state hash. FullState resync folds this
    /// fingerprint into the existing static-geometry fingerprint check
    /// (<see cref="xpTURN.Klotho.Deterministic.Physics.IStaticColliderService.GetStaticFingerprint"/>
    /// mirror), so a diverged navmesh surfaces at join/resync time instead of several ticks
    /// later as an unexplained agent-position desync.
    ///
    /// Register the implementing system (e.g. <see cref="FPNavAgentSystem"/>) with the
    /// simulation's system runner so the engine can discover it via GetSystem.
    /// </summary>
    public interface INavFingerprintSource
    {
        /// <summary>
        /// Fingerprint of the current navigation state. Never 0 while a navmesh is present
        /// (0 = "no navmesh" and is skipped by the resync check).
        /// </summary>
        long GetNavFingerprint();
    }
}
