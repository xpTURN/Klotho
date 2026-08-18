using xpTURN.Klotho.Logging;
using xpTURN.Klotho.ECS;

namespace xpTURN.Klotho.Deterministic.Navigation
{
    /// <summary>
    /// The derivation every consumer of a placement table needs: collect it, filter it to a tick,
    /// order it canonically, and audit the two things that make that ordering safe.
    ///
    /// <para><b>Why this is shared rather than duplicated.</b> Two callers ask the same question of
    /// the same table — <see cref="FPNavMeshRebakeDriver"/> asks "what is in the mesh at tick T", and
    /// a command handler asks "what will be in it when this placement lands". A game that writes the
    /// second one itself has to re-derive the tick window, re-sort by the same key, and re-implement
    /// both audits; and the audits are the load-bearing part, because getting either wrong is a
    /// DESYNC that no hash sees. Keeping one copy is what makes the game unable to get it wrong.</para>
    ///
    /// <para><b>Stateless by construction.</b> Every buffer is the caller's. That is what lets the
    /// driver keep its per-tick scratch and a validator keep its own without either being able to
    /// disturb the other — a live sliced rebake and a synchronous trial rebake must never share
    /// working storage (the rebake buffer pool refuses overlapping use).</para>
    /// </summary>
    internal static class FPNavMeshPlacementTableOps
    {
        /// <summary>
        /// Fills <paramref name="table"/> from the game and reports a truncation. Returns the count,
        /// clamped to what the buffer holds.
        ///
        /// <para><b>Audit 8.</b> A truncated rebake input is the quietest failure in this pipeline:
        /// the dropped entries are missing from the mesh while the state hash still matches on every
        /// peer, so nothing else would ask why. It is loud and it CONTINUES — refusing to install
        /// would leave a STALE mesh, which is not better than a short one, and the actual repair is
        /// on the game's side (size Capacity from the storage bound).</para>
        /// </summary>
        internal static int Collect(
            IFPNavMeshPlacementSource source, ref Frame frame, FPNavMeshTimedPlacement[] table)
        {
            int count = source.Collect(ref frame, table, out int eligible);

            if (eligible > count)
            {
                frame.Logger?.KError(
                    $"[FPNavMeshRebakeDriver] placement table truncated at tick={frame.Tick}: the " +
                    $"frame holds {eligible} but Capacity fits {table.Length}. The rest are missing " +
                    $"from the rebake input, so this peer's navmesh disagrees with everyone else's " +
                    $"while the STATE hash still matches. Size IFPNavMeshPlacementSource.Capacity " +
                    $"from the STORAGE bound — tombstones included — never from the policy bound.");
            }
            if (count > table.Length)
                count = table.Length;
            return count;
        }

        /// <summary>
        /// Whether <paramref name="tick"/> is a boundary, and which tick the next one is. Derived from
        /// the table rather than asked of the game, because two predicates written by two people are
        /// two chances to disagree about when a swap happens.
        /// </summary>
        internal static void DeriveBoundaries(
            FPNavMeshTimedPlacement[] table, int count, int tick,
            out bool isBoundary, out int nextBoundary)
        {
            isBoundary = false;
            nextBoundary = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                ref readonly var p = ref table[i];
                if (p.EffectiveTick == tick || p.RemovalEffectiveTick == tick)
                    isBoundary = true;
                if (p.EffectiveTick > tick && p.EffectiveTick < nextBoundary)
                    nextBoundary = p.EffectiveTick;
                if (p.RemovalEffectiveTick > tick && p.RemovalEffectiveTick < nextBoundary)
                    nextBoundary = p.RemovalEffectiveTick;
            }
        }

        /// <summary>
        /// Identifies the active set at a tick. Order-independent by construction: it sums per-entry
        /// terms, so it does not depend on how the game happened to enumerate.
        ///
        /// <para>A DIGEST, and used only to answer "did it change". It is a sum, so collisions are
        /// easy to construct, and that is tolerable exactly because a collision costs one skipped
        /// comparison that the next tick repeats. It must never be used to CHOOSE a mesh.</para>
        /// </summary>
        internal static long Digest(FPNavMeshTimedPlacement[] table, int count, int atTick)
        {
            long tag = 0;
            int live = 0;
            for (int i = 0; i < count; i++)
            {
                ref readonly var p = ref table[i];
                if (atTick < p.EffectiveTick || atTick >= p.RemovalEffectiveTick)
                    continue;
                live++;
                unchecked
                {
                    long term = p.Sequence * 0x100000001B3L;
                    term ^= p.Placement.ShapeId * 31L + p.Placement.Orientation;
                    term ^= p.Placement.CentreX.RawValue * 17L
                          + p.Placement.CentreZ.RawValue * 13L + p.Placement.Y.RawValue;
                    tag += term;
                }
            }
            unchecked { return tag * 31L + live; }
        }

