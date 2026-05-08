using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

using ZLogger;

using xpTURN.Klotho.Core;

namespace Brawler
{
    public class BrawlerViewCallbacks : IViewCallbacks
    {
        private readonly BrawlerSimulationCallbacks _sim;
        private readonly ILogger _logger;

        public BrawlerViewCallbacks(BrawlerSimulationCallbacks sim, ILogger logger)
        {
            _sim = sim;
            _logger = logger;
        }

        public void OnGameStart(IKlothoEngine engine)
        {
            _logger?.ZLogInformation($"[Brawler] Game started: playerId={engine.LocalPlayerId}, tick={engine.CurrentTick}");

            _sim.SetEngine(engine);
            if (!engine.IsReplayMode)
                _sim.SendSpawnCommand(engine);   // During replay playback, use the recorded SpawnCharacterCommand — prevent duplicate send
        }

        public void OnTickExecuted(int tick) { }

        public void OnLateJoinActivated(IKlothoEngine engine)
        {
            _logger?.ZLogInformation($"[Brawler] Late join activated: playerId={engine.LocalPlayerId}, tick={engine.CurrentTick}");

            _sim.SetEngine(engine);
            _sim.SendSpawnCommand(engine);
        }
    }
}
