using System;
using System.Collections.Generic;

using xpTURN.Klotho.Diagnostics;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// The online desync probe. Each peer already logs its own layered hashes; this adds the
    /// round trip that fetches the other peer's and diffs them, so the engine can say which tick
    /// diverged and which component type / system diverged in it, instead of leaving a human to diff
    /// two log files.
    ///
    /// <para>Three properties hold this together, and every method below is written to preserve them:</para>
    /// <list type="bullet">
    /// <item><b>No rewind.</b> A responder answers out of its history ring or answers "I don't have it".
    /// It never rolls back a live simulation to reconstruct a tick for someone else's diagnosis.</item>
    /// <item><b>No trust.</b> Nothing arriving on these messages reaches the recovery ladder. The worst a
    /// forged probe can do is produce a wrong log line, and even that is fenced (sender-peer checks,
    /// range validation, names resolved from the local registry).</item>
    /// <item><b>Capture before recovery.</b> The numbers are copied out at detection, BEFORE the ring is
    /// flushed and the rollback re-sim overwrites it. The response arrives an RTT later, by which time
    /// the ring no longer holds the truth — but the capture does.</item>
    /// </list>
    /// </summary>
    public partial class KlothoEngine
    {
        /// <summary>
        /// A desync was diagnosed: the diverged tick, whether inputs or state caused it, and the
        /// component type / system it was localized to. Diagnostic — no recovery decision reads this.
        /// </summary>
        public event Action<DesyncVerdict> OnDesyncDiagnosed;

        // Concurrent episodes. Three is a budget, not a limit discovered by tuning: a desync fires
        // repeatedly as it escalates, and without a cap the captures (each the size of the ring) would
        // pile up exactly when the machine is already under network stress.
        private const int MAX_PROBE_EPISODES = 3;

        private IDesyncProbeNetwork _probeNetwork;

        /// <summary>
        /// One in-flight diagnosis. Buffers are allocated once per slot and reused across episodes —
        /// capture runs on a cold path, but a cold path that coincides with a network stall, so it does
        /// not also hand the GC a burst of multi-kilobyte arrays.
        /// </summary>
        private sealed class ProbeEpisode
        {
            public bool Active;
            public int PeerId;                 // the responder

            /// <summary>
            /// The diverged tick as the HISTORY RINGS label it — every probe tick, capture index and
            /// diff is in this space, on both peers.
            /// </summary>
            public int DetectTick;

            /// <summary>
            /// Added to a ring tick to get the tick humans and the rest of the logs use. 0 in P2P, where
            /// the sync-hash tick IS the ring label. 1 in SD, where the two disagree: the server records
            /// the state produced by executing tick N under label N, but broadcasts it as verified tick
            /// N+1, and the client mirrors that (executionTick = entry.Tick - 1). So the rings agree with
            /// each other while the wire is one ahead of both — probe in ring ticks, report in wire ticks.
            /// </summary>
            public int ReportTickOffset;

            public long LocalHash;
            public long RemoteHash;
            // Remote total AT the diverged tick, for the verdict's RemoteHash. RemoteHash above is the
            // DETECT-tick value; in P2P the L1 search routinely lands divergedTick < detectTick, so the
            // verdict would otherwise pair a diverged-tick LocalHash with a detect-tick RemoteHash. Seeded
            // = RemoteHash at detection (correct for SD, which has no L1 and divergedTick == detectTick)
            // and refined to the L1-reported total once the diverged tick is found.
            public long RemoteHashAtDiverged;
            public bool ServerDriven;
            public DesyncClass LocalClass;     // SD: settled at detection. P2P: Unknown until L2 answers.

            // Captured history window, ascending by tick. Flat component/system arrays: [i * Count + j].
            public int WindowCount;
            public int[] Ticks = Array.Empty<int>();
            public long[] Totals = Array.Empty<long>();
            public long[] CmdHashes = Array.Empty<long>();   // 0 = outside input retention, see TryComputeCmdDigestAt
            public int TypeCount;
            public int ParticipantCount;
            public ulong[] ComponentHashes = Array.Empty<ulong>();
            public int[] ComponentCounts = Array.Empty<int>();
            public ulong[] SystemHashes = Array.Empty<ulong>();

            public int PendingCorrId;          // 0 = nothing in flight
            public byte PendingLevel;
            public long PendingSentMs;
        }

        private readonly ProbeEpisode[] _probeEpisodes = new ProbeEpisode[MAX_PROBE_EPISODES];
        private readonly Dictionary<int, ProbeEpisode> _probePending = new Dictionary<int, ProbeEpisode>();

        // Monotonic, requester-private. Correlation ids are not derived from tick or state: this is the
        // network layer, where nothing is deterministic, and mixing the two would be a determinism trap.
        // The responder echoes the id and stores nothing, so two peers can never collide.
        private int _nextProbeCorrId = 1;

        // Suppresses the second capture when one tick reaches CompareAndReportSyncHash twice (the local
        // hash arriving after the remote one, and the remote after the local, are both compare points).
        private readonly Dictionary<int, int> _lastProbedTickByPeer = new Dictionary<int, int>();

        private readonly List<int> _probeTickScratch = new List<int>();
        private readonly List<long> _probeTotalScratch = new List<long>();

        // Wall-clock for the probe round-trip timeout, injectable for tests (mirrors
        // ServerNetworkService.SetNowProviderForTest). null = real clock. The probe is off the
        // deterministic path, so overriding this changes nothing a peer simulates — it only lets a test
        // expire a timeout without a real 2-second wait.
        private Func<long> _probeNowProvider;
        private long ProbeNowMs() => _probeNowProvider != null
            ? _probeNowProvider()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Test seam: overrides the probe-timeout clock. Ignored if null. Diagnostic-only path,
        /// so it never touches anything deterministic.</summary>
        public void SetProbeNowProviderForTest(Func<long> nowProvider)
        {
            if (nowProvider != null) _probeNowProvider = nowProvider;
        }

        // ── wiring ───────────────────────────────────────────────────

        private void SubscribeDesyncProbe()
        {
            // A service without the probe surface (a test double, a spectator transport) simply has no
            // diagnosis: the cast fails, every entry point below no-ops, and the local logs stay.
            _probeNetwork = _networkService as IDesyncProbeNetwork;
            if (_probeNetwork == null) return;

            _probeNetwork.OnDesyncProbeRequested += HandleDesyncProbeRequested;
            _probeNetwork.OnDesyncProbeResponse += HandleDesyncProbeResponse;
            _probeNetwork.OnDesyncVerdictReported += HandleDesyncVerdictReported;
        }

        private void UnsubscribeDesyncProbe()
        {
            if (_probeNetwork == null) return;

            _probeNetwork.OnDesyncProbeRequested -= HandleDesyncProbeRequested;
            _probeNetwork.OnDesyncProbeResponse -= HandleDesyncProbeResponse;
            _probeNetwork.OnDesyncVerdictReported -= HandleDesyncVerdictReported;
            _probeNetwork = null;

            _probePending.Clear();
            for (int i = 0; i < _probeEpisodes.Length; i++)
                if (_probeEpisodes[i] != null) _probeEpisodes[i].Active = false;
        }

        // ── requester: capture + launch ──────────────────────────────

        /// <summary>
        /// P2P detection hook. Called at the TOP of HandleNetworkDesync — before the ring is flushed and
        /// before the rollback re-sims over it. The gates run BEFORE the copy-out: a desync re-fires as it
        /// escalates, and capturing a ring-sized window for every re-fire only to throw it away would
        /// allocate megabytes across an episode.
        /// </summary>
        private void TryBeginDesyncProbe(int remotePlayerId, int tick, long localHash, long remoteHash)
        {
            if (_probeNetwork == null || _diagHistorySim == null) return;

            // No local history means nothing to compare a remote answer against. P2P degrades to
            // check-tick granularity here — a coarser diagnosis, not a failure.
            if (_diagHistorySim.HashHistoryCapacity == 0) return;

            if (!_probeNetwork.TryResolveProbePeer(remotePlayerId, out int peerId))
            {
                _logger?.KTrace($"[Desync][Probe] skipped: cannot resolve peer for playerId={remotePlayerId}");
                return;
            }

            if (_lastProbedTickByPeer.TryGetValue(peerId, out int lastTick) && lastTick == tick)
                return;   // same tick reached the compare point twice

            var episode = AcquireProbeEpisode(peerId);
            if (episode == null) return;   // already probing this peer, or the budget is full

            episode.ServerDriven = false;
            episode.LocalClass = DesyncClass.Unknown;   // P2P learns this from the responder's digest
            episode.DetectTick = tick;
            episode.ReportTickOffset = 0;               // the sync-hash tick IS the ring label in P2P
            episode.LocalHash = localHash;
            episode.RemoteHash = remoteHash;
            episode.RemoteHashAtDiverged = remoteHash;   // refined to the diverged-tick total once L1 finds it

            if (!CaptureHistoryWindow(episode))
            {
                ReleaseProbeEpisode(episode);
                return;
            }

            _lastProbedTickByPeer[peerId] = tick;

            // The L1 window starts at the last tick both peers agreed on. The anchor field is demoted
            // INLINE here rather than read raw: the demotion in HandleNetworkDesync happens further down
            // (after the flush), and an out-of-order sync hash can leave the raw field at or beyond the
            // diverged tick — which would ask for a window that runs backwards.
            int anchor = _lastMatchedSyncTick > 0 && _lastMatchedSyncTick >= tick
                ? _prevMatchedSyncTick
                : _lastMatchedSyncTick;
            int fromTick = anchor > 0 ? anchor : episode.Ticks[0];
            if (fromTick > tick) fromTick = episode.Ticks[0];

            SendProbeRequest(episode, DesyncProbeLevel.TickHashes, fromTick, tick);
        }

        /// <summary>
        /// SD detection hook. The live simulation is exactly at the diverged tick here, and the client
        /// already holds both command digests — so there is no window to search and no classification to
        /// ask for. It needs the server's breakdown, and only that (L2 straight away, no L1).
        ///
        /// <para>The scalars are copied now and not re-read later: the command cache is overwritten by
        /// the next verified tick, and the response is an RTT away.</para>
        ///
        /// <para><paramref name="executionTick"/> is the tick BOTH history rings key this state under;
        /// <paramref name="verifiedTick"/> is what the wire and every other SD log line call it, and the
        /// two differ by one (see <see cref="ProbeEpisode.ReportTickOffset"/>). The probe must speak the
        /// ring's language or the lookup misses on both sides.</para>
        /// </summary>
        private void TryBeginDesyncProbeSD(int executionTick, int verifiedTick, long localHash, long serverHash,
            DesyncClass detectedClass)
        {
            if (_probeNetwork == null || _diagHistorySim == null) return;
            if (_diagHistorySim.HashHistoryCapacity == 0) return;

            const int serverPeerId = 0;
            if (_lastProbedTickByPeer.TryGetValue(serverPeerId, out int lastTick) && lastTick == executionTick)
                return;

            var episode = AcquireProbeEpisode(serverPeerId);
            if (episode == null) return;

            episode.ServerDriven = true;
            episode.LocalClass = detectedClass;
            episode.DetectTick = executionTick;
            episode.ReportTickOffset = verifiedTick - executionTick;
            episode.LocalHash = localHash;
            episode.RemoteHash = serverHash;
            episode.RemoteHashAtDiverged = serverHash;   // SD has no L1; divergedTick == detectTick, so this is final

            if (!CaptureHistoryWindow(episode))
            {
                ReleaseProbeEpisode(episode);
                return;
            }

            _lastProbedTickByPeer[serverPeerId] = executionTick;
            SendProbeRequest(episode, DesyncProbeLevel.Breakdown, executionTick, executionTick);
        }

        private ProbeEpisode AcquireProbeEpisode(int peerId)
        {
            ProbeEpisode free = null;
            for (int i = 0; i < _probeEpisodes.Length; i++)
            {
                var e = _probeEpisodes[i];
                if (e == null)
                {
                    _probeEpisodes[i] = e = new ProbeEpisode();
                }
                if (e.Active)
                {
                    // One episode per peer. A desync fires repeatedly while the ladder climbs; the
                    // follow-ups describe the same divergence, so the first capture is the one that matters.
                    if (e.PeerId == peerId) return null;
                    continue;
                }
                if (free == null) free = e;
            }

            if (free == null)
            {
                _logger?.KTrace($"[Desync][Probe] skipped: {MAX_PROBE_EPISODES} captures already in flight");
                return null;
            }

            free.Active = true;
            free.PeerId = peerId;
            free.PendingCorrId = 0;
            free.WindowCount = 0;
            return free;
        }

        private void ReleaseProbeEpisode(ProbeEpisode episode)
        {
            if (episode.PendingCorrId != 0)
                _probePending.Remove(episode.PendingCorrId);
            episode.PendingCorrId = 0;
            episode.WindowCount = 0;
            episode.Active = false;
        }

        /// <summary>
        /// Copies the whole live ring — not just a computed window. Which ticks the window will need is
        /// decided from the anchor, and the anchor is not trustworthy until it has been demoted; taking
        /// the ring whole sidesteps that ordering entirely, and the ring IS the budget (~24KB).
        /// </summary>
        private bool CaptureHistoryWindow(ProbeEpisode episode)
        {
            var sim = _diagHistorySim;
            int capacity = sim.HashHistoryCapacity;
            int typeCount = sim.HashHistoryTypeCount;
            int participantCount = sim.HashHistoryParticipantCount;
            if (capacity == 0) return false;

            EnsureCaptureBuffers(episode, capacity, typeCount, participantCount);

            int n = sim.GetHashHistoryTicks(episode.Ticks);
            if (n == 0) return false;

            for (int i = 0; i < n; i++)
            {
                int tick = episode.Ticks[i];
                if (!sim.TryGetHashHistoryBreakdown(tick, out var comp, out var counts, out var sys, out long total))
                {
                    episode.Totals[i] = 0;
                    continue;
                }

                episode.Totals[i] = total;
                for (int c = 0; c < typeCount && c < comp.Length; c++)
                {
                    episode.ComponentHashes[i * typeCount + c] = comp[c];
                    episode.ComponentCounts[i * typeCount + c] = counts[c];
                }
                for (int s = 0; s < participantCount && s < sys.Length; s++)
                    episode.SystemHashes[i * participantCount + s] = sys[s];

                // The digest, not the commands. The command OBJECTS are pool instances and the list is a
                // reused cache — a resync returns them to the pool and the next tick refills the list, so a
                // reference captured here would be describing someone else's commands by the time the
                // response lands. What classification actually needs is a number, so take the number now.
                episode.CmdHashes[i] = TryComputeCmdDigestAt(tick);
            }

            episode.WindowCount = n;
            return true;
        }

        private static void EnsureCaptureBuffers(ProbeEpisode episode, int capacity, int typeCount, int participantCount)
        {
            if (episode.Ticks.Length < capacity)
            {
                episode.Ticks = new int[capacity];
                episode.Totals = new long[capacity];
                episode.CmdHashes = new long[capacity];
            }
            if (episode.TypeCount != typeCount || episode.ComponentHashes.Length < capacity * typeCount)
            {
                episode.ComponentHashes = new ulong[capacity * typeCount];
                episode.ComponentCounts = new int[capacity * typeCount];
            }
            if (episode.ParticipantCount != participantCount || episode.SystemHashes.Length < capacity * participantCount)
                episode.SystemHashes = new ulong[capacity * Math.Max(1, participantCount)];

            episode.TypeCount = typeCount;
            episode.ParticipantCount = participantCount;
        }

        /// <summary>
        /// The command digest for a tick, or 0 when this peer cannot honestly produce one.
        ///
        /// <para>The range check is the whole point, and it cannot be replaced by inspecting the digest:
        /// GetCommandList returns an EMPTY list for a tick that has been trimmed away just as it does for
        /// a tick that genuinely had no commands, and the digest of an empty set is an ordinary-looking
        /// number. Digesting a trimmed tick would therefore manufacture a difference against a peer that
        /// still holds the tick — and report a state divergence as an input divergence.</para>
        ///
        /// <para>0 is a safe "unavailable" marker: the digest of an empty set is the FNV offset basis,
        /// which is not 0.</para>
        /// </summary>
        private long TryComputeCmdDigestAt(int tick)
        {
            if (_inputBuffer == null) return 0;

            int oldest = _inputBuffer.OldestTick;
            int newest = _inputBuffer.NewestTick;

            // A cleared buffer reports (0, 0) — the same as a buffer holding only tick 0. Treat it as
            // "nothing retained": a resync clears the buffer, and that is precisely the moment a stale
            // tick would otherwise digest as an empty set and be reported as an input divergence.
            if (oldest == 0 && newest == 0) return 0;
            if (tick < oldest || tick > newest) return 0;

            return ComputeCommandDigest(_inputBuffer.GetCommandList(tick));
        }

        private void SendProbeRequest(ProbeEpisode episode, DesyncProbeLevel level, int fromTick, int toTick)
        {
            int corrId = _nextProbeCorrId++;
            if (_nextProbeCorrId == 0) _nextProbeCorrId = 1;   // 0 is the "nothing in flight" marker

            episode.PendingCorrId = corrId;
            episode.PendingLevel = (byte)level;
            episode.PendingSentMs = ProbeNowMs();
            _probePending[corrId] = episode;

            var msg = new DesyncProbeRequestMessage
            {
                CorrelationId = corrId,
                LevelEnum = level,
                FromTick = fromTick,
                ToTick = toTick,
                RequesterPlayerId = LocalPlayerId,
            };
            _probeNetwork.SendDesyncProbeRequest(episode.PeerId, msg);

            _logger?.KInformation(
                $"[Desync][Probe] request: peer={episode.PeerId}, level={level}, corrId={corrId}, window=[{fromTick}..{toTick}]");
        }

        // ── responder: serve from the ring, never from a rewind ───────

        private void HandleDesyncProbeRequested(int fromPeerId, DesyncProbeRequestMessage msg)
        {
            var response = new DesyncProbeResponseMessage
            {
                CorrelationId = msg.CorrelationId,
                Level = msg.Level,
                BaseTick = msg.FromTick,
                CmdHashAtTick = 0,
                Payload = Array.Empty<byte>(),
            };

            var sim = _diagHistorySim;
            bool haveHistory = sim != null && sim.HashHistoryCapacity > 0;

            if (haveHistory && msg.LevelEnum == DesyncProbeLevel.TickHashes)
            {
                BuildTickHashPayload(sim, msg, response);
            }
            else if (haveHistory && msg.LevelEnum == DesyncProbeLevel.Breakdown)
            {
                response.BaseTick = msg.ToTick;
                if (sim.TryGetHashHistoryBreakdown(msg.ToTick, out var comp, out var counts, out var sys, out _))
                {
                    response.Payload = DesyncProbePayload.PackL2(comp, counts, sys);
                    response.CmdHashAtTick = TryComputeCmdDigestAt(msg.ToTick);
                }
            }

            // An empty payload IS the answer when history is off or the ticks are gone: it tells the
            // requester to give up now rather than sit out its timeout. What it never does is rewind to
            // rebuild what the ring no longer holds.
            _probeNetwork.SendDesyncProbeResponse(fromPeerId, response);
        }

        private void BuildTickHashPayload(xpTURN.Klotho.ECS.EcsSimulation sim, DesyncProbeRequestMessage msg,
            DesyncProbeResponseMessage response)
        {
            int from = msg.FromTick;
            int to = msg.ToTick;
            if (to < from) return;                                    // malformed window → unavailable

            // Both ends arrive off the wire and are NOT trusted. `to - from` can overflow int, and a `to`
            // at/near int.MaxValue would make a `for (tick <= to; tick++)` loop spin forever once tick++
            // wraps (unchecked). The ring holds at most HashHistoryCapacity ticks, so answer only the
            // newest that many and iterate by a bounded counter — never walking up to a wire-supplied `to`.
            int cap = sim.HashHistoryCapacity;
            long span = (long)to - from;                              // overflow-safe span
            if (span > cap) { from = to - cap; span = cap; }          // to - cap can't underflow: clamp
                                                                      // fires only when to - from > cap

            _probeTickScratch.Clear();
            _probeTotalScratch.Clear();

            // Partial overlap is normal and is answered partially: two peers retain different spans, so
            // the responder returns the ticks it HAS and lets the requester search the intersection.
            for (long i = 0; i <= span; i++)
            {
                int tick = (int)(from + i);                           // in [from, to], so the cast is exact
                if (tick < 0) continue;
                if (!sim.TryGetHashHistory(tick, out long total, out _)) continue;
                _probeTickScratch.Add(tick);
                _probeTotalScratch.Add(total);
            }

            int n = _probeTickScratch.Count;
            if (n == 0) return;

            var ticks = new int[n];
            var totals = new long[n];
            for (int i = 0; i < n; i++)
            {
                ticks[i] = _probeTickScratch[i];
                totals[i] = _probeTotalScratch[i];
            }

            response.BaseTick = ticks[0];
            response.Payload = DesyncProbePayload.PackL1(ticks, totals, n);
        }

        // ── requester: accept, diff, conclude ─────────────────────────

        private void HandleDesyncProbeResponse(int fromPeerId, DesyncProbeResponseMessage msg)
        {
            if (!_probePending.TryGetValue(msg.CorrelationId, out var episode))
                return;   // timed out, duplicated, or forged — nothing to correlate it with

            // The sender must be the peer we actually asked. Correlation ids are small monotonic ints and
            // therefore guessable; without this a third peer could answer a round trip it was never part
            // of. It cannot corrupt recovery (nothing here feeds it) but it would poison the diagnosis.
            if (fromPeerId != episode.PeerId)
            {
                _logger?.KWarning(
                    $"[Desync][Probe] rejected: response for corrId={msg.CorrelationId} came from peer={fromPeerId}, expected peer={episode.PeerId}");
                return;
            }

            if (msg.Level != episode.PendingLevel)
                return;

            _probePending.Remove(msg.CorrelationId);
            episode.PendingCorrId = 0;

            if (msg.LevelEnum == DesyncProbeLevel.TickHashes)
                HandleTickHashResponse(episode, msg);
            else
                HandleBreakdownResponse(episode, msg);
        }

        private void HandleTickHashResponse(ProbeEpisode episode, DesyncProbeResponseMessage msg)
        {
            if (!DesyncProbePayload.TryUnpackL1(msg.Payload, out int[] remoteTicks, out long[] remoteTotals))
            {
                _logger?.KWarning($"[Desync][Probe] malformed L1 payload from peer={episode.PeerId}");
                ReleaseProbeEpisode(episode);
                return;
            }

            if (remoteTicks.Length == 0)
            {
                _logger?.KWarning(
                    $"[Desync][Diag] remote diagnostics unavailable: peer={episode.PeerId}, tick={episode.DetectTick} — history disabled or already overwritten there");
                ConcludeWithoutLayer(episode);
                return;
            }

            // Walk the ticks both peers still hold, oldest first, looking for the first disagreement that
            // FOLLOWS an agreement. The preceding agreement is what makes the answer meaningful: without a
            // tick the two peers are known to have shared, the oldest visible difference could just be the
            // tail of a divergence that started before either peer's retention.
            int divergedTick = -1;
            bool sawMatch = false;

            for (int i = 0; i < episode.WindowCount && divergedTick < 0; i++)
            {
                int tick = episode.Ticks[i];
                int r = IndexOfTick(remoteTicks, tick);
                if (r < 0) continue;

                if (episode.Totals[i] == remoteTotals[r])
                {
                    sawMatch = true;
                    continue;
                }
                if (sawMatch)
                {
                    divergedTick = tick;
                    episode.RemoteHashAtDiverged = remoteTotals[r];   // pair the verdict's RemoteHash to this tick (M2)
                }
                else
                    break;   // the intersection opens on a disagreement — no anchor
            }

            if (divergedTick < 0)
            {
                // Honest failure beats a confident wrong answer: report that the tick could not be
                // localized rather than naming the oldest tick we happen to be able to see.
                _logger?.KWarning(
                    $"[Desync][Diag] tick localization failed: peer={episode.PeerId}, detectTick={episode.DetectTick} — no agreed tick inside the shared history window");
                ConcludeWithoutLayer(episode);
                return;
            }

            SendProbeRequest(episode, DesyncProbeLevel.Breakdown, divergedTick, divergedTick);
        }

        private void HandleBreakdownResponse(ProbeEpisode episode, DesyncProbeResponseMessage msg)
        {
            if (!DesyncProbePayload.TryUnpackL2(msg.Payload, out ulong[] remoteComponents, out int[] remoteCounts,
                    out ulong[] remoteSystems))
            {
                _logger?.KWarning($"[Desync][Probe] malformed L2 payload from peer={episode.PeerId}");
                ConcludeWithoutLayer(episode);
                return;
            }

            int divergedTick = msg.BaseTick;
            int index = IndexOfTick(episode.Ticks, divergedTick, episode.WindowCount);

            if (remoteComponents.Length == 0)
            {
                _logger?.KWarning(
                    $"[Desync][Diag] remote breakdown unavailable: peer={episode.PeerId}, ringTick={divergedTick} — history disabled there, or the tick has been overwritten");
                ConcludeWithoutLayer(episode, divergedTick);
                return;
            }

            // The responder answered but the tick is not in OUR capture — the two sides are not agreeing
            // on what to call this tick. Kept distinct from the line above: that one is the remote peer
            // saying "I don't have it", this one is a local defect, and conflating them once already hid
            // an off-by-one between the wire tick and the ring label.
            if (index < 0)
            {
                _logger?.KError(
                    $"[Desync][Diag] captured window does not contain ringTick={divergedTick} " +
                    $"(captured [{episode.Ticks[0]}..{episode.Ticks[episode.WindowCount - 1]}]) — probe tick space mismatch");
                ConcludeWithoutLayer(episode, divergedTick);
                return;
            }

            // Same match, same build — so if the arrays are different lengths the peers are not
            // running the same registry, and every index in them means something different. Localizing
            // against them would name a component with total confidence and be wrong.
            if (remoteComponents.Length != episode.TypeCount || remoteSystems.Length != episode.ParticipantCount)
            {
                _logger?.KError(
                    $"[Desync][Diag] build/registry mismatch: peer={episode.PeerId}, tick={divergedTick}, " +
                    $"components={episode.TypeCount} vs {remoteComponents.Length}, systems={episode.ParticipantCount} vs {remoteSystems.Length} — layer diff skipped");
                ConcludeWithoutLayer(episode, divergedTick);
                return;
            }

            DesyncClass desyncClass = ClassifyDivergence(episode, index, msg.CmdHashAtTick);
            LocalizeLayer(episode, index, remoteComponents, remoteCounts, remoteSystems,
                out DesyncLayer layer, out int typeIdOrIdx);

            var verdict = new DesyncVerdict
            {
                DivergedTick = divergedTick + episode.ReportTickOffset,   // ring space → the tick the logs name
                Class = desyncClass,
                Layer = layer,
                TypeIdOrParticipantIdx = typeIdOrIdx,
                LocalHash = episode.Totals[index],
                RemoteHash = episode.RemoteHashAtDiverged,   // diverged-tick remote total (M2), not detect-tick
            };
            PublishVerdict(episode, verdict);
            ReleaseProbeEpisode(episode);
        }

        /// <summary>
        /// SD settles this at detection (it holds its own executed set AND the server's confirmed set).
        /// P2P cannot: a peer has no view of what the OTHER peer executed, so the responder supplies its
        /// digest and the comparison happens here. When the responder could not produce one, the class is
        /// withheld rather than guessed — the layer is still reported.
        /// </summary>
        private static DesyncClass ClassifyDivergence(ProbeEpisode episode, int index, long remoteCmdHash)
        {
            if (episode.ServerDriven) return episode.LocalClass;

            long localCmdHash = episode.CmdHashes[index];
            if (localCmdHash == 0 || remoteCmdHash == 0) return DesyncClass.Unknown;

            return localCmdHash != remoteCmdHash ? DesyncClass.Input : DesyncClass.State;
        }

        private void LocalizeLayer(ProbeEpisode episode, int index, ulong[] remoteComponents, int[] remoteCounts,
            ulong[] remoteSystems, out DesyncLayer layer, out int typeIdOrIdx)
        {
            int typeCount = episode.TypeCount;
            int baseIndex = index * typeCount;

            for (int c = 0; c < typeCount; c++)
            {
                if (episode.ComponentHashes[baseIndex + c] == remoteComponents[c]) continue;

                layer = DesyncLayer.Component;
                typeIdOrIdx = xpTURN.Klotho.ECS.EcsSimulation.ComponentTypeIdAt(c);
                string name = xpTURN.Klotho.ECS.EcsSimulation.ComponentTypeName(typeIdOrIdx) ?? "?";
                _logger?.KError(
                    $"[Desync][Diag] layer=Component type={name}({typeIdOrIdx}) " +
                    $"localCount={episode.ComponentCounts[baseIndex + c]} remoteCount={remoteCounts[c]} " +
                    $"local=0x{episode.ComponentHashes[baseIndex + c]:X16} remote=0x{remoteComponents[c]:X16}");
                return;
            }

            int participantCount = episode.ParticipantCount;
            int systemBase = index * participantCount;
            for (int s = 0; s < participantCount; s++)
            {
                if (episode.SystemHashes[systemBase + s] == remoteSystems[s]) continue;

                layer = DesyncLayer.System;
                typeIdOrIdx = s;
                string name = _diagHistorySim?.SnapshotParticipantName(s) ?? "?";
                _logger?.KError(
                    $"[Desync][Diag] layer=System participant={s}({name}) " +
                    $"local=0x{episode.SystemHashes[systemBase + s]:X16} remote=0x{remoteSystems[s]:X16}");
                return;
            }

            // Every component and every system agrees, yet the totals do not: the difference is in the
            // frame envelope itself (tick label, entity count) rather than in any one layer.
            layer = DesyncLayer.None;
            typeIdOrIdx = -1;
        }

        /// <summary>
        /// Ends an episode that could not reach a layer. It still concludes: for SD the class and the
        /// diverged tick were known at detection, and a verdict carrying those is strictly more than the
        /// server would otherwise learn. Withholding it because one field is missing would recreate the
        /// gap this whole path exists to close.
        /// </summary>
        private void ConcludeWithoutLayer(ProbeEpisode episode, int ringTick = -1)
        {
            var verdict = new DesyncVerdict
            {
                DivergedTick = (ringTick >= 0 ? ringTick : episode.DetectTick) + episode.ReportTickOffset,
                Class = episode.ServerDriven ? episode.LocalClass : DesyncClass.Unknown,
                Layer = DesyncLayer.None,
                TypeIdOrParticipantIdx = -1,
                LocalHash = episode.LocalHash,
                RemoteHash = episode.RemoteHash,
            };
            PublishVerdict(episode, verdict);
            ReleaseProbeEpisode(episode);
        }

        private void PublishVerdict(ProbeEpisode episode, DesyncVerdict verdict)
        {
            string target = verdict.Layer == DesyncLayer.Component
                ? xpTURN.Klotho.ECS.EcsSimulation.ComponentTypeName(verdict.TypeIdOrParticipantIdx) ?? "?"
                : verdict.Layer == DesyncLayer.System
                    ? _diagHistorySim?.SnapshotParticipantName(verdict.TypeIdOrParticipantIdx) ?? "?"
                    : "-";

            _logger?.KError(
                $"[Desync][Verdict] tick={verdict.DivergedTick} class={verdict.Class} layer={verdict.Layer} " +
                $"target={target}({verdict.TypeIdOrParticipantIdx}) peer={episode.PeerId} " +
                $"local=0x{verdict.LocalHash:X16} remote=0x{verdict.RemoteHash:X16}");

            OnDesyncDiagnosed?.Invoke(verdict);

            // P2P keeps it local: peers there are symmetric and mutually untrusted, so a verdict sent
            // to another peer is a claim nobody can check and nobody can act on.
            if (!episode.ServerDriven) return;

            _probeNetwork?.SendDesyncVerdict(new DesyncVerdictReportMessage
            {
                DivergedTick = verdict.DivergedTick,
                Class = (byte)verdict.Class,
                Layer = (byte)verdict.Layer,
                TypeIdOrParticipantIdx = verdict.TypeIdOrParticipantIdx,
                LocalHash = verdict.LocalHash,
                RemoteHash = verdict.RemoteHash,
            });
        }

        // ── timeouts ─────────────────────────────────────────────────

        /// <summary>
        /// Expires round trips whose answer never came. Deliberately no retry: a probe is an observation,
        /// and re-asking a peer that just failed to answer adds load to a session already in trouble.
        /// Recovery is untouched either way — it never waited on this.
        /// </summary>
        private void SweepDesyncProbes()
        {
            if (_probePending.Count == 0) return;

            long now = ProbeNowMs();
            for (int i = 0; i < _probeEpisodes.Length; i++)
            {
                var episode = _probeEpisodes[i];
                if (episode == null || !episode.Active || episode.PendingCorrId == 0) continue;
                if (now - episode.PendingSentMs < ProbePolicy.PROBE_TIMEOUT_MS) continue;

                _logger?.KWarning(
                    $"[Desync][Probe] timeout: peer={episode.PeerId}, level={episode.PendingLevel}, corrId={episode.PendingCorrId}, detectTick={episode.DetectTick}");

                _probePending.Remove(episode.PendingCorrId);
                episode.PendingCorrId = 0;
                ConcludeWithoutLayer(episode);
            }
        }

        // ── SD server: receive a client's verdict ─────────────────────

        /// <summary>
        /// A client's diagnosis, logged so it reaches the operator instead of dying on the client. It is
        /// treated as a claim, not a finding: validated, rendered through the SERVER's own registry (a
        /// client sends integers, never names — there is no way to write text into this log), consumed
        /// into one line, and dropped. No recovery-ladder state is touched, and the server does not
        /// re-run the diff to "check" it.
        /// </summary>
        private void HandleDesyncVerdictReported(int peerId, DesyncVerdictReportMessage msg)
        {
            if (msg.Class > (byte)DesyncClass.Static || msg.Layer > (byte)DesyncLayer.System)
                return;   // outside the enums, including the client-only Unknown class

            if (msg.DivergedTick < 0 || msg.DivergedTick > CurrentTick + _simConfig.MaxRollbackTicks)
                return;

            var layer = (DesyncLayer)msg.Layer;
            string target;

            // The index means a different thing per layer, so it is validated against a different range
            // per layer. One shared bound would wave through a participant index that happens to be small
            // and then resolve it as some unrelated component type.
            switch (layer)
            {
                case DesyncLayer.Component:
                    target = xpTURN.Klotho.ECS.EcsSimulation.ComponentTypeName(msg.TypeIdOrParticipantIdx);
                    if (target == null) return;   // not a registered typeId on this build
                    break;

                case DesyncLayer.System:
                    target = _diagHistorySim?.SnapshotParticipantName(msg.TypeIdOrParticipantIdx);
                    if (target == null) return;   // outside this build's participant list
                    break;

                default:
                    target = "-";
                    break;
            }

            _logger?.KError(
                $"[Desync][Verdict][src=client-reported unverified] peerId={peerId} tick={msg.DivergedTick} " +
                $"class={(DesyncClass)msg.Class} layer={layer} target={target}({msg.TypeIdOrParticipantIdx}) " +
                $"clientLocal=0x{msg.LocalHash:X16} clientRemote=0x{msg.RemoteHash:X16}");
        }

        // ── helpers ──────────────────────────────────────────────────

        private static int IndexOfTick(int[] ticks, int tick, int count = -1)
        {
            int n = count < 0 ? ticks.Length : count;
            for (int i = 0; i < n; i++)
                if (ticks[i] == tick) return i;
            return -1;
        }
    }
}
