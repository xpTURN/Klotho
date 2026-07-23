using System;

using UnityEngine;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.Systems;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.Serialization;
using System.Collections.Generic;
using xpTURN.Klotho.Deterministic.Physics;

namespace Brawler
{
    public class BrawlerSimulationCallbacks
        : ISimulationCallbacks, INavMeshProvider, INavAgentProvider, IFPPhysicsProviderSource
    {
        private readonly BrawlerInputCapture _input;
        private readonly List<FPStaticCollider> _staticColliders;
        private readonly FPNavMesh _navMesh;
        private readonly List<IDataAsset> _dataAssets;
        private readonly int _maxPlayers;
        private readonly int _botCount;
        private readonly int _stageId;

        private IKlothoEngine _engine;
        private EcsSimulation _simulation;

        // Outstanding reliable handle for the local player's SpawnCharacterCommand. Resolved either by
        // wire-level Duplicate ack (framework) or by state-driven Confirm() (OnPollInput when the
        // character entity becomes visible in the simulation frame).
        private IReliableCommandHandle _spawnHandle;

        // Bound delegate cached once in ctor — retry path reuses the same delegate instance, avoiding
        // per-retry closure allocation. Factory acquires a fresh CommandPool instance each invocation
        // so InputBuffer never holds two slots referencing the same cmd.
        private Func<ICommand> _spawnBuilder;

        // Bound delegate for the in-match UseConsumableCommand (retry path reuses it, no per-retry closure).
        // Factory stamps the captured _pendingUseSeq so the initial send and every retry of one use carry the
        // same UseSeq (the simulation dedups on it).
        private Func<ICommand> _consumeBuilder;

        // Outstanding reliable handle for the local player's UseConsumableCommand. Resolved by state-driven
        // Confirm() (OnPollInput, once the use's UseSeq is applied to the local character), which stops the
        // P2P legacy-slot retry loop. _pendingUseSeq is the current use's id; _useCounter is the per-player
        // monotonic source, lower-bounded at issue by the applied seq so a cold reconnect does not collide.
        private IReliableCommandHandle _consumeHandle;
        private int _pendingUseSeq;
        private int _useCounter;

        // Demo consumable id. MUST match the lobby producer's owned id (IdentitySdRef DemoEntitlement.ConsumableId)
        // so the authority's entitlement gate recognizes it; kept as a local constant to avoid a Manager→identity
        // sample dependency (the gate likewise decodes ownership inline).
        private const int DemoConsumableId = 100;

        public FPNavMesh NavMesh { get { return _navMesh; } }
        public FPNavMeshQuery NavQuery { get; private set; }
        public BotFSMSystem BotFSMSystem { get; private set; }

        public INavAgentSnapshotProvider NavAgentSnapshotProvider => BotFSMSystem;
        public IFPPhysicsWorldProvider PhysicsProvider => _simulation?.GetSystem<PhysicsSystem>();

        public BrawlerSimulationCallbacks(BrawlerInputCapture input,
                                          List<FPStaticCollider> colliders,
                                          FPNavMesh navMesh,
                                          int maxPlayers,
                                          int botCount,
                                          List<IDataAsset> dataAssets = null,
                                          int stageId = 0)
        {
            _input = input;
            _staticColliders = colliders;
            _navMesh = navMesh;
            _dataAssets = dataAssets;

            _maxPlayers = maxPlayers;
            _botCount = botCount;
            _stageId = stageId;

            // Cache bound delegates once — retry path reuses these instances, no per-call closure alloc.
            _spawnBuilder = BuildSpawnCommand;
            _consumeBuilder = BuildUseConsumableCommand;
        }

        public void RegisterSystems(EcsSimulation simulation)
        {
            _simulation = simulation;

            BotFSMSystem botFSMSystem = null;

            var query       = new FPNavMeshQuery(_navMesh, null);
            var pathfinder  = new FPNavMeshPathfinder(_navMesh, query, null);
            var funnel      = new FPNavMeshFunnel(_navMesh, query, null);
            var agentSystem = new FPNavAgentSystem(_navMesh, query, pathfinder, funnel, null);
            agentSystem.SetAvoidance(new FPNavAvoidance());
            // Registers the NavMesh boundary as ORCA static obstacles (wall avoidance) and applies
            // the baked asset's own Agent Radius as the obstacle inset — both peers load the same
            // asset, so the clearance correction stays symmetric without a hand-synced constant.
            agentSystem.LoadNavMeshObstacles();
            if (agentSystem.DebugObstacleCount == 0)
                simulation.Frame.Logger?.KWarning($"[BrawlerSimulationCallbacks] ORCA obstacles empty — NavMesh obstacle wiring missing or boundary-free mesh");

            botFSMSystem = new BotFSMSystem(agentSystem);
            botFSMSystem.SetQuery(query);

            NavQuery = query;
            BotFSMSystem = botFSMSystem;

            BrawlerSimSetup.RegisterSystems(
                simulation,
                simulation.Frame.Logger,
                _dataAssets,
                _staticColliders,
                botFSMSystem,
                _stageId
            );
        }

        public void OnInitializeWorld(IKlothoEngine engine)
        {
            BrawlerSimSetup.InitializeWorldState(engine, _maxPlayers, _botCount);
        }

        // A late-joiner enters the world at its (deterministic) join tick — seed its entitlement loadout the
        // same way tick-0 players are seeded, so a restricted (e.g. "guest") late-joiner is actually gated
        // in-match instead of falling back to full via the spawn else-branch. Deterministic (same signed
        // entitlement bytes on every peer via the propagated ticket) + rollback-safe (invoked once per join
        // inside the participant-slot guard; SeedOneLoadout is also create-iff-not-exists).
        public void OnPlayerJoinedWorld(IKlothoEngine engine, Frame frame, int playerId)
        {
            BrawlerSimSetup.SeedOneLoadout(ref frame, engine, playerId);
        }

        public void OnPollInput(int playerId, int tick, ICommandSender sender)
        {
            if (_engine == null) return;

            // ECS frame is the single source of truth — listener-pattern flags are vulnerable to rollback noise.
            var frame = ((EcsSimulation)_engine.Simulation).Frame;
            if (!HasOwnCharacter(frame, playerId))
            {
                // Framework's reliability tracker handles retry / escalation / fault injection for the
                // outstanding spawn cmd. Emit an empty-move filler only when it does not collide with
                // the outstanding spawn cmd's target slot (single cmd per (tick, playerId)).
                if (_spawnHandle != null && !_spawnHandle.WouldCollideAt(tick))
                {
                    var emptyInput = CommandPool.Get<PlayerInputCommand>();
                    emptyInput.PlayerId = playerId;
                    emptyInput.Buttons  = 0;   // no movement/action intent; dispatch is a no-op until the character exists
                    sender.Send(emptyInput);
                }
                return;
            }

            // Character exists — resolve the outstanding spawn handle (state-driven ack, faster than
            // waiting for the server's Duplicate reject round-trip).
            if (_spawnHandle != null && !_spawnHandle.IsResolved)
            {
                _spawnHandle.Confirm();
                _spawnHandle = null;
            }

            // In-match consumable handle: resolve once the use's effect is observed in the frame (its UseSeq
            // has been applied to the local character). This is the state-driven ack that stops the reliable
            // retry loop (P2P legacy slot path has no wire-level ack).
            if (_consumeHandle != null && !_consumeHandle.IsResolved
                && OwnConsumableSeq(frame, playerId) >= _pendingUseSeq)
            {
                _consumeHandle.Confirm();
                _consumeHandle = null;
            }

            // The consumable retry and this per-tick input share the single (tick, playerId) slot (last write
            // wins). On the tick the outstanding consumable targets, skip this send so it is not overwritten;
            // leave the one-shot inputs unconsumed so they fire next tick (deferred, not lost). Only the move
            // axis of this one tick is dropped (physics keeps momentum).
            if (_consumeHandle != null && !_consumeHandle.IsResolved && _consumeHandle.WouldCollideAt(tick))
                return;

            // Single unified per-tick input (InputCommand sets Tick to CurrentTick+InputDelay). Move +
            // jump + attack + skill packed into one (tick, playerId) slot. Attack and skill are NOT
            // mutually exclusive — both fire if pressed the same tick.
            bool useSkill = _input.SkillSlot >= 0;

            byte buttons = PlayerInputCommand.HAS_MOVE_BIT;   // human always carries movement intent (neutral = stop)
            if (_input.Jump)             buttons |= PlayerInputCommand.JUMP_PRESSED_BIT;
            if (_input.JumpHeld)         buttons |= PlayerInputCommand.JUMP_HELD_BIT;
            if (_input.Attack)           buttons |= PlayerInputCommand.ATTACK_BIT;
            if (useSkill)              { buttons |= PlayerInputCommand.HAS_SKILL_BIT;
                                         if (_input.SkillSlot == 1) buttons |= PlayerInputCommand.SKILL_SLOT_BIT; }

            var cmd = CommandPool.Get<PlayerInputCommand>();
            cmd.PlayerId       = playerId;
            cmd.HorizontalAxis = _input.H;
            cmd.VerticalAxis   = _input.V;
            cmd.Buttons        = buttons;
            // Set every serialized field each send — pooled instances reuse stale data fields. Aim is
            // serialized unconditionally, so default it to zero when there is no attack/skill.
            cmd.AimDirection   = (_input.Attack || useSkill)
                               ? (GetNearestEnemyDirection(playerId) ?? _input.AimDirection)
                               : FPVector2.Zero;
            sender.Send(cmd);

            // Consume event-style input (send only once)
            _input.ConsumeOneShot();
        }

        public void SetEngine(IKlothoEngine engine)
        {
            _engine = engine;
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
            // Framework's reliability tracker owns retry / escalation / drop / log. SendSpawnCommand
            // is the one-shot entry point — initial send + handle creation. Subsequent retries run
            // inside the tracker via _spawnBuilder factory invocation.
            _spawnHandle = engine.IssueOnce(_spawnBuilder);
        }

        // Factory body invoked by the reliability tracker on initial send AND every retry. Each call
        // acquires a fresh CommandPool instance — framework takes ownership. Payload is re-evaluated
        // every invocation, so PlayerConfig arriving after the first attempt is picked up by retries.
        private ICommand BuildSpawnCommand()
        {
            int playerId = _engine.LocalPlayerId;
            var rules    = ((EcsSimulation)_engine.Simulation).Frame.AssetRegistry.Get<BrawlerGameRulesAsset>();
            int spawnIdx = playerId % rules.SpawnPositions.Length;
            FPVector3 pos = rules.SpawnPositions[spawnIdx];

            // Query character selection from local player's BrawlerPlayerConfig (network-shared data).
            // If PlayerConfig has not arrived yet, fallback to 0 (Warrior) — next retry re-evaluates.
            var playerConfig = _engine.GetPlayerConfig<BrawlerPlayerConfig>(playerId);

            var cmd = CommandPool.Get<SpawnCharacterCommand>();
            cmd.CharacterClass = playerConfig?.SelectedCharacterClass ?? 0;
            cmd.SpawnPosition  = new FPVector2(pos.x, pos.z);
            return cmd;
        }

        // One-shot entry point for an in-match consumable use (call from a UI button / key input during a
        // match). Issues on the reliable channel; the framework owns retry/resolution. On a dedicated
        // server the authority drops this command when the local account does not own the consumable
        // (the in-match entitlement gate), which the local player observes as the effect not happening.
        // No-op before the engine exists.
        public void SendUseConsumableCommand(IKlothoEngine engine)
        {
            if (engine == null) return;
            int playerId = engine.LocalPlayerId;
            var frame = ((EcsSimulation)engine.Simulation).Frame;

            // spawn-confirmed guard: only issue when the local character exists. IssueOnce replaces the single
            // per-player tracker slot, so issuing during the spawn window would cancel the outstanding spawn
            // handle → the character would never spawn. No character yet → ignore the button.
            int appliedSeq = OwnConsumableSeq(frame, playerId);
            if (appliedSeq < 0) return;

            // Stable per-use id, lower-bounded by the applied seq so a cold reconnect (new callbacks instance
            // → _useCounter reset to 0, LastConsumableUseSeq restored high via full-state) does not emit a
            // UseSeq the simulation already applied (which would be dedup-skipped).
            _pendingUseSeq = Math.Max(_useCounter + 1, appliedSeq + 1);
            _useCounter = _pendingUseSeq;
            _consumeHandle = engine.IssueOnce(_consumeBuilder);
        }

        // Factory invoked by the reliability tracker on initial send and any retry. Acquires a fresh
        // CommandPool instance each call (framework takes ownership). Stamps the captured _pendingUseSeq so
        // every send of this use carries the same UseSeq (simulation dedups on it).
        private ICommand BuildUseConsumableCommand()
        {
            var cmd = CommandPool.Get<UseConsumableCommand>();
            cmd.ConsumableId = DemoConsumableId;
            cmd.UseSeq       = _pendingUseSeq;
            return cmd;
        }

        // Own character's LastConsumableUseSeq, or -1 when the character does not exist yet.
        private static int OwnConsumableSeq(Frame frame, int playerId)
        {
            var filter = frame.Filter<OwnerComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var owner = ref frame.GetReadOnly<OwnerComponent>(entity);
                if (owner.OwnerId == playerId)
                    return frame.GetReadOnly<CharacterComponent>(entity).LastConsumableUseSeq;
            }
            return -1;
        }
    }
}
