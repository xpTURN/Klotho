using xpTURN.Klotho.Core;
using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Deterministic.Random;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.FSM;

namespace Brawler
{
    public class BotFSMSystem : ISystem, IInitSystem, INavAgentSnapshotProvider, INavFingerprintSource, IGameFingerprintSource
    {
        readonly FPNavAgentSystem _navSystem;

        FPNavMeshQuery            _query;
        PlatformerCommandSystem   _commandSystem;
        IPhysicsRayCaster         _rayCaster;

        BotBehaviorAsset          _behavior;
        BotDifficultyAsset[]      _diffAssets;  // index = (int)BotDifficulty (0~2)
        SkillConfigAsset[][]      _skills;      // [classIdx][slot]

        EntityRef[]  _navEntities    = new EntityRef[16];
        int          _navEntityCount;

        Frame        _lastFrame;

        // Single reused unified input command (bot bypasses the buffer/network — direct OnCommand).
        readonly PlayerInputCommand _inputCmd = new PlayerInputCommand();

        // Same pattern, same reason: RunBuildingDemo also calls OnCommand directly, which
        // dispatches synchronously and keeps no reference, so one instance can serve every beat.
        // Every field the command DECLARES is assigned at each use — a reused instance still
        // holds the last one's values, which is the trap the CommandPool.Get docs call out.
        //
        // All three, not just the removal. The two place branches used to `new` a command per
        // beat while this comment claimed otherwise two hundred lines above them, which left a
        // reader to pick which one to believe. The reasoning was always the same for all three.
        //
        // CommandBase.Tick is the one exception, and deliberately: nothing on this path sets or
        // reads it, because a directly dispatched command never passes through the input buffer
        // that would. _inputCmd above leaves it alone for the same reason. If a handler ever
        // starts reading it, all three of these sites become wrong at once.
        readonly RemoveBuildingCommand _removeCmd = new RemoveBuildingCommand();
        readonly PlaceBuildingCommand _placeCmd = new PlaceBuildingCommand();
        readonly PlaceHexBuildingCommand _placeHexCmd = new PlaceHexBuildingCommand();

        public BotFSMSystem(FPNavAgentSystem navSystem)
        {
            _navSystem = navSystem;
        }

        public void SetCommandSystem(PlatformerCommandSystem commandSystem)
        {
            _commandSystem = commandSystem;
        }

        public void SetRayCaster(IPhysicsRayCaster rayCaster)
        {
            _rayCaster = rayCaster;
        }

        public void SetQuery(FPNavMeshQuery query)
        {
            _query = query;
        }

        // ── IInitSystem ───────────────────────────────────────────────────────

        public void OnInit(ref Frame frame)
        {
            _behavior = frame.AssetRegistry.Get<BotBehaviorAsset>();

            _diffAssets = new BotDifficultyAsset[3];
            for (int i = 0; i < 3; i++)
                _diffAssets[i] = frame.AssetRegistry.Get<BotDifficultyAsset>(1700 + i);

            _skills = new SkillConfigAsset[4][];
            for (int c = 0; c < 4; c++)
            {
                var stats = frame.AssetRegistry.Get<CharacterStatsAsset>(1100 + c);
                _skills[c] = new SkillConfigAsset[2];
                _skills[c][0] = frame.AssetRegistry.Get<SkillConfigAsset>(stats.Skill0Id);
                _skills[c][1] = frame.AssetRegistry.Get<SkillConfigAsset>(stats.Skill1Id);
            }

            var attack = frame.AssetRegistry.Get<BasicAttackConfigAsset>();
            BotHFSMRoot.Build(_behavior, _diffAssets, attack, _skills);

            var filter = frame.Filter<BotComponent>();
            while (filter.Next(out var entity))
                HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id);
        }

        // Headless demo: bots inject building commands
        // through the same OnCommand entry as networked ones. Injection is part of tick
        // execution, so the flag MUST be identical on every peer — enable only on headless
        // server runs (no clients) or via a shared match config. 0 = off.
        int _buildingDemoInterval;

        public void SetBuildingDemo(int intervalTicks) => _buildingDemoInterval = intervalTicks;

        /// <summary>The stage's expanded shape table, needed to snap a hexagon onto its own tiling
        /// lattice. Null leaves the demo placing at the plain quantised position.</summary>
        public void SetShapeExpansion(FPBuildingShapeExpansion expansion) => _rebakeExpansion = expansion;

        FPBuildingShapeExpansion _rebakeExpansion;

