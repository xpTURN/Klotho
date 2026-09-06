using System;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// Per-instance sizes for the navigation working set: the buffers each navigation type
    /// allocates once in its constructor, and the loop budgets that bound them.
    ///
    /// <para><b>These change simulation output.</b> Every value here can alter where an agent ends
    /// up — a smaller neighbour cap drops avoidance lines, a smaller iteration budget abandons a
    /// search, a smaller corridor cap replans more often. They are therefore <b>build identity, not
    /// per-peer preference</b>: lockstep peers must construct their navigation with the same tuning,
    /// and a replay must be played back against the tuning it was recorded with.</para>
    ///
    /// <para><b>Immutable by construction.</b> The buffers are sized when the owning type is built,
    /// so a value that could change afterwards would leave the caps and the buffers disagreeing.
    /// Build one of these, validate it, hand it to the constructors, and keep it.</para>
    ///
    /// <para><b><see cref="Default"/> is the way in.</b> A struct has an implicit parameterless
    /// constructor, so <c>new FPNavTuning()</c> and <c>default</c> are all zeros — invalid, and
    /// <see cref="Validate"/> says so rather than letting a zero-sized buffer through. Start from
    /// <see cref="Default"/>, or name the values you want:
    /// <c>new FPNavTuning(maxAgents: 128, corridorCap: 32)</c>.</para>
    /// </summary>
    public readonly struct FPNavTuning : IEquatable<FPNavTuning>
    {
        /// <summary>
        /// Upper bound for the position-correction pass. Agents past this are still moved, but not
        /// collision-resolved. <b>The pass is O(iterations · n²)</b>, so raising this is a cost
        /// cliff rather than a free knob — at the default it is 4 · 64².
        /// </summary>
        public int MaxAgents { get; }

        /// <summary>Relaxation passes the position-correction loop makes per tick.</summary>
        public int CollisionResolveIterations { get; }

        /// <summary>
        /// Frontier bound for the graph-local obstacle BFS. The candidate buffer is three times
        /// this (max three boundary edges per triangle), which is what lets candidate collection
        /// never truncate; the two move together and only this one is named.
        /// </summary>
        public int BfsFrontierCap { get; }

        /// <summary>A* expansion budget for one <c>FindPath</c>.</summary>
        public int MaxIterations { get; }

        /// <summary>
        /// Portal buffer for the funnel. A corridor of length <c>L</c> wants <c>L + 1</c> portals
        /// (start + shared edges + end); below that the funnel drops the corridor tail and appends
        /// the end portal, which is the shipped behaviour at the defaults (128 portals against a
        /// 128 corridor). Raise this with <see cref="CorridorCap"/> if you want the whole chain.
        /// </summary>
        public int MaxPortals { get; }

        /// <summary>
        /// Waypoint buffer for the string pull. Same truncating relationship to
        /// <see cref="MaxPortals"/>, and also already truncating at the defaults (64 against 128).
        /// </summary>
        public int MaxWaypoints { get; }

        /// <summary>Agents ORCA considers per solve, nearest first.</summary>
        public int MaxNeighbors { get; }

        /// <summary>
        /// Total ORCA half-plane budget. <see cref="MaxNeighbors"/> of it is reserved for agent
        /// lines, so obstacle lines get <see cref="MaxObstLines"/> and can never starve avoidance.
        /// </summary>
        public int MaxOrcaLines { get; }

        /// <summary>BFS bound for one <c>MoveAlongSurface</c> step.</summary>
        public int MoveMaxQueue { get; }

        /// <summary>
        /// Triangles kept from a completed search. This is a clamp on the result, not a search
        /// budget: the A* cost is unchanged, and what a smaller cap buys is a smaller copy and more
        /// frequent replans. <b>It does not shrink the frame reservation</b> — the corridor lives in
        /// <c>NavAgentComponent</c>'s fixed buffer, whose size is a compile-time constant.
        /// </summary>
        public int CorridorCap { get; }

        /// <summary>Obstacle line budget — derived, never set (see <see cref="MaxOrcaLines"/>).</summary>
        public int MaxObstLines => MaxOrcaLines - MaxNeighbors;

        /// <summary>
        /// The largest corridor cap the storage can hold: the smaller of the two compile-time
        /// ceilings, so a cap validated against it fits both the search buffer and the component.
        /// </summary>
        public static int CorridorCeiling =>
            FPNavMeshPathfinder.MAX_CORRIDOR < NavAgentComponent.MAX_CORRIDOR
                ? FPNavMeshPathfinder.MAX_CORRIDOR
                : NavAgentComponent.MAX_CORRIDOR;

        /// <summary>
        /// The shipped values — what every navigation type uses when handed no tuning.
        ///
        /// <para>The named argument is load-bearing: with it omitted, <c>new FPNavTuning()</c> binds
        /// to the struct's implicit parameterless constructor (which wins overload resolution over a
        /// constructor whose parameters are all optional) and this would be a zeroed tuning.</para>
        /// </summary>
        public static readonly FPNavTuning Default = new FPNavTuning(maxAgents: FPNavAgentSystem.MAX_AGENTS);

        public FPNavTuning(
            int maxAgents = FPNavAgentSystem.MAX_AGENTS,
            int collisionResolveIterations = 4,
            int bfsFrontierCap = 256,
            int maxIterations = FPNavMeshPathfinder.MAX_ITERATIONS,
            int maxPortals = FPNavMeshFunnel.MAX_PORTALS,
            int maxWaypoints = FPNavMeshFunnel.MAX_WAYPOINTS,
            int maxNeighbors = FPNavAvoidance.MAX_NEIGHBORS,
            int maxOrcaLines = FPNavAvoidance.MAX_ORCA_LINES,
            int moveMaxQueue = 48,
            int corridorCap = NavAgentComponent.MAX_CORRIDOR)
        {
            MaxAgents = maxAgents;
            CollisionResolveIterations = collisionResolveIterations;
            BfsFrontierCap = bfsFrontierCap;
            MaxIterations = maxIterations;
            MaxPortals = maxPortals;
            MaxWaypoints = maxWaypoints;
            MaxNeighbors = maxNeighbors;
            MaxOrcaLines = maxOrcaLines;
            MoveMaxQueue = moveMaxQueue;
            CorridorCap = corridorCap;
        }

        // ── Identity: one field list, two very different consumers ──────────────────────
        //
        // Equals and Digest must count the SAME ten knobs, so the list lives once, here. What they
        // must NOT share is the fold: GetHashCode only has to hold within a process, while Digest
        // is compared ACROSS processes and builds. Deriving one from the other would make peers
        // disagree every run.
        //
        // MaxObstLines is deliberately absent: it is MaxOrcaLines - MaxNeighbors, so counting it
        // would count the same information twice and would move the digest if that derivation ever
        // changed without any knob changing.

        /// <summary>Equal knob for knob. Adding a knob means adding it here AND to <see cref="Digest"/>.</summary>
        public bool Equals(FPNavTuning other) =>
            MaxAgents == other.MaxAgents
            && CollisionResolveIterations == other.CollisionResolveIterations
            && BfsFrontierCap == other.BfsFrontierCap
            && MaxIterations == other.MaxIterations
            && MaxPortals == other.MaxPortals
            && MaxWaypoints == other.MaxWaypoints
            && MaxNeighbors == other.MaxNeighbors
            && MaxOrcaLines == other.MaxOrcaLines
            && MoveMaxQueue == other.MoveMaxQueue
            && CorridorCap == other.CorridorCap;

        public override bool Equals(object obj) => obj is FPNavTuning other && Equals(other);

        /// <summary>
        /// In-process hashing only. <b>Never build <see cref="Digest"/> on this</b> — the runtime is
        /// free to randomise hash seeds per process, and a fingerprint that moves every run would
        /// make two peers disagree at every handshake.
        /// </summary>
        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + MaxAgents;
                h = h * 31 + CollisionResolveIterations;
                h = h * 31 + BfsFrontierCap;
                h = h * 31 + MaxIterations;
                h = h * 31 + MaxPortals;
                h = h * 31 + MaxWaypoints;
                h = h * 31 + MaxNeighbors;
                h = h * 31 + MaxOrcaLines;
                h = h * 31 + MoveMaxQueue;
                h = h * 31 + CorridorCap;
                return h;
            }
        }

        public static bool operator ==(FPNavTuning a, FPNavTuning b) => a.Equals(b);
        public static bool operator !=(FPNavTuning a, FPNavTuning b) => !a.Equals(b);

        /// <summary>
        /// A build-comparable fold of the ten knobs, <b>0 for <see cref="Default"/> by
        /// construction</b>.
        ///
        /// <para><b>Why Default folds to zero.</b> This is XORed into the navigation fingerprint,
        /// which a replay records and the Ready exchange compares. If the default tuning produced a
        /// non-zero term, adding this feature would move every existing fingerprint and every
        /// replay recorded before it would be refused — for a change that alters no behaviour.
        /// Normalising against <see cref="Default"/> makes the common path bit-identical while
        /// keeping every distinction: two defaults agree, a default and a custom differ, equal
        /// customs agree, different customs differ.</para>
        ///
        /// <para>Fixed multipliers, not <c>HashCode.Combine</c>: this value crosses processes.</para>
        /// </summary>
        public long Digest => unchecked(RawFold() ^ DefaultRawFold);

        private long RawFold()
        {
            unchecked
            {
                const long Prime = unchecked((long)0x100000001B3UL);   // FNV-1a 64 prime
                long h = unchecked((long)0xCBF29CE484222325UL);        // FNV-1a 64 offset basis
                h = (h ^ MaxAgents) * Prime;
                h = (h ^ CollisionResolveIterations) * Prime;
                h = (h ^ BfsFrontierCap) * Prime;
                h = (h ^ MaxIterations) * Prime;
                h = (h ^ MaxPortals) * Prime;
                h = (h ^ MaxWaypoints) * Prime;
                h = (h ^ MaxNeighbors) * Prime;
                h = (h ^ MaxOrcaLines) * Prime;
                h = (h ^ MoveMaxQueue) * Prime;
                h = (h ^ CorridorCap) * Prime;

                // Final avalanche (splitmix64). Without it a tuning that differs from another only
                // in the LAST knob folded produces a digest whose difference is still visibly
                // structured — the value works, but it does not look like it mixes, and the next
                // reader has to re-derive that it is safe.
                h ^= (long)((ulong)h >> 33);
                h *= unchecked((long)0xFF51AFD7ED558CCDUL);
                h ^= (long)((ulong)h >> 33);
                h *= unchecked((long)0xC4CEB9FE1A85EC53UL);
                h ^= (long)((ulong)h >> 33);
                return h;
            }
        }

        private static readonly long DefaultRawFold = Default.RawFold();

        /// <summary>
        /// Throws <see cref="ArgumentException"/> on a tuning that cannot be honoured. Every
        /// navigation constructor calls this, so an unusable value fails where it was built rather
        /// than as a divergence later.
        ///
        /// <para><b>What is deliberately not checked</b>: the portal and waypoint budgets against
        /// <see cref="CorridorCap"/>. Those relationships truncate rather than break, and the
        /// shipped defaults already sit on the truncating side — a check would reject
        /// <see cref="Default"/>. They are documented on the properties instead.</para>
        /// </summary>
        public void Validate()
        {
            Positive(MaxAgents, nameof(MaxAgents));
            Positive(CollisionResolveIterations, nameof(CollisionResolveIterations));
            Positive(BfsFrontierCap, nameof(BfsFrontierCap));
            Positive(MaxIterations, nameof(MaxIterations));
            Positive(MaxPortals, nameof(MaxPortals));
            Positive(MaxWaypoints, nameof(MaxWaypoints));
            Positive(MaxNeighbors, nameof(MaxNeighbors));
            Positive(MaxOrcaLines, nameof(MaxOrcaLines));
            Positive(MoveMaxQueue, nameof(MoveMaxQueue));
            Positive(CorridorCap, nameof(CorridorCap));

            if (MaxNeighbors >= MaxOrcaLines)
                throw new ArgumentException(
                    $"FPNavTuning: MaxNeighbors ({MaxNeighbors}) must be smaller than MaxOrcaLines " +
                    $"({MaxOrcaLines}) — the difference is the obstacle line budget, and at zero the " +
                    $"navmesh boundary stops constraining agents at all.");

            if (CorridorCap > CorridorCeiling)
                throw new ArgumentException(
                    $"FPNavTuning: CorridorCap ({CorridorCap}) exceeds the storage ceiling " +
                    $"({CorridorCeiling}) — the corridor lives in a fixed buffer sized at compile time. " +
                    $"Lower the cap, or raise both NavAgentComponent.MAX_CORRIDOR and " +
                    $"FPNavMeshPathfinder.MAX_CORRIDOR and rebuild every binary that talks to another.");
        }

        private static void Positive(int value, string name)
        {
            if (value > 0) return;
            throw new ArgumentException(
                $"FPNavTuning: {name} is {value}; every cap must be positive. A zeroed tuning is " +
                $"usually 'new FPNavTuning()' or 'default' — start from FPNavTuning.Default instead.");
        }
    }
}
