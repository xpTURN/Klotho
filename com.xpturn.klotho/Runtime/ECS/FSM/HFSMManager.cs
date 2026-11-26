using System;

namespace xpTURN.Klotho.ECS.FSM
{
    public static class HFSMManager
    {
        // ─────────────────────────────────────────────────────────────────────────
        // Generic entry points — one HFSM axis per component type (TComp embeds HFSMState
        // as its first field; reinterpreted via Unsafe.As). The non-generic overloads below
        // delegate to <HFSMComponent> so existing single-axis callers are unchanged.
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>Reinterpret a host component's first field as its HFSMState (mutable).</summary>
        private static unsafe ref HFSMState StateOf<TComp>(ref Frame frame, EntityRef entity)
            where TComp : unmanaged, IComponent, IHFSMHost
            => ref KUnsafe.As<TComp, HFSMState>(ref frame.Get<TComp>(entity));

        public static unsafe void Init<TComp>(ref Frame frame, EntityRef entity, int rootId)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            var context = new AIContext { Frame = frame, Entity = entity };
            Init<TComp>(ref frame, entity, rootId, ref context);
        }

        public static unsafe void Init<TComp>(ref Frame frame, EntityRef entity, int rootId, ref AIContext context)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (!HFSMRoot.Has(rootId))
                throw new ArgumentException($"[HFSMManager] Unknown rootId: {rootId}. Call HFSMRoot.Register() before Init().");

