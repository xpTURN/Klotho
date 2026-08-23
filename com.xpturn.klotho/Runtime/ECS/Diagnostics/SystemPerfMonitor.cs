using System;
using System.Diagnostics;
using System.Text;

namespace xpTURN.Klotho.ECS.Diagnostics
{
    // Per-system execution profiler: accumulates execution time and per-tick heap allocation for each
    // registered system. Off by default — the monitor is only driven when SystemRunner has it enabled
    // via the config gate. Read-only with respect to simulation state: it records only into its own
    // struct array, so timing and allocation never enter the frame, the state hash, or the wire, and
    // the profiler is determinism-neutral.
    public sealed class SystemPerfMonitor
    {
        // Value-type stat so recording allocates nothing per call. The profile is dumped once, at
        // engine Stop, and reports derived avg (SumTicks / steady executions) and peak (the worst single
        // execution, including rollback resim ticks). No "last" sample is kept: a single value taken
        // just before teardown carries no meaning for a once-at-Stop dump.
        //
        // Warmup segregation: the first WarmupExecutions executions of each system fold into the
        // Warmup* buckets instead of Sum*/Max*, so the steady metrics exclude first-tick JIT, lazy
        // init, and pool/list growth. This keeps "steady sum mem == 0 ⇒ GC-clean" a clean signal for
        // the allocation-regression use case, while the Warmup* fields keep early allocation visible
        // (not discarded). WarmupExecutions == 0 (the default) means no segregation — all executions
        // fold into Sum*/Max*, identical to a plain profiler.
        public struct SystemPerfStat
        {
            public string Name;          // system type name, set once at bind
            public string Group;         // registration-time group label, or null; DIAGNOSTIC ONLY —
                                         // never a sort key and never folded into any fingerprint
            public long   UpdateCount;   // total executions (incl. warmup + predicted/resim ticks)
            public long   SumTicks;      // steady (post-warmup) accumulated Stopwatch ticks
            public long   MaxTicks;      // steady worst single-execution Stopwatch ticks (peak)
            public long   SumMemory;     // steady accumulated allocation bytes (should stop growing)
            public long   MaxMemory;     // steady worst single-execution allocation bytes (peak)
            public long   WarmupTicks;   // Stopwatch ticks accumulated during the warmup window
            public long   WarmupMemory;  // allocation bytes accumulated during the warmup window
        }

        // Must match SystemRunner's implicit label for the builtin passes.
        internal const string BuiltinGroup = "(builtin)";

        private SystemPerfStat[] _stats = Array.Empty<SystemPerfStat>();
        private int _count;
        private int _warmup;   // executions/system folded into Warmup* before steady accrual (0 = off)

        // First N executions per system counted as warmup (excluded from steady avg/peak/sum).
        // Set before/at enable; changing it mid-run only affects executions recorded afterward.
        public int WarmupExecutions { get => _warmup; set => _warmup = value < 0 ? 0 : value; }

        // Read accessor for inspection and tests. Sliced to _count because the backing array's
        // capacity may exceed the number of currently bound systems.
        public ReadOnlySpan<SystemPerfStat> Stats => _stats.AsSpan(0, _count);

        // (Re)bind to the runner's sorted system list. Names set once; counters reset.
        // Called on first enable and whenever SystemRunner re-sorts (_dirty).
        // NOTE: a rebind DISCARDS previously accumulated stats — a mid-array insert would
        // misalign old counters anyway, so reset is the safe (and documented) choice.
        // (A double EnablePerfMonitor also rebinds -> resets; this cannot happen in a session
        // since ArmSystemPerfMonitor runs from exactly one Initialize body.)
        // <paramref name="groups"/> is a PARALLEL array (same length/order as systemNames), not an
        // encoding inside the name: the group is kept as its own field so the summary rows below can
        // aggregate without parsing display strings back apart. Passing default() = no groups.
        public void Bind(ReadOnlySpan<string> systemNames, ReadOnlySpan<string> groups = default)
        {
            if (_stats.Length < systemNames.Length)
                _stats = new SystemPerfStat[systemNames.Length];
            for (int i = 0; i < systemNames.Length; i++)
                _stats[i] = new SystemPerfStat
                {
                    Name = systemNames[i],
                    Group = i < groups.Length ? groups[i] : null,
                };
            _count = systemNames.Length;
        }

