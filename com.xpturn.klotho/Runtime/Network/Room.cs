using System;
using xpTURN.Klotho.Logging;
using System.Collections.Concurrent;

using xpTURN.Klotho.Core;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Room lifecycle state
    /// </summary>
    public enum RoomState
    {
        Empty,
        Active,
        Draining,
        Disposing
    }

    /// <summary>
    /// An independent game room within a multi-room server.
    /// Each room owns its own simulation, network service, and engine instance.
    /// No state is shared between rooms.
    /// </summary>
    public class Room
    {
        public int RoomId { get; }
        public RoomState State { get; set; }

        public ISimulationConfig SimulationConfig { get; }
        public ISessionConfig SessionConfig { get; }
        public ISimulation Simulation { get; }
        public CommandFactory CommandFactory { get; }
        public RoomScopedTransport Transport { get; }
        public ServerNetworkService NetworkService { get; }
        public KlothoEngine Engine { get; }
        public ISimulationCallbacks Callbacks { get; }

        public ConcurrentQueue<InboundEntry> InboundQueue { get; } = new ConcurrentQueue<InboundEntry>();

        // Straggler tracking. The history is a sliding window over the cycles this room was actually
        // DISPATCHED — one bit per cycle, 1 = did not finish inside the stage-2 budget. Cycles that
        // told us nothing about this room (the budget itself collapsed) push no bit at all, so the
        // denominator stays "recent cycles that carried information", not "recent wall time".
        //
        // StragglerCount is that window's popcount, so the force-close threshold reads as "straggled
        // this often lately" rather than "straggled this many times ever". The window
        // length is structural — it is the width of the ulong — and the threshold that pairs with it
        // lives in ServerLoop; the two together are what fixes the rate.
        public const int StragglerWindowCycles = 64;

        private ulong _stragglerHistory;

        public int StragglerCount => PopCount(_stragglerHistory);
        public bool IsStraggler { get; set; }

        // Set by the ThreadPool worker at the end of Update(); read by the main thread (ServerLoop)
        // to decide straggler marking/recovery. volatile for cross-thread visibility.
        public volatile bool UpdateComplete;

        // Lifetime / drain metrics (set by RoomManager.CreateRoomAt; phase captured at drain transition)
        public long CreatedAtMs { get; set; }
        public SessionPhase DrainPhase { get; private set; }

        // Additive drain hook (set by RoomManager.CreateRoomAt from RoomManagerConfig.OnRoomDraining; null = no-op).
        // Invoked exactly once on this room's own update thread the tick Update() transitions State → Draining
        // (either branch). The core stays semantics-free: the subscriber decides what a drain MEANS (abandon vs
        // normal/abort) from EndRequestedAtUtc / DrainPhase, both core truths captured before this fires.
        public Action<Room> OnDraining { get; set; }

        // Match-end / abort grace state. Wall-time based so the EndGracePolicy.Pause case
        // (tick frozen) still progresses naturally. _endRequested (volatile) gates publication:
        // RequestEnd (ThreadPool worker) writes _endRequestedAtMs/EndReason/EndGrace, then sets
        // _endRequested last (release); cross-thread readers (RoomRouter, main thread) check
        // _endRequested first (acquire) so the companion fields are always visible.
        private volatile bool _endRequested;
        private long _endRequestedAtMs;
        public DateTimeOffset? EndRequestedAtUtc =>
            _endRequested ? DateTimeOffset.FromUnixTimeMilliseconds(_endRequestedAtMs) : (DateTimeOffset?)null;
        public TimeSpan EndGrace { get; private set; }
        public EndReason EndReason { get; private set; }

        // Engine event handlers held for unsubscription at Dispose. Set by RoomManager via AttachEngineHandlers.
        private Action<int, IMatchEndEvent> _engineMatchEndedHandler;
        private Action<AbortReason> _engineMatchAbortedHandler;

        private readonly IKLogger _logger;

        public Room(
            int roomId,
            ISimulationConfig simConfig,
            ISessionConfig sessionConfig,
            ISimulation simulation,
            CommandFactory commandFactory,
            RoomScopedTransport transport,
            ServerNetworkService networkService,
            KlothoEngine engine,
            ISimulationCallbacks callbacks,
            IKLogger logger)
        {
            RoomId = roomId;
            SimulationConfig = simConfig;
            SessionConfig = sessionConfig;
            Simulation = simulation;
            CommandFactory = commandFactory;
            Transport = transport;
            NetworkService = networkService;
            Engine = engine;
            Callbacks = callbacks;
            _logger = logger;

            State = RoomState.Empty;
        }

        /// <summary>
        /// Invoked by a ThreadPool worker during stage 2 of the server loop.
        /// Drains the inbound queue and updates the engine.
        /// </summary>
        public void Update(float elapsedSec)
        {
            DrainInboundQueue();
            Engine.Update(elapsedSec);

            if (State != RoomState.Active) return;

            // Match-end / abort grace expiry. Checked before ShouldDrain so the
            // explicit match-end / match-aborted reason wins when both fire in the same Update.
            if (EndRequestedAtUtc.HasValue && DateTimeOffset.UtcNow - EndRequestedAtUtc.Value >= EndGrace)
            {
                DrainPhase = NetworkService.Phase;
                State = RoomState.Draining;
                string reasonStr = EndReason == EndReason.MatchEnded ? "match-ended" : "match-aborted";
                _logger?.KInformation($"[Room {RoomId}] → Draining (reason={reasonStr}, phase={DrainPhase})");
                OnDraining?.Invoke(this); // subscriber filters this out via EndRequestedAtUtc.HasValue
                return;
            }

            if (ShouldDrain())
            {
                DrainPhase = NetworkService.Phase;
                State = RoomState.Draining;
                _logger?.KInformation($"[Room {RoomId}] → Draining (reason=all-peers-gone, phase={DrainPhase})");
                OnDraining?.Invoke(this); // all-peers-gone: EndRequestedAtUtc == null → the abandon case
            }
        }

        /// <summary>
        /// Requests room drain after a grace window. Wall-time based — works for both
        /// EndGracePolicy.Continue (user input keeps flowing) and EndGracePolicy.Pause (clients send
        /// StopCommand each frame so verified state halts character motion deterministically). In
        /// both cases the engine keeps ticking through the grace window.
        /// First-write-wins: subsequent calls are no-ops while a request is already pending. This naturally
        /// handles abort-during-grace — if a normal-end RequestEnd is already pending, a later abort RequestEnd
        /// is ignored so the result screen plays out to completion.
        /// </summary>
        public void RequestEnd(EndReason reason, TimeSpan grace)
        {
            if (_endRequested)
            {
                _logger?.KDebug($"[Room {RoomId}] RequestEnd ignored (already pending: reason={EndReason}, grace={EndGrace.TotalMilliseconds:F0}ms); incoming reason={reason}");
                return;
            }
            // Companion fields first, then the volatile flag last (release) — publishes the above to readers.
            _endRequestedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            EndReason = reason;
            EndGrace = grace;
            _endRequested = true;
            _logger?.KInformation($"[Room {RoomId}] RequestEnd: reason={reason}, grace={grace.TotalMilliseconds:F0}ms");
        }

        /// <summary>
        /// Subscribes the given handlers to Engine.OnMatchEnded / Engine.OnMatchAborted and stores the
        /// delegate references for unsubscription at Dispose time. Called by RoomManager on room creation.
        /// </summary>
        public void AttachEngineHandlers(Action<int, IMatchEndEvent> onMatchEnded, Action<AbortReason> onMatchAborted)
        {
            _engineMatchEndedHandler = onMatchEnded;
            _engineMatchAbortedHandler = onMatchAborted;
            Engine.OnMatchEnded += onMatchEnded;
            Engine.OnMatchAborted += onMatchAborted;
        }

        private void DetachEngineHandlers()
        {
            if (_engineMatchEndedHandler != null) Engine.OnMatchEnded -= _engineMatchEndedHandler;
            if (_engineMatchAbortedHandler != null) Engine.OnMatchAborted -= _engineMatchAbortedHandler;
            _engineMatchEndedHandler = null;
            _engineMatchAbortedHandler = null;
        }

        /// <summary>
        /// Consumes every message from the inbound queue and raises them as RoomScopedTransport events.
        /// Consumed Data buffers are returned via StreamPool.ReturnBuffer.
        /// </summary>
        public void DrainInboundQueue()
        {
            while (InboundQueue.TryDequeue(out InboundEntry entry))
            {
                switch (entry.Type)
                {
                    case InboundEventType.Data:
                        try
                        {
                            Transport.RaiseDataReceived(entry.PeerId, entry.Buffer, entry.Length);
                        }
                        catch (Exception ex)
                        {
                            _logger?.KError($"[Room {RoomId}] RaiseDataReceived exception: peerId={entry.PeerId}, len={entry.Length}, ex={ex.Message}");
                        }
                        finally
                        {
                            StreamPool.ReturnBuffer(entry.Buffer);
                        }
                        break;

                    case InboundEventType.Connected:
                        Transport.RaisePeerConnected(entry.PeerId);
                        break;

                    case InboundEventType.Disconnected:
                        Transport.RaisePeerDisconnected(entry.PeerId);
                        break;
                }
            }
        }

        /// <summary>
        /// Determines whether the room should transition to Draining.
        /// Must be evaluated after DrainInboundQueue() completes — evaluating it
        /// while a ConnectEvent from the same batch is still unprocessed can yield a false positive.
        /// </summary>
        public bool ShouldDrain()
        {
            return NetworkService.PeerToPlayerCount == 0
                && NetworkService.PendingPeerCount == 0
                && NetworkService.PeerSyncStateCount == 0
                && NetworkService.DisconnectedPlayerCount == 0;
        }

        /// <summary>
        /// Releases resources after draining completes. Invoked by RoomManager on the main thread.
        /// </summary>
        public void Dispose()
        {
            // Diagnostic — pre-dispose dump of per-room ECS storage and static pools.
            // Summary line stays at Information; per-component breakdown demoted to Debug.
            _logger?.KInformation($"[Room {RoomId}] Dispose pre-dump: commandPoolTotal={CommandPool.GetTotalPooledCount()} commandPoolTypes={CommandPool.GetPooledTypeCount()}");
            if (Simulation is xpTURN.Klotho.ECS.EcsSimulation ecsSimDiag)
                ecsSimDiag.LogComponentHashes(_logger, $"RoomDispose:{RoomId}", logLevel: KLogLevel.Debug);

            DetachEngineHandlers();

            NetworkService.LeaveRoom();

            // Clear any leftover queue entries (safety guard)
            while (InboundQueue.TryDequeue(out InboundEntry entry))
            {
                if (entry.Type == InboundEventType.Data && entry.Buffer != null)
                    StreamPool.ReturnBuffer(entry.Buffer);
            }

            _stragglerHistory = 0;   // slot reuse, unrelated to the sliding window's own decay
            IsStraggler = false;
            UpdateComplete = false;
            State = RoomState.Empty;

            _logger?.KInformation($"[Room {RoomId}] → Empty (disposed)");
        }

        /// <summary>
        /// Records that this room did not complete its Update within the stage-2 budget.
        /// <para>
        /// <paramref name="countTowardOverload"/> separates the two things this used to conflate:
        /// <see cref="IsStraggler"/> is a fact ("this room's worker is still running", which the
        /// dispatch and teardown guards depend on) while <see cref="StragglerCount"/> is a judgement
        /// ("this room is bad"). When the cycle itself denied every room a usable window, the fact
        /// still holds but the judgement is not about this room.
        /// </para>
        /// <para>
        /// No default argument on purpose: the single call site is inside the loop that decides
        /// attribution, and a default would let a new call site skip that decision silently.
        /// </para>
        /// </summary>
        public void MarkStraggler(bool countTowardOverload)
        {
            IsStraggler = true;
            if (countTowardOverload)
                _stragglerHistory = (_stragglerHistory << 1) | 1UL;
            // else: no bit either way. Pushing a 0 would read as "this room was fine", which would
            // forgive real history for a reason that has nothing to do with this room's health.
        }

        /// <summary>
        /// Records that this room finished inside the stage-2 budget. This is the recovery half of
        /// the window: enough clean cycles push old straggles out of it, so a burst is absorbed while
        /// a room that keeps missing stays above the threshold.
        /// <para>
        /// Called for every completed room every cycle — including cycles whose budget collapsed.
        /// Finishing inside a 1ms window is strong evidence of health, so it is not an exception to
        /// the budget-collapse suppression, which only ever works in the opposite direction.
        /// </para>
        /// </summary>
        public void NoteCleanCycle()
        {
            _stragglerHistory <<= 1;
        }

        // Local SWAR popcount: this assembly has no System.Numerics dependency, and
        // BitOperations.PopCount is absent from the netstandard2.1 profile Unity compiles against.
        private static int PopCount(ulong v)
        {
            v -= (v >> 1) & 0x5555555555555555UL;
            v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
            v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((v * 0x0101010101010101UL) >> 56);
        }
    }
}