            Init<TComp>(ref frame, entity, HFSMRoot.Get(rootId), ref context);
        }

        public static unsafe void Init<TComp>(ref Frame frame, EntityRef entity, HFSMRoot root)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            var context = new AIContext { Frame = frame, Entity = entity };
            Init<TComp>(ref frame, entity, root, ref context);
        }

        public static unsafe void Init<TComp>(ref Frame frame, EntityRef entity, HFSMRoot root, ref AIContext context)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (frame.Has<TComp>(entity))
                throw new InvalidOperationException($"[HFSMManager] Entity {entity} already has {typeof(TComp).Name}. Init() must not be called twice.");

            frame.Add(entity, default(TComp));

            ref HFSMState fsm = ref StateOf<TComp>(ref frame, entity);
            fsm.RootId = root.RootId;
            for (int i = 0; i < HFSMState.MaxDepth; i++) fsm.ActiveStateIds[i] = -1;

            EnterChain(ref fsm, root, root.DefaultStateId, ref context);
            fsm.StateElapsedTicks = 0;
            AssertChainConsistent(ref fsm, root);
        }

        public static unsafe void Deinit<TComp>(ref Frame frame, EntityRef entity)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            var context = new AIContext { Frame = frame, Entity = entity };
            Deinit<TComp>(ref frame, entity, ref context);
        }

        public static unsafe void Deinit<TComp>(ref Frame frame, EntityRef entity, ref AIContext context)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (!frame.Has<TComp>(entity))
                return;

            ref HFSMState fsm = ref StateOf<TComp>(ref frame, entity);
            Deinit<TComp>(ref frame, entity, HFSMRoot.Get(fsm.RootId), ref context);
        }

        public static unsafe void Deinit<TComp>(ref Frame frame, EntityRef entity, HFSMRoot root)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            var context = new AIContext { Frame = frame, Entity = entity };
            Deinit<TComp>(ref frame, entity, root, ref context);
        }

        public static unsafe void Deinit<TComp>(ref Frame frame, EntityRef entity, HFSMRoot root, ref AIContext context)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (!frame.Has<TComp>(entity))
                return;

            ref HFSMState fsm = ref StateOf<TComp>(ref frame, entity);
            ExitChain(ref fsm, root, fsm.ActiveDepth, 0, ref context);
            frame.Remove<TComp>(entity);
        }

        public static unsafe void Update<TComp>(ref Frame frame, EntityRef entity)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            var context = new AIContext { Frame = frame, Entity = entity };
            Update<TComp>(ref frame, entity, ref context);
        }

        public static unsafe void Update<TComp>(ref Frame frame, EntityRef entity, ref AIContext context)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (!frame.Has<TComp>(entity))
                throw new InvalidOperationException($"[HFSMManager] Entity {entity} does not have {typeof(TComp).Name}. Call Init() first.");

            ref HFSMState fsm = ref StateOf<TComp>(ref frame, entity);
            Update<TComp>(ref frame, entity, HFSMRoot.Get(fsm.RootId), ref context);
        }

        public static unsafe void Update<TComp>(ref Frame frame, EntityRef entity, HFSMRoot root)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            var context = new AIContext { Frame = frame, Entity = entity };
            Update<TComp>(ref frame, entity, root, ref context);
        }

        public static unsafe void Update<TComp>(ref Frame frame, EntityRef entity, HFSMRoot root, ref AIContext context)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (!frame.Has<TComp>(entity))
                throw new InvalidOperationException($"[HFSMManager] Entity {entity} does not have {typeof(TComp).Name}. Call Init() first.");

            ref HFSMState fsm = ref StateOf<TComp>(ref frame, entity);

            // OnUpdate — run the whole active chain root→leaf (parent before child).
            for (int d = 0; d < fsm.ActiveDepth; d++)
                ExecuteActions(root.States[fsm.ActiveStateIds[d]].OnUpdateActions, ref context);
            fsm.StateElapsedTicks++;

            // Evaluate transitions leaf→root (transition inheritance): a parent's transitions apply to all
            // descendants. Child level is evaluated first, so a child transition preempts a parent's (hierarchy
            // over priority); within a level, priority-sorted order. First firing transition wins.
            TryFireTransition(ref fsm, root, ref context);

            fsm.PendingEventCount = 0;
        }

        // Read-only query: returns the sentinel -1 for an entity that carries no TComp (never Init'd / already
        // Deinit'd) instead of throwing — these APIs feed debug overlays/logs where a missing FSM is a valid answer.
        public static unsafe int GetLeafStateId<TComp>(ref Frame frame, EntityRef entity)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (!frame.Has<TComp>(entity)) return -1;
            ref readonly HFSMState fsm = ref KUnsafe.As<TComp, HFSMState>(ref KUnsafe.AsRef(in frame.GetReadOnly<TComp>(entity)));
            if (fsm.ActiveDepth <= 0) return -1;
            fixed (int* ids = fsm.ActiveStateIds)
                return ids[fsm.ActiveDepth - 1];
        }

        public static unsafe bool TriggerEvent<TComp>(ref Frame frame, EntityRef entity, int eventId)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (!frame.Has<TComp>(entity))
                throw new InvalidOperationException($"[HFSMManager] Entity {entity} does not have {typeof(TComp).Name}.");

            ref HFSMState fsm = ref StateOf<TComp>(ref frame, entity);

            // Dedup: matching is membership-based (HasPendingEvent is boolean), so a second copy of an
            // already-pending eventId changes no transition — it only wastes a slot and makes the queue fill
            // (and drop distinct events) sooner. Treat a re-trigger as an idempotent success without consuming
            // a slot, even when the queue is full.
            if (HasPendingEvent(ref fsm, eventId))
                return true;

            if (fsm.PendingEventCount >= HFSMState.MaxPendingEvents)
            {
                return false;
            }

            fsm.PendingEventIds[fsm.PendingEventCount] = eventId;
            fsm.PendingEventCount++;
            return true;
        }

        public static unsafe int GetActiveStateIds<TComp>(ref Frame frame, EntityRef entity, Span<int> output)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (!frame.Has<TComp>(entity)) return 0;
            ref readonly HFSMState fsm = ref KUnsafe.As<TComp, HFSMState>(ref KUnsafe.AsRef(in frame.GetReadOnly<TComp>(entity)));
            int depth = fsm.ActiveDepth;
            fixed (int* ids = fsm.ActiveStateIds)
                for (int i = 0; i < depth; i++) output[i] = ids[i];
            return depth;
        }

        public static unsafe int GetPendingEventIds<TComp>(ref Frame frame, EntityRef entity, Span<int> output)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (!frame.Has<TComp>(entity)) return 0;
            ref readonly HFSMState fsm = ref KUnsafe.As<TComp, HFSMState>(ref KUnsafe.AsRef(in frame.GetReadOnly<TComp>(entity)));
            int count = fsm.PendingEventCount;
            fixed (int* evs = fsm.PendingEventIds)
                for (int i = 0; i < count; i++) output[i] = evs[i];
            return count;
        }

        public static unsafe void GetDebugInfo<TComp>(ref Frame frame, EntityRef entity,
            out int rootId, out int activeDepth, out int stateElapsedTicks, out int pendingEventCount)
            where TComp : unmanaged, IComponent, IHFSMHost
        {
            if (!frame.Has<TComp>(entity))
            {
                rootId = -1; activeDepth = 0; stateElapsedTicks = 0; pendingEventCount = 0;
                return;
            }
            ref readonly HFSMState fsm = ref KUnsafe.As<TComp, HFSMState>(ref KUnsafe.AsRef(in frame.GetReadOnly<TComp>(entity)));
            rootId = fsm.RootId;
            activeDepth = fsm.ActiveDepth;
            stateElapsedTicks = fsm.StateElapsedTicks;
            pendingEventCount = fsm.PendingEventCount;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Back-compat non-generic overloads (single-axis = HFSMComponent). Existing callers unchanged.
        // ─────────────────────────────────────────────────────────────────────────

        public static void Init(ref Frame frame, EntityRef entity, int rootId)
            => Init<HFSMComponent>(ref frame, entity, rootId);
        public static void Init(ref Frame frame, EntityRef entity, int rootId, ref AIContext context)
            => Init<HFSMComponent>(ref frame, entity, rootId, ref context);
        public static void Init(ref Frame frame, EntityRef entity, HFSMRoot root)
            => Init<HFSMComponent>(ref frame, entity, root);
        public static void Init(ref Frame frame, EntityRef entity, HFSMRoot root, ref AIContext context)
            => Init<HFSMComponent>(ref frame, entity, root, ref context);
        public static void Deinit(ref Frame frame, EntityRef entity)
            => Deinit<HFSMComponent>(ref frame, entity);
        public static void Deinit(ref Frame frame, EntityRef entity, ref AIContext context)
            => Deinit<HFSMComponent>(ref frame, entity, ref context);
        public static void Deinit(ref Frame frame, EntityRef entity, HFSMRoot root)
            => Deinit<HFSMComponent>(ref frame, entity, root);
        public static void Deinit(ref Frame frame, EntityRef entity, HFSMRoot root, ref AIContext context)
            => Deinit<HFSMComponent>(ref frame, entity, root, ref context);
        public static void Update(ref Frame frame, EntityRef entity)
            => Update<HFSMComponent>(ref frame, entity);
        public static void Update(ref Frame frame, EntityRef entity, ref AIContext context)
            => Update<HFSMComponent>(ref frame, entity, ref context);
        public static void Update(ref Frame frame, EntityRef entity, HFSMRoot root)
            => Update<HFSMComponent>(ref frame, entity, root);
        public static void Update(ref Frame frame, EntityRef entity, HFSMRoot root, ref AIContext context)
            => Update<HFSMComponent>(ref frame, entity, root, ref context);
        public static int GetLeafStateId(ref Frame frame, EntityRef entity)
            => GetLeafStateId<HFSMComponent>(ref frame, entity);
        public static bool TriggerEvent(ref Frame frame, EntityRef entity, int eventId)
            => TriggerEvent<HFSMComponent>(ref frame, entity, eventId);
        public static int GetActiveStateIds(ref Frame frame, EntityRef entity, Span<int> output)
            => GetActiveStateIds<HFSMComponent>(ref frame, entity, output);
        public static int GetPendingEventIds(ref Frame frame, EntityRef entity, Span<int> output)
            => GetPendingEventIds<HFSMComponent>(ref frame, entity, output);
        public static void GetDebugInfo(ref Frame frame, EntityRef entity,
            out int rootId, out int activeDepth, out int stateElapsedTicks, out int pendingEventCount)
            => GetDebugInfo<HFSMComponent>(ref frame, entity, out rootId, out activeDepth, out stateElapsedTicks, out pendingEventCount);

        // ─────────────────────────────────────────────────────────────────────────
        // Internal — operate on the concrete HFSMState (axis-independent; unchanged algorithm).
        // ─────────────────────────────────────────────────────────────────────────

        // Transition inheritance: evaluate the active chain leaf→root. Child level first (preempts parent),
        // priority-sorted within a level. First firing transition performs the state change and returns true.
        // Pure additive over the old leaf-only path: the leaf (d == ActiveDepth-1) is evaluated first, so when a
        // leaf transition fires the result is identical to leaf-only; parents are only consulted on a leaf miss.
        private static unsafe bool TryFireTransition(ref HFSMState fsm, HFSMRoot root, ref AIContext context)
        {
            for (int d = fsm.ActiveDepth - 1; d >= 0; d--)
            {
                var transitions = root.States[fsm.ActiveStateIds[d]].Transitions;
                if (transitions == null) continue;
                for (int i = 0; i < transitions.Length; i++)
                {
                    var t = transitions[i];

                    if (t.EventId != 0 && !HasPendingEvent(ref fsm, t.EventId))
                        continue;

                    if (t.Decision != null && !t.Decision.Decide(ref context))
                        continue;

                    ChangeState(ref fsm, root, ref context, t.TargetStateId);
                    return true;
                }
            }
            return false;
        }

        private static unsafe void EnterChain(ref HFSMState fsm, HFSMRoot root, int stateId, ref AIContext context)
        {
            // Runtime guard (defense-in-depth; unreachable after HFSMBuilder validation). Before OnEnter so a
            // rejected state runs no side effects.
            if (fsm.ActiveDepth >= HFSMState.MaxDepth)
                throw new InvalidOperationException($"[HFSMManager] Active chain exceeds MaxDepth ({HFSMState.MaxDepth}); graph validation should have prevented this.");

            var state = root.States[stateId];
            ExecuteActions(state.OnEnterActions, ref context);

            fsm.ActiveStateIds[fsm.ActiveDepth] = stateId;
            fsm.ActiveDepth++;

            if (state.DefaultChildId != -1)
                EnterChain(ref fsm, root, state.DefaultChildId, ref context);
        }

        private static unsafe void ExitChain(ref HFSMState fsm, HFSMRoot root, int fromDepth, int toDepth, ref AIContext context)
        {
            // OnExit in order from fromDepth-1 (leaf) → toDepth (just before LCA)
            for (int d = fromDepth - 1; d >= toDepth; d--)
            {
                var state = root.States[fsm.ActiveStateIds[d]];
                ExecuteActions(state.OnExitActions, ref context);
            }
        }

        private static unsafe void ChangeState(ref HFSMState fsm, HFSMRoot root, ref AIContext context, int targetStateId)
        {
            int fromLeafId = fsm.ActiveStateIds[fsm.ActiveDepth - 1];

            // CollectAncestors: leaf → root order (list of stateIds)
            Span<int> fromPath = stackalloc int[HFSMState.MaxDepth];
            Span<int> toPath   = stackalloc int[HFSMState.MaxDepth];
            int fromLen = CollectAncestors(root, fromLeafId, fromPath);
            int toLen   = CollectAncestors(root, targetStateId, toPath);

            // LCA search (root direction = compare from the end of the array)
            // Both fromPath / toPath are in [leaf, ..., root] order
            int lcaDepth;      // LCA depth in fsm.ActiveStateIds terms (LCA inclusive; exit starts from LCA+1)
            int enterStartIdx; // enter start index within toPath (root→leaf direction)

            int exitToDepth;   // toDepth for ExitChain (exit from this depth down to the leaf)
            int newBaseDepth;  // fsm.ActiveDepth value after exit (enter start reference)

            if (fromLeafId == targetStateId)
            {
                // self-transition: exit/enter only the leaf
                lcaDepth      = fsm.ActiveDepth - 1;
                exitToDepth   = lcaDepth;       // d=lcaDepth → OnExit on leaf
                newBaseDepth  = lcaDepth;       // on enter, write to ActiveStateIds[lcaDepth]
                enterStartIdx = 0;
            }
            else
            {
                int fi = fromLen - 1;
                int ti = toLen - 1;
                int lastCommonFi = -1;
                while (fi >= 0 && ti >= 0 && fromPath[fi] == toPath[ti])
                {
                    lastCommonFi = fi;
                    fi--;
                    ti--;
                }
                if (lastCommonFi == -1)
                {
                    // No common ancestor → exit all, enter all
                    lcaDepth      = 0;
                    exitToDepth   = 0;          // exit everything down to the root
                    newBaseDepth  = 0;
                    enterStartIdx = toLen - 1;  // all of toPath
                }
                else
                {
                    // fromPath[lastCommonFi] is the LCA; LCA depth in fsm terms = fromLen-1-lastCommonFi
                    lcaDepth      = fromLen - 1 - lastCommonFi;
                    exitToDepth   = lcaDepth + 1;   // LCA itself is not exited
                    newBaseDepth  = lcaDepth + 1;
                    enterStartIdx = toLen - 2 - lcaDepth;
                }
            }

            // Exit chain: leaf → exitToDepth
            ExitChain(ref fsm, root, fsm.ActiveDepth, exitToDepth, ref context);

            // Initialize ActiveStateIds and set ActiveDepth
            for (int i = newBaseDepth; i < HFSMState.MaxDepth; i++) fsm.ActiveStateIds[i] = -1;
            fsm.ActiveDepth = newBaseDepth;
            for (int i = enterStartIdx; i >= 0; i--)
            {
                // Runtime guard (defense-in-depth; unreachable after HFSMBuilder validation).
                if (fsm.ActiveDepth >= HFSMState.MaxDepth)
                    throw new InvalidOperationException($"[HFSMManager] Active chain exceeds MaxDepth ({HFSMState.MaxDepth}); graph validation should have prevented this.");

                int stateId = toPath[i];
                var state = root.States[stateId];
                ExecuteActions(state.OnEnterActions, ref context);
                fsm.ActiveStateIds[fsm.ActiveDepth] = stateId;
                fsm.ActiveDepth++;
            }

            // Automatically enter DefaultChild of targetStateId (toPath[0])
            var targetLeaf = root.States[toPath[0]];
            if (targetLeaf.DefaultChildId != -1)
                EnterChain(ref fsm, root, targetLeaf.DefaultChildId, ref context);

            fsm.StateElapsedTicks = 0;
            // Single exit point — covers self-transition / no-common-ancestor / LCA branches.
            AssertChainConsistent(ref fsm, root);
        }

        private static int CollectAncestors(HFSMRoot root, int stateId, Span<int> path)
        {
            int len = 0;
            int cur = stateId;
            while (cur != -1)
            {
                // Throw (never clamp): a truncated path would silently yield a wrong LCA. Bound by path.Length
                // (== MaxDepth-sized caller buffer). Unreachable after HFSMBuilder validation.
                if (len >= path.Length)
                    throw new InvalidOperationException($"[HFSMManager] Ancestor chain exceeds {path.Length}; graph validation should have prevented this.");
                path[len++] = cur;
                cur = root.States[cur].ParentId;
            }
            return len;
        }

        // Debug-only invariant: the active chain (root→leaf, built via DefaultChild descent) must equal the
        // reverse of the leaf's ancestor chain (built via ParentId). A mismatch means BUG-3-class corruption
        // (DefaultChild↔Parent inconsistency) slipped past build validation. Compiled out in release.
        [System.Diagnostics.Conditional("DEBUG")]
        private static unsafe void AssertChainConsistent(ref HFSMState fsm, HFSMRoot root)
        {
            int depth = fsm.ActiveDepth;
            int leaf = fsm.ActiveStateIds[depth - 1];
            Span<int> path = stackalloc int[HFSMState.MaxDepth];
            int len = CollectAncestors(root, leaf, path);
            if (len != depth)
                throw new InvalidOperationException($"[HFSMManager] Active chain depth {depth} != ancestor depth {len} for leaf {leaf}.");
            for (int i = 0; i < depth; i++)
            {
                if (fsm.ActiveStateIds[i] != path[len - 1 - i])
                    throw new InvalidOperationException($"[HFSMManager] Active chain diverges from ancestor path at index {i} (leaf {leaf}).");
            }
        }

        private static unsafe bool HasPendingEvent(ref HFSMState fsm, int eventId)
        {
            for (int i = 0; i < fsm.PendingEventCount; i++)
            {
                if (fsm.PendingEventIds[i] == eventId)
                    return true;
            }
            return false;
        }

        private static void ExecuteActions(AIAction[] actions, ref AIContext context)
        {
            if (actions == null) return;
            for (int i = 0; i < actions.Length; i++)
                actions[i].Execute(ref context);
        }
    }
}
