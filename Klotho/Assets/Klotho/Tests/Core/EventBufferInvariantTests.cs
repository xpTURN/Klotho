using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Static invariant: the number of <c>_eventBuffer.AddEvent</c> call sites in the KlothoEngine
    /// partial source files matches the ledger documented alongside this test. Each call site has a
    /// known ClearTick predecessor (immediate or outer-loop). A new call site must be added to the
    /// ledger and reviewed for its ClearTick strategy before this constant is bumped.
    /// </summary>
    [TestFixture]
    public class EventBufferInvariantTests
    {
        private const int ExpectedAddEventCallSites = 15;

        private static readonly Regex AddEventCallRegex =
            new Regex(@"_eventBuffer\.AddEvent\b", RegexOptions.Compiled);

        [Test]
        public void AddEvent_CallSiteCount_MatchesLedger()
        {
            string engineDir = Path.Combine(
                Application.dataPath, "Klotho", "Runtime", "Core", "Engine");
            Assert.IsTrue(Directory.Exists(engineDir),
                $"Engine source directory not found: {engineDir}");

            int total = 0;
            var perFile = new SortedDictionary<string, int>();
            foreach (string path in Directory.EnumerateFiles(engineDir, "*.cs", SearchOption.AllDirectories))
            {
                string content = File.ReadAllText(path);
                int matches = AddEventCallRegex.Matches(content).Count;
                if (matches > 0)
                {
                    total += matches;
                    perFile[Path.GetFileName(path)] = matches;
                }
            }

            if (total == ExpectedAddEventCallSites)
                return;

            var summary = new StringBuilder();
            summary.Append("_eventBuffer.AddEvent call sites = ").Append(total)
                .Append(" (expected ").Append(ExpectedAddEventCallSites).Append("). ");
            summary.Append("Per-file: ");
            bool first = true;
            foreach (var kv in perFile)
            {
                if (!first) summary.Append(", ");
                first = false;
                summary.Append(kv.Key).Append('=').Append(kv.Value);
            }
            summary.Append(". If a call site was added or removed, update the ledger entry that documents")
                .Append(" the ClearTick predecessor for the new path before bumping ExpectedAddEventCallSites.");

            Assert.Fail(summary.ToString());
        }

        // All OnSyncedEvent dispatches must route through DispatchSyncedEventsForTick — the
        // single batch helper that owns the _syncedDispatchHighWaterMark guard. Direct
        // _dispatcher.Dispatch(OnSyncedEvent, ...) elsewhere bypasses the guard; direct delegate
        // invocation (OnSyncedEvent?.Invoke / .Invoke / OnSyncedEvent(...)) does the same.
        private static readonly Regex DispatcherDispatchOnSyncedRegex =
            new Regex(@"_dispatcher\.Dispatch\(OnSyncedEvent", RegexOptions.Compiled);

        private static readonly Regex DirectInvokeOnSyncedRegex =
            new Regex(@"OnSyncedEvent\s*(?:\?\s*\.\s*Invoke|\.\s*Invoke|\()", RegexOptions.Compiled);

        [Test]
        public void DirectOnSyncedDispatch_NoCallSitesOutsideHelper()
        {
            string engineDir = Path.Combine(
                Application.dataPath, "Klotho", "Runtime", "Core", "Engine");
            Assert.IsTrue(Directory.Exists(engineDir),
                $"Engine source directory not found: {engineDir}");

            int dispatcherSiteCount = 0;
            int directInvokeSiteCount = 0;
            var dispatcherPerFile = new SortedDictionary<string, int>();
            var directPerFile = new SortedDictionary<string, int>();
            foreach (string path in Directory.EnumerateFiles(engineDir, "*.cs", SearchOption.AllDirectories))
            {
                string content = File.ReadAllText(path);
                int dispatcherMatches = DispatcherDispatchOnSyncedRegex.Matches(content).Count;
                int directMatches = DirectInvokeOnSyncedRegex.Matches(content).Count;
                if (dispatcherMatches > 0)
                {
                    dispatcherSiteCount += dispatcherMatches;
                    dispatcherPerFile[Path.GetFileName(path)] = dispatcherMatches;
                }
                if (directMatches > 0)
                {
                    directInvokeSiteCount += directMatches;
                    directPerFile[Path.GetFileName(path)] = directMatches;
                }
            }

            // Exactly one dispatcher-routed call is expected, inside DispatchSyncedEventsForTick.
            if (dispatcherSiteCount != 1)
            {
                var sb = new StringBuilder();
                sb.Append("_dispatcher.Dispatch(OnSyncedEvent count = ").Append(dispatcherSiteCount)
                  .Append(" (expected 1, inside DispatchSyncedEventsForTick). Per-file: ");
                bool first = true;
                foreach (var kv in dispatcherPerFile)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(kv.Key).Append('=').Append(kv.Value);
                }
                Assert.Fail(sb.ToString());
            }

            // Direct delegate invocation (any form) must be 0 — all paths go through the helper.
            if (directInvokeSiteCount != 0)
            {
                var sb = new StringBuilder();
                sb.Append("Direct OnSyncedEvent invoke count = ").Append(directInvokeSiteCount)
                  .Append(" (expected 0, route through DispatchSyncedEventsForTick). Per-file: ");
                bool first = true;
                foreach (var kv in directPerFile)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(kv.Key).Append('=').Append(kv.Value);
                }
                Assert.Fail(sb.ToString());
            }
        }
    }
}
