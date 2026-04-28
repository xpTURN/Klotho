using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger;

using UnityEngine;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using System.Collections.Generic;
using xpTURN.Klotho.Deterministic.Physics;

namespace Brawler
{
    public class BrawlerSimulationCallbacks : ISimulationCallbacks
    {
        private readonly BrawlerInputCapture _input;
        private readonly ILogger _logger;
        private readonly List<FPStaticCollider> _staticColliders;
        private readonly FPNavMesh _navMesh;
        private readonly List<IDataAsset> _dataAssets;
        private readonly int _maxPlayers;
        private readonly int _botCount;

        private IKlothoEngine _engine;
        private int _lastSpawnAttemptTick = -1;
        private const int SpawnRetryInterval = 20; // ~500ms at 40Hz

        public bool Spawned { get; set; }

        public FPNavMesh NavMesh { get { return _navMesh; } }
        public FPNavMeshQuery NavQuery { get; private set; }
        public BotFSMSystem BotFSMSystem { get; private set; }

        public BrawlerSimulationCallbacks(BrawlerInputCapture input,
                                          ILogger logger,
                                          List<FPStaticCollider> colliders,
                                          FPNavMesh navMesh,
                                          int maxPlayers,
                                          int botCount,
                                          List<IDataAsset> dataAssets = null)
        {
            _input = input;
            _logger = logger;
            _staticColliders = colliders;
            _navMesh = navMesh;
            _dataAssets = dataAssets;

            _maxPlayers = maxPlayers;
            _botCount = botCount;
        }

        // Called from BrawlerGameController — bot spawn is determined by botCount, so unused
        public void SetNetworkService(IKlothoNetworkService _) { }

        public void RegisterSystems(EcsSimulation simulation)
        {
            BotFSMSystem botFSMSystem = null;

            var query       = new FPNavMeshQuery(_navMesh, null);
            var pathfinder  = new FPNavMeshPathfinder(_navMesh, query, null);
            var funnel      = new FPNavMeshFunnel(_navMesh, query, null);
            var agentSystem = new FPNavAgentSystem(_navMesh, query, pathfinder, funnel, null);
            agentSystem.SetAvoidance(new FPNavAvoidance());

            botFSMSystem = new BotFSMSystem(agentSystem);
            botFSMSystem.SetQuery(query);

            NavQuery = query;
            BotFSMSystem = botFSMSystem;

            BrawlerSimSetup.RegisterSystems(
                simulation,
                _logger,
                _dataAssets,
                _staticColliders,
                botFSMSystem
            );
        }

        public void OnInitializeWorld(IKlothoEngine engine)
        {
            BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);
        }

        public void OnPollInput(int playerId, int tick, ICommandSender sender)
        {
            if (!Spawned)
            {
                if (_engine != null && _lastSpawnAttemptTick >= 0 && tick >= _lastSpawnAttemptTick + SpawnRetryInterval)
                    SendSpawnCommand(_engine);

                // Exclude spawn command send tick (InputBuffer 1 command per player — prevent overwrite)
                if (_lastSpawnAttemptTick >= 0 && tick > _lastSpawnAttemptTick)
                {
                    var emptyMove = CommandPool.Get<MoveInputCommand>();
                    emptyMove.PlayerId = playerId;
                    sender.Send(emptyMove);
                }
                return;
            }

            // Move command (InputCommand sets Tick to CurrentTick+InputDelay)
            var moveCmd = CommandPool.Get<MoveInputCommand>();
            moveCmd.PlayerId       = playerId;
            moveCmd.HorizontalAxis = _input.H;
            moveCmd.VerticalAxis   = _input.V;
            moveCmd.JumpPressed    = _input.Jump;
            moveCmd.JumpHeld       = _input.JumpHeld;
            sender.Send(moveCmd);

            // Attack command
            if (_input.Attack)
            {
                var attackCmd = CommandPool.Get<AttackCommand>();
                attackCmd.PlayerId     = playerId;
                attackCmd.AimDirection = GetNearestEnemyDirection(playerId) ?? _input.AimDirection;
                sender.Send(attackCmd);
            }

            // Skill command
            if (_input.SkillSlot >= 0)
            {
                var skillCmd = CommandPool.Get<UseSkillCommand>();
                skillCmd.PlayerId     = playerId;
                skillCmd.SkillSlot    = _input.SkillSlot;
                skillCmd.AimDirection = GetNearestEnemyDirection(playerId) ?? _input.AimDirection;
                sender.Send(skillCmd);
            }

            // Consume event-style input (send only once)
            _input.ConsumeOneShot();
        }

        public void SetEngine(IKlothoEngine engine)
        {
            _engine = engine;
        }

        private FPVector2? GetNearestEnemyDirection(int playerId)
        {
            var frame = ((EcsSimulation)_engine.Simulation).Frame;

            // Find my position
            FPVector3 selfPos = default;
            bool found = false;
            var selfFilter = frame.Filter<TransformComponent, OwnerComponent, CharacterComponent>();
            while (selfFilter.Next(out var e))
            {
                ref readonly var o = ref frame.GetReadOnly<OwnerComponent>(e);
                if (o.OwnerId != playerId) continue;
                ref readonly var c = ref frame.GetReadOnly<CharacterComponent>(e);
                if (c.IsDead) continue;
                selfPos = frame.GetReadOnly<TransformComponent>(e).Position;
                found = true;
                break;
            }
            if (!found) return null;

            // Search for nearest enemy
            FP64 minDistSqr = FP64.MaxValue;
            FPVector2 bestDir = default;
            bool hasTarget = false;
            var filter = frame.Filter<TransformComponent, OwnerComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                if (owner.OwnerId == playerId) continue;
                ref readonly var ch = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (ch.IsDead) continue;
                ref readonly var tr = ref frame.GetReadOnly<TransformComponent>(entity);
                FP64 dx = tr.Position.x - selfPos.x;
                FP64 dz = tr.Position.z - selfPos.z;
                FP64 distSqr = dx * dx + dz * dz;
                if (distSqr < minDistSqr)
                {
                    minDistSqr = distSqr;
                    FP64 len = FP64.Sqrt(distSqr);
                    bestDir = len > FP64.Zero
                        ? new FPVector2(dx / len, dz / len)
                        : FPVector2.Zero;
                    hasTarget = true;
                }
            }
            return hasTarget && bestDir != FPVector2.Zero ? bestDir : null;
        }

        public void SendSpawnCommand(IKlothoEngine engine)
        {
            int playerId = engine.LocalPlayerId;
            var rules    = ((EcsSimulation)engine.Simulation).Frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);
            int spawnIdx = playerId % rules.SpawnPositions.Length;
            FPVector3 pos  = rules.SpawnPositions[spawnIdx];

            // Query character selection from local player's BrawlerPlayerConfig (network-shared data)
            // If PlayerConfig has not arrived yet, fallback to 0 (Warrior) — Spawn retry loop will be called again
            var playerConfig = engine.GetPlayerConfig<BrawlerPlayerConfig>(playerId);

            var cmd = CommandPool.Get<SpawnCharacterCommand>();
            cmd.PlayerId       = playerId;
            cmd.Tick           = engine.CurrentTick + engine.InputDelay;
            cmd.CharacterClass = playerConfig?.SelectedCharacterClass ?? 0;
            cmd.SpawnPosition  = new FPVector2(pos.x, pos.z);
            _lastSpawnAttemptTick = cmd.Tick;
            engine.InputCommand(cmd);

            _logger?.ZLogInformation($"[Brawler] Spawn command sent (PlayerId={playerId}, Tick={engine.CurrentTick + engine.InputDelay})");
        }
    }
}