        void RunBuildingDemo(ref Frame frame)
        {
            if (_buildingDemoInterval <= 0 || _commandSystem == null)
                return;
            if (frame.Tick <= 0 || frame.Tick % _buildingDemoInterval != 0)
                return;

            // Which beat this is, DERIVED FROM THE FRAME rather than counted in a field. The gate
            // above means frame.Tick is a multiple of the interval, so this is 1, 2, 3, … on
            // successive beats — the same sequence the old `_buildingDemoCounter++` produced on a
            // forward run, and unlike it, the same value when a tick is re-executed.
            //
            // A plain field is not frame state and so is not rolled back: the same tick coming
            // round again would read a different beat number and issue a DIFFERENT command from an
            // identical input stream, and a replay starting the field at 0 would reproduce neither.
            // The orientation RNG below is already drawn from RandomSeedComponent + frame.Tick for
            // exactly this reason — the branch selector was the one place that was not.
            //
            // Today the demo only runs on the dedicated server (BrawlerServerCallbacks, env-gated),
            // which never rolls back, so nothing was actually diverging. That is a property of
            // where it is wired, not of this code; this is the tool used to demonstrate
            // determinism, so it should not depend on that.
            int beat = frame.Tick / _buildingDemoInterval;
            if ((beat % 3) == 0)
            {
                // Every 3rd beat: remove the oldest building (exercises Remove + stale-id safety).
                var bFilter = frame.Filter<BuildingComponent>();
                if (bFilter.Next(out var oldest))
                {
                    _removeCmd.TargetEntityId = oldest.ToId();
                    _removeCmd.SequenceNumber = 0;   // reused instance — assign, do not inherit
                    _removeCmd.PlayerId = 0;
                    _commandSystem.OnCommand(ref frame, _removeCmd);
                }
                return;
            }

            // Place a building two cells ahead of the first bot; with no bot in the match
            // (headless lifecycle suites), fall back to a fixed 8-spot cycle around the origin —
            // the cycle wrap makes later beats overlap earlier buildings, exercising the reject
            // path without any participant. Spacing is 4 rather than 2 because the box footprint
            // is 2x1 and expands to 3x2, so at 2 the cycle would reject every beat.
            FP64 wx, wz, y = FP64.Zero;
            var filter = frame.Filter<TransformComponent, BotComponent>();
            if (filter.Next(out var entity))
            {
                ref readonly var tr = ref frame.GetReadOnly<TransformComponent>(entity);
                wx = tr.Position.x + FP64.FromInt(2);
                wz = tr.Position.z;
                y = tr.Position.y;
            }
            else
            {
                int k = beat % 8;
                wx = FP64.FromInt((k % 4) * 4 - 8);
                wz = FP64.FromInt((k / 4) * 4 - 4);
            }
            // Alternate the two place commands so the headless demo exercises BOTH — it is the only
            // automated run of this path, and a demo that only ever sent boxes would leave the
            // hexagon command untried until someone pressed a button.
            //
            // Random orientation, drawn from the match seed keyed by tick: this runs INSIDE the
            // simulation, so it is re-executed on rollback and every peer must draw the same value.
            // UnityEngine.Random or System.Random here would be a desync.
            ref readonly var seedComp = ref frame.GetReadOnlySingleton<RandomSeedComponent>();
            var rng = DeterministicRandom.FromSeed(
                seedComp.Seed, BrawlerBuildingShapes.OrientationFeatureKey, (ulong)frame.Tick);
            // Same key as the UI sender's, different index — the streams stay apart because the
            // indices do, which only holds while both sides read the key from one declaration.

            // Quantised to the 1/1024 placement grid, never rounded to whole units — and the
            // hexagon additionally snapped to its own tiling lattice, so the headless demo
            // exercises flush packing and not just "two buildings near each other". Both are
            // integer arithmetic, so this stays identical on every peer and across rollback.
            // Reused instances — so every DECLARED field is assigned, not just the interesting
            // ones. An object initializer on a fresh command could lean on the zeroes; these have
            // last beat's values in them, and SequenceNumber is the one that would carry over
            // unnoticed.
            CommandBase place;
            if ((beat % 3) == 1)
            {
                _placeCmd.ShapeId = BrawlerBuildingShapes.BoxShape;
                _placeCmd.Orientation = rng.NextInt(0, BrawlerBuildingShapes.Directions);
                _placeCmd.Centre = new FPVector3(
                    FPGeoPredicates.Quantize(wx), y, FPGeoPredicates.Quantize(wz));
                _placeCmd.SequenceNumber = 0;
                place = _placeCmd;
            }
            else
            {
                BrawlerBuildingShapes.SnapHexPlacement(
                    _rebakeExpansion, wx, wz, out FP64 hx, out FP64 hz);
                _placeHexCmd.Centre = new FPVector3(hx, y, hz);
                _placeHexCmd.SequenceNumber = 0;
                place = _placeHexCmd;
            }
            place.PlayerId = 0;
            _commandSystem.OnCommand(ref frame, place);
        }

