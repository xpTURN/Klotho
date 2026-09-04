namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// One tick's contact/trigger snapshot accounting, as a single value: what the caller copied,
    /// what the world actually held, and how many copies have been truncated over this world's life.
    ///
    /// It exists because <c>copied == buffer.Length</c> cannot tell "the buffer was exactly full"
    /// apart from "the tail was dropped" — only <c>copied &lt; total</c> can, and six view call sites
    /// would otherwise each spell that comparison out. Copied and total travel together so they can
    /// never be read from different ticks, and the truncation questions are answered here rather
    /// than by each view.
    ///
    /// The counters are COPIES of the owner's values (<see cref="FPPhysicsWorld"/>) taken at the
    /// copy site; an accumulator cannot live inside an immutable value.
    ///
    /// The constructor is public because the code that fills this in — the copy site in
    /// <c>PhysicsSystem</c> — lives in a different assembly (Gameplay) than the world it reads
    /// (Runtime), so an internal constructor would be invisible to the one caller that needs it.
    /// Widening Runtime's internals to the whole Gameplay assembly to hide one debug constructor
    /// would be the worse trade. Nothing reads a consumer-built value: the engine hands its own to
    /// the views through <c>IFPPhysicsWorldProvider</c>.
    /// </summary>
    public readonly struct FPContactSnapshotStats
    {
        public readonly int ContactCopied;
        public readonly int ContactTotal;
        public readonly int StaticContactCopied;
        public readonly int StaticContactTotal;
        public readonly int TriggerCopied;
        public readonly int TriggerTotal;

        /// <summary>Truncating copy calls over the world's life, per kind. See <see cref="FPPhysicsWorld.DebugContactCopyTruncatedCount"/>.</summary>
        public readonly int ContactTruncatedCount;
        public readonly int StaticContactTruncatedCount;
        public readonly int TriggerTruncatedCount;

        public FPContactSnapshotStats(
            int contactCopied, int contactTotal,
            int staticContactCopied, int staticContactTotal,
            int triggerCopied, int triggerTotal,
            int contactTruncatedCount, int staticContactTruncatedCount, int triggerTruncatedCount)
        {
            ContactCopied = contactCopied;
            ContactTotal = contactTotal;
            StaticContactCopied = staticContactCopied;
            StaticContactTotal = staticContactTotal;
            TriggerCopied = triggerCopied;
            TriggerTotal = triggerTotal;
            ContactTruncatedCount = contactTruncatedCount;
            StaticContactTruncatedCount = staticContactTruncatedCount;
            TriggerTruncatedCount = triggerTruncatedCount;
        }

        public bool ContactsTruncated => ContactCopied < ContactTotal;
        public bool StaticContactsTruncated => StaticContactCopied < StaticContactTotal;
        public bool TriggersTruncated => TriggerCopied < TriggerTotal;

        /// <summary>Any of the three views is showing less than the world holds.</summary>
        public bool AnyTruncated => ContactsTruncated || StaticContactsTruncated || TriggersTruncated;

        /// <summary>
        /// Whether a per-body contact list should be drawn at all, given how many contacts that body
        /// has in the COPIED arrays. The two inspectors bail out when the count is zero, and a body
        /// whose contacts were all truncated away is exactly the zero case — so bailing out there
        /// hides the warning from the only person who needs it. Answering here keeps Unity and Godot
        /// from drifting, and puts the decision under a .NET test.
        /// </summary>
        public bool ShouldDrawContactList(int copiedDynCount, int copiedStaticCount)
            => copiedDynCount + copiedStaticCount > 0 || ContactsTruncated || StaticContactsTruncated;

        /// <summary>
        /// Whether a per-body contact list needs the "you may not be seeing everything" note. Same
        /// condition as the dropped tail: the list is assembled from the copied arrays, so any
        /// truncation makes a per-body tally a lower bound rather than a count.
        /// </summary>
        public bool ContactListMayBeIncomplete => ContactsTruncated || StaticContactsTruncated;
    }
}
