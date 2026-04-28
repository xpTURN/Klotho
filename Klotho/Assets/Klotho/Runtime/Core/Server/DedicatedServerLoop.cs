using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using ZLogger;

using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Server main loop utility.
    /// Calls engine.Update(elapsed) using a hybrid Stopwatch + Thread.Sleep timing strategy.
    /// Used in headless .NET environments.
    /// </summary>
    public class DedicatedServerLoop
    {
        private const int DRIFT_LOG_INTERVAL_TICKS = 1000;

        private readonly ILogger _logger;
        private readonly IKlothoEngine _engine;
        private readonly INetworkTransport _transport;
        private readonly int _tickIntervalMs;
        private readonly CancellationTokenSource _cts;

        private readonly Stopwatch _stopwatch = new Stopwatch();
        private long _lastUpdateTime;
        private float _sleepAccumulator;

        // Drift measurement (anchored at the first tick execution)
        private long _startTimeMs;
        private int _lastLoggedTick;
        private int _startTick;

        /// <summary>
        /// CancellationToken that lets external code request the loop to stop.
        /// </summary>
        public CancellationToken Token => _cts.Token;

        public DedicatedServerLoop(IKlothoEngine engine, int tickIntervalMs, ILogger logger)
            : this(engine, null, tickIntervalMs, logger, new CancellationTokenSource())
        {
        }

        public DedicatedServerLoop(IKlothoEngine engine, INetworkTransport transport, int tickIntervalMs, ILogger logger)
            : this(engine, transport, tickIntervalMs, logger, new CancellationTokenSource())
        {
        }

        public DedicatedServerLoop(IKlothoEngine engine, int tickIntervalMs, ILogger logger, CancellationTokenSource cts)
            : this(engine, null, tickIntervalMs, logger, cts)
        {
        }

        public DedicatedServerLoop(IKlothoEngine engine, INetworkTransport transport, int tickIntervalMs, ILogger logger, CancellationTokenSource cts)
        {
            _logger = logger;
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _transport = transport;
            _tickIntervalMs = tickIntervalMs > 0 ? tickIntervalMs : 25;
            _cts = cts ?? new CancellationTokenSource();
        }

        /// <summary>
        /// Run the server loop. Blocks until the CancellationToken is cancelled.
        /// Automatically wires up OS signals (Ctrl+C, ProcessExit).
        /// </summary>
        public void Run()
        {
            // OS signals → CancellationToken wiring
            Console.CancelKeyPress += OnCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            _stopwatch.Start();
            _lastUpdateTime = _stopwatch.ElapsedMilliseconds;
            _sleepAccumulator = 0;
            _startTimeMs = _stopwatch.ElapsedMilliseconds;
            _lastLoggedTick = _engine.CurrentTick;

            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    long now = _stopwatch.ElapsedMilliseconds;
                    float elapsed = now - _lastUpdateTime;
                    _lastUpdateTime = now;
                    _sleepAccumulator += elapsed;

                    // Poll incoming events (Transport → NetworkService event callbacks)
                    _transport?.PollEvents();

                    // Forward elapsed time to the engine (ms → s)
                    int tickBefore = _engine.CurrentTick;
                    _engine.Update(elapsed / 1000f);

                    // Record the first tick execution time (excluding Lobby/Syncing wait)
                    if (_startTick == 0 && _engine.CurrentTick > tickBefore)
                    {
                        _startTick = tickBefore + 1;
                        _startTimeMs = now;
                    }

                    // Drift logging
                    LogDriftIfNeeded(now);

                    // Yield CPU (Stopwatch + Sleep hybrid)
                    while (_sleepAccumulator >= _tickIntervalMs)
                        _sleepAccumulator -= _tickIntervalMs;

                    float remainingMs = _tickIntervalMs - _sleepAccumulator;
                    if (remainingMs > 2)
                        Thread.Sleep(Math.Max(1, (int)remainingMs - 1));
                }
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                _stopwatch.Stop();
            }
        }

        private void LogDriftIfNeeded(long nowMs)
        {
            int tick = _engine.CurrentTick;
            if (_startTick == 0 || tick <= _startTick) return;
            if (tick - _lastLoggedTick < DRIFT_LOG_INTERVAL_TICKS) return;
            _lastLoggedTick = tick;

            int ticksSinceStart = tick - _startTick;
            long elapsedMs = nowMs - _startTimeMs;
            long expectedMs = (long)ticksSinceStart * _tickIntervalMs;
            long driftMs = elapsedMs - expectedMs;
            float tickRate = elapsedMs > 0 ? ticksSinceStart / (elapsedMs / 1000f) : 0;
            float targetHz = 1000f / _tickIntervalMs;

            _logger?.ZLogInformation(
                $"[DedicatedServerLoop][TickStats] tick={tick}, drift={driftMs}ms, " +
                $"tickRate={tickRate:F1}Hz (target={targetHz:F0}Hz)");
        }

        /// <summary>
        /// Request the loop to stop.
        /// </summary>
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
