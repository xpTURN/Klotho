using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ZLogger;
using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    public partial class KlothoNetworkService
    {
        // ── Mid-match dynamic InputDelay push ─────────────────────

        private readonly Dictionary<int, PlayerRttSmoother> _rttSmoothers = new Dictionary<int, PlayerRttSmoother>();
        private readonly Dictionary<int, int> _lastPushedExtraDelay = new Dictionary<int, int>();
        private readonly Dictionary<int, long> _lastPushTimeMs = new Dictionary<int, long>();

        private const int EXTRA_DELAY_PUSH_THRESHOLD_UP = 2;
        private const int EXTRA_DELAY_PUSH_THRESHOLD_DOWN = 4;
        private const long MIN_PUSH_INTERVAL_MS = 500;

        private void MaybePushExtraDelayUpdate(int playerId, int peerId)
        {
            if (!IsHost) return;
            if (!_rttSmoothers.TryGetValue(playerId, out var smoother)) return;
            if (!smoother.TryGetSmoothedRtt(out int smoothedRtt)) return;

            // Pure compute — no per-sample log emit. Instance wrapper ComputeRecommendedExtraDelay
            // emits [KlothoNetworkService][{tag}] + [Metrics][{tag}] on every call and is reserved
            // for 1-shot seed events (LateJoin/Reconnect/Sync). Mid-match push calls the static
            // calculator directly so only real push events are logged.
            var (newExtraDelay, _, _, _, _) = RecommendedExtraDelayCalculator.Compute(
                smoothedRtt,
                _simConfig.TickIntervalMs,
                _simConfig.LateJoinDelaySafety,
                _simConfig.RttSanityMaxMs,
                _simConfig.MaxRollbackTicks);

            int prev = _lastPushedExtraDelay.TryGetValue(peerId, out var p) ? p : 0;
            int diff = newExtraDelay - prev;
            int absDiff = diff >= 0 ? diff : -diff;
            int threshold = (diff > 0) ? EXTRA_DELAY_PUSH_THRESHOLD_UP : EXTRA_DELAY_PUSH_THRESHOLD_DOWN;
            if (absDiff < threshold) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastPushTimeMs.TryGetValue(peerId, out var last) && now - last < MIN_PUSH_INTERVAL_MS)
                return;

            string reason = diff > 0 ? "threshold_up" : "threshold_down";
            PushExtraDelayUpdate(peerId, playerId, newExtraDelay, smoothedRtt, prev, reason);
            _lastPushedExtraDelay[peerId] = newExtraDelay;
            _lastPushTimeMs[peerId] = now;
        }

        private void PushExtraDelayUpdate(int peerId, int playerId, int extraDelay, int avgRttMs, int prevDelay, string reason)
        {
            var msg = new RecommendedExtraDelayUpdateMessage
            {
                RecommendedExtraDelay = extraDelay,
                AvgRttMs = avgRttMs,
            };
            // Broadcast to all peers and apply locally on the host. Transport.Broadcast does not
            // loop back to the sender, so the host needs the direct handler call to update its
            // own engine — same pattern as StartGame's GameStartMessage path.
            BroadcastMessagePooled(msg, DeliveryMethod.ReliableOrdered);
            HandleRecommendedExtraDelayUpdate(msg);

            _logger?.ZLogDebug(
                $"[KlothoNetworkService][DynamicDelay] Push: targetPlayerId={playerId}, prev={prevDelay}, new={extraDelay}, avgRtt={avgRttMs}ms, reason={reason}");
            _logger?.ZLogInformation(
                $"[Metrics][DynamicDelay] {{\"playerId\":{playerId},\"peerId\":{peerId},\"tag\":\"DynamicDelayPush\",\"avgRtt\":{avgRttMs},\"prevDelay\":{prevDelay},\"newDelay\":{extraDelay},\"reason\":\"{reason}\"}}");
        }

        private void HandleRecommendedExtraDelayUpdate(RecommendedExtraDelayUpdateMessage msg)
        {
            if (_engine == null) return;
            _engine.ApplyExtraDelay(msg.RecommendedExtraDelay, ExtraDelaySource.DynamicPush);
        }
    }
}
