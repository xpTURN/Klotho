using System.Collections.Generic;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Helper.Tests
{
    /// <summary>
    /// Edit-mode unit sweep over the disconnect-length × QuorumMissDropTicks matrix.
    /// Each cell exercises disconnect → stalled-host advance → reconnect → recovery, and
    /// asserts SweepPassCriteria (no WIPED, chain advance, hash consistent, no OnResyncFailed).
    /// In-process; designed for CI per-PR execution. Cells that fail surface real regressions
    /// or as-yet-unsupported (cell.DisconnectDurationSec, N) combinations.
    /// </summary>
    [TestFixture]
    internal class SweepReconnectRecoveryTests
    {
        private ILogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddZLoggerUnityDebug();
            });
            _logger = loggerFactory.CreateLogger("SweepReconnectRecoveryTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
        }

        public static IEnumerable<SweepMatrixConfigGenerator.Cell> Cells()
            => SweepMatrixConfigGenerator.AllCells();

        [TestCaseSource(nameof(Cells))]
        public void Sweep_DisconnectReconnectRecovery(SweepMatrixConfigGenerator.Cell cell)
        {
            var simConfig = new SimulationConfig
            {
                TickIntervalMs = 50,
                QuorumMissDropTicks = cell.QuorumMissDropTicks,
            };
            var harness = new KlothoTestHarness(_logger).WithSimulationConfig(simConfig);
            try
            {
                harness.CreateHost(4);
                var guest = harness.AddGuest();
                harness.StartPlaying();

                const int baselineTick = 50;
                harness.AdvanceAllToTick(baselineTick);

                using var criteria = new SweepPassCriteria(harness);
                criteria.CaptureBaseline();

                int stallPlayerId = guest.LocalPlayerId;
                int disconnectTicks = (int)(cell.DisconnectDurationSec * 1000f / simConfig.TickIntervalMs);
                int stalledEndTick = baselineTick + disconnectTicks;

                harness.DisconnectPeer(guest);
                harness.AdvanceWithStalledPeer(stalledEndTick, stallPlayerId);

                harness.ReconnectPeer(guest);
                harness.PumpMessages(20);

                int recoverEndTick = stalledEndTick + 100;
                harness.AdvanceAllToTick(recoverEndTick);

                criteria.AssertAll(recoverEndTick);

                TestContext.WriteLine(
                    $"{cell} OK | wipe(in/sync)={criteria.WipeCountInput}/{criteria.WipeCountSyncedEvent} " +
                    $"resyncFailed={criteria.ResyncFailedCount} " +
                    $"hostTick={harness.Host.CurrentTick} lastVerified={harness.Host.Engine.LastVerifiedTick}");
            }
            finally
            {
                harness.Reset();
            }
        }
    }
}
