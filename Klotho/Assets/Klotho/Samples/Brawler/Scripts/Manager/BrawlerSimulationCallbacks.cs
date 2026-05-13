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

        // Client-reactive fallback state. Engine-tick-based sliding window + post-push grace —
        // bumps EscalateExtraDelay only when server push is delayed/missed.
        private int _lastServerPushTick = int.MinValue;
        private int _reactiveWindowStartTick = int.MinValue;
        private int _reactiveRejectCount = 0;
        private const int SERVER_PUSH_GRACE_TICKS = 40;        // ~1s at 40Hz — ignore rejects within grace of latest server push
        private const int REACTIVE_WINDOW_TICKS = 80;          // ~2s — reject-count reset interval (boundary-jitter carry-over guard)
        private const int REACTIVE_ESCALATE_THRESHOLD = 3;     // count within window before escalating
        private const int REACTIVE_STEP = 4;                   // ticks per escalation
        private const int REACTIVE_MAX = 40;                   // cap — matches SPAWN_DELAY_MAX (~1s at 40Hz)

        // Client-side rollback-amplitude reactive state. Primary path for P2P guests (which have
        // no CommandRejectedMessage); supplementary on SD clients alongside the PastTick F-3 path
        // above. Rollback amplitude is used instead of chain-advance break events because baseline
        // matches produce 0 rollbacks while RTT-spike matches produce mean=4-7 depth — a clean
        // signal that chainbreak burst lacks. Host is excluded by the IsHost guard.
        private int _lastRollbackBurstWindowStartTick = int.MinValue;
        private int _rollbackCountInWindow = 0;
        private int _lastReactiveEscalateTick = int.MinValue;
        private const int ROLLBACK_BURST_COUNT = 3;                    // rollback events within window before escalating; baseline matches measure 0, RTT-spike matches multiple per 5s
        private const int ROLLBACK_WINDOW_TICKS = 200;                 // ~5s at 40Hz — fixed window
        private const int REACTIVE_ESCALATE_COOLDOWN_TICKS = 80;       // ~2s — minimum gap between successive reactive escalations

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
            // Grace-window anchor for the reactive fallback: refresh on every server-driven ExtraDelay update.
            engine.OnExtraDelayChanged += HandleExtraDelayChanged;
            // P2P guest fallback: rollback burst → reactive escalate.
            engine.OnRollbackExecuted += HandleRollback;
        }

        private void HandleExtraDelayChanged(int newDelay)
        {
            _lastServerPushTick = _engine.CurrentTick;
        }

        // Receives only LocalPlayer's command rejections. CommandRejectedMessage is unicast from
        // server to the originating client and dispatched through KlothoEngine.HandleCommandRejected,
        // so the playerId is implicitly _engine.LocalPlayerId.
        private void HandleCommandRejected(int tick, int cmdTypeId, RejectionReason reason)
        {
            // Client-reactive fallback: non-spawn PastTick rejects drive a server-push fallback path.
            // Spawn falls through to the existing spawn-only escalation below — avoids double-bump
            // (spawn lead = _extraSpawnDelay + _recommendedExtraDelay both rising simultaneously).
            if (cmdTypeId != SpawnCharacterCommand.TYPE_ID)
            {
                if (reason == RejectionReason.PastTick)
                    HandleReactivePastTick(tick);
                return;
            }

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

        // Client-reactive fallback for non-spawn PastTick rejects. Engine-tick units only — Time.time
        // is avoided so the same logic works under deterministic resim/rollback noise.
        private void HandleReactivePastTick(int tick)
        {
            int currentTick = _engine.CurrentTick;

            // Grace: skip rejects observed within SERVER_PUSH_GRACE_TICKS of the latest server push —
            // the prior recommended value was just received and resim/in-flight cmds may still trail.
            if (_lastServerPushTick != int.MinValue
                && currentTick - _lastServerPushTick < SERVER_PUSH_GRACE_TICKS)
                return;

            // Sliding window reset — boundary-jitter rejects shouldn't carry over forever.
            if (currentTick - _reactiveWindowStartTick > REACTIVE_WINDOW_TICKS)
            {
                _reactiveRejectCount = 0;
                _reactiveWindowStartTick = currentTick;
            }

            _reactiveRejectCount++;
            _logger?.ZLogDebug(
                $"[Brawler][DynamicDelay] F-3 trigger: count={_reactiveRejectCount}, windowStart={_reactiveWindowStartTick}, currentTick={currentTick}");
            if (_reactiveRejectCount < REACTIVE_ESCALATE_THRESHOLD)
                return;

            _engine.EscalateExtraDelay(REACTIVE_STEP, REACTIVE_MAX);
            _reactiveRejectCount = 0;
            _reactiveWindowStartTick = currentTick;
        }

        // Client-side rollback-amplitude reactive. Counts rollback events in a fixed engine-tick
        // window and escalates extra InputDelay when the burst exceeds threshold and no recent
        // server push covers the current tick. Host has direct push authority and is excluded.
        // Fires on P2P guests (primary fallback) and SD clients (supplementary alongside PastTick
        // F-3); both share REACTIVE_STEP/MAX and the OnExtraDelayChanged grace anchor, so the cap
        // bounds total escalation regardless of which path triggers.
        private void HandleRollback(int fromTick, int toTick)
        {
            if (_engine.IsHost) return;
            // Cap reached — EscalateExtraDelay would be a no-op. Skip the bookkeeping and log spam.
            if (_engine.RecommendedExtraDelay >= REACTIVE_MAX) return;

            int now = _engine.CurrentTick;

            if (_lastRollbackBurstWindowStartTick == int.MinValue
                || now - _lastRollbackBurstWindowStartTick > ROLLBACK_WINDOW_TICKS)
            {
                _lastRollbackBurstWindowStartTick = now;
                _rollbackCountInWindow = 0;
            }
            _rollbackCountInWindow++;

            if (_rollbackCountInWindow >= ROLLBACK_BURST_COUNT
                && (_lastReactiveEscalateTick == int.MinValue || now - _lastReactiveEscalateTick > REACTIVE_ESCALATE_COOLDOWN_TICKS)
                && (_lastServerPushTick == int.MinValue || now - _lastServerPushTick > SERVER_PUSH_GRACE_TICKS))
            {
                _engine.EscalateExtraDelay(REACTIVE_STEP, REACTIVE_MAX);
                _lastReactiveEscalateTick = now;
                _logger?.ZLogWarning(
                    $"[Brawler][DynamicDelay] Reactive escalate triggered: rollbackCount={_rollbackCountInWindow}, depth={fromTick - toTick}, windowTicks={ROLLBACK_WINDOW_TICKS}");
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