        // Hot path: called once per system per execution. No allocation.
        // The first _warmup executions fold into the Warmup* buckets; the rest into steady Sum*/Max*.
        public void Record(int index, long ticks, long memory)
        {
            ref var s = ref _stats[index];
            s.UpdateCount++;
            if (s.UpdateCount <= _warmup)
            {
                s.WarmupTicks += ticks;
                s.WarmupMemory += memory;
                return;
            }
            s.SumTicks += ticks;   if (ticks > s.MaxTicks)  s.MaxTicks = ticks;
            s.SumMemory += memory; if (memory > s.MaxMemory) s.MaxMemory = memory;
        }

        // No Reset() — Bind resets counters, and there is no other reset path (the profile is dumped once).

        // Only allocating path (like ComponentMemoryReport.ToText). Called at dump.
        // Name column widened to the longest system name (ComponentMemoryReport.ToText precedent:
        // pre-pass nameWidth + PadRight/PadLeft) so numeric columns stay aligned for any name length.
        public string ToText()
        {
            double tickToMs = 1000.0 / Stopwatch.Frequency;
            double tickToUs = 1_000_000.0 / Stopwatch.Frequency;

            // Rows that never executed are omitted (see the report loop below), so they must not widen
            // the name column either.
            int nameWidth = 6; // "system"
            for (int i = 0; i < _count; i++)
            {
                if (_stats[i].UpdateCount == 0) continue;
                int display = DisplayName(in _stats[i]).Length;
                if (display > nameWidth) nameWidth = display;
                // The summary rows below print "= <group>", so they can be the widest line.
                int groupRow = (_stats[i].Group?.Length ?? 0) + 2;
                if (groupRow > nameWidth) nameWidth = groupRow;
            }

            var sb = new StringBuilder();
            if (_warmup > 0)
                sb.Append("(warmup: first ").Append(_warmup)
                  .Append(" executions/system excluded from avg/peak/sum; see warmup mem)").AppendLine();
            // avg/peak/sum/mem are steady (post-warmup); updates is the total execution count.
            sb.Append("system".PadRight(nameWidth))
              .Append("updates".PadLeft(10))
              .Append("avg us".PadLeft(11))
              .Append("peak us".PadLeft(11))
              .Append("sum ms".PadLeft(11))
              .Append("peak mem".PadLeft(12))
              .Append("sum mem".PadLeft(12))
              .Append("warmup mem".PadLeft(13))
              .AppendLine();
            sb.Append('-', nameWidth + 80).AppendLine();   // 80 = 10+11+11+11+12+12+13
            for (int i = 0; i < _count; i++)
            {
                ref var s = ref _stats[i];
                // Never executed -> nothing to report. Keeps the builtin [KlothoCleanup] slots (and any
                // registered-but-never-run system) out of the report instead of printing zero rows: the
                // slot indices are fixed consts, so they cannot be bound conditionally.
                if (s.UpdateCount == 0) continue;

                long warmupCount = s.UpdateCount < _warmup ? s.UpdateCount : _warmup;
                long steady = s.UpdateCount - warmupCount;
                double avgUs = steady > 0 ? (double)s.SumTicks / steady * tickToUs : 0.0;
                sb.Append(DisplayName(in s).PadRight(nameWidth))
                  .Append(s.UpdateCount.ToString().PadLeft(10))
                  .Append(avgUs.ToString("F2").PadLeft(11))
                  .Append((s.MaxTicks * tickToUs).ToString("F2").PadLeft(11))
                  .Append((s.SumTicks * tickToMs).ToString("F3").PadLeft(11))
                  .Append(s.MaxMemory.ToString().PadLeft(12))
                  .Append(s.SumMemory.ToString().PadLeft(12))
                  .Append(s.WarmupMemory.ToString().PadLeft(13))
                  .AppendLine();
            }

            AppendGroupTotals(sb, nameWidth, tickToMs, tickToUs);
            return sb.ToString();
        }

