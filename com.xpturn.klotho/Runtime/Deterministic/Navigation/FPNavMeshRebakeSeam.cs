using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// One placement plus the tick window it occupies the mesh for. The whole game-specific half of
    /// a delayed-rebake pipeline is a table of these.
    ///
    /// <para><b>The tick window has to be FRAME STATE on the game's side.</b> That is the
    /// precondition for everything <see cref="FPNavMeshRebakeDriver"/> does: it derives the installed
    /// mesh from the frame on every tick rather than swapping on an event, and it can only do that if
    /// "what is in the mesh at tick T" is answerable from state that rolls back. A game that keeps
    /// its pending placements in a plain field instead gets a mesh that survives a rewind while the
    /// frame that described it does not.</para>
    /// </summary>
    public struct FPNavMeshTimedPlacement
    {
        /// <summary>
        /// Sort key, and it must be UNIQUE among the entries of one collect.
        ///
        /// <para>Uniqueness is checked rather than assumed (<see cref="FPNavMeshRebakeDriver"/>
        /// audits it) because the sort is not stable: with a tie, the order depends on the order the
        /// game happened to enumerate, and the rebake input stops being a function of the frame.
        /// Every peer then sorts its own enumeration order and the navmeshes diverge while the STATE
        /// hash still matches, so nothing else would ask why. A game numbering per owner, or reusing
        /// a freed slot's number, trips this immediately.</para>
        /// </summary>
        public int Sequence;

        public FPBuildingPlacement Placement;

        /// <summary>The first tick this is part of the mesh.</summary>
        public int EffectiveTick;

        /// <summary>The first tick this is NOT part of the mesh. <c>int.MaxValue</c> for "never".</summary>
        public int RemovalEffectiveTick;
    }

    /// <summary>
    /// Where the driver gets the placement table, and who destroys the entries that have come due.
    ///
    /// <para>Implemented by the game, because the components are the game's. Everything else the
    /// driver needs — the active set at a tick, whether a tick is a boundary, which tick the next
    /// boundary is, the digest that detects change — it DERIVES from the table. That split is
    /// deliberate: getting the boundary predicate wrong is a desync, and two predicates written by
    /// the game are two chances to get it wrong.</para>
    /// </summary>
    public interface IFPNavMeshPlacementSource
    {
        /// <summary>
        /// How many entries the driver's buffers must hold.
        ///
        /// <para>⚠ This is the STORAGE bound, not the policy one. A placement being demolished keeps
        /// its slot until its removal tick, so the number of entries a frame can hold is larger than
        /// the number a game lets stand. Sizing this from the policy bound makes
        /// <see cref="Collect"/> truncate, and a truncated rebake input is the quietest failure in
        /// this whole pipeline: the dropped entries are missing from the mesh while the state hash
        /// still matches on every peer.</para>
        /// </summary>
        int Capacity { get; }

        /// <summary>
        /// Fills <paramref name="buffer"/> with EVERY placement the frame holds — no tick filtering,
        /// because the driver needs the windows to derive boundaries. Returns how many were written.
        ///
        /// <para><paramref name="eligible"/> is how many the frame actually held. The driver compares
        /// the two and complains loudly when they differ; an implementation that silently stops at
        /// the buffer's end without counting the rest defeats that check.</para>
        /// </summary>
        int Collect(ref Frame frame, FPNavMeshTimedPlacement[] buffer, out int eligible);

        /// <summary>
        /// Destroys whatever the game keeps for placements whose removal tick has arrived
        /// (<c>&lt;= tick</c>, not <c>==</c> — a tombstone that slipped past one tick must still go).
        ///
        /// <para>A frame WRITE, so it cannot be delegated: the driver does not know the game's
        /// components. The driver calls it on every tick and at a fixed point in its own order, which
        /// is what makes the destroy reproduce on a re-execution.</para>
        /// </summary>
        void DestroyDue(ref Frame frame, int tick);
    }

    /// <summary>
    /// The two halves of putting a mesh in front of the agents, as the driver needs them.
    ///
    /// <para>Implemented by the game rather than supplied by the core, because the agent buffer these
    /// walk belongs to the game's own per-tick pipeline (see
    /// <see cref="FPNavAgentInstaller"/> for why that is, and for the helper that does the actual
    /// work once the buffer is handed to it).</para>
    ///
    /// <para><b>Two methods, not one.</b> Installing is derived state — the driver skips it when the
    /// right mesh is already in place. Reseeding writes hashed frame state, so the driver runs it
    /// whenever the tick that owns it executes, including every re-execution after a rollback. An
    /// implementation that reseeds inside <see cref="Install"/> puts the write back under a
    /// peer-local condition that does not roll back, which is the divergence four live matches were
    /// spent finding.</para>
    /// </summary>
    public interface IFPNavMeshInstaller
    {
        /// <summary>Makes <paramref name="mesh"/> the live one. Must NOT reseed.</summary>
        void Install(ref Frame frame, FPNavMesh mesh);

        /// <summary>
        /// Reseeds the agents against the mesh already installed.
        ///
        /// <para>⚠ Must re-collect the agent set. The driver calls this on a boundary tick even when
        /// no <see cref="Install"/> preceded it — that is the case where the wanted mesh was already
        /// live — so nothing else has refreshed a cached set, and the game's buffer may hold the
        /// previous tick's entities.</para>
        /// </summary>
        void Reseed(ref Frame frame);
    }
}
