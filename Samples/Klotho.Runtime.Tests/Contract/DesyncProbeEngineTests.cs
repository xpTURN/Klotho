using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Diagnostics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core.Tests
{
    // NOTE: The former xUnit [Collection("EcsRegistry")] that serialized the registry-touching test
    // classes is unnecessary under NUnit — this assembly runs sequentially (no [Parallelizable]), so
    // the process-global ComponentStorageRegistry is never grown mid-fold by a concurrent class.

    /// <summary>
    /// The engine half of the online probe: capture at detection, the L1→L2 round trip, the diff,
    /// and the fences around what a response is allowed to do.
    ///
    /// <para>Driven headlessly. The history ring lives on the simulation, so a test can fill it directly
    /// and then raise a desync on the network stub — that reaches the same detection hook a real
    /// mismatch does, without needing a running match.</para>
    /// </summary>
    public sealed class DesyncProbeEngineTests
    {
        private const int MaxEntities = 64;
        private const int RingTicks = 16;

        [Test] // the round trip end to end: narrow to the tick, then name the component
        public void RoundTrip_LocalizesTheDivergedTickAndComponent()
        {
            var (engine, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 6);
            var remote = SnapshotRing(sim, 1, 6);

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 111, remoteHash: 222);

            // L1 first: the responder is asked for its per-tick totals over the window.
            var l1 = XAssert.Single(net.SentRequests);
            Assert.AreEqual((byte)DesyncProbeLevel.TickHashes, l1.Level);
            Assert.AreEqual(6, l1.ToTick);

            // Answer as a peer that agreed through tick 3 and diverged from tick 4 on.
            net.RaiseProbeResponse(0, TickHashResponse(remote, l1, agreeThrough: 3, from: 1, to: 6));

            // The engine must now ask for the breakdown at exactly the first diverging tick. Note the
            // ring was flushed by the detection — this can only come from the capture.
            Assert.AreEqual(2, net.SentRequests.Count);
            var l2 = net.SentRequests[1];
            Assert.AreEqual((byte)DesyncProbeLevel.Breakdown, l2.Level);
            Assert.AreEqual(4, l2.FromTick);
            Assert.AreEqual(4, l2.ToTick);

            // Answer with a breakdown that differs in exactly one component type.
            int transformIndex = IndexOfType<TransformComponent>();
            net.RaiseProbeResponse(0, BreakdownResponse(remote, l2, tick: 4, corruptComponentIndex: transformIndex));

            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(4, verdict.Value.DivergedTick);
            Assert.AreEqual(DesyncLayer.Component, verdict.Value.Layer);
            Assert.AreEqual(ComponentStorageRegistry.GetTypeId(typeof(TransformComponent)), verdict.Value.TypeIdOrParticipantIdx);
        }

        [Test] // M2: the verdict must pair LocalHash and RemoteHash at the SAME tick (the diverged one).
               // Before the fix RemoteHash was episode.RemoteHash — the DETECT-tick remote hash (222 here),
               // while LocalHash is the diverged-tick local total. In P2P the L1 search routinely lands
               // divergedTick < detectTick, so the pair was mislabeled. The diverged-tick remote total is
               // carried from the L1 response.
        public void Verdict_RemoteHash_IsTheDivergedTickValue_NotDetectTick()
        {
            var (engine, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 6);
            var remote = SnapshotRing(sim, 1, 6);

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 111, remoteHash: 222);
            var l1 = net.SentRequests[0];
            net.RaiseProbeResponse(0, TickHashResponse(remote, l1, agreeThrough: 3, from: 1, to: 6));  // diverge from tick 4
            var l2 = net.SentRequests[1];
            net.RaiseProbeResponse(0, BreakdownResponse(remote, l2, tick: 4, corruptComponentIndex: IndexOfType<TransformComponent>()));

            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(4, verdict.Value.DivergedTick);
            // TickHashResponse reports tick>agreeThrough as (localTotal ^ 0x5A5A_5A5A) — so the remote total
            // AT the diverged tick (4) is that, NOT the detect-tick (6) hash 222.
            long expectedRemoteAtDiverged = remote.Totals[4] ^ 0x5A5A_5A5A;
            Assert.AreEqual(expectedRemoteAtDiverged, verdict.Value.RemoteHash);
            Assert.AreNotEqual(222, verdict.Value.RemoteHash);          // the old (mislabeled) detect-tick value
            Assert.AreEqual(remote.Totals[4], verdict.Value.LocalHash); // pair is same-tick: local total at 4
        }

        [Test] // the system layer is a diagnostic blind spot — the probe must reach it too
        public void RoundTrip_LocalizesADivergingSystem()
        {
            var (engine, sim, net) = NewEngine(withParticipant: true);
            RecordHistory(sim, firstTick: 1, lastTick: 4);
            var remote = SnapshotRing(sim, 1, 4);

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 4, localHash: 1, remoteHash: 2);
            net.RaiseProbeResponse(0, TickHashResponse(remote, net.SentRequests[0], agreeThrough: 2, from: 1, to: 4));

            var l2 = net.SentRequests[1];
            net.RaiseProbeResponse(0, BreakdownResponse(remote, l2, tick: 3, corruptSystemIndex: 0));

            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(DesyncLayer.System, verdict.Value.Layer);
            Assert.AreEqual(0, verdict.Value.TypeIdOrParticipantIdx);
        }

        [Test] // L2: the requester-side probe de-dup guard (_lastProbedTickByPeer) is keyed by peerId and
               // outlives the episode. A departed player must clear its entry so a recycled peerId is not
               // wrongly suppressed — mirrors the service-side _probeServeState disconnect cleanup. RED
               // until NotifyPlayerLeft prunes the guard.
        public void PlayerLeft_ClearsProbeDedupGuard_SoRecycledPeerCanProbeAgain()
        {
            var (engine, sim, net) = NewEngine(withParticipant: true);
            RecordHistory(sim, firstTick: 1, lastTick: 4);
            var remote = SnapshotRing(sim, 1, 4);

            // First probe at tick 4, concluded to a verdict so the episode releases (the guard entry
            // persists past release — that is the whole de-dup mechanism).
            net.RaiseDesyncDetected(playerId: 1, tick: 4, localHash: 1, remoteHash: 2);
            net.RaiseProbeResponse(0, TickHashResponse(remote, net.SentRequests[0], agreeThrough: 2, from: 1, to: 4));
            net.RaiseProbeResponse(0, BreakdownResponse(remote, net.SentRequests[1], tick: 3, corruptSystemIndex: 0));
            int afterFirst = net.SentRequests.Count;   // L1 + L2

            // Same tick again, no leave → suppressed by the de-dup guard (no new request).
            net.RaiseDesyncDetected(playerId: 1, tick: 4, localHash: 1, remoteHash: 2);
            Assert.AreEqual(afterFirst, net.SentRequests.Count);

            // The player leaves (its peerId is now free to be recycled). The guard must clear so the
            // same tick can probe again.
            engine.NotifyPlayerLeft(1);
            net.RaiseDesyncDetected(playerId: 1, tick: 4, localHash: 1, remoteHash: 2);
            Assert.AreEqual(afterFirst + 1, net.SentRequests.Count);
        }

        // ── P2P classification: Input vs State ──────────────────
        // The layer says WHERE it diverged; the class says WHY — inputs differed (propagation/buffer =
        // engine-side) or the same inputs produced different state (determinism = game-side). SD settles
        // this at detection; P2P cannot (a peer has no view of what the other executed), so the responder
        // ships its command digest and the requester compares. These pin that comparison headlessly.

        [Test] // same command digest both sides, state still diverged ⇒ State (determinism violation)
        public void P2P_SameCommandDigest_ClassifiesStateDivergence()
        {
            var (engine, sim, net) = NewEngine(withCommandFactory: true);
            RecordHistory(sim, firstTick: 1, lastTick: 6);
            var remote = SnapshotRing(sim, 1, 6);

            // Seat a command at the diverged tick so the local digest is a real, non-zero number.
            net.RaiseCommandReceived(new EmptyCommand(playerId: 1, tick: 4));
            long localCmd = LocalCmdDigestAt(net, 4);
            Assert.AreNotEqual(0L, localCmd);   // guard: factory present + tick in input retention

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);
            net.RaiseProbeResponse(0, TickHashResponse(remote, net.SentRequests[0], agreeThrough: 3, from: 1, to: 6));

            var l2 = net.SentRequests[1];
            net.RaiseProbeResponse(0, BreakdownResponse(remote, l2, tick: 4,
                corruptComponentIndex: IndexOfType<TransformComponent>(), cmdHashAtTick: localCmd));

            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(DesyncClass.State, verdict.Value.Class);
            Assert.AreEqual(DesyncLayer.Component, verdict.Value.Layer);
        }

        [Test] // command digests differ ⇒ Input divergence (the peers executed different input sets)
        public void P2P_DifferingCommandDigest_ClassifiesInputDivergence()
        {
            var (engine, sim, net) = NewEngine(withCommandFactory: true);
            RecordHistory(sim, firstTick: 1, lastTick: 6);
            var remote = SnapshotRing(sim, 1, 6);

            net.RaiseCommandReceived(new EmptyCommand(playerId: 1, tick: 4));
            long localCmd = LocalCmdDigestAt(net, 4);
            Assert.AreNotEqual(0L, localCmd);

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);
            net.RaiseProbeResponse(0, TickHashResponse(remote, net.SentRequests[0], agreeThrough: 3, from: 1, to: 6));

            var l2 = net.SentRequests[1];
            net.RaiseProbeResponse(0, BreakdownResponse(remote, l2, tick: 4,
                corruptComponentIndex: IndexOfType<TransformComponent>(), cmdHashAtTick: localCmd ^ 0x1234));

            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(DesyncClass.Input, verdict.Value.Class);
        }

        [Test] // responder could not produce a digest (tick trimmed / history off) ⇒ class withheld as
               // Unknown, but the layer is still reported — a missing class must not suppress the whole verdict
        public void P2P_ResponderDigestUnavailable_ClassUnknownButLayerReported()
        {
            var (engine, sim, net) = NewEngine(withCommandFactory: true);
            RecordHistory(sim, firstTick: 1, lastTick: 6);
            var remote = SnapshotRing(sim, 1, 6);

            net.RaiseCommandReceived(new EmptyCommand(playerId: 1, tick: 4));   // local digest is real
            Assert.AreNotEqual(0L, LocalCmdDigestAt(net, 4));

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);
            net.RaiseProbeResponse(0, TickHashResponse(remote, net.SentRequests[0], agreeThrough: 3, from: 1, to: 6));

            var l2 = net.SentRequests[1];
            // Responder reports 0 = "no digest". Even with a real local digest, class must not be guessed.
            net.RaiseProbeResponse(0, BreakdownResponse(remote, l2, tick: 4,
                corruptComponentIndex: IndexOfType<TransformComponent>(), cmdHashAtTick: 0));

            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(DesyncClass.Unknown, verdict.Value.Class);
            Assert.AreEqual(DesyncLayer.Component, verdict.Value.Layer);   // layer survives a missing class
        }

        [Test] // no agreed tick inside the shared window ⇒ say so, do NOT name the oldest visible one
        public void NoAnchorInWindow_DegradesInsteadOfGuessingATick()
        {
            var (engine, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 6);
            var remote = SnapshotRing(sim, 1, 6);

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);

            // Every tick the responder holds already disagrees — its retention starts after the split.
            net.RaiseProbeResponse(0, TickHashResponse(remote, net.SentRequests[0], agreeThrough: 0, from: 1, to: 6));

            XAssert.Single(net.SentRequests);                       // no L2 — there is no tick to ask about
            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(DesyncLayer.None, verdict.Value.Layer);   // honest "not localized"
        }

        [Test] // history off on the far side ⇒ conclude immediately, do not sit out the timeout
        public void UnavailableRemoteHistory_ConcludesWithoutLayer()
        {
            var (engine, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 4);

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 4, localHash: 1, remoteHash: 2);

            var l1 = net.SentRequests[0];
            net.RaiseProbeResponse(0, new DesyncProbeResponseMessage
            {
                CorrelationId = l1.CorrelationId,
                Level = l1.Level,
                BaseTick = l1.FromTick,
                Payload = DesyncProbePayload.PackL1(new int[0], new long[0], 0),
            });

            XAssert.Single(net.SentRequests);
            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(DesyncLayer.None, verdict.Value.Layer);
        }

        [Test] // a round trip whose answer never arrives must expire on the timeout and conclude without a
               // layer — driven on a virtual clock so it costs no real 2s (the timeout is wall-clock and off
               // the deterministic path, hence the injectable probe clock rather than a sim-tick accumulator)
        public void ProbeTimeout_ExpiresAndConcludesWithoutLayer()
        {
            var (engine, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 6);

            long now = 1_000_000;
            engine.SetProbeNowProviderForTest(() => now);

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 7, remoteHash: 8);
            XAssert.Single(net.SentRequests);        // L1 in flight, awaiting a response that never comes

            // Well before the deadline (PROBE_TIMEOUT_MS = 2000): the sweep must not conclude early.
            now += 100;
            engine.Update(0.025f);                  // runs SweepDesyncProbes (before the State gate)
            Assert.IsFalse(verdict.HasValue);
            XAssert.Single(net.SentRequests);        // still pending — no L2, no premature verdict

            // Well past the deadline: the pending probe is dropped and the episode concludes without a layer.
            now += 100_000;
            engine.Update(0.025f);

            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(DesyncLayer.None, verdict.Value.Layer);
            Assert.AreEqual(6, verdict.Value.DivergedTick);   // ConcludeWithoutLayer falls back to DetectTick (P2P offset 0)
        }

        [Test] // registries of different sizes ⇒ every index means something else. Refuse to name a component.
        public void BuildSkew_ReportsMismatchInsteadOfALayer()
        {
            var (engine, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 4);
            var remote = SnapshotRing(sim, 1, 4);

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 4, localHash: 1, remoteHash: 2);
            net.RaiseProbeResponse(0, TickHashResponse(remote, net.SentRequests[0], agreeThrough: 2, from: 1, to: 4));

            var l2 = net.SentRequests[1];
            net.RaiseProbeResponse(0, new DesyncProbeResponseMessage
            {
                CorrelationId = l2.CorrelationId,
                Level = l2.Level,
                BaseTick = l2.ToTick,
                // One component type fewer than this build has: a peer on another build.
                Payload = DesyncProbePayload.PackL2(new ulong[sim.HashHistoryTypeCount - 1],
                    new int[sim.HashHistoryTypeCount - 1], new ulong[sim.HashHistoryParticipantCount]),
            });

            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(DesyncLayer.None, verdict.Value.Layer);
            Assert.AreEqual(-1, verdict.Value.TypeIdOrParticipantIdx);
        }

        [Test] // A responder that answers about a tick we did not capture means the two sides disagree on
               // what to CALL a tick. This actually happened: SD keys both history rings by the tick that
               // produced the state, while the wire calls the same state one tick later, and probing in
               // wire ticks quietly missed the capture on one side and read the neighbouring slot on the
               // other. It must fail loudly and localize nothing — never diff against a mislabelled tick.
        public void ResponseForATickOutsideTheCapture_IsRefusedLoudly()
        {
            var (engine, sim, net, log) = NewEngineWithLog();
            RecordHistory(sim, firstTick: 1, lastTick: 6);
            var remote = SnapshotRing(sim, 1, 6);

            DesyncVerdict? verdict = null;
            engine.OnDesyncDiagnosed += v => verdict = v;

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);
            net.RaiseProbeResponse(0, TickHashResponse(remote, net.SentRequests[0], agreeThrough: 3, from: 1, to: 6));

            // A breakdown for a tick one past the captured window — the shape the tick-space bug took.
            var l2 = net.SentRequests[1];
            var offByOne = BreakdownResponse(remote, l2, tick: 4, corruptComponentIndex: IndexOfType<TransformComponent>());
            offByOne.BaseTick = 7;
            net.RaiseProbeResponse(0, offByOne);

            XAssert.Contains(log.Lines, l => l.Contains("probe tick space mismatch"));
            Assert.IsTrue(verdict.HasValue);
            Assert.AreEqual(DesyncLayer.None, verdict.Value.Layer);   // no component named off a mislabelled tick
        }

        [Test] // correlation ids are small monotonic ints — a third peer must not be able to answer for the responder
        public void ResponseFromTheWrongPeer_IsRejected()
        {
            var (engine, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 6);
            var remote = SnapshotRing(sim, 1, 6);

            bool diagnosed = false;
            engine.OnDesyncDiagnosed += _ => diagnosed = true;

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);
            var l1 = net.SentRequests[0];

            // Right correlation id, wrong sender.
            net.RaiseProbeResponse(fromPeerId: 9, TickHashResponse(remote, l1, agreeThrough: 3, from: 1, to: 6));

            XAssert.Single(net.SentRequests);   // no L2 was launched off the forged answer
            Assert.IsFalse(diagnosed);

            // The round trip is untouched — the real responder can still complete it.
            net.RaiseProbeResponse(0, TickHashResponse(remote, l1, agreeThrough: 3, from: 1, to: 6));
            Assert.AreEqual(2, net.SentRequests.Count);
        }

        [Test] // late, duplicated or invented correlation ids are dropped without a trace
        public void ResponseWithUnknownCorrelationId_IsIgnored()
        {
            var (engine, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 6);
            var remote = SnapshotRing(sim, 1, 6);

            bool diagnosed = false;
            engine.OnDesyncDiagnosed += _ => diagnosed = true;

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);
            var l1 = net.SentRequests[0];

            var stray = TickHashResponse(remote, l1, agreeThrough: 3, from: 1, to: 6);
            stray.CorrelationId = l1.CorrelationId + 500;
            net.RaiseProbeResponse(0, stray);

            XAssert.Single(net.SentRequests);
            Assert.IsFalse(diagnosed);
        }

        [Test] // a desync fires repeatedly as it escalates — one capture per peer, not one per re-fire
        public void RepeatedDetections_ProbeOnce()
        {
            var (_, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 6);

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);
            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);   // same tick, second compare point
            net.RaiseDesyncDetected(playerId: 1, tick: 7, localHash: 1, remoteHash: 2);   // ladder re-fire

            XAssert.Single(net.SentRequests);
        }

        [Test] // without local history there is nothing to diff a remote answer against — do not ask
        public void HistoryDisabled_SkipsTheProbeEntirely()
        {
            var (_, _, net) = NewEngine(historyTicks: 0);

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);

            Assert.IsEmpty(net.SentRequests);
        }

        [Test] // serving is a ring read, never a rewind: a tick the ring lost is answered "unavailable"
        public void Responder_ServesFromTheRing_AndAnswersEmptyWhenItCannot()
        {
            var (_, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 10, lastTick: 14);

            net.RaiseProbeRequested(3, new DesyncProbeRequestMessage
            {
                CorrelationId = 77,
                LevelEnum = DesyncProbeLevel.TickHashes,
                FromTick = 10,
                ToTick = 14,
            });

            var (peer, served) = XAssert.Single(net.SentResponses);
            Assert.AreEqual(3, peer);                       // answered to the peer it came FROM
            Assert.AreEqual(77, served.CorrelationId);
            Assert.IsTrue(DesyncProbePayload.TryUnpackL1(served.Payload, out int[] ticks, out long[] totals));
            Assert.AreEqual(new[] { 10, 11, 12, 13, 14 }, ticks);
            for (int i = 0; i < ticks.Length; i++)
            {
                Assert.IsTrue(sim.TryGetHashHistory(ticks[i], out long expected, out _));
                Assert.AreEqual(expected, totals[i]);
            }

            // Ticks the ring never held: an empty answer, not a reconstruction.
            net.SentResponses.Clear();
            net.RaiseProbeRequested(3, new DesyncProbeRequestMessage
            {
                CorrelationId = 78,
                LevelEnum = DesyncProbeLevel.Breakdown,
                FromTick = 900,
                ToTick = 900,
            });

            var (_, empty) = XAssert.Single(net.SentResponses);
            Assert.IsTrue(DesyncProbePayload.TryUnpackL2(empty.Payload, out ulong[] components, out _, out _));
            Assert.IsEmpty(components);
        }

        [Test] // a wire-supplied window must never spin the responder. The old serve walked `tick <= to`
               // with an unvalidated `to`: to=int.MaxValue makes tick++ wrap (unchecked) and loop forever,
               // and `to - from` overflowed the int span clamp. The scan is now bounded to
               // HashHistoryCapacity by a counter, so a hostile window returns promptly (empty — its
               // clamped span sits far past the ring's low ticks). Pre-fix this test HANGS.
        public void Responder_BoundsHostileTickWindow_AndDoesNotSpin()
        {
            var (_, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 6);

            // Case A: tiny span, but `to` at the int ceiling — the loop's upper bound is the trap, not the span.
            net.RaiseProbeRequested(3, new DesyncProbeRequestMessage
            {
                CorrelationId = 1,
                LevelEnum = DesyncProbeLevel.TickHashes,
                FromTick = int.MaxValue - 5,
                ToTick = int.MaxValue,
            });
            var (_, a) = XAssert.Single(net.SentResponses);
            Assert.IsTrue(DesyncProbePayload.TryUnpackL1(a.Payload, out int[] ticksA, out _));
            Assert.IsEmpty(ticksA);

            // Case B: from below zero and to at the ceiling — `to - from` overflows the old int span clamp.
            net.SentResponses.Clear();
            net.RaiseProbeRequested(3, new DesyncProbeRequestMessage
            {
                CorrelationId = 2,
                LevelEnum = DesyncProbeLevel.TickHashes,
                FromTick = -1,
                ToTick = int.MaxValue,
            });
            var (_, b) = XAssert.Single(net.SentResponses);
            Assert.IsTrue(DesyncProbePayload.TryUnpackL1(b.Payload, out int[] ticksB, out _));
            Assert.IsEmpty(ticksB);
        }

        [Test] // A peer that has just desynced must STILL be able to answer a probe. In P2P both peers
               // detect the same divergence and both dump their history at the same moment; when that dump
               // also cleared the ring, each side wiped exactly the data the other was about to ask for an
               // RTT later, and both diagnoses collapsed to "remote diagnostics unavailable". SD never
               // showed it — the server does not desync, so it never flushed.
        public void Responder_CanStillServe_AfterItsOwnDesyncFlushedTheHistory()
        {
            var (_, sim, net, log) = NewEngineWithLog();
            RecordHistory(sim, firstTick: 1, lastTick: 6);

            // The local peer desyncs too: this is what dumps the history.
            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);
            XAssert.Contains(log.Lines, l => l.Contains("[Desync][Diag][History]"));   // the dump really ran

            // The other peer's probe now arrives. It must be answered from the ring, not with "unavailable".
            net.SentResponses.Clear();
            net.RaiseProbeRequested(3, new DesyncProbeRequestMessage
            {
                CorrelationId = 42,
                LevelEnum = DesyncProbeLevel.Breakdown,
                FromTick = 4,
                ToTick = 4,
            });

            var (_, served) = XAssert.Single(net.SentResponses);
            Assert.IsTrue(DesyncProbePayload.TryUnpackL2(served.Payload, out ulong[] components, out _, out _));
            Assert.IsNotEmpty(components);
        }

        [Test] // the dump is per diverged tick: a re-firing desync must not re-print the same history
        public void HistoryDump_EmitsOncePerDivergedTick()
        {
            var (_, sim, net, log) = NewEngineWithLog();
            RecordHistory(sim, firstTick: 1, lastTick: 6);

            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);
            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);
            net.RaiseDesyncDetected(playerId: 1, tick: 6, localHash: 1, remoteHash: 2);

            XAssert.Single(log.Lines.FindAll(l => l.Contains("[Desync][Diag][History]")));
        }

        [Test] // a response must never route by the wire's RequesterPlayerId — only by the transport peer
        public void Responder_IgnoresTheClaimedRequesterPlayerId()
        {
            var (_, sim, net) = NewEngine();
            RecordHistory(sim, firstTick: 1, lastTick: 4);

            net.RaiseProbeRequested(2, new DesyncProbeRequestMessage
            {
                CorrelationId = 1,
                LevelEnum = DesyncProbeLevel.TickHashes,
                FromTick = 1,
                ToTick = 4,
                RequesterPlayerId = 999,   // forged
            });

            var (peer, _) = XAssert.Single(net.SentResponses);
            Assert.AreEqual(2, peer);   // the transport peer, not the claim
        }

        // ── verdict validation (SD server side) ───────────────────────

        [Test] // the index means a different thing per layer, so it is bounded per layer — one shared
               // bound would wave a participant index through and resolve it as some unrelated component
        public void Verdict_IndexIsValidatedAgainstTheLayerItBelongsTo()
        {
            var (_, _, net, log) = NewEngineWithLog(withParticipant: true);

            // System layer, index past the participant list → dropped.
            net.RaiseVerdictReported(4, new DesyncVerdictReportMessage
            {
                DivergedTick = 5,
                Class = (byte)DesyncClass.State,
                Layer = (byte)DesyncLayer.System,
                TypeIdOrParticipantIdx = 999,
            });
            XAssert.DoesNotContain(log.Lines, l => l.Contains("[Desync][Verdict][src=client-reported"));

            // Component layer, an unregistered typeId → dropped.
            net.RaiseVerdictReported(4, new DesyncVerdictReportMessage
            {
                DivergedTick = 5,
                Class = (byte)DesyncClass.State,
                Layer = (byte)DesyncLayer.Component,
                TypeIdOrParticipantIdx = 12345,
            });
            XAssert.DoesNotContain(log.Lines, l => l.Contains("[Desync][Verdict][src=client-reported"));

            // A well-formed one → logged, and marked as the untrusted claim it is.
            net.RaiseVerdictReported(4, new DesyncVerdictReportMessage
            {
                DivergedTick = 5,
                Class = (byte)DesyncClass.State,
                Layer = (byte)DesyncLayer.System,
                TypeIdOrParticipantIdx = 0,
            });
            XAssert.Contains(log.Lines, l => l.Contains("[Desync][Verdict][src=client-reported unverified]"));
        }

        [Test] // Unknown is a client-local degradation; an SD client always holds both digests, so it
               // cannot legitimately report it — and a report claiming it is refused
        public void Verdict_UnknownClass_IsRefused()
        {
            var (_, _, net, log) = NewEngineWithLog();

            net.RaiseVerdictReported(4, new DesyncVerdictReportMessage
            {
                DivergedTick = 5,
                Class = (byte)DesyncClass.Unknown,
                Layer = (byte)DesyncLayer.None,
                TypeIdOrParticipantIdx = -1,
            });

            XAssert.DoesNotContain(log.Lines, l => l.Contains("[Desync][Verdict][src=client-reported"));
        }

        [Test] // a tick far past the server's own is a bogus claim
        public void Verdict_OutOfRangeTick_IsRefused()
        {
            var (_, _, net, log) = NewEngineWithLog();

            net.RaiseVerdictReported(4, new DesyncVerdictReportMessage
            {
                DivergedTick = int.MaxValue,
                Class = (byte)DesyncClass.State,
                Layer = (byte)DesyncLayer.None,
                TypeIdOrParticipantIdx = -1,
            });

            XAssert.DoesNotContain(log.Lines, l => l.Contains("[Desync][Verdict][src=client-reported"));
        }

        // ── harness ───────────────────────────────────────────────────

        private static (KlothoEngine engine, EcsSimulation sim, ProbeNetworkStub net) NewEngine(
            int historyTicks = RingTicks, bool withParticipant = false, bool withCommandFactory = false)
        {
            var (engine, sim, net, _) = NewEngineWithLog(withParticipant, historyTicks, withCommandFactory);
            return (engine, sim, net);
        }

        private static (KlothoEngine engine, EcsSimulation sim, ProbeNetworkStub net, CapturingLogger log)
            NewEngineWithLog(bool withParticipant = false, int historyTicks = RingTicks, bool withCommandFactory = false)
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: 50);
            sim.Initialize();
            if (withParticipant)
                sim.AddSystem(new StatefulTestSystem { State = 1 }, SystemPhase.Update);

            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new TransformComponent { Position = FPVector3.Zero, Scale = FPVector3.One });

            var config = new SimulationConfig
            {
                Mode = NetworkMode.P2P,
                DiagnosticHistoryTicks = historyTicks,
                MaxRollbackTicks = 8,
            };
            var log = new CapturingLogger();
            var net = new ProbeNetworkStub();
            var engine = new KlothoEngine(config, new SessionConfig());
            engine.Initialize(sim, net, log);

            // Classification (Input vs State, P2P) needs a real command digest — without a factory
            // ComputeCommandDigest returns 0 and every class degrades to Unknown. Off by default so the
            // existing tests keep exercising the Unknown path; the classification tests opt in.
            if (withCommandFactory)
                engine.SetCommandFactory(new CommandFactory());

            return (engine, sim, net, log);
        }

        // Fills the ring with a distinct hash per tick, the way a running engine would.
        private static void RecordHistory(EcsSimulation sim, int firstTick, int lastTick)
        {
            for (int tick = firstTick; tick <= lastTick; tick++)
            {
                var entity = sim.Frame.CreateEntity();
                sim.Frame.Add(entity, new TransformComponent
                {
                    Position = new FPVector3(FP64.FromInt(tick), FP64.Zero, FP64.Zero),
                    Scale = FPVector3.One,
                });
                sim.ComputeAndRecordHashHistory(tick);
            }
        }

        /// <summary>
        /// The ring as it stands BEFORE a detection — which is the only chance to read it. Detection
        /// flushes the ring (and a rollback would overwrite it anyway), so a test standing in for the
        /// remote peer has to take its copy first, exactly as the engine's own capture does. That the
        /// engine still produces a correct diff against a ring that no longer exists is the property
        /// these tests are here to hold.
        /// </summary>
        private sealed class RingSnapshot
        {
            public readonly Dictionary<int, long> Totals = new Dictionary<int, long>();
            public readonly Dictionary<int, (ulong[] components, int[] counts, ulong[] systems)> Breakdowns =
                new Dictionary<int, (ulong[], int[], ulong[])>();
        }

        private static RingSnapshot SnapshotRing(EcsSimulation sim, int from, int to)
        {
            var snapshot = new RingSnapshot();
            for (int tick = from; tick <= to; tick++)
            {
                if (!sim.TryGetHashHistoryBreakdown(tick, out var components, out var counts, out var systems, out long total))
                    continue;
                snapshot.Totals[tick] = total;
                snapshot.Breakdowns[tick] = (components.ToArray(), counts.ToArray(), systems.ToArray());
            }
            return snapshot;
        }

        // A responder that agreed with us up to `agreeThrough` and disagreed after it.
        private static DesyncProbeResponseMessage TickHashResponse(RingSnapshot snapshot, DesyncProbeRequestMessage request,
            int agreeThrough, int from, int to)
        {
            var ticks = new List<int>();
            var totals = new List<long>();
            for (int tick = from; tick <= to; tick++)
            {
                if (!snapshot.Totals.TryGetValue(tick, out long total)) continue;
                ticks.Add(tick);
                totals.Add(tick <= agreeThrough ? total : total ^ 0x5A5A_5A5A);
            }

            return new DesyncProbeResponseMessage
            {
                CorrelationId = request.CorrelationId,
                Level = request.Level,
                BaseTick = ticks.Count > 0 ? ticks[0] : from,
                Payload = DesyncProbePayload.PackL1(ticks.ToArray(), totals.ToArray(), ticks.Count),
            };
        }

        // The responder's breakdown at `tick`: ours, with exactly one slot perturbed. cmdHashAtTick is the
        // responder's command digest for the tick — 0 (default) means "no digest", which degrades class to
        // Unknown; the classification tests pass the real value (or a perturbed one) to drive State/Input.
        private static DesyncProbeResponseMessage BreakdownResponse(RingSnapshot snapshot, DesyncProbeRequestMessage request,
            int tick, int corruptComponentIndex = -1, int corruptSystemIndex = -1, long cmdHashAtTick = 0)
        {
            var (components, counts, systems) = snapshot.Breakdowns[tick];

            var componentHashes = (ulong[])components.Clone();
            var systemHashes = (ulong[])systems.Clone();

            if (corruptComponentIndex >= 0) componentHashes[corruptComponentIndex] ^= 0xDEAD_BEEF;
            if (corruptSystemIndex >= 0) systemHashes[corruptSystemIndex] ^= 0xDEAD_BEEF;

            return new DesyncProbeResponseMessage
            {
                CorrelationId = request.CorrelationId,
                Level = request.Level,
                BaseTick = tick,
                CmdHashAtTick = cmdHashAtTick,
                Payload = DesyncProbePayload.PackL2(componentHashes, counts, systemHashes),
            };
        }

        /// <summary>
        /// The engine's OWN command digest for a tick, obtained by asking it to serve a probe (its
        /// responder reads the same input buffer the requester capture does). Lets a test set the remote
        /// digest to exactly match (State) or deliberately differ (Input) without recomputing it — and
        /// without the test project needing InternalsVisibleTo. Must be called while the ring still holds
        /// the tick (before a detection flushes it).
        /// </summary>
        private static long LocalCmdDigestAt(ProbeNetworkStub net, int tick)
        {
            net.SentResponses.Clear();
            net.RaiseProbeRequested(0, new DesyncProbeRequestMessage
            {
                CorrelationId = 9999,
                LevelEnum = DesyncProbeLevel.Breakdown,
                FromTick = tick,
                ToTick = tick,
            });
            var (_, served) = XAssert.Single(net.SentResponses);
            net.SentResponses.Clear();
            return served.CmdHashAtTick;
        }

        private static int IndexOfType<T>() where T : unmanaged, IComponent
        {
            int typeId = ComponentStorageRegistry.GetTypeId(typeof(T));
            int index = 0;
            foreach (var type in ComponentStorageRegistry.RegisteredTypes)
                if (ComponentStorageRegistry.GetTypeId(type) < typeId) index++;
            return index;
        }

        private sealed class StatefulTestSystem : ISystem, ISnapshotParticipant
        {
            public long State;

            public void Update(ref Frame frame) { }
            public int GetSnapshotSize() => 8;
            public void SaveSnapshot(ref SpanWriter writer) => writer.WriteInt64(State);
            public void RestoreSnapshot(ref SpanReader reader) => State = reader.ReadInt64();
        }

        private sealed class CapturingLogger : IKLogger
        {
            public readonly List<string> Lines = new List<string>();

            public bool IsEnabled(KLogLevel level) => true;
            public void Log(KLogLevel level, string message, Exception exception) => Lines.Add(message);
        }

        // IKlothoNetworkService with the probe surface bolted on. Records what the engine sends and lets
        // a test play the other peer by raising the receive events.
