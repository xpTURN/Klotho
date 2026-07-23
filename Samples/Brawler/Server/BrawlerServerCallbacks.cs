using System.IO;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;


using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Serialization;
using Brawler;

namespace xpTURN.Klotho.BrawlerDedicatedServer
{
    public class BrawlerServerCallbacks : ISimulationCallbacks
    {
        private readonly IKLogger _logger;

        private readonly List<FPStaticCollider> _staticColliders;
        private readonly FPNavMesh _navMesh;
        private readonly List<IDataAsset> _dataAssets;

        private readonly int _maxPlayers;
        private readonly int _botCount;
        private readonly int _stageId;
#if DEBUG
        private readonly long _devAbortAfterMs; // DEV: >0 → StateDivergence-abort N ms into the match (abort-path e2e)
        private IKlothoEngine _engine;          // captured at OnInitializeWorld for the dev abort trigger
#endif

        public BrawlerServerCallbacks(IKLogger logger,
                                        List<FPStaticCollider> staticColliders,
                                        FPNavMesh navMesh,
                                        int maxPlayers,
                                        int botCount,
                                        List<IDataAsset> dataAssets = null,
                                        int stageId = 0,
                                        long devAbortAfterMs = 0)
        {
            _logger = logger;
            _staticColliders = staticColliders;
            _navMesh = navMesh;
            _dataAssets = dataAssets;

            _maxPlayers = maxPlayers;
            _botCount = botCount;
            _stageId = stageId;
#if DEBUG
            _devAbortAfterMs = devAbortAfterMs;
#endif
        }

        public void RegisterSystems(EcsSimulation simulation)
        {
            BotFSMSystem botFSMSystem = null;

            var query       = new FPNavMeshQuery(_navMesh, _logger);
            var pathfinder  = new FPNavMeshPathfinder(_navMesh, query, _logger);
            var funnel      = new FPNavMeshFunnel(_navMesh, query, _logger);
            var agentSystem = new FPNavAgentSystem(_navMesh, query, pathfinder, funnel, _logger);
            agentSystem.SetAvoidance(new FPNavAvoidance());
            // Registers the NavMesh boundary as ORCA static obstacles (wall avoidance) and applies
            // the baked asset's own Agent Radius as the obstacle inset — both peers load the same
            // asset, so the clearance correction stays symmetric without a hand-synced constant.
            agentSystem.LoadNavMeshObstacles();
            if (agentSystem.DebugObstacleCount == 0)
                _logger?.KWarning($"[BrawlerServerCallbacks] ORCA obstacles empty — NavMesh obstacle wiring missing or boundary-free mesh");

            botFSMSystem = new BotFSMSystem(agentSystem);
            botFSMSystem.SetQuery(query);

            BrawlerSimSetup.RegisterSystems(simulation, _logger, _dataAssets, _staticColliders, botFSMSystem, _stageId);
#if DEBUG
            // DEV: after BrawlerSimSetup so it runs last in the tick; fires the abort on the engine thread.
            if (_devAbortAfterMs > 0)
                simulation.AddSystem(
                    new DevAbortSystem(() => _engine?.AbortMatch(AbortReason.StateDivergence), _devAbortAfterMs),
                    SystemPhase.PostUpdate);
#endif
        }

        public void OnInitializeWorld(IKlothoEngine engine)
        {
            _logger?.KInformation($"[BrawlerServerCallbacks] OnInitializeWorld: seed={engine.RandomSeed}");
#if DEBUG
            _engine = engine; // capture for the dev abort trigger (called before any tick)
#endif
            BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);
        }

        public void OnPollInput(int playerId, int tick, ICommandSender sender)
        {
            // no-op: ServerInputCollector handles network input collection
        }

        // A late-joiner enters the world at its join tick — seed its entitlement loadout the same way
        // tick-0 players are seeded (OnInitializeWorld), so a restricted (e.g. "guest") late-joiner is
        // gated in-match. The server's verified entitlement is authoritative; clients receive the same
        // bytes via the late-join propagation, so every node seeds identical state at the join tick.
        public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId)
        {
            BrawlerSimSetup.SeedOneLoadout(ref frame, engine, playerId);
        }
    }
}
