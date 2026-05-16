using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
#if KLOTHO_FAULT_INJECTION
using xpTURN.Klotho.Diagnostics;
#endif

namespace xpTURN.Klotho.Helper.Tests
{
    /// <summary>
    /// Disconnect-length × QuorumMissDropTicks sweep pass-criteria aggregator. Subscribes to
    /// engine events on construction, counts occurrences, and asserts all main + auxiliary
    /// criteria with a single AssertAll call.
    ///   Main (1) WIPED emit 0 — OnPendingWipe (Input + SyncedEvent kinds, both expected 0)
    ///   Main (2) per-peer LastVerifiedTick reaches matchEndTick; chainBreak count unchanged after baseline
    ///   Main (3) chainResumeLatency >= 0 for every spike window
    ///   Main (4) state hash consistent across peers (and unchanged vs pre-disconnect snapshot if captured)
    ///   Aux  (1) OnResyncFailed = 0
    ///   Aux  (2) Host RelaySealDropCount delta = 0 (vs baseline)
    ///   Aux  (3) Host PresumedDrop false-positive ratio (fp / (fp + tp)) delta < 5% (only checked if total delta > 0)
    ///   Aux  (4) Per-peer Engine.ResyncHashMismatchCount delta = 0 (vs baseline)
    /// Aux deltas use CaptureBaseline snapshot — call before triggering disconnect.
    /// </summary>
    internal sealed class SweepPassCriteria : IDisposable
    {
        private readonly KlothoTestHarness _harness;

        private readonly struct Subscription
        {
            public readonly TestPeer Peer;
            public readonly Action<int, int, WipeKind> OnWipe;
            public readonly Action OnResyncFailed;

            public Subscription(TestPeer peer, Action<int, int, WipeKind> onWipe, Action onResyncFailed)
            {
                Peer = peer;
                OnWipe = onWipe;
                OnResyncFailed = onResyncFailed;
            }
        }

        private readonly List<Subscription> _subs = new List<Subscription>();

        private int _wipeCountInput;
        private int _wipeCountSyncedEvent;
        private int _resyncFailedCount;

        private long _preDisconnectHash;
        private bool _baselineCaptured;
#if KLOTHO_FAULT_INJECTION
        private int _preReconnectChainBreakCount;
#endif
        private int _baselineRelaySealDropCount;
        private int _baselinePresumedDropFalsePositiveCount;
        private int _baselinePresumedDropTrueCount;
        private readonly Dictionary<int, int> _baselineResyncHashMismatchByPeer = new Dictionary<int, int>();

        public int WipeCountInput => _wipeCountInput;
        public int WipeCountSyncedEvent => _wipeCountSyncedEvent;
        public int ResyncFailedCount => _resyncFailedCount;

        public SweepPassCriteria(KlothoTestHarness harness)
        {
            _harness = harness;
            foreach (var peer in harness.AllPeers)
            {
                Action<int, int, WipeKind> onWipe = (t, pid, kind) =>
                {
                    if (kind == WipeKind.Input) _wipeCountInput++;
                    else _wipeCountSyncedEvent++;
                };
                Action onResyncFailed = () => _resyncFailedCount++;
                peer.Engine.OnPendingWipe += onWipe;
                peer.Engine.OnResyncFailed += onResyncFailed;
                _subs.Add(new Subscription(peer, onWipe, onResyncFailed));
            }
        }

        /// <summary>
        /// Capture pre-disconnect snapshot (state hash + chainBreak baseline).
        /// Call immediately before triggering the fault injection / disconnect.
        /// </summary>
        public void CaptureBaseline()
        {
            _preDisconnectHash = _harness.CaptureStateHash();
#if KLOTHO_FAULT_INJECTION
            _preReconnectChainBreakCount = RttSpikeMetricsCollector.ChainBreakCount;
#endif
            var hostService = _harness.Host.NetworkService;
            _baselineRelaySealDropCount = hostService.RelaySealDropCount;
            _baselinePresumedDropFalsePositiveCount = hostService.PresumedDropFalsePositiveCount;
            _baselinePresumedDropTrueCount = hostService.PresumedDropTrueCount;

            _baselineResyncHashMismatchByPeer.Clear();
            foreach (var peer in _harness.AllPeers)
                _baselineResyncHashMismatchByPeer[peer.LocalPlayerId] = peer.Engine.ResyncHashMismatchCount;

            _baselineCaptured = true;
        }