        // Display name for a row: the group is PREPENDED for display only — the stat's Name field stays
        // the bare type name so name-based lookups keep working. Two systems can share a short type name
        // (the engine and a game both ship a CombatSystem), and without this the two rows would be
        // indistinguishable. Builtin rows keep their exact names, and an unlabelled row is unchanged, so
        // a report from a project that uses no labels is byte-identical to before.
        private static string DisplayName(in SystemPerfStat s)
            => s.Group == null || s.Group == BuiltinGroup ? s.Name : s.Group + "/" + s.Name;

        // Group totals, appended AFTER the per-system rows so the body keeps execution order (that
        // ordering is the point of the report — the rows are a pipeline, not a ranking).
        //
        // Only additive columns are filled. peak us / peak mem are left blank on purpose: each system's
        // peak happened on its OWN tick, so neither max nor sum of them is "what this group cost in one
        // tick", and per-tick group figures are not recoverable from these aggregates. updates is blank
        // for the same reason (summing it would just report systems × ticks). avg us is recomputed from
        // the group's steady sums rather than averaged from per-system averages.
        //
        // Caveat printed with the totals: command systems (ICommandSystem) are not measured at all —
        // instrumentation lives inside RunUpdateSystems — so these totals are not the whole tick.
        private void AppendGroupTotals(StringBuilder sb, int nameWidth, double tickToMs, double tickToUs)
        {
            // Emitted only when the GAME labelled something. The builtin passes carry an implicit
            // group so that totals add up once they exist, but on their own they must not conjure a
            // totals section into a report that had none — an unlabelled project's output stays
            // byte-identical to before this feature.
            bool anyGameGroup = false;
            for (int i = 0; i < _count; i++)
                if (_stats[i].UpdateCount > 0 && _stats[i].Group != null && _stats[i].Group != BuiltinGroup)
                { anyGameGroup = true; break; }
            if (!anyGameGroup) return;

            sb.Append('-', nameWidth + 80).AppendLine();

            // O(groups × rows), and groups are few; this runs once at dump.
            for (int i = 0; i < _count; i++)
            {
                string group = _stats[i].Group;
                if (group == null || _stats[i].UpdateCount == 0) continue;

                bool firstOfGroup = true;
                for (int j = 0; j < i; j++)
                    if (_stats[j].UpdateCount > 0 && _stats[j].Group == group) { firstOfGroup = false; break; }
                if (!firstOfGroup) continue;

                long sumTicks = 0, sumMem = 0, warmupMem = 0, steadyTotal = 0;
                for (int j = 0; j < _count; j++)
                {
                    ref var g = ref _stats[j];
                    if (g.UpdateCount == 0 || g.Group != group) continue;
                    long warmupCount = g.UpdateCount < _warmup ? g.UpdateCount : _warmup;
                    steadyTotal += g.UpdateCount - warmupCount;
                    sumTicks    += g.SumTicks;
                    sumMem      += g.SumMemory;
                    warmupMem   += g.WarmupMemory;
                }

                double avgUs = steadyTotal > 0 ? (double)sumTicks / steadyTotal * tickToUs : 0.0;
                sb.Append(("= " + group).PadRight(nameWidth))
                  .Append("".PadLeft(10))                                    // updates: not additive
                  .Append(avgUs.ToString("F2").PadLeft(11))
                  .Append("".PadLeft(11))                                    // peak us: not derivable
                  .Append((sumTicks * tickToMs).ToString("F3").PadLeft(11))
                  .Append("".PadLeft(12))                                    // peak mem: not derivable
                  .Append(sumMem.ToString().PadLeft(12))
                  .Append(warmupMem.ToString().PadLeft(13))
                  .AppendLine();
            }

            sb.Append("(group totals: additive columns only — peak/updates are per-system and do not aggregate. ")
              .Append("Command systems are not measured, so these totals are not the whole tick.)")
              .AppendLine();
        }
    }
}
