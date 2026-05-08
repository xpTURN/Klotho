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

        // Spawn cmd extra lead. Escalates by SPAWN_DELAY_STEP on each PastTick reject.
        // Monotonic latch until match boundary (BrawlerGameController re-news _simCallbacks).
        private int _extraSpawnDelay = 0;
        private const int SPAWN_DELAY_STEP = 4;     // ~100ms at 40Hz, one escalation step.
        private const int SPAWN_DELAY_MAX  = 40;    // ~1s, cap. Hit triggers warning + latch.

        // Cap-hit Error log fires once only to avoid burst on repeated post-cap rejects.
        private bool _capHitLogged = false;
        // Post-cap reject counter — diagnostic visibility for retry frequency after cap.
        private int _capHitRejectCount = 0;

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
            if (_engine == null) return;

#if KLOTHO_FAULT_INJECTION
            // Duplicate path: bypass HasOwnCharacter so spawn cmd is re-sent on cooldown even
            // after spawn success → server's HandleSpawn hits TryFindCharacter guard → Duplicate reject
            // → CommandRejectedMessage unicast back to client.
            // IMPORTANT: spawn cmd and move cmd both target (CurrentTick + InputDelayTicks). If we let
            // the regular Move/Attack send proceed in the same poll, it overwrites the spawn cmd in the
            // InputBuffer (single cmd per (tick, playerId)) — defeating the force retry. Return early
            // on the spawn-send poll so the spawn cmd survives the round-trip.
            if (xpTURN.Klotho.Diagnostics.FaultInjection.ForceSpawnRetryPlayerIds.Contains(playerId))
            {
                if (_lastSpawnAttemptTick < 0 || tick >= _lastSpawnAttemptTick + SpawnRetryInterval)
                {
                    _logger?.ZLogWarning($"[FaultInjection][Brawler] Forced spawn retry: playerId={playerId}, tick={tick}");
                    SendSpawnCommand(_engine);
                    return;
                }
            }