        /// <summary>
        /// Swaps the rebaked mesh into the agent system
        /// and reseeds every current NavAgent. Entities are RE-COLLECTED from the frame here —
        /// the cached _navEntities may be last tick's stale set (command phase runs before Update).
        /// Runs in the command phase = before agent Update = the tick-boundary swap contract.
        ///
        /// <para>For the COMMAND path only (place / remove). Every peer executes that command on
        /// the same tick, so every peer reseeds — the write to hashed NavAgent state is symmetric,
        /// and it is necessary: the corridor holds triangle indices into the mesh being replaced.
        /// The FullState path is the opposite on both counts — see <see cref="SwapForRestoredState"/>.</para>
        /// </summary>
        public void SwapAndReseed(ref Frame frame, FPNavMesh newMesh)
        {
            int count = SwapCore(ref frame, newMesh);
            _navSystem.ReseedAgents(ref frame, _navEntities, count);
            frame.Logger?.KInformation($"[BotFSMSystem] navmesh swapped + reseeded {count} agents (tick={frame.Tick})");
        }

        /// <summary>
        /// Swaps the rebaked mesh in after a FullState apply — WITHOUT reseeding.
        ///
        /// <para>The reseed is not just unnecessary here, it manufactures a divergence. Only the
        /// applying peer runs this hook, so whatever it writes to hashed NavAgent state is written
        /// on that peer alone — right after its state hash was verified against the authority's.
        /// And <c>ReseedAgents</c> does write: it clears the corridor and path flags for EVERY
        /// agent (idle ones included) and RE-QUERIES CurrentTriangleIndex through
        /// <c>FPNavMeshQuery.FindTriangle</c>, a spatial-grid scan. The value it overwrites came
        /// from <c>MoveAlongSurface</c>, a topological walk. Those two agree on a point strictly
        /// inside a triangle and may disagree on a shared edge — both answers valid, not the same
        /// answer. So the reseed can move this peer off the authority's value even on a
        /// byte-identical mesh.</para>
        ///
        /// <para>Skipping it is sound because the restored state already matches the mesh being
        /// installed: the joiner's from-scratch rebake reproduces the incumbents' patched mesh
        /// byte for byte (pinned by FPNavMeshIncrementalPatchTests), so the restored triangle
        /// index and corridor are valid on it. If the rebake did NOT reproduce it — a build or
        /// asset mismatch — reseeding would not repair anything either; it would just pick a
        /// different wrong answer, and the engine's nav fingerprint is what surfaces that.</para>
        /// </summary>
        public void SwapForRestoredState(ref Frame frame, FPNavMesh newMesh)
        {
            int count = SwapCore(ref frame, newMesh);
            frame.Logger?.KInformation(
                $"[BotFSMSystem] navmesh swapped for restored state, {count} agent(s) left as restored (tick={frame.Tick})");
        }

        /// <summary>Rebinds the mesh and re-collects the agent set. Returns the agent count.</summary>
        private int SwapCore(ref Frame frame, FPNavMesh newMesh)
        {
            // Rebinds the query/pathfinder/funnel the agent system already holds instead of
            // building new ones — on a Field-sized stage that is ~1.4 MB a placement. _query needs
            // no reassignment either: the instance survives, so the reference SetQuery handed us
            // at load stays valid for the life of the room.
            _navSystem.SwapNavMesh(newMesh);

            // Grow rather than truncate. The Update path grows through EnsureNavCapacity, so a
            // `break` here made the two paths disagree about how many agents exist — and the
            // agents past the cut would keep a CurrentTriangleIndex and corridor that index the
            // OLD mesh, which is precisely what the command path's reseed exists to prevent.
            //
            // The asymmetry bites hardest on the post-fullstate swap: that peer may not have run
            // a single Update yet, so the array is still at its initial size while the authority
            // (which has been running Updates) already grew. Both fields live in the hashed frame
            // state, so a peer reseeding fewer agents than another is a desync, not a glitch.
            int count = 0;
            var filter = frame.Filter<NavAgentComponent>();
            while (filter.Next(out var entity))
            {
                EnsureNavCapacity(count + 1);
                _navEntities[count++] = entity;
            }
            _navEntityCount = count;
            return count;
        }