        /// <summary>
        /// Asserts all sweep main + auxiliary criteria.
        /// matchEndTick: tick the match should have reached by sweep cell completion (typically the
        ///   AdvanceAllToTick target). A 10-tick tolerance is applied to absorb boundary races.
        /// endMs: wall-clock cut-off for chainResumeLatency windows. Pass 0 to use UtcNow.
        /// </summary>
        public void AssertAll(int matchEndTick, long endMs = 0)
        {
            // Main (1) WIPED emit 0
            Assert.AreEqual(0, _wipeCountInput,
                $"WIPED Input emit expected 0, got {_wipeCountInput}");
            Assert.AreEqual(0, _wipeCountSyncedEvent,
                $"WIPED SyncedEvent emit expected 0, got {_wipeCountSyncedEvent}");

            // Main (2a) per-peer LastVerifiedTick advance
            foreach (var peer in _harness.AllPeers)
            {
                Assert.GreaterOrEqual(peer.Engine.LastVerifiedTick, matchEndTick - 10,
                    $"Peer {peer.LocalPlayerId} LastVerifiedTick {peer.Engine.LastVerifiedTick} < matchEndTick-10 ({matchEndTick - 10})");
            }

#if KLOTHO_FAULT_INJECTION
            // Main (2b) chainBreak count unchanged after baseline
            if (_baselineCaptured)
            {
                int currentChainBreak = RttSpikeMetricsCollector.ChainBreakCount;
                Assert.AreEqual(_preReconnectChainBreakCount, currentChainBreak,
                    $"ChainBreak count increased after baseline: {_preReconnectChainBreakCount} -> {currentChainBreak}");
            }

            // Main (3) chainResumeLatency >= 0 for all spike windows
            long endMsResolved = endMs > 0 ? endMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var latencies = RttSpikeMetricsCollector.GetChainResumeLatencies(endMsResolved);
            for (int i = 0; i < latencies.Length; i++)
            {
                Assert.GreaterOrEqual(latencies[i], 0L,
                    $"chainResumeLatency[{i}] = {latencies[i]} (negative = unresolved spike window)");
            }
#endif

            // Main (4) state hash consistent (and unchanged vs baseline if captured)
            if (_baselineCaptured)
                _harness.AssertPostReconnectHashUnchanged(_preDisconnectHash);
            else
                _harness.AssertStateHashConsistent();

            // Aux (1) OnResyncFailed = 0
            Assert.AreEqual(0, _resyncFailedCount,
                $"OnResyncFailed fired {_resyncFailedCount} time(s) — expected 0");

            var hostService = _harness.Host.NetworkService;

            // Aux (2) Host RelaySealDropCount delta = 0
            int relaySealDelta = hostService.RelaySealDropCount - _baselineRelaySealDropCount;
            Assert.AreEqual(0, relaySealDelta,
                $"Host RelaySealDropCount increased by {relaySealDelta} (late retransmits would have caused divergence without the seal guard)");

            // Aux (3) Host PresumedDrop false-positive ratio delta < 5%
            int fpDelta = hostService.PresumedDropFalsePositiveCount - _baselinePresumedDropFalsePositiveCount;
            int tpDelta = hostService.PresumedDropTrueCount - _baselinePresumedDropTrueCount;
            int totalDelta = fpDelta + tpDelta;
            if (totalDelta > 0)
            {
                float ratio = (float)fpDelta / totalDelta;
                Assert.Less(ratio, 0.05f,
                    $"Host PresumedDrop false-positive ratio {ratio:P1} >= 5% (fp={fpDelta}, tp={tpDelta}) — consider tuning QuorumMissDropTicks N");
            }

            // Aux (4) Per-peer Engine.ResyncHashMismatchCount delta = 0
            foreach (var peer in _harness.AllPeers)
            {
                int baseline = _baselineResyncHashMismatchByPeer.TryGetValue(peer.LocalPlayerId, out var v) ? v : 0;
                int delta = peer.Engine.ResyncHashMismatchCount - baseline;
                Assert.AreEqual(0, delta,
                    $"Peer {peer.LocalPlayerId} ResyncHashMismatchCount increased by {delta} (ApplyFullState observed hash divergence)");
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _subs.Count; i++)
            {
                var sub = _subs[i];
                sub.Peer.Engine.OnPendingWipe -= sub.OnWipe;
                sub.Peer.Engine.OnResyncFailed -= sub.OnResyncFailed;
            }
            _subs.Clear();
        }
    }
}
