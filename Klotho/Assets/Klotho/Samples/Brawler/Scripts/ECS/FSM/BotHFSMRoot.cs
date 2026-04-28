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

        // ── Transition pool ──────────────────────────────────────────────────
        // T_Evade     Priority 90 → Evade
        // T_Knockback Priority 80 → Chase
        // T_Attack    Priority 70 → Attack
        // T_Skill     Priority 60 → Skill
        // T_Chase     Priority 50 → Chase
        // T_Idle      Priority 40 → Idle

        static HFSMTransitionNode T_Evade     => new HFSMTransitionNode { Priority = 90, TargetStateId = Evade,  Decision = _shouldEvade    };
        static HFSMTransitionNode T_Knockback => new HFSMTransitionNode { Priority = 80, TargetStateId = Chase,  Decision = _isKnockback    };
        static HFSMTransitionNode T_Attack    => new HFSMTransitionNode { Priority = 70, TargetStateId = Attack, Decision = _inAttackRange  };
        static HFSMTransitionNode T_Skill     => new HFSMTransitionNode { Priority = 60, TargetStateId = Skill,  Decision = _shouldUseSkill };
        static HFSMTransitionNode T_Chase     => new HFSMTransitionNode { Priority = 50, TargetStateId = Chase,  Decision = _hasTarget      };
        static HFSMTransitionNode T_Idle      => new HFSMTransitionNode { Priority = 40, TargetStateId = Idle,   Decision = _noTarget       };

        // Evade only
        static HFSMTransitionNode T_EvadeArrived => new HFSMTransitionNode { Priority = 50, TargetStateId = Idle, Decision = _evadeArrived };

        // Skill only — Chase transition after ActionLock is released, Priority 100
        static HFSMTransitionNode T_SkillDone => new HFSMTransitionNode { Priority = 100, TargetStateId = Chase, Decision = _skillDone };

        // ── Build ─────────────────────────────────────────────────────────────

        public static void Build(BotBehaviorAsset behavior, BotDifficultyAsset[] diffAssets,
                                 BasicAttackConfigAsset attack, SkillConfigAsset[][] skills)
        {
            if (HFSMRoot.Has(Id)) return;

            _shouldEvade    = new ShouldEvadeDecision(behavior, diffAssets);
            _inAttackRange  = new InAttackRangeDecision(attack);
            _shouldUseSkill = new ShouldUseSkillDecision(behavior, diffAssets, skills);
            _evadeEnter     = new EvadeEnterAction(behavior);
            _skillUpdate    = new SkillUpdateAction(behavior, diffAssets, skills);

            var states = new HFSMStateNode[5]; // index = StateId

            // Idle (0): excludes T_Idle
            states[Idle] = new HFSMStateNode
            {
                StateId        = Idle,
                ParentId       = -1,
                DefaultChildId = -1,
                OnEnterActions  = new AIAction[] { _clearDest },
                OnUpdateActions = null,
                OnExitActions   = null,
                Transitions     = new[]
                {
                    T_Evade, T_Knockback, T_Attack, T_Skill, T_Chase,
                },
            };

            // Chase (1): excludes T_Chase
            states[Chase] = new HFSMStateNode
            {
                StateId        = Chase,
                ParentId       = -1,
                DefaultChildId = -1,
                OnEnterActions  = null,
                OnUpdateActions = null,
                OnExitActions   = null,
                Transitions     = new[]
                {
                    T_Evade, T_Knockback, T_Attack, T_Skill, T_Idle,
                },
            };

            // Attack (2): excludes T_Attack
            states[Attack] = new HFSMStateNode
            {
                StateId        = Attack,
                ParentId       = -1,
                DefaultChildId = -1,
                OnEnterActions  = new AIAction[] { _clearDest },
                OnUpdateActions = null,
                OnExitActions   = null,
                Transitions     = new[]
                {
                    T_Evade, T_Knockback, T_Skill, T_Chase, T_Idle,
                },
            };

            // Evade (3): excludes all common transitions, holds only T_EvadeArrived
            states[Evade] = new HFSMStateNode
            {
                StateId        = Evade,
                ParentId       = -1,
                DefaultChildId = -1,
                OnEnterActions  = new AIAction[] { _evadeEnter },
                OnUpdateActions = null,
                OnExitActions   = null,
                Transitions     = new[]
                {
                    T_EvadeArrived,
                },
            };

            // Skill (4): excludes all common transitions, holds only T_SkillDone
            states[Skill] = new HFSMStateNode
            {
                StateId        = Skill,
                ParentId       = -1,
                DefaultChildId = -1,
                OnEnterActions  = new AIAction[] { _clearDest },
                OnUpdateActions = new AIAction[] { _skillUpdate },
                OnExitActions   = null,
                Transitions     = new[]
                {
                    T_SkillDone,
                },
            };

            var root = new HFSMRoot
            {
                RootId         = Id,
                DefaultStateId = Idle,
                States         = states,
            };

            HFSMRoot.Register(root);
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
}
