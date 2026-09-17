using xpTURN.Klotho.ECS.FSM;

namespace Brawler
{
    /// <summary>
    /// Bot AI state-graph definition.
    /// Built as a flat FSM (5 states).
    /// Registered by calling Build() once at app start (or when BotFSMSystem is created).
    /// </summary>
    public static class BotHFSMRoot
    {
        public const int Id = 1; // HFSMRoot registry key

        // ── State IDs ─────────────────────────────────────────────────────────
        public const int Idle   = BotStateId.Idle;   // 0
        public const int Chase  = BotStateId.Chase;  // 1
        public const int Attack = BotStateId.Attack; // 2
        public const int Evade  = BotStateId.Evade;  // 3
        public const int Skill  = BotStateId.Skill;  // 4

        // ── Singleton Decision/Action (initialized when Build() is called) ────
        static ShouldEvadeDecision    _shouldEvade;
        static IsKnockbackDecision    _isKnockback    = new IsKnockbackDecision();
        static InAttackRangeDecision  _inAttackRange;
        static ShouldUseSkillDecision _shouldUseSkill;
        static HasTargetDecision      _hasTarget      = new HasTargetDecision();
        static NoTargetDecision       _noTarget       = new NoTargetDecision();
        static EvadeArrivedDecision   _evadeArrived   = new EvadeArrivedDecision();
        static SkillActionDoneDecision _skillDone     = new SkillActionDoneDecision();

        static ClearDestinationAction _clearDest  = new ClearDestinationAction();
        static EvadeEnterAction       _evadeEnter;
        static SkillUpdateAction      _skillUpdate;

        // ── Build ─────────────────────────────────────────────────────────────
        // Transition priorities live in BotPriority (higher = evaluated first).
        // Evade/Skill are committed states holding only their single exit transition.

        public static void Build(BotBehaviorAsset behavior, BotDifficultyAsset[] diffAssets,
                                 BasicAttackConfigAsset attack, SkillConfigAsset[][] skills)
        {
            if (HFSMRoot.Has(Id)) return;

            _shouldEvade    = new ShouldEvadeDecision(
                                  AIParam.From(new EvadeMarginByDifficulty(diffAssets)),
                                  AIParam.From(new EvadeKnockbackPctByDifficulty(diffAssets)),
                                  AIParam.Const(behavior.StageBoundary));
            _inAttackRange  = new InAttackRangeDecision(AIParam.Const(attack.MeleeRangeSqr));
            _shouldUseSkill = new ShouldUseSkillDecision(behavior, diffAssets, skills);
            _evadeEnter     = new EvadeEnterAction(behavior);
            _skillUpdate    = new SkillUpdateAction(behavior, diffAssets, skills);

            new HFSMBuilder(Id)
                .Default(Idle)
                .State(Idle).Named(nameof(Idle))                   // excludes the self transition
                    .OnEnter(_clearDest)
                    .To(Evade,  _shouldEvade,    priority: BotPriority.Evade)
                    .To(Chase,  _isKnockback,    priority: BotPriority.Knockback)
                    .To(Attack, _inAttackRange,  priority: BotPriority.Attack)
                    .To(Skill,  _shouldUseSkill, priority: BotPriority.Skill)
                    .To(Chase,  _hasTarget,      priority: BotPriority.HasTarget)
                .State(Chase).Named(nameof(Chase))                 // excludes the hasTarget transition
                    .To(Evade,  _shouldEvade,    priority: BotPriority.Evade)
                    .To(Chase,  _isKnockback,    priority: BotPriority.Knockback)
                    .To(Attack, _inAttackRange,  priority: BotPriority.Attack)
                    .To(Skill,  _shouldUseSkill, priority: BotPriority.Skill)
                    .To(Idle,   _noTarget,       priority: BotPriority.NoTarget)
                .State(Attack).Named(nameof(Attack))               // excludes the self transition
                    .OnEnter(_clearDest)
                    .To(Evade,  _shouldEvade,    priority: BotPriority.Evade)
                    .To(Chase,  _isKnockback,    priority: BotPriority.Knockback)
                    .To(Skill,  _shouldUseSkill, priority: BotPriority.Skill)
                    .To(Chase,  _hasTarget,      priority: BotPriority.HasTarget)
                    .To(Idle,   _noTarget,       priority: BotPriority.NoTarget)
                .State(Evade).Named(nameof(Evade))                 // committed: single exit transition
                    .OnEnter(_evadeEnter)
                    .To(Idle,   _evadeArrived,   priority: BotPriority.EvadeArrived)
                .State(Skill).Named(nameof(Skill))                 // committed: returns to Chase once the action lock clears
                    .OnEnter(_clearDest)
                    .OnUpdate(_skillUpdate)
                    .To(Chase,  _skillDone,      priority: BotPriority.SkillDone)
                .Build();
        }
    }

    /// <summary>State ID constants.</summary>
    public static class BotStateId
    {
        public const int Idle   = 0;
        public const int Chase  = 1;
        public const int Attack = 2;
        public const int Evade  = 3;
        public const int Skill  = 4;
    }

    /// <summary>Transition priorities for the bot HFSM. Higher is evaluated first
    /// (Build sorts each state's transitions by descending priority).</summary>
    public static class BotPriority
    {
        public const int SkillDone    = 100; // Skill → Chase (committed exit)
        public const int Evade        = 90;
        public const int Knockback    = 80;  // → Chase
        public const int Attack       = 70;
        public const int Skill        = 60;
        public const int HasTarget    = 50;  // → Chase
        public const int EvadeArrived = 50;  // Evade → Idle (committed exit)
        public const int NoTarget     = 40;  // → Idle
    }
}
