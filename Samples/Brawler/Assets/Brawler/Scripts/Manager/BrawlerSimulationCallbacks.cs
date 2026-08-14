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
using xpTURN.Klotho.Deterministic.Random;

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

        // INavMeshProvider must expose the SIMULATION's mesh/query, not the one it started
        // with: a runtime rebake swaps them, and diagnostics that keep showing the base
        // mesh draw paths straight through buildings. Delegating to the agent system — the
        // object SwapNavMesh actually rebinds — makes staleness impossible, including on the
        // direct SwapNavMesh path.
        // NOTE: _navMesh stays readonly and keeps holding the BASE mesh — RebakeContext
        // rebakes from it on every call, so overwriting it would break the snapshot contract.
        //
        // Only NavMesh has a fallback, and not by oversight: before RegisterSystems there IS no
        // query to fall back to. _navMesh arrives with the callbacks; the query is built inside
        // RegisterSystems, in the same breath as the agent system that holds it. So the two are
        // null at different times and nothing can close that gap here.
        //
        // It never shows, because both are read from inside the simulation and the simulation
        // does not exist until RegisterSystems has run — the sample's only NavQuery consumer is
        // a bot FSM action. Worth naming rather than leaving as an apparent inconsistency: a
        // consumer that null-checks NavMesh and then dereferences NavQuery would be relying on
        // that timing, not on anything guaranteed here.
        public FPNavMesh NavMesh { get { return _agentSystem?.CurrentMesh ?? _navMesh; } }
        public FPNavMeshQuery NavQuery { get { return _agentSystem?.CurrentQuery; } }

        private FPNavAgentSystem _agentSystem;
        public FPNavMeshRebakeContext RebakeContext { get; private set; }
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

            // The trio keeps whatever logger it is born with — rebinding does not replace the
            // objects, so a null here would silence FindPath's out-of-navmesh errors for the whole
            // room. It used to get quietly upgraded by the first building placement, back when a
            // swap built new ones; that accident is now a decision.
            IKLogger navLogger = simulation.Frame.Logger;
            var query       = new FPNavMeshQuery(_navMesh, navLogger);
            var pathfinder  = new FPNavMeshPathfinder(_navMesh, query, navLogger);
            var funnel      = new FPNavMeshFunnel(_navMesh, query, navLogger);
            var agentSystem = new FPNavAgentSystem(_navMesh, query, pathfinder, funnel, navLogger);
            agentSystem.SetAvoidance(new FPNavAvoidance());
            // Registers the NavMesh boundary as ORCA static obstacles (wall avoidance) and applies
            // the baked asset's own Agent Radius as the obstacle inset — both peers load the same
            // asset, so the clearance correction stays symmetric without a hand-synced constant.
            agentSystem.LoadNavMeshObstacles();
            if (agentSystem.DebugObstacleCount == 0)
                simulation.Frame.Logger?.KWarning($"[BrawlerSimulationCallbacks] ORCA obstacles empty — NavMesh obstacle wiring missing or boundary-free mesh");

            botFSMSystem = new BotFSMSystem(agentSystem);
            botFSMSystem.SetQuery(query);

            // Per-match rebake snapshot (JIT warming is built into CreateSnapshot;
            // it is a no-op under IL2CPP). Throws on unsupported bases (multi-level/off-grid):
            // runtime building placement is then unavailable for this stage — surfaced at load.
            try
            {
                // Through the shared helper, never FPNavMeshRebaker.CreateContext directly — the
                // server has to build the SAME catalog into its snapshot or a placement one end
                // accepts is refused by the other (BrawlerBuildingShapes.CreateContext).
                RebakeContext = BrawlerBuildingShapes.CreateContext(_navMesh, simulation.Frame.Logger);
            }
            catch (System.Exception e)
            {
                simulation.Frame.Logger?.KWarning($"[BrawlerSimulationCallbacks] rebake snapshot unavailable for this stage: {e.Message}");
            }

            _agentSystem = agentSystem;
            BotFSMSystem = botFSMSystem;

            BrawlerSimSetup.RegisterSystems(
                simulation,
                simulation.Frame.Logger,
                _dataAssets,
                _staticColliders,
                botFSMSystem,
                _stageId,
                RebakeContext
            );
        }

        /// <summary>
        /// After a full state is applied (late join /
        /// corrective reset) the restored BuildingComponents are frame state but the navmesh
        /// is still the base — rebake once from the components so the nav fingerprint
        /// matches the peers. No-op when the stage has no rebake snapshot or no buildings.
        /// </summary>
        public void OnFullStateApplied(IKlothoEngine engine, Frame frame)
        {
            if (RebakeContext == null || BotFSMSystem == null)
                return;
            // Sized from the ENGINE STORAGE bound, not the game's policy cap and not a literal:
            // this must hold every building the frame can possibly contain, whatever policy is in
            // force. The literal 33 that used to sit here was wrong twice over — it could not
            // follow a raised MaxCount (so only the joining peer rebaked a subset), and its `+1`
            // was copied from the placement path, which needs a slot for the candidate being
            // validated. There is no candidate here.
            //
            // A local rather than a field, deliberately. The guide blesses a one-off allocation
            // ("fine for a one-off") and reserves the reused-buffer rule for paths that run per
            // placement; this one runs at join and resync. Hoisting it would cost the command
            // path's _placeScratch its stated reason for existing — that field is there because
            // THAT path is hot, and a copy of it here would say otherwise.
            var buffer = new FPBuildingPlacement[PlatformerCommandSystem.BuildingSlotCapacity];
            int count = PlatformerCommandSystem.CollectBuildings(ref frame, buffer);
            if (count == 0)
                return;

            // The hook contract is "do not throw" (ISimulationCallbacks.OnFullStateApplied), so the
            // rejection cases are ours to report, not the engine's to absorb. They are reachable
            // here in a way the command path's are not: this also runs on a hash MISMATCH, i.e. on
            // a building set nothing has vouched for. Leaving the old mesh installed is the honest
            // outcome — the engine's nav fingerprint check is what surfaces the resulting
            // divergence, and swapping in a half-carved mesh would be worse than not swapping.
            FPNavMesh mesh;
            try
            {
                // The buffer goes in with its live count, not trimmed to an exact-size copy —
                // that trim is what the length-is-the-count reading used to force, and
                // placementCount exists to remove it.
                mesh = FPNavMeshRebaker.RebakePlacements(
                    RebakeContext, buffer, frame.Logger, PlatformerCommandSystem.PlacementRules, count);
            }
            catch (System.Exception e)
            {
                frame.Logger?.KError(
                    $"[BrawlerSimulationCallbacks] post-fullstate rebake rejected {count} building(s), " +
                    $"keeping the current navmesh: {e.Message}");
                return;
            }

            // Swap WITHOUT reseeding. Only this peer runs this hook, so a reseed here writes hashed
            // NavAgent state on one side of a state hash that was just verified — see
            // BotFSMSystem.SwapForRestoredState for why that is a divergence and not a repair.
            BotFSMSystem.SwapForRestoredState(ref frame, mesh);
            RebakeContext.CommitSwap(mesh);
            frame.Logger?.KInformation($"[BrawlerSimulationCallbacks] post-fullstate rebake: {count} building(s)");
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

        /// <summary>Picks the orientation for a new building. "Random" here still has to be
        /// reproducible, so it comes from the match seed keyed by tick rather than from
        /// UnityEngine.Random — a replay of this session has to make the same choice.</summary>
        private static int RandomOrientation(Frame frame, ulong salt)
        {
            ref readonly var seed = ref frame.GetReadOnlySingleton<RandomSeedComponent>();
            var rng = DeterministicRandom.FromSeed(
                seed.Seed, BrawlerBuildingShapes.OrientationFeatureKey, (ulong)frame.Tick ^ salt);
            return rng.NextInt(0, BrawlerBuildingShapes.Directions);
        }

        /// <summary>
        /// Test entry: places an oriented building two cells in front of the local
        /// character via the reliable channel (IssueOnce → server-routed → verified stream) —
        /// safe to trigger from a single client in SD, unlike the headless bot-injection demo.
        /// The orientation is drawn at random from the shape catalog, which is the point of the P4
        /// wiring: an axis-aligned rect would not have one.
        /// No-op when the stage has no rebake snapshot or the local character is absent.
        /// </summary>
        public void SendPlaceBuildingCommand(IKlothoEngine engine)
        {
            if (RebakeContext == null)
                return;
            var frame = ((EcsSimulation)engine.Simulation).Frame;
            int playerId = engine.LocalPlayerId;

            var filter = frame.Filter<TransformComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var ch = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (ch.PlayerId != playerId || ch.IsDead)
                    continue;
                ref readonly var tr = ref frame.GetReadOnly<TransformComponent>(entity);
                // Quantised to the placement grid, not rounded to whole units. The grid is
                // 1/1024 of a world unit, so the character's actual position survives instead of
                // being flattened — which is what lets a placement sit where a flush neighbour
                // would go (FPGeoPredicates.Quantize).
                var centre = new FPVector3(
                    FPGeoPredicates.Quantize(tr.Position.x + FP64.FromInt(2)),
                    tr.Position.y,
                    FPGeoPredicates.Quantize(tr.Position.z));
                int orientation = RandomOrientation(frame, (ulong)playerId);
                // Pooled, not `new`: IssueOnce's factory hands the instance to the framework, which
                // owns and eventually recycles it (ILockstepEngine.IssueOnce · ReliableCommandTracker
                // .Issue both state this). The factory is re-invoked on every retry, so a `new` here
                // is a fresh allocation per attempt on the one path the pool exists to keep quiet.
                //
                // Every field the command declares is assigned below, deliberately: CommandPool.Get
                // resets only PlayerId and Tick, so a recycled instance still carries the previous
                // caller's values in everything else. SequenceNumber is the one exception — the
                // tracker stamps it on both the initial send and each retry.
                engine.IssueOnce(() =>
                {
                    var cmd = CommandPool.Get<PlaceBuildingCommand>();
                    cmd.ShapeId = BrawlerBuildingShapes.BoxShape;
                    cmd.Orientation = orientation;
                    cmd.Centre = centre;
                    return cmd;
                });
                return;
            }
        }

        /// <summary>
        /// Test entry: places a HEXAGON in front of the local character, via its own
        /// command. Separate from the box because a hexagon has no orientation to send — no integer
        /// hexagon is symmetric under 60 degrees, so the turns a rotate button would offer are not
        /// in the catalog (see BrawlerBuildingShapes). Wire to a second UI button.
        /// </summary>
        public void SendPlaceHexBuildingCommand(IKlothoEngine engine)
        {
            if (RebakeContext == null)
                return;
            var frame = ((EcsSimulation)engine.Simulation).Frame;
            int playerId = engine.LocalPlayerId;

            var filter = frame.Filter<TransformComponent, CharacterComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var ch = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (ch.PlayerId != playerId || ch.IsDead)
                    continue;
                ref readonly var tr = ref frame.GetReadOnly<TransformComponent>(entity);
                // Snapped to the hexagon's own tiling lattice, so pressing this repeatedly builds
                // a honeycomb rather than a scatter of near misses. Without it the touch rule is
                // on but unreachable from the UI: no tiling delta is a whole world unit, so a
                // free-hand position essentially never lands flush.
                BrawlerBuildingShapes.SnapHexPlacement(
                    RebakeContext.ShapeExpansion,
                    tr.Position.x + FP64.FromInt(2), tr.Position.z,
                    out FP64 hx, out FP64 hz);
                var centre = new FPVector3(hx, tr.Position.y, hz);
                // Pooled — see the sibling in SendPlaceBuildingCommand for why, and for why every
                // declared field is assigned rather than relying on a fresh object's zeroes.
                engine.IssueOnce(() =>
                {
                    var cmd = CommandPool.Get<PlaceHexBuildingCommand>();
                    cmd.Centre = centre;
                    return cmd;
                });
                return;
            }
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
