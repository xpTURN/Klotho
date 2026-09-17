using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.ECS.FSM
{
    /// <summary>Thrown when a graph fails registration-time validation.</summary>
    public sealed class HFSMValidationException : ArgumentException
    {
        public HFSMValidationException(string message) : base(message) { }
    }

    /// <summary>
    /// Fluent assembler for <see cref="HFSMRoot"/>. Collects states/transitions in declaration order,
    /// validates the graph, sorts each state's transitions by descending priority (stable), and registers it.
    /// The runtime evaluates transitions in array order (not by the Priority field), so the stable
    /// descending sort is what gives the priority argument meaning.
    /// Build() runs once at init time, so the collection allocations here are not on a per-frame path.
    /// </summary>
    public sealed class HFSMBuilder
    {
        internal sealed class StateDef
        {
            public int StateId;
            public int ParentId;
            public int DefaultChildId;
            public string Name;
            public AIAction[] OnEnterActions;
            public AIAction[] OnUpdateActions;
            public AIAction[] OnExitActions;
            public readonly List<HFSMTransitionNode> Transitions = new List<HFSMTransitionNode>();
        }

        private readonly int _rootId;
        private readonly IKLogger _logger;
        private readonly List<StateDef> _states = new List<StateDef>();

        private int _defaultStateId;
        private bool _defaultSet;

        /// <param name="rootId">Registry key for the assembled root.</param>
        /// <param name="logger">Target for advisory warnings. Null silences warnings (throws are unaffected).</param>
        public HFSMBuilder(int rootId, IKLogger logger = null)
        {
            _rootId = rootId;
            _logger = logger;
        }

        /// <summary>Declares a state. parentId/defaultChildId default to -1 (top-level leaf).
        /// They may reference a stateId that has not been declared yet; references are resolved at Build time.</summary>
        public StateBuilder State(int stateId, int parentId = -1, int defaultChildId = -1)
        {
            var def = new StateDef
            {
                StateId        = stateId,
                ParentId       = parentId,
                DefaultChildId = defaultChildId,
            };
            _states.Add(def);
            return new StateBuilder(this, def);
        }

        /// <summary>Sets the root's default (entry) state. Must be called before the first State().</summary>
        public HFSMBuilder Default(int stateId)
        {
            _defaultStateId = stateId;
            _defaultSet = true;
            return this;
        }

        /// <summary>Validates the graph and registers it via HFSMRoot.Register. Throws on a broken graph.
        /// strict=true promotes advisory findings (unreachable / duplicate priority / self-transition) to throws.</summary>
        public HFSMRoot Build(bool strict = false)
        {
            var root = BuildRoot(strict);
            HFSMRoot.Register(root);
            return root;
        }

        private HFSMRoot BuildRoot(bool strict)
        {
            if (!_defaultSet)
                throw new HFSMValidationException($"Default state not set for HFSM root {_rootId}");

            // Map stateId -> def, detecting duplicates and negative ids.
            var byId = new Dictionary<int, StateDef>(_states.Count);
            int maxId = -1;
            foreach (var s in _states)
            {
                if (s.StateId < 0)
                    throw new HFSMValidationException($"State id {s.StateId} must be >= 0 in HFSM root {_rootId}");
                if (byId.ContainsKey(s.StateId))
                    throw new HFSMValidationException($"Duplicate state {s.StateId} in HFSM root {_rootId}");
                byId.Add(s.StateId, s);
                if (s.StateId > maxId) maxId = s.StateId;
            }

            if (!byId.ContainsKey(_defaultStateId))
                throw new HFSMValidationException($"Default state {_defaultStateId} not declared in HFSM root {_rootId}");

            // Reference integrity + advisory findings (declaration order = deterministic).
            var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s in _states)
            {
                if (s.ParentId >= 0 && !byId.ContainsKey(s.ParentId))
                    throw new HFSMValidationException($"State {s.StateId} parent {s.ParentId} not declared in HFSM root {_rootId}");
                if (s.DefaultChildId >= 0 && !byId.ContainsKey(s.DefaultChildId))
                    throw new HFSMValidationException($"State {s.StateId} defaultChild {s.DefaultChildId} not declared in HFSM root {_rootId}");

                var seenPriorities = new HashSet<int>();
                foreach (var t in s.Transitions)
                {
                    if (!byId.ContainsKey(t.TargetStateId))
                        throw new HFSMValidationException($"State {s.StateId} transitions to undeclared state {t.TargetStateId} in HFSM root {_rootId}");
                    if (t.TargetStateId == s.StateId)
                        Advisory(strict, $"State {s.StateId} has a self-transition (priority {t.Priority}) in HFSM root {_rootId}");
                    if (!seenPriorities.Add(t.Priority))
                        Advisory(strict, $"State {s.StateId} has duplicate transition priority {t.Priority} in HFSM root {_rootId}");
                }

                if (s.Name != null)
                {
                    if (seenNames.TryGetValue(s.Name, out int firstId))
                        Advisory(strict, $"States {firstId} and {s.StateId} share the name \"{s.Name}\" in HFSM root {_rootId}");
                    else
                        seenNames.Add(s.Name, s.StateId);
                }
            }

            // Hierarchy validation. MUST run after the reference-integrity loop above: the per-state walk
            // does byId[cur].ParentId and assumes every ParentId references a declared state (otherwise a
            // KeyNotFoundException would leak instead of a clean HFSMValidationException).
            ValidateHierarchy(byId);

            // Dense array: the runtime indexes States by StateId directly, so 0..maxId must be gap-free.
            var nodes = new HFSMStateNode[maxId + 1];
            for (int i = 0; i <= maxId; i++)
            {
                if (!byId.TryGetValue(i, out var s))
                    throw new HFSMValidationException($"State id {i} missing — States must be dense 0..{maxId} (StateId==index) in HFSM root {_rootId}");

                nodes[i] = new HFSMStateNode
                {
                    StateId         = s.StateId,
                    ParentId        = s.ParentId,
                    DefaultChildId  = s.DefaultChildId,
                    Name            = s.Name,
                    OnEnterActions  = s.OnEnterActions,
                    OnUpdateActions = s.OnUpdateActions,
                    OnExitActions   = s.OnExitActions,
                    // Stable descending sort by priority — OrderBy* is stable, so equal priorities keep declaration order.
                    Transitions     = s.Transitions.OrderByDescending(t => t.Priority).ToArray(),
                };
            }

            ReportUnreachable(byId, strict);

            ValidateAIParams(nodes);

            return new HFSMRoot
            {
                RootId         = _rootId,
                DefaultStateId = _defaultStateId,
                States         = nodes,
            };
        }

        // Validates the hierarchy: prevents runtime fixed-buffer overflow (depth), infinite recursion
        // (parent / default-child cycles), and active-chain vs ancestor-chain divergence (DefaultChild↔Parent
        // inconsistency). Unconditional (not gated on strict): these are crash/desync-class defects, so they
        // throw even for states unreachable from the default (reachability can change).
        //
        // DERIVED SAFETY — DO NOT REMOVE THE CONSISTENCY CHECK: default-child descent depth and acyclicity are
        // NOT checked separately. A consistent (DefaultChild.Parent == self) + acyclic parent forest makes every
        // default-child edge the exact reverse of a parent edge, so the per-state ParentId walk already bounds
        // default-child descent and rules out default-child cycles. Removing the consistency check below
        // (thinking "the depth walk covers descent") silently reintroduces the unbounded-descent overflow.
        private void ValidateHierarchy(Dictionary<int, StateDef> byId)
        {
            // anchoring (DefaultState ONLY): the entry state must be a root, because Init enters from it
            // without climbing parents. Transition targets may legitimately be non-root (hierarchical entry),
            // so this is NOT applied to them — ChangeState rebuilds their full ancestor path at transition time.
            if (byId[_defaultStateId].ParentId != -1)
                throw new HFSMValidationException(
                    $"Default state {_defaultStateId} must be top-level (ParentId == -1) in HFSM root {_rootId}");

            var seen = new HashSet<int>();
            // _states (declaration order) → deterministic which violation throws first.
            foreach (var s in _states)
            {
                // consistency: a DefaultChild's parent must be this state (see DERIVED SAFETY note above).
                if (s.DefaultChildId >= 0 && byId[s.DefaultChildId].ParentId != s.StateId)
                    throw new HFSMValidationException(
                        $"State {s.StateId} DefaultChild {s.DefaultChildId} has ParentId {byId[s.DefaultChildId].ParentId}, expected {s.StateId} in HFSM root {_rootId}");

                // parent cycle + depth bound, single ParentId walk to root.
                seen.Clear();
                int cur = s.StateId;
                int steps = 0;
                while (cur != -1)
                {
                    if (!seen.Add(cur))
                        throw new HFSMValidationException(
                            $"State {s.StateId} has a cycle in its ParentId chain in HFSM root {_rootId}");
                    if (++steps > HFSMComponent.MaxDepth)
                        throw new HFSMValidationException(
                            $"State {s.StateId} hierarchy depth exceeds MaxDepth ({HFSMComponent.MaxDepth}) in HFSM root {_rootId}");
                    cur = byId[cur].ParentId;
                }
            }
        }

        // Reachability from the default state through transitions and the default-child chain.
        private void ReportUnreachable(Dictionary<int, StateDef> byId, bool strict)
        {
            var reached = new HashSet<int>();
            var queue = new Queue<int>();
            reached.Add(_defaultStateId);
            queue.Enqueue(_defaultStateId);
            while (queue.Count > 0)
            {
                var def = byId[queue.Dequeue()];
                if (def.DefaultChildId >= 0 && reached.Add(def.DefaultChildId))
                    queue.Enqueue(def.DefaultChildId);
                foreach (var t in def.Transitions)
                {
                    if (reached.Add(t.TargetStateId))
                        queue.Enqueue(t.TargetStateId);
                }
            }

            foreach (var s in _states)
            {
                if (!reached.Contains(s.StateId))
                    Advisory(strict, $"State {s.StateId} is unreachable from default state {_defaultStateId} in HFSM root {_rootId}");
            }
        }

        // Reference-identity comparer for the visited set — dedups shared singletons and guards source cycles by
        // object identity, never by an overridden Equals/GetHashCode (which would wrongly skip a distinct instance).
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            bool IEqualityComparer<object>.Equals(object a, object b) => ReferenceEquals(a, b);
            int IEqualityComparer<object>.GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        // Build-time guard for the AIParam unassigned-field hole: a default(AIParam<T>) (a field never wired
        // via Const/From) silently resolves to default(T) in release — the runtime Debug.Assert is compiled out
        // (and, on Unity, may not surface even in DEBUG). Here we walk every Decision/Action the graph references,
        // reflect their AIParam<> fields, and throw on any unassigned one — caught at Build() on all builds,
        // before the first tick. Recurses into AIFunction sources (AIParam.From(...)) so chained sub-params are
        // covered too. Init-time only (reflection cost is not on a per-frame path).
        //
        // Coverage: direct AIParam<>/AIFunction-typed fields of each decision/action and (recursively) of their
        // sources. NOT covered: an AIParam nested inside a user struct field, or inside an array/collection — wire
        // such cases explicitly (a raw, unassigned AIFunction field instead fails loud at runtime with an NRE, not
        // a silent default, so it needs no separate guard here).
        private void ValidateAIParams(HFSMStateNode[] nodes)
        {
            var visited = new HashSet<object>(ReferenceComparer.Instance);  // identity dedup + source-cycle guard
            var fieldCache = new Dictionary<Type, FieldInfo[]>();
            foreach (var node in nodes)
            {
                if (node.Transitions != null)
                    foreach (var t in node.Transitions)
                        ProbeAIParams(t.Decision, node.StateId, $"transition→{t.TargetStateId} decision", visited, fieldCache);
                ProbeActionArray(node.OnEnterActions,  node.StateId, "OnEnter",  visited, fieldCache);
                ProbeActionArray(node.OnUpdateActions, node.StateId, "OnUpdate", visited, fieldCache);
                ProbeActionArray(node.OnExitActions,   node.StateId, "OnExit",   visited, fieldCache);
            }
        }

        private void ProbeActionArray(AIAction[] actions, int stateId, string role,
            HashSet<object> visited, Dictionary<Type, FieldInfo[]> fieldCache)
        {
            if (actions == null) return;
            for (int i = 0; i < actions.Length; i++)
                ProbeAIParams(actions[i], stateId, $"{role}[{i}]", visited, fieldCache);
        }

        // Reflects the AIParam<>/AIFunction-typed fields of one owner (Decision/Action/AIFunction source) and
        // throws on an unassigned AIParam; recurses into each param's source and any directly-held AIFunction.
        private void ProbeAIParams(object owner, int stateId, string role,
            HashSet<object> visited, Dictionary<Type, FieldInfo[]> fieldCache)
        {
            if (owner == null || !visited.Add(owner)) return;

            foreach (var f in GetParamRelevantFields(owner.GetType(), fieldCache))
            {
                if (IsClosedGenericOf(f.FieldType, typeof(AIParam<>)))
                {
                    var probe = (IAIParamProbe)f.GetValue(owner);
                    if (!probe.IsAssigned)
                        throw new HFSMValidationException(
                            $"State {stateId} {role}: {owner.GetType().Name}.{f.Name} has an unassigned " +
                            $"AIParam<{f.FieldType.GenericTypeArguments[0].Name}> (wire it with AIParam.Const/From) in HFSM root {_rootId}");
                    if (probe.SourceOrNull != null)
                        ProbeAIParams(probe.SourceOrNull, stateId, $"{role}.{f.Name} source", visited, fieldCache);
                }
                else // a directly-held AIFunction<> source field (not wrapped in an AIParam)
                {
                    ProbeAIParams(f.GetValue(owner), stateId, $"{role}.{f.Name}", visited, fieldCache);
                }
            }
        }

        // AIParam<> and AIFunction<>-derived instance fields (incl. inherited private), cached per type.
        private static FieldInfo[] GetParamRelevantFields(Type type, Dictionary<Type, FieldInfo[]> cache)
        {
            if (cache.TryGetValue(type, out var cached)) return cached;

            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (IsClosedGenericOf(f.FieldType, typeof(AIParam<>)) || DerivesFromGeneric(f.FieldType, typeof(AIFunction<>)))
                        list.Add(f);
                }
            }
            var arr = list.ToArray();
            cache[type] = arr;
            return arr;
        }

        private static bool IsClosedGenericOf(Type t, Type genericDefinition)
            => t.IsGenericType && t.GetGenericTypeDefinition() == genericDefinition;

        private static bool DerivesFromGeneric(Type t, Type genericDefinition)
        {
            for (var b = t; b != null; b = b.BaseType)
                if (b.IsGenericType && b.GetGenericTypeDefinition() == genericDefinition) return true;
            return false;
        }

        private void Advisory(bool strict, string message)
        {
            if (strict)
                throw new HFSMValidationException(message);
            if (_logger != null && _logger.IsEnabled(KLogLevel.Warning))
                _logger.Log(KLogLevel.Warning, message, null);
        }

        /// <summary>Per-state builder returned by State(...). Chains back to the parent HFSMBuilder.</summary>
        public sealed class StateBuilder
        {
            private readonly HFSMBuilder _builder;
            private readonly StateDef _def;

            internal StateBuilder(HFSMBuilder builder, StateDef def)
            {
                _builder = builder;
                _def = def;
            }

            /// <summary>Sets the current state's enter actions. May be called at most once.</summary>
            public StateBuilder OnEnter(params AIAction[] actions)
            {
                if (_def.OnEnterActions != null)
                    throw new HFSMValidationException($"State {_def.StateId} OnEnter set more than once in HFSM root {_builder._rootId}");
                _def.OnEnterActions = actions;
                return this;
            }

            /// <summary>Sets the current state's update actions. May be called at most once.</summary>
            public StateBuilder OnUpdate(params AIAction[] actions)
            {
                if (_def.OnUpdateActions != null)
                    throw new HFSMValidationException($"State {_def.StateId} OnUpdate set more than once in HFSM root {_builder._rootId}");
                _def.OnUpdateActions = actions;
                return this;
            }

            /// <summary>Sets the current state's exit actions. May be called at most once.</summary>
            public StateBuilder OnExit(params AIAction[] actions)
            {
                if (_def.OnExitActions != null)
                    throw new HFSMValidationException($"State {_def.StateId} OnExit set more than once in HFSM root {_builder._rootId}");
                _def.OnExitActions = actions;
                return this;
            }

            /// <summary>Sets the current state's display name (<see cref="HFSMStateNode.Name"/>). May be called at most once.</summary>
            public StateBuilder Named(string name)
            {
                if (_def.Name != null)
                    throw new HFSMValidationException($"State {_def.StateId} Named set more than once in HFSM root {_builder._rootId}");
                if (string.IsNullOrWhiteSpace(name))
                    throw new HFSMValidationException($"State {_def.StateId} name must not be null or blank in HFSM root {_builder._rootId}");
                _def.Name = name;
                return this;
            }

            /// <summary>Adds a transition. Higher priority is evaluated first (Build sorts stably by descending priority).
            /// Multiple transitions to the same target are allowed.</summary>
            public StateBuilder To(int targetStateId, HFSMDecision decision, int priority, int eventId = 0)
            {
                _def.Transitions.Add(new HFSMTransitionNode
                {
                    Priority      = priority,
                    TargetStateId = targetStateId,
                    Decision      = decision,
                    EventId       = eventId,
                });
                return this;
            }

            /// <summary>Declares the next state.</summary>
            public StateBuilder State(int stateId, int parentId = -1, int defaultChildId = -1)
                => _builder.State(stateId, parentId, defaultChildId);

            /// <summary>Validates, sorts, and registers the graph.</summary>
            public HFSMRoot Build(bool strict = false) => _builder.Build(strict);
        }
    }
}
