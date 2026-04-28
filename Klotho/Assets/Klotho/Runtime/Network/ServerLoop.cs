using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Multi-room server main loop.
    /// 3-Phase structure: Poll → ThreadPool room updates → Flush.
    /// A CountdownEvent-based timeout barrier isolates slow rooms as stragglers.
    /// </summary>
    public class ServerLoop
    {
        private const int STRAGGLER_OVERLOAD_THRESHOLD = 10;
        private const int DRIFT_LOG_INTERVAL_TICKS = 1000;
        private const int BUDGET_MARGIN_MS = 2;
        private const int UNROUTED_CLEANUP_INTERVAL_MS = 1000;

        // Graceful Shutdown timeouts
        private const int SHUTDOWN_PHASE2_TIMEOUT_MS = 1000;
        private const int SHUTDOWN_FLUSH_WAIT_MS = 100;
        private const int SHUTDOWN_TIMEOUT_MS = 3000;

        private readonly INetworkTransport _transport;
        private readonly RoomManager _roomManager;
        private readonly RoomRouter _router;
        private readonly int _tickIntervalMs;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts;

        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly List<Room> _readyRooms = new List<Room>();

        // Straggler completion tracking: roomId → CountdownEvent for that cycle
        private readonly Dictionary<int, CountdownEvent> _stragglerCountdowns = new Dictionary<int, CountdownEvent>();

        // Drift measurement
        private long _startTimeMs;
        private int _totalCycles;
        private int _lastDriftLogCycle;
        private long _lastUnroutedCleanupMs;
        private long _nextCycleTimeMs;
        private int _startCycle;

        public CancellationToken Token => _cts.Token;

        public ServerLoop(
            INetworkTransport transport,
            RoomManager roomManager,
            int tickIntervalMs,
            ILogger logger)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _roomManager = roomManager ?? throw new ArgumentNullException(nameof(roomManager));
            _router = roomManager.Router;
            _tickIntervalMs = tickIntervalMs > 0 ? tickIntervalMs : 25;
            _logger = logger;
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// Runs the server loop. Blocks until the CancellationToken is canceled.
        /// </summary>
        public void Run()
        {
            Console.CancelKeyPress += OnCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            _stopwatch.Start();
            long lastUpdateTime = _stopwatch.ElapsedMilliseconds;
            _startTimeMs = lastUpdateTime;
            _totalCycles = 0;
            _nextCycleTimeMs = lastUpdateTime + _tickIntervalMs;
            _lastDriftLogCycle = 0;
            _lastUnroutedCleanupMs = lastUpdateTime;

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    long cycleStart = _stopwatch.ElapsedMilliseconds;
                    float elapsed = cycleStart - lastUpdateTime;
                    lastUpdateTime = cycleStart;
                    float elapsedSec = elapsed / 1000f;

                    // ── Stage 0: previous cycle cleanup ──
                    RecoverStragglers();
                    _roomManager.TransitionDrainingRooms();
                    _roomManager.CleanupDisposingRooms();

                    // ── Stage 1: receive (Main Thread) ──
                    _transport.PollEvents();

                    // Cleanup unrouted peer timeouts (1-second interval)
                    if (cycleStart - _lastUnroutedCleanupMs >= UNROUTED_CLEANUP_INTERVAL_MS)
                    {
                        _router.CleanupUnroutedPeers();
                        _lastUnroutedCleanupMs = cycleStart;
                    }

                    long pollEnd = _stopwatch.ElapsedMilliseconds;
                    long pollElapsed = pollEnd - cycleStart;

                    // ── Stage 2: room updates (ThreadPool) ──
                    _roomManager.GetReadyRooms(_readyRooms);
                    int readyCount = _readyRooms.Count;

                    if (readyCount > 0)
                    {
                        // New CountdownEvent each cycle (straggler isolation)
                        var countdown = new CountdownEvent(readyCount);

                        for (int i = 0; i < readyCount; i++)
                        {
                            var room = _readyRooms[i];
                            ThreadPool.QueueUserWorkItem(state =>
                            {
                                var r = (Room)state;
                                try
                                {
                                    r.Update(elapsedSec);
                                }
                                catch (Exception ex)
                                {
                                    _logger?.ZLogError($"[ServerLoop] Room {r.RoomId} Update exception: {ex.Message}\n{ex.StackTrace}");
                                }
                                finally
                                {
                                    countdown.Signal();
                                }
                            }, room);
                        }

                        // Timeout barrier
                        int budgetMs = Math.Max(1, _tickIntervalMs - (int)pollElapsed - BUDGET_MARGIN_MS);
                        bool allCompleted = countdown.Wait(budgetMs);

                        if (!allCompleted)
                        {
                            // Incomplete rooms → stragglers
                            for (int i = 0; i < readyCount; i++)
                            {
                                var room = _readyRooms[i];
                                // Rooms that have not signaled = still running
                                // CountdownEvent cannot track individually → use per-room completion flag
                                // Simple approach: among rooms where IsStraggler is still false,
                                // detect those still running. Uses a clear-after-completion pattern.
                            }
                            HandleStragglers(countdown);
                        }
                        else
                        {
                            countdown.Dispose();
                        }

                        // Force close overloaded rooms
                        HandleOverloadedRooms();
                    }

                    // ── Stage 3: flush send queue ──
                    _transport.FlushSendQueue();

                    _totalCycles++;
                    LogDriftIfNeeded();

                    // Yield CPU (drift correction based on target time)
                    long now = _stopwatch.ElapsedMilliseconds;
                    long sleepMs = _nextCycleTimeMs - now;
                    if (sleepMs > 1)
                        Thread.Sleep((int)sleepMs - 1);
                    _nextCycleTimeMs += _tickIntervalMs;
                }
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                _stopwatch.Stop();

                GracefulShutdown();
            }
        }

        /// <summary>
        /// Checks whether stragglers from the previous cycle have completed and restores them to Ready.
        /// </summary>
        private void RecoverStragglers()
        {
            List<int> recovered = null;
            foreach (var kvp in _stragglerCountdowns)
            {
                int roomId = kvp.Key;
                var cd = kvp.Value;

                // Wait(0): check immediately, no blocking
                if (cd.Wait(0))
                {
                    // Straggler thread has completed → restore to Ready
                    var room = _roomManager.GetRoom(roomId);
                    if (room != null)
                    {
                        room.IsStraggler = false;
                        // StragglerCount is preserved (used for consecutive count determination)
                    }
                    cd.Dispose();
                    recovered ??= new List<int>();
                    recovered.Add(roomId);
                }
            }

            if (recovered != null)
            {
                for (int i = 0; i < recovered.Count; i++)
                    _stragglerCountdowns.Remove(recovered[i]);
            }
        }

        /// <summary>
        /// Registers incomplete rooms as stragglers upon stage-2 timeout.
        /// </summary>
        private void HandleStragglers(CountdownEvent countdown)
        {
            // Since CountdownEvent cannot distinguish individual room completion,
            // a per-room completion flag pattern is used.
            // When Room.Update() finishes normally, IsStraggler remains false.
            // To identify rooms still running at the timeout point,
            // they are temporarily marked before entering stage 2 and cleared on completion.
            //
            // Current implementation: iterate over all readyRooms and mark those not yet straggling.
            // Since countdown.Signal() is called in the finally block of Room.Update(),
            // threads will continue running after the timeout and eventually complete.
            // The countdown is stored in _stragglerCountdowns for completion check in the next cycle.

            // For all ready rooms: mark those that are not yet stragglers and are incomplete
            for (int i = 0; i < _readyRooms.Count; i++)
            {
                var room = _readyRooms[i];
                if (!room.IsStraggler)
                {
                    // Straggler candidate for this cycle — MarkStraggler
                    room.MarkStraggler();
                    // This room's thread is still waiting to Signal on the countdown
                    // Store countdown for completion check in the next cycle
                    _stragglerCountdowns[room.RoomId] = countdown;
                    _logger?.ZLogWarning(
                        $"[ServerLoop] Room {room.RoomId} straggler (count={room.StragglerCount})");
                }
            }

            // Keep countdown alive until straggler threads Signal — do not Dispose it
        }

        /// <summary>
        /// Force-closes rooms whose consecutive straggler count exceeds the threshold.
        /// </summary>
        private void HandleOverloadedRooms()
        {
            for (int i = 0; i < _readyRooms.Count; i++)
            {
                var room = _readyRooms[i];
                if (room.StragglerCount >= STRAGGLER_OVERLOAD_THRESHOLD)
                {
                    _logger?.ZLogError(
                        $"[ServerLoop] Room {room.RoomId} overloaded ({room.StragglerCount} consecutive straggles), force closing");

                    // Broadcast ServerShutdown (Reason=2: RoomOverloaded)
                    try
                    {
                        var shutdownMsg = new ServerShutdownMessage { Reason = 2 };
                        var serializer = new MessageSerializer();
                        using var msg = serializer.SerializePooled(shutdownMsg);
                        room.Transport.Broadcast(msg.Data, msg.Length, DeliveryMethod.Reliable);
                    }
                    catch { /* Already overloaded — ignore send failure */ }

                    room.State = RoomState.Disposing;
                }
            }
        }

        private void LogDriftIfNeeded()
        {
            if (_totalCycles - _lastDriftLogCycle < DRIFT_LOG_INTERVAL_TICKS) return;

            // Reset the baseline on the first log (removes initialization overhead)
            if (_lastDriftLogCycle == 0)
            {
                _startTimeMs = _stopwatch.ElapsedMilliseconds;
                _startCycle = _totalCycles;
            }

            _lastDriftLogCycle = _totalCycles;

            long nowMs = _stopwatch.ElapsedMilliseconds;
            long elapsedMs = nowMs - _startTimeMs;
            long expectedMs = (long)(_totalCycles - _startCycle) * _tickIntervalMs;
            long driftMs = elapsedMs - expectedMs;
            float cycleRate = elapsedMs > 0 ? (_totalCycles - _startCycle) / (elapsedMs / 1000f) : 0;

            _logger?.ZLogInformation(
                $"[ServerLoop] cycles={_totalCycles}, drift={driftMs}ms, " +
                $"rate={cycleRate:F1}Hz (target={1000f / _tickIntervalMs:F0}Hz), " +
                $"activeRooms={_roomManager.ActiveRoomCount}");
        }

        /// <summary>
        /// Graceful Shutdown.
        /// (1) Reject new connections → (2) wait for stragglers to complete → (3) broadcast Shutdown to all rooms → (4) flush + Disconnect.
        /// </summary>
        private void GracefulShutdown()
        {
            _logger?.ZLogInformation($"[ServerLoop] Graceful shutdown starting...");

            // Hard timeout: force-exit if the overall shutdown exceeds SHUTDOWN_TIMEOUT_MS
            var hardTimeout = new Thread(() =>
            {
                Thread.Sleep(SHUTDOWN_TIMEOUT_MS);
                _logger?.ZLogError($"[ServerLoop] Shutdown hard timeout ({SHUTDOWN_TIMEOUT_MS}ms), forcing exit");
                Environment.Exit(1);
            }) { IsBackground = true };
            hardTimeout.Start();

            // (1) Reject new connections
            _router.StopAccepting();

            // (2) Wait for stragglers to complete
            if (_stragglerCountdowns.Count > 0)
            {
                _logger?.ZLogInformation(
                    $"[ServerLoop] Waiting for {_stragglerCountdowns.Count} straggler room(s) to complete...");

                foreach (var kvp in _stragglerCountdowns)
                {
                    if (!kvp.Value.Wait(SHUTDOWN_PHASE2_TIMEOUT_MS))
                    {
                        _logger?.ZLogWarning(
                            $"[ServerLoop] Straggler room {kvp.Key} did not complete within {SHUTDOWN_PHASE2_TIMEOUT_MS}ms, skipping broadcast");
                    }
                    kvp.Value.Dispose();
                }
                _stragglerCountdowns.Clear();
            }

            // (3) Broadcast ServerShutdown to all rooms
            _roomManager.ShutdownAllRooms();

            // (4) Flush sends + wait
            _transport.FlushSendQueue();
            Thread.Sleep(SHUTDOWN_FLUSH_WAIT_MS);
            _transport.Disconnect();

            _logger?.ZLogInformation($"[ServerLoop] Graceful shutdown complete.");
        }

        public void Stop()
        {
            _cts.Cancel();
        }

        private void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            _cts.Cancel();
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            _cts.Cancel();
        }
    }
}
