namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// The game's own contribution to the static environment fingerprint the engine exchanges on
    /// FullState — for anything that must be identical across builds but is deliberately outside
    /// the state hash.
    ///
    /// <para>The engine folds three sources of its own: the component-registry layout, the static
    /// colliders, and the navmesh. What it cannot know is the rest of a game's shared setup. The
    /// runtime-rebake shape catalog is the case that prompted this — a building placement is a
    /// REFERENCE into that table, so two builds that disagree about it carve different navmeshes
    /// from identical commands, and until a building is actually placed the meshes match and
    /// nothing is wrong yet. Tuning tables, asset id maps and balance data have the same shape.
    /// </para>
    ///
    /// <para>Implement it on a system and register that system; the engine finds it the same way
    /// it finds the other sources. Returning 0 means "nothing to contribute" and folds to a no-op,
    /// so leaving it unimplemented costs nothing.</para>
    ///
    /// <para><b>It must be a pure function of data the peers are supposed to share</b> — the same
    /// build must produce the same value every run, or every FullState reports a mismatch. Hash
    /// the table, not the objects: an object hash code varies per process.</para>
    /// </summary>
    public interface IGameFingerprintSource
    {
        /// <summary>
        /// A value identifying the game-side data that peers must agree on, or 0 for none.
        /// Folded into the environment fingerprint and reported separately when it diverges, so a
        /// mismatch here does not read as a navmesh problem.
        /// </summary>
        long GetGameFingerprint();
    }
}