        // Brawler registers the agent system only through this wrapper, so the
        // engine's FullState fingerprint fold (KlothoEngine.FullStateResync) discovers the
        // nav fingerprint here (GetSystem<INavFingerprintSource> scans AddSystem entries).
        long INavFingerprintSource.GetNavFingerprint()
        {
            return ((INavFingerprintSource)_navSystem).GetNavFingerprint();
        }

        // The SHAPE CATALOG, in the game's own slot of the same fold. This is the one determinism
        // hazard the rebake design cannot detect by itself: a placement is a reference INTO the
        // catalog, so two builds that disagree about the table carve different navmeshes from
        // identical commands — and until a building is actually placed the meshes are identical
        // and nothing is wrong yet. Folding the table's hash makes the disagreement itself
        // comparable, before any of that. The guide asks games to carry catalog.Hash for exactly
        // this; this is where a game with no separate match-config channel can put it.
        //
        // Its OWN slot, not folded into the nav term above, so that a mismatch report names the
        // right thing. The engine prints the fold broken down by source, and a table difference
        // showing up as "nav differs" would send the reader to look at the mesh.
        //
        // What it does and does not reach. The engine compares this only when a FullState arrives
        // (CheckStaticGeometryFingerprint has two callers, both receive handlers), so: a
        // dedicated-server client is covered at JOIN, which is as close to load time as this
        // sample gets; two P2P peers that start together and never resync are NOT — for those a
        // match-config check at load is still the right instrument.
        long IGameFingerprintSource.GetGameFingerprint()
        {
            return unchecked((long)BrawlerBuildingShapes.Catalog.Hash);
        }

        void INavAgentSnapshotProvider.CollectSnapshots(NavAgentSnapshot[] buffer, out int count)
        {
            count = 0;
            for (int i = 0; i < _navEntityCount && i < buffer.Length; i++)
            {
                if (!_lastFrame.Has<NavAgentComponent>(_navEntities[i]))
                    continue;
                ref readonly var nav = ref _lastFrame.GetReadOnly<NavAgentComponent>(_navEntities[i]);
                buffer[count++] = new NavAgentSnapshot
                {
                    Entity               = _navEntities[i],
                    Position             = nav.Position,
                    Destination          = nav.Destination,
                    HasDestination       = nav.HasNavDestination,
                    HasPath              = nav.HasPath,
                    CurrentTriangleIndex = nav.CurrentTriangleIndex,
                };
            }
        }

        // ── ISystem ───────────────────────────────────────────────────────────

        public void Update(ref Frame frame)
        {
            if (_commandSystem == null) return;

            _navEntityCount = 0;
            FP64 dt = FP64.FromInt(frame.DeltaTimeMs) / FP64.FromInt(1000);

            // ── Pass 1: FSM decision ─────────────────────────────────────────
            var filter = frame.Filter<TransformComponent, CharacterComponent, BotComponent,
                                       PhysicsBodyComponent, HFSMComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var character = ref frame.GetReadOnly<CharacterComponent>(entity);
                ref var          bot       = ref frame.Get<BotComponent>(entity);

                if (character.IsDead)
                {
                    ResetBotState(ref frame, entity, ref bot);
                    continue;
                }

                bot.StateTimer++;
                BotFSMHelper.ValidateTarget(ref frame, ref bot);
                if (bot.EvadeCooldown > 0) bot.EvadeCooldown--;

                if (bot.DecisionCooldown > 0)
                {
                    bot.DecisionCooldown--;
                    BotFSMHelper.UpdateDestination(ref frame, entity, ref bot, in character, _query, in _behavior, frame.Logger);
                }
                else
                {
                    var target = bot.Target;
                    if (!target.IsValid)
                    {
                        ref readonly var selfT = ref frame.GetReadOnly<TransformComponent>(entity);
                        target = BotFSMHelper.SelectTarget(ref frame, entity, in character,
                                                           selfT.Position, (BotDifficulty)bot.Difficulty,
                                                           in _behavior);
                        bot.Target = target;
                    }

                    var context = new AIContext
                    {
                        Frame         = frame,
                        Entity        = entity,
                        NavQuery      = _query,
                        CommandSystem = _commandSystem,
                        RayCaster     = _rayCaster,
                        Logger        = frame.Logger,
                    };
                    HFSMManager.Update(ref frame, entity, ref context);

                    BotFSMHelper.UpdateDestination(ref frame, entity, ref bot, in character, _query, in _behavior, frame.Logger);

                    bot.DecisionCooldown = _diffAssets[bot.Difficulty].DecisionCooldown;
                }
            }

