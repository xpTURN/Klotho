using xpTURN.Klotho.Core;
using xpTURN.Klotho.Logging;

namespace Brawler
{
    public class BrawlerViewCallbacks : IViewCallbacks
    {
        private readonly BrawlerSimulationCallbacks _sim;

        public BrawlerViewCallbacks(BrawlerSimulationCallbacks sim)
        {
            _sim = sim;
        }

        public void OnGameStart(IKlothoEngine engine)
        {
            engine.Logger?.KInformation($"[Brawler] Game started: playerId={engine.LocalPlayerId}, tick={engine.CurrentTick}");

            _sim.SetEngine(engine);
            // The slice heartbeat used to be wired here — an SD client's only door, because the
            // engine skips world init for it. The engine now paces slices itself from Update, which
            // reaches this host without a door at all.
            if (!engine.IsReplayMode)
                _sim.SendSpawnCommand(engine);   // During replay playback, use the recorded SpawnCharacterCommand — prevent duplicate send
        }

        public void OnTickExecuted(int tick) { }

        public void OnLateJoinActivated(IKlothoEngine engine)
        {
            engine.Logger?.KInformation($"[Brawler] Late join activated: playerId={engine.LocalPlayerId}, tick={engine.CurrentTick}");

            _sim.SetEngine(engine);
            _sim.SendSpawnCommand(engine);
        }
    }
}