#endif

            // ECS frame is the single source of truth — listener-pattern flags are vulnerable to rollback noise.
            var frame = ((EcsSimulation)_engine.Simulation).Frame;
            if (!HasOwnCharacter(frame, playerId))
            {
                if (_lastSpawnAttemptTick < 0 || tick >= _lastSpawnAttemptTick + SpawnRetryInterval)
                    SendSpawnCommand(_engine);

                // Exclude (a) the spawn send tick itself, and (b) the tick whose emptyMove target tick
                // equals the spawn cmd's target tick. (b) happens at T1 = T0 + _extraSpawnDelay during
                // escalation: emptyMove target (T1 + InputDelay) collides with spawn target
                // (T0 + InputDelay + _extraSpawnDelay) and would overwrite the spawn cmd in the server's
                // InputBuffer (one command per (tick, playerId) slot, last write wins). Without this
                // guard the spawn cmd never reaches HandleSpawn → no PastTick reject → no escalation.
                if (_lastSpawnAttemptTick >= 0
                    && tick > _lastSpawnAttemptTick
                    && tick != _lastSpawnAttemptTick + _extraSpawnDelay)
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
            engine.OnCommandRejected += HandleCommandRejected;
        }

        // Receives only LocalPlayer's command rejections. CommandRejectedMessage is unicast from
        // server to the originating client and dispatched through KlothoEngine.HandleCommandRejected,
        // so the playerId is implicitly _engine.LocalPlayerId.
        private void HandleCommandRejected(int tick, int cmdTypeId, RejectionReason reason)
        {
            if (cmdTypeId != SpawnCharacterCommand.TYPE_ID) return;

            // Spawn cmd duplicate-rejected: server already has the character. Clear the cooldown latch
            // so the next OnPollInput re-evaluates the state-driven query without waiting out the cooldown.
            // The state-driven query is the primary self-heal path; this hint just shortens the latency.
            if (reason == RejectionReason.Duplicate)
            {
                _lastSpawnAttemptTick = -1;
                _logger?.ZLogInformation($"[Brawler] Spawn duplicate-rejected: tick={tick} — cooldown cleared");
                return;
            }

            // Spawn cmd past-tick rejected: the cmd's target tick fell behind server's _lastExecutedTick.
            // Escalate spawn-only lead by SPAWN_DELAY_STEP so the next retry overshoots far enough.
            // Cooldown is cleared so the next OnPollInput re-issues immediately with the new lead.
            if (reason == RejectionReason.PastTick)
            {
                _lastSpawnAttemptTick = -1;

                if (_extraSpawnDelay < SPAWN_DELAY_MAX)
                {
                    _extraSpawnDelay += SPAWN_DELAY_STEP;
                    _logger?.ZLogWarning($"[Brawler] Spawn past-tick rejected: tick={tick}, extraSpawnDelay={_extraSpawnDelay}");
                }
                else
                {
                    // Cap reached. Emit Error once, then track post-cap rejects at Debug level for diagnostics.
                    if (!_capHitLogged)
                    {
                        _capHitLogged = true;
                        _logger?.ZLogError($"[Brawler] Spawn past-tick reject loop: extraSpawnDelay capped at {SPAWN_DELAY_MAX} — server may be unreachable or RTT abnormal");
                    }
                    _capHitRejectCount++;
                    _logger?.ZLogDebug($"[Brawler] Spawn past-tick post-cap: count={_capHitRejectCount}, tick={tick}");
                }
            }
        }

        /// <summary>
        /// Reset spawn-cooldown after a FullState resync — ECS reconstruction invalidates the previous attempt tick.
        /// </summary>
        public void OnResyncCompleted(int _)
        {
            _lastSpawnAttemptTick = -1;
        }

        private static bool HasOwnCharacter(Frame frame, int playerId)
        {
            var filter = frame.Filter<OwnerComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                if (owner.OwnerId == playerId) return true;
            }
            return false;
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
#if KLOTHO_FAULT_INJECTION
            // Intentional spawn-cmd drop. Mark the cooldown as if we sent so the retry
            // schedule still advances — this exercises the rejection self-heal path.
            if (xpTURN.Klotho.Diagnostics.FaultInjection.DropSpawnCommandPlayerIds.Contains(playerId))
            {
                _lastSpawnAttemptTick = engine.CurrentTick;
                _logger?.ZLogWarning($"[FaultInjection][Brawler] Spawn cmd dropped: playerId={playerId}, tick={engine.CurrentTick}");
                return;
            }
#endif
            var rules    = ((EcsSimulation)engine.Simulation).Frame.AssetRegistry.Get<BrawlerGameRulesAsset>(1001);
            int spawnIdx = playerId % rules.SpawnPositions.Length;
            FPVector3 pos  = rules.SpawnPositions[spawnIdx];

            // Query character selection from local player's BrawlerPlayerConfig (network-shared data)
            // If PlayerConfig has not arrived yet, fallback to 0 (Warrior) — Spawn retry loop will be called again
            var playerConfig = engine.GetPlayerConfig<BrawlerPlayerConfig>(playerId);

            var cmd = CommandPool.Get<SpawnCharacterCommand>();
            cmd.CharacterClass = playerConfig?.SelectedCharacterClass ?? 0;
            cmd.SpawnPosition  = new FPVector2(pos.x, pos.z);
            // Track in CurrentTick axis so OnPollInput's `tick` arg (= CurrentTick) lines up with cooldown comparison.
            // PlayerId / Tick are set by InputCommand (CurrentTick + InputDelayTicks).
            int prevAttemptTick = _lastSpawnAttemptTick;
            _lastSpawnAttemptTick = engine.CurrentTick;
            engine.InputCommand(cmd, extraDelay: _extraSpawnDelay);

            int targetTick = engine.CurrentTick + engine.InputDelay + _extraSpawnDelay;
            if (prevAttemptTick < 0)
                _logger?.ZLogInformation($"[Brawler] Spawn cmd (initial): playerId={playerId}, tick={targetTick}, extraSpawnDelay={_extraSpawnDelay}");
            else
                _logger?.ZLogWarning($"[Brawler] Spawn cmd (retry after cooldown): playerId={playerId}, tick={targetTick}, ticksSinceLast={engine.CurrentTick - prevAttemptTick}, extraSpawnDelay={_extraSpawnDelay}");
        }
    }
}