#pragma warning disable 67
        private sealed class ProbeNetworkStub : IKlothoNetworkService, IDesyncProbeNetwork
        {
            public readonly List<DesyncProbeRequestMessage> SentRequests = new List<DesyncProbeRequestMessage>();
            public readonly List<(int peerId, DesyncProbeResponseMessage msg)> SentResponses =
                new List<(int, DesyncProbeResponseMessage)>();
            public readonly List<DesyncVerdictReportMessage> SentVerdicts = new List<DesyncVerdictReportMessage>();

            public void SendDesyncProbeRequest(int targetPeerId, DesyncProbeRequestMessage msg) => SentRequests.Add(msg);
            public void SendDesyncProbeResponse(int targetPeerId, DesyncProbeResponseMessage msg) => SentResponses.Add((targetPeerId, msg));
            public void SendDesyncVerdict(DesyncVerdictReportMessage msg) => SentVerdicts.Add(msg);

            public bool TryResolveProbePeer(int playerId, out int peerId)
            {
                peerId = 0;
                return true;
            }

            public event Action<int, DesyncProbeRequestMessage> OnDesyncProbeRequested;
            public event Action<int, DesyncProbeResponseMessage> OnDesyncProbeResponse;
            public event Action<int, DesyncVerdictReportMessage> OnDesyncVerdictReported;

            public void RaiseDesyncDetected(int playerId, int tick, long localHash, long remoteHash)
                => OnDesyncDetected?.Invoke(playerId, tick, localHash, remoteHash);
            public void RaiseCommandReceived(ICommand command)
                => OnCommandReceived?.Invoke(command);
            public void RaiseProbeRequested(int fromPeerId, DesyncProbeRequestMessage msg)
                => OnDesyncProbeRequested?.Invoke(fromPeerId, msg);
            public void RaiseProbeResponse(int fromPeerId, DesyncProbeResponseMessage msg)
                => OnDesyncProbeResponse?.Invoke(fromPeerId, msg);
            public void RaiseVerdictReported(int peerId, DesyncVerdictReportMessage msg)
                => OnDesyncVerdictReported?.Invoke(peerId, msg);

            public SessionPhase Phase => SessionPhase.Playing;
            public SharedTimeClock SharedClock => default;
            public int PlayerCount => 0;
            public int SpectatorCount => 0;
            public int PendingLateJoinCatchupCount => 0;
            public bool AllPlayersReady => true;
            public int LocalPlayerId => 0;
            public bool IsHost => false;
            public int RandomSeed => 0;
            public IReadOnlyList<IPlayerInfo> Players { get; } = new List<IPlayerInfo>();

            public void Initialize(INetworkTransport transport, ICommandFactory commandFactory, IKLogger logger) { }
            public void CreateRoom(string roomName, int maxPlayers) { }
            public void JoinRoom(string roomName) { }
            public void LeaveRoom(bool keepReconnectCredentials = false) { }
            public void SetReady(bool ready) { }
            public void SendCommand(ICommand command) { }
            public void RequestCommandsForTick(int tick) { }
            public void SendSyncHash(int tick, long hash, long cmdHash) { }
            public void SendResyncFailureReport(int tick, ResyncFailureReason reason, long localHash, long remoteHash) { }
            public void BroadcastMatchAbort(byte reason) { }
            public void InvalidateLocalSyncHashes(int fromTick) { }
            public void InvalidateSyncHashes(int fromTick) { }
            public void Update() { }
            public void FlushSendQueue() { }
            public void ClearOldData(int tick) { }
            public void SendPlayerConfig(int playerId, PlayerConfigBase playerConfig) { }
            public void SetLocalTick(int tick) { }
            public void SetLocalAdvantage(int advantage) { }
            public void SendFullStateRequest(int currentTick) { }
            public void SendFullStateResponse(int peerId, int tick, byte[] stateData, long stateHash) { }
            public void BroadcastFullState(int tick, byte[] stateData, long stateHash, FullStateKind kind = FullStateKind.Unicast) { }

            public event Action OnGameStart;
            public event Action<long> OnCountdownStarted;
            public event Action<IPlayerInfo> OnPlayerJoined;
            public event Action<IPlayerInfo> OnPlayerLeft;
            public event Action<ICommand> OnCommandReceived;
            public event Action<int, int, long, long> OnDesyncDetected;
            public event Action<int, int> OnResyncFailureReported;
            public event Action<int> OnMatchAbortReceived;
            public event Action<int, int, bool> OnSyncHashCompared;
            public event Action<int, int, int> OnFrameAdvantageReceived;
            public event Action<int> OnLocalPlayerIdAssigned;
            public event Action<int, int> OnFullStateRequested;
            public event Action<int, byte[], long, FullStateKind> OnFullStateReceived;
            public event Action<IPlayerInfo> OnPlayerDisconnected;
            public event Action<IPlayerInfo> OnPlayerReconnected;
            public event Action OnReconnecting;
            public event Action<ReconnectRejectReason> OnReconnectFailed;
            public event Action OnReconnected;
            public event Action<int, int> OnLateJoinPlayerAdded;
            public event Action<SessionPhase> OnPhaseChanged;
            public event Action<int> OnPlayerCountChanged;
            public event Action<bool> OnAllPlayersReadyChanged;
        }
#pragma warning restore 67
    }
}