            // ── Pass 2: NavAgentComponent sync ───────────────────────────────
            filter = frame.Filter<TransformComponent, CharacterComponent, BotComponent,
                                   PhysicsBodyComponent, HFSMComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var character = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (character.IsDead) continue;

                ref var bot = ref frame.Get<BotComponent>(entity);

                if (bot.HasDestination)
                {
                    if (!frame.Has<NavAgentComponent>(entity))
                    {
                        frame.Add(entity, default(NavAgentComponent));
                        ref var nav = ref frame.Get<NavAgentComponent>(entity);
                        ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
                        FPVector2 snapXZ = _query.ClosestPointOnNavMesh(transform.Position.ToXZ(), out int snapTri);
                        FPVector3 snapPos = snapTri >= 0
                            ? new FPVector3(snapXZ.x, transform.Position.y, snapXZ.y)
                            : transform.Position;
                        NavAgentComponent.Init(ref nav, snapPos);
                        nav.CurrentTriangleIndex = snapTri >= 0 ? snapTri : -1;
                        NavAgentComponent.SetDestination(ref nav, bot.Destination);
                    }
                    else
                    {
                        ref var nav = ref frame.Get<NavAgentComponent>(entity);

                        // Position sync
                        ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
                        FPVector2 snapXZ = _query.ClosestPointOnNavMesh(transform.Position.ToXZ(), out int snapTri);
                        nav.Position = snapTri >= 0
                            ? new FPVector3(snapXZ.x, transform.Position.y, snapXZ.y)
                            : transform.Position;

                        // Detect destination change
                        bool destChanged = bot.Destination.x != nav.Destination.x
                                        || bot.Destination.y != nav.Destination.y
                                        || bot.Destination.z != nav.Destination.z;
                        if (destChanged)
                            NavAgentComponent.SetDestination(ref nav, bot.Destination);
                    }

                    EnsureNavCapacity(_navEntityCount + 1);
                    _navEntities[_navEntityCount++] = entity;
                }
                else
                {
                    if (frame.Has<NavAgentComponent>(entity))
                    {
                        ref var nav = ref frame.Get<NavAgentComponent>(entity);
                        NavAgentComponent.Stop(ref nav);
                    }
                }
            }

            // ── Building demo (headless, env-gated) ──────────────────────────
            // Before the agents step, and OUTSIDE the agent-count guard. It used to sit after
            // _navSystem.Update inside `if (_navEntityCount > 0)`, which broke two things its own
            // comments promise:
            //   - the no-bot fallback was unreachable. NavAgentComponent is only ever added to
            //     BotComponent holders (Pass 2), so a non-zero count already means a bot exists,
            //     and the fallback's `else` branch could never be taken.
            //   - the rebake's swap landed mid-Update rather than before the agents move, which is
            //     the tick-boundary contract SwapAndReseed documents for itself.
            // The swap re-collects _navEntities, so Pass 3 and Pass 4 below run on the post-swap
            // set instead of one that indexes the mesh that was just replaced.
            RunBuildingDemo(ref frame);

            // ── Pass 3: Nav simulation ───────────────────────────────────────
            if (_navEntityCount > 0)
                _navSystem.Update(ref frame, _navEntities, _navEntityCount, frame.Tick, dt);

            // ── Pass 4: Result feedback ──────────────────────────────────────
            for (int i = 0; i < _navEntityCount; i++)
            {
                ref var nav = ref frame.Get<NavAgentComponent>(_navEntities[i]);
                if (nav.Status == (byte)FPNavAgentStatus.Arrived)
                {
                    ref var bot = ref frame.Get<BotComponent>(_navEntities[i]);
                    bot.HasDestination = false;
                }
            }

            // ── Pass 5: Command injection ────────────────────────────────────
            filter = frame.Filter<TransformComponent, CharacterComponent, BotComponent,
                                   PhysicsBodyComponent, HFSMComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var character = ref frame.GetReadOnly<CharacterComponent>(entity);
                if (character.IsDead) continue;

                ref var bot = ref frame.Get<BotComponent>(entity);

                FPVector2 desiredVelocity = FPVector2.Zero;
                if (frame.Has<NavAgentComponent>(entity))
                {
                    ref readonly var nav = ref frame.GetReadOnly<NavAgentComponent>(entity);
                    desiredVelocity = nav.Velocity;
                }

                // PathFailed fallback
                if (desiredVelocity.x == FP64.Zero && desiredVelocity.y == FP64.Zero && bot.HasDestination)
                {
                    ref readonly var t = ref frame.GetReadOnly<TransformComponent>(entity);
                    FPVector2 dir = new FPVector2(bot.Destination.x - t.Position.x,
                                                  bot.Destination.z - t.Position.z);
                    FP64 dirSqr = dir.x * dir.x + dir.y * dir.y;
                    if (dirSqr > FP64.Zero)
                    {
                        FP64 mag = FP64.Sqrt(dirSqr);
                        desiredVelocity = new FPVector2(dir.x / mag, dir.y / mag);
                    }
                }

                int leafStateId = HFSMManager.GetLeafStateId(ref frame, entity);
                EmitCommands(ref frame, entity, ref bot, in character, desiredVelocity, leafStateId);
            }

            _lastFrame = frame;
        }

        void EmitCommands(ref Frame frame, EntityRef entity, ref BotComponent bot,
                          in CharacterComponent character, FPVector2 desiredVelocity,
                          int leafStateId)
        {
            // Build one unified input: movement (always) + attack (Attack state) folded into the same
            // command. Dispatched once at the end — HandleMove runs before HandleAttack (case order),
            // matching the old two-command behavior.
            FP64 h = FP64.Zero, v = FP64.Zero;
            FP64 sqrMag = desiredVelocity.x * desiredVelocity.x + desiredVelocity.y * desiredVelocity.y;
            if (sqrMag > FP64.Zero)
            {
                FP64 mag = FP64.Sqrt(sqrMag);
                h = desiredVelocity.x / mag;
                v = desiredVelocity.y / mag;
            }

            byte buttons = PlayerInputCommand.HAS_MOVE_BIT;   // bot move intent (no jump)

            if (bot.AttackCooldown > 0)
                bot.AttackCooldown--;

            // Attack (Attack state)
            if (leafStateId == BotStateId.Attack && bot.AttackCooldown <= 0)
            {
                if (character.ActionLockTicks <= 0)
                {
                    var target = bot.Target;
                    if (target.IsValid && frame.Has<CharacterComponent>(target))
                    {
                        ref readonly var selfT   = ref frame.GetReadOnly<TransformComponent>(entity);
                        ref readonly var targetT = ref frame.GetReadOnly<TransformComponent>(target);
                        FPVector3 dir3 = targetT.Position - selfT.Position;
                        FP64 len = FP64.Sqrt(dir3.x * dir3.x + dir3.z * dir3.z);
                        FPVector2 aimDir = len > FP64.Zero
                                        ? new FPVector2(dir3.x / len, dir3.z / len)
                                        : new FPVector2(FP64.Sin(selfT.Rotation), FP64.Cos(selfT.Rotation));

                        buttons |= PlayerInputCommand.ATTACK_BIT;
                        _inputCmd.AimDirection = aimDir;
                        bot.AttackCooldown = _diffAssets[bot.Difficulty].AttackCooldownBase;
                    }
                }
            }

            _inputCmd.PlayerId       = character.PlayerId;
            _inputCmd.HorizontalAxis = h;
            _inputCmd.VerticalAxis   = v;
            _inputCmd.Buttons        = buttons;
            _commandSystem.OnCommand(ref frame, _inputCmd);
        }

        static void ResetBotState(ref Frame frame, EntityRef entity, ref BotComponent bot)
        {
            bot.HasDestination = false;
            bot.Target         = EntityRef.None;
            bot.AttackCooldown = 0;

            if (frame.Has<NavAgentComponent>(entity))
            {
                ref var nav = ref frame.Get<NavAgentComponent>(entity);
                NavAgentComponent.Stop(ref nav);
            }

            HFSMManager.Deinit(ref frame, entity);
            HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id);
        }

        void EnsureNavCapacity(int required)
        {
            if (required <= _navEntities.Length) return;
            // Double UNTIL it fits, not once: the callers that grow by one at a time were fine
            // with a single doubling, but a caller asking for a bulk count (SwapAndReseed) would
            // silently get a still-too-small array and write past it.
            int newSize = _navEntities.Length;
            while (newSize < required)
                newSize *= 2;
            System.Array.Resize(ref _navEntities, newSize);
        }
    }
}