        /// <summary>
        /// Fills <paramref name="active"/> with the set live at <paramref name="atTick"/>, sorted by
        /// <c>Sequence</c>, and returns the count. This is the rebake input and the cache key.
        ///
        /// <para><paramref name="skipSequence"/> excludes one entry by its sequence number, for a
        /// caller validating a REMOVAL: the target is still inside its own tick window at the moment
        /// the command is handled, so the window alone will not drop it.</para>
        ///
        /// <para><b>Audit 9.</b> <c>Array.Sort</c> is introsort, which is NOT stable. That is only
        /// fine because the keys are unique, so uniqueness is load-bearing rather than incidental —
        /// with a tie the output depends on the pre-sort order, which is the game's enumeration order,
        /// and the rebake input stops being a function of the frame. Reported loudly because the
        /// damage is silent: every peer sorts its own order, the navmeshes diverge, and the state hash
        /// still matches because the components are identical.</para>
        /// </summary>
        /// <param name="duplicateSequence">
        /// True when two live entries shared a sequence number. A caller that cannot tolerate an
        /// ambiguous <paramref name="skipSequence"/> should refuse rather than proceed — with a
        /// duplicate, "the one to exclude" is not a well-defined entry.
        /// </param>
        internal static int BuildActiveSet(
            ref Frame frame, FPNavMeshTimedPlacement[] table, int count, int atTick,
            FPBuildingPlacement[] active, int[] sortKeys, int skipSequence, out bool duplicateSequence)
        {
            int n = 0;
            int skipped = 0;
            for (int i = 0; i < count; i++)
            {
                ref readonly var p = ref table[i];
                if (atTick < p.EffectiveTick || atTick >= p.RemovalEffectiveTick)
                    continue;
                if (skipSequence >= 0 && p.Sequence == skipSequence)
                {
                    skipped++;
                    continue;
                }
                sortKeys[n] = p.Sequence;
                active[n++] = p.Placement;
            }

            System.Array.Sort(sortKeys, active, 0, n);

            // A duplicate that IS the skip target leaves no trace in the surviving set — both copies
            // were excluded, so the scan below finds nothing and the caller would be told the removal
            // is fine while it actually deleted two entries. Counting the exclusions is what makes
            // that case visible, and it is the case a removal command reaches first.
            duplicateSequence = skipped > 1;

            for (int i = 1; i < n && !duplicateSequence; i++)
            {
                if (sortKeys[i] != sortKeys[i - 1])
                    continue;
                duplicateSequence = true;
            }

            if (duplicateSequence)
            {
                frame.Logger?.KError(
                    $"[FPNavMeshRebakeDriver] duplicate Sequence at tick={frame.Tick}. " +
                    $"The sort is not stable, so with a tie the rebake input depends on enumeration " +
                    $"order and stops being a function of the frame — every peer sorts its own order, " +
                    $"the navmeshes diverge, and the state hash still matches. Number placements so " +
                    $"that no two live at once share a Sequence (max + 1 works; per-owner numbering " +
                    $"and reusing a freed slot's number do not).");
            }

            return n;
        }

        /// <summary>
        /// One past the highest <c>Sequence</c> in the WHOLE table — not the active subset.
        ///
        /// <para>The asymmetry is deliberate. Narrowing this to the active set would hand out a number
        /// a pending or tombstoned entry still holds, and a duplicate makes the unstable sort above
        /// fall back to enumeration order — at which point the rebake input stops being a function of
        /// the frame and peers carve different navmeshes from identical components.</para>
        /// </summary>
        internal static int NextSequence(FPNavMeshTimedPlacement[] table, int count)
        {
            int max = -1;
            for (int i = 0; i < count; i++)
                if (table[i].Sequence > max)
                    max = table[i].Sequence;
            return max + 1;
        }
    }
}
