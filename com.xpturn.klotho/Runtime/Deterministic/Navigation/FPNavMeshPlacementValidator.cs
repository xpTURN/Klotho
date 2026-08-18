using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Validates one placement or removal command against the set that will be live when it lands.
    ///
    /// <para><b>What this replaces.</b> A game handling placement commands has to derive the active
    /// set at a FUTURE tick, order it canonically, audit the two things that make that order safe, and
    /// only then ask the rebaker whether the result bakes. Every one of those steps is the same one
    /// <see cref="FPNavMeshRebakeDriver"/> performs to decide what to install — so a game writing them
    /// itself keeps a second copy of the derivation AND a second copy of two DESYNC-class audits. The
    /// audits exist precisely because the game is expected to get it wrong; sharing the derivation
    /// removes the opportunity instead of reporting it.</para>
    ///
    /// <para><b>Two steps, not one.</b> <see cref="Survey"/> then <see cref="TryPreview"/> /
    /// <see cref="TryPreviewWith"/>, because a policy cap belongs between them: a game refusing "you
    /// already have 32 buildings" should not pay for a trial rebake first. Fusing them would put that
    /// cost behind the cheapest refusal.</para>
    ///
    /// <para><b>Not reentrant.</b> The survey result lives in this object's buffers, so one command is
    /// one Survey followed by its preview. A nested Survey overwrites the first, and there is no
    /// legitimate reason to nest — a command handler validates one command.</para>
    ///
    /// <para><b>The context is a parameter, and it must be the game's own.</b> Never the driver's:
    /// the driver holds contexts whose buffer pools are occupied by an in-flight sliced rebake across
    /// frames, and the pool refuses overlapping use. The driver does not expose them for that reason.</para>
    /// </summary>
    public sealed class FPNavMeshPlacementValidator
    {
        private readonly IFPNavMeshPlacementSource _source;
        private readonly FPBuildingPlacementRules _rules;

        private readonly FPNavMeshTimedPlacement[] _table;
        private readonly FPBuildingPlacement[] _active;
        private readonly int[] _sortKeys;

        private int _tableCount;
        private int _activeCount = NotSurveyed;

        private const int NotSurveyed = -2;

        /// <param name="source">
        /// The same table the driver reads. <c>Capacity</c> must be the STORAGE bound — a demolished
        /// entry keeps its slot until its removal tick, so the number a frame can hold is larger than
        /// the number a game lets stand. Sizing it from the policy bound makes the collect truncate,
        /// and a truncated validation input silently accepts placements the mesh will refuse.
        /// </param>
        public FPNavMeshPlacementValidator(
            IFPNavMeshPlacementSource source, FPBuildingPlacementRules rules = default)
        {
            if (source == null)
                throw new System.ArgumentException("FPNavMeshPlacementValidator: source is null");

            int cap = source.Capacity;
            if (cap <= 0)
                throw new System.ArgumentException(
                    "FPNavMeshPlacementValidator: source.Capacity must be positive. It is the STORAGE " +
                    "bound (tombstones included), not the number of placements the game lets stand.");

            _source = source;
            _rules = rules;
            _table = new FPNavMeshTimedPlacement[cap];
            // One past capacity: a placement validates the stored set PLUS the candidate.
            _active = new FPBuildingPlacement[cap + 1];
            _sortKeys = new int[cap + 1];
        }

        /// <summary>
        /// Reads the table and derives the set live at <paramref name="atTick"/>. Returns how many
        /// entries that is, or <b>-1</b> when the survey could not be performed (see below).
        ///
        /// <para><paramref name="atTick"/> is the tick the command TAKES EFFECT, not the current one.
        /// Validating against the present set would let two placements queued inside the same delay
        /// window pass without seeing each other, and both would then land on a mesh that refuses the
        /// pair — a refusal with no command left to refuse, arriving after both were reported
        /// accepted.</para>
        ///
        /// <para><paramref name="skipSequence"/> excludes one entry, for validating a REMOVAL: at the
        /// moment the command is handled the target is still inside its own tick window, so the window
        /// alone will not drop it.</para>
        ///
        /// <para><b>Returns -1 when a requested skip is ambiguous.</b> If two live entries share a
        /// Sequence, "the one to exclude" is not a well-defined entry — and that is exactly the state
        /// audit 9 exists to report. The caller must refuse the command rather than remove whichever
        /// one the comparison happened to reach. This is stricter than excluding by entity identity
        /// (which is immune to the duplicate), and the strictness is deliberate: proceeding would
        /// delete a building that is still standing while its hole stayed carved in the mesh.</para>
        /// </summary>
        public int Survey(ref Frame frame, int atTick, int skipSequence = -1)
        {
            _tableCount = FPNavMeshPlacementTableOps.Collect(_source, ref frame, _table);
            NextSequence = FPNavMeshPlacementTableOps.NextSequence(_table, _tableCount);

            _activeCount = FPNavMeshPlacementTableOps.BuildActiveSet(
                ref frame, _table, _tableCount, atTick, _active, _sortKeys,
                skipSequence, out bool duplicate);

            if (duplicate && skipSequence >= 0)
            {
                _activeCount = NotSurveyed;
                return -1;
            }
            return _activeCount;
        }

        /// <summary>
        /// One past the highest <c>Sequence</c> in the whole table as of the last
        /// <see cref="Survey"/> — the number to give a new entry.
        ///
        /// <para>Over the WHOLE table, not the active subset: narrowing it would hand out a number a
        /// pending or tombstoned entry still holds, and a duplicate is what makes the canonical order
        /// depend on enumeration order.</para>
        /// </summary>
        public int NextSequence { get; private set; }

        /// <summary>
        /// Asks whether the surveyed set bakes, and throws the answer away. Use for a REMOVAL, where
        /// the set only shrinks.
        ///
        /// <para>A refused set comes back as <c>false</c> plus a reason — that is the normal outcome
        /// of a player pointing somewhere they cannot build. A malformed request still throws: a stale
        /// shape id is the game's bug, and reporting it as "you cannot build there" would show a
        /// developer error to the player on every peer at once.</para>
        /// </summary>
        public bool TryPreview(
            FPNavMeshRebakeContext context, out FPBuildingRejectionInfo rejection, IKLogger logger = null)
        {
            RequireSurvey();
            return FPNavMeshRebaker.TryPreviewPlacements(
                context, _active, out rejection, logger, _rules, _activeCount);
        }

        /// <summary>
        /// Asks whether the surveyed set PLUS <paramref name="candidate"/> bakes. Use for a placement.
        ///
        /// <para>The candidate is APPENDED, never re-sorted in. The surveyed set is already in
        /// sequence order and the candidate's number is one past the highest, so the end is where it
        /// belongs — and keeping it there is what lets the rebaker patch the previous mesh instead of
        /// rebuilding it (the hole vertices are appended in list order, so a list that only grows at
        /// the end keeps every existing vertex index meaningful).</para>
        /// </summary>
        public bool TryPreviewWith(
            FPNavMeshRebakeContext context, in FPBuildingPlacement candidate,
            out FPBuildingRejectionInfo rejection, IKLogger logger = null)
        {
            RequireSurvey();
            _active[_activeCount] = candidate;
            return FPNavMeshRebaker.TryPreviewPlacements(
                context, _active, out rejection, logger, _rules, _activeCount + 1);
        }

        private void RequireSurvey()
        {
            if (_activeCount == NotSurveyed)
                throw new System.ArgumentException(
                    "FPNavMeshPlacementValidator: Survey has not run, or it returned -1 (an ambiguous " +
                    "removal target). One command is one Survey followed by one preview; a caller that " +
                    "ignored a -1 would validate against a set the survey refused to define.");
        }
    }
}
