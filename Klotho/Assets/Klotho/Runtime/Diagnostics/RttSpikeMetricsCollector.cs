#if KLOTHO_FAULT_INJECTION
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace xpTURN.Klotho.Diagnostics
{
    /// <summary>
    /// Match-scoped accumulator for the RTT spike measurement mini-IMP
    /// (Plan-P2PRttSpikeMeasurement section 3.3). Each client (host/guest) runs its own
    /// instance — host does not receive guest metrics over the wire. Engine emit sites
    /// (ChainBreak / Rollback) push events here; the controller emits the summary line
    /// when the match phase exits Playing.
    /// </summary>
    public static class RttSpikeMetricsCollector
    {
        private const long PostSpikeWindowMs = 5000;
        private const long PreSpikeWindowMs = 5000;
        private const long ResumeQuiescenceMs = 1000;

        private static bool _matchActive;
        private static long _anchorMs;
        private static string _role;
        private static int _playerId;

        private static readonly List<float> _spikeAtSec = new List<float>();
        private static readonly List<int> _spikeRttMs = new List<int>();
        private static readonly List<long> _chainBreakMs = new List<long>();
        private static readonly List<int> _rollbackDepths = new List<int>();

        public static void OnMatchStart(string role, int playerId)
        {
            Reset();
            _anchorMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _role = role;
            _playerId = playerId;
            _matchActive = true;
        }

        public static void OnSpike(float atSec, int rttMs)
        {
            if (!_matchActive) return;
            _spikeAtSec.Add(atSec);
            _spikeRttMs.Add(rttMs);
        }

        public static void OnChainBreak()
        {
            if (!_matchActive) return;
            _chainBreakMs.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public static void OnRollback(int depth)
        {
            if (!_matchActive) return;
            _rollbackDepths.Add(depth);
        }

        public static void EmitSummary(ILogger logger)
        {
            if (!_matchActive) return;
            long endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            float matchDurationSec = (endMs - _anchorMs) / 1000f;

            string spikesStr = FormatSpikes();
            string postWin = FormatWindowedCounts(post: true);
            string preWin = FormatWindowedCounts(post: false);
            string resumeLat = FormatResumeLatencies(endMs);
            (double mean, double p95) = ComputeRollbackStats();

            logger?.ZLogInformation(
                $"[Metrics][RttSpike] role={_role} playerId={_playerId} matchDurationSec={matchDurationSec:F1} " +
                $"spikes=[{spikesStr}] chainBreak={_chainBreakMs.Count} " +
                $"chainBreakWindowed=[{postWin}] chainBreakPreSpikeWindowed=[{preWin}] " +
                $"rollbackDepthMean={mean:F2} rollbackDepthP95={p95:F2} " +
                $"chainResumeLatencyMs=[{resumeLat}]");

            _matchActive = false;
        }

        private static void Reset()
        {
            _matchActive = false;
            _anchorMs = 0;
            _role = null;
            _playerId = -1;
            _spikeAtSec.Clear();
            _spikeRttMs.Clear();
            _chainBreakMs.Clear();
            _rollbackDepths.Clear();
        }

        private static string FormatSpikes()
        {
            if (_spikeAtSec.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < _spikeAtSec.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('(').Append(_spikeAtSec[i].ToString("F1")).Append("s,").Append(_spikeRttMs[i]).Append("ms)");
            }
            return sb.ToString();
        }

        private static string FormatWindowedCounts(bool post)
        {
            if (_spikeAtSec.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < _spikeAtSec.Count; i++)
            {
                long spikeMs = _anchorMs + (long)(_spikeAtSec[i] * 1000f);
                long winStart = post ? spikeMs : spikeMs - PreSpikeWindowMs;
                long winEnd = post ? spikeMs + PostSpikeWindowMs : spikeMs;
                int count = CountInRange(winStart, winEnd);
                if (i > 0) sb.Append(',');
                sb.Append(count);
            }
            return sb.ToString();
        }

        private static int CountInRange(long startMs, long endMsExclusive)
        {
            int count = 0;
            for (int j = 0; j < _chainBreakMs.Count; j++)
            {
                long t = _chainBreakMs[j];
                if (t >= startMs && t < endMsExclusive) count++;
            }
            return count;
        }

        // chainResumeLatencyMs[i]: latency from spike i to chain-resume (no ChainBreak emit for
        // ResumeQuiescenceMs). Approximation rationale: ChainBreak emit is throttled to one log per
        // stalled tick per 1s — so 1s of silence is the practical proxy for "chain head advancing
        // again" without instrumenting every per-tick advance. -1 sentinel when stuck until window end.
        private static string FormatResumeLatencies(long endMs)
        {
            if (_spikeAtSec.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < _spikeAtSec.Count; i++)
            {
                long spikeMs = _anchorMs + (long)(_spikeAtSec[i] * 1000f);
                long windowEnd = (i + 1 < _spikeAtSec.Count)
                    ? _anchorMs + (long)(_spikeAtSec[i + 1] * 1000f)
                    : endMs;

                long lastChainBreakInWindow = -1;
                for (int j = 0; j < _chainBreakMs.Count; j++)
                {
                    long t = _chainBreakMs[j];
                    if (t >= spikeMs && t < windowEnd && t > lastChainBreakInWindow)
                        lastChainBreakInWindow = t;
                }

                long latencyMs;
                if (lastChainBreakInWindow < 0)
                    latencyMs = 0;
                else if (lastChainBreakInWindow + ResumeQuiescenceMs >= windowEnd)
                    latencyMs = -1;
                else
                    latencyMs = (lastChainBreakInWindow - spikeMs) + ResumeQuiescenceMs;

                if (i > 0) sb.Append(',');
                sb.Append(latencyMs);
            }
            return sb.ToString();
        }

        private static (double mean, double p95) ComputeRollbackStats()
        {
            int n = _rollbackDepths.Count;
            if (n == 0) return (0d, 0d);
            double sum = 0;
            for (int i = 0; i < n; i++) sum += _rollbackDepths[i];
            double mean = sum / n;

            var sorted = new int[n];
            _rollbackDepths.CopyTo(sorted);
            Array.Sort(sorted);
            int p95Idx = (int)Math.Ceiling(0.95 * n) - 1;
            if (p95Idx < 0) p95Idx = 0;
            if (p95Idx >= n) p95Idx = n - 1;
            return (mean, sorted[p95Idx]);
        }
    }
}
#endif
