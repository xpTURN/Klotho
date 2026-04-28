using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Navigation;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.FSM;

namespace Brawler
{
    public class BotFSMSystem : ISystem, IInitSystem, INavAgentSnapshotProvider
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

        readonly MoveInputCommand _moveCmd   = new MoveInputCommand();
        readonly AttackCommand    _attackCmd = new AttackCommand();

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
            _behavior = frame.AssetRegistry.Get<BotBehaviorAsset>(1600);

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

            var attack = frame.AssetRegistry.Get<BasicAttackConfigAsset>(1301);
            BotHFSMRoot.Build(_behavior, _diffAssets, attack, _skills);

            var filter = frame.Filter<BotComponent>();
            while (filter.Next(out var entity))
                HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id);
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

            // ── Pass 3: Nav simulation ───────────────────────────────────────
            if (_navEntityCount > 0)
            {
                _navSystem.Update(ref frame, _navEntities, _navEntityCount, frame.Tick, dt);
            }

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
            // Move command (always)
            FP64 h = FP64.Zero, v = FP64.Zero;
            FP64 sqrMag = desiredVelocity.x * desiredVelocity.x + desiredVelocity.y * desiredVelocity.y;
            if (sqrMag > FP64.Zero)
            {
                FP64 mag = FP64.Sqrt(sqrMag);
                h = desiredVelocity.x / mag;
                v = desiredVelocity.y / mag;
            }
            _moveCmd.PlayerId       = character.PlayerId;
            _moveCmd.HorizontalAxis = h;
            _moveCmd.VerticalAxis   = v;
            _moveCmd.JumpPressed    = false;
            _moveCmd.JumpHeld       = false;
            _commandSystem.OnCommand(ref frame, _moveCmd);

            if (bot.AttackCooldown > 0)
                bot.AttackCooldown--;

            // Attack command (Attack state)
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

                        _attackCmd.PlayerId     = character.PlayerId;
                        _attackCmd.AimDirection = aimDir;
                        _commandSystem.OnCommand(ref frame, _attackCmd);
                        bot.AttackCooldown = _diffAssets[bot.Difficulty].AttackCooldownBase;
                    }
                }
            }
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

            if (frame.Has<HFSMComponent>(entity))
                frame.Remove<HFSMComponent>(entity);
            HFSMManager.Init(ref frame, entity, BotHFSMRoot.Id);
        }

        void EnsureNavCapacity(int required)
        {
            if (required <= _navEntities.Length) return;
            int newSize = _navEntities.Length * 2;
            System.Array.Resize(ref _navEntities, newSize);
        }
    }
}
