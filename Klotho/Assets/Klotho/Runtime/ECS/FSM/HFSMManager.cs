using System;

namespace xpTURN.Klotho.ECS.FSM
{
    public static class HFSMManager
    {
        public static unsafe void Init(ref Frame frame, EntityRef entity, int rootId)
        {
            if (frame.Has<HFSMComponent>(entity))
                throw new InvalidOperationException($"[HFSMManager] Entity {entity} already has HFSMComponent. Init() must not be called twice.");

            if (!HFSMRoot.Has(rootId))
                throw new ArgumentException($"[HFSMManager] Unknown rootId: {rootId}. Call HFSMRoot.Register() before Init().");

            var component = new HFSMComponent { RootId = rootId };
            for (int i = 0; i < 8; i++) component.ActiveStateIds[i] = -1;
            frame.Add(entity, component);

            var root = HFSMRoot.Get(rootId);
            ref var fsm = ref frame.Get<HFSMComponent>(entity);
            var context = new AIContext { Frame = frame, Entity = entity };

            EnterChain(ref fsm, root, root.DefaultStateId, ref context);
            fsm.StateElapsedTicks = 0;
        }

        public static unsafe void Update(ref Frame frame, EntityRef entity)
        {
            var context = new AIContext { Frame = frame, Entity = entity };
            Update(ref frame, entity, ref context);
        }

        public static unsafe void Update(ref Frame frame, EntityRef entity, ref AIContext context)
        {
            if (!frame.Has<HFSMComponent>(entity))
                throw new InvalidOperationException($"[HFSMManager] Entity {entity} does not have HFSMComponent. Call Init() first.");

            ref var fsm = ref frame.Get<HFSMComponent>(entity);
            var root = HFSMRoot.Get(fsm.RootId);

            var leafState = root.States[fsm.ActiveStateIds[fsm.ActiveDepth - 1]];

            // OnUpdate
            ExecuteActions(leafState.OnUpdateActions, ref context);
            fsm.StateElapsedTicks++;

            // Evaluate transitions
            if (leafState.Transitions != null)
            {
                for (int i = 0; i < leafState.Transitions.Length; i++)
                {
                    var t = leafState.Transitions[i];

                    if (t.EventId != 0 && !HasPendingEvent(ref fsm, t.EventId))
                        continue;

                    if (t.Decision != null && !t.Decision.Decide(ref context))
                        continue;

                    ChangeState(ref frame, ref fsm, root, ref context, t.TargetStateId);
                    break;
                }
            }

            fsm.PendingEventCount = 0;
        }

        public static unsafe int GetLeafStateId(ref Frame frame, EntityRef entity)
        {
            ref var fsm = ref frame.Get<HFSMComponent>(entity);
            return fsm.ActiveStateIds[fsm.ActiveDepth - 1];
        }

        public static unsafe bool TriggerEvent(ref Frame frame, EntityRef entity, int eventId)
        {
            if (!frame.Has<HFSMComponent>(entity))
                throw new InvalidOperationException($"[HFSMManager] Entity {entity} does not have HFSMComponent.");

            ref var fsm = ref frame.Get<HFSMComponent>(entity);

            if (fsm.PendingEventCount >= 4)
            {
                return false;
            }

            fsm.PendingEventIds[fsm.PendingEventCount] = eventId;
            fsm.PendingEventCount++;
            return true;
        }

        public static unsafe int GetActiveStateIds(ref Frame frame, EntityRef entity, Span<int> output)
        {
            ref var fsm = ref frame.Get<HFSMComponent>(entity);
            int depth = fsm.ActiveDepth;
            for (int i = 0; i < depth; i++) output[i] = fsm.ActiveStateIds[i];
            return depth;
        }

        public static unsafe int GetPendingEventIds(ref Frame frame, EntityRef entity, Span<int> output)
        {
            ref var fsm = ref frame.Get<HFSMComponent>(entity);
            int count = fsm.PendingEventCount;
            for (int i = 0; i < count; i++) output[i] = fsm.PendingEventIds[i];
            return count;
        }

        public static void GetDebugInfo(ref Frame frame, EntityRef entity,
            out int rootId, out int activeDepth, out int stateElapsedTicks, out int pendingEventCount)
        {
            ref readonly var fsm = ref frame.GetReadOnly<HFSMComponent>(entity);
            rootId = fsm.RootId;
            activeDepth = fsm.ActiveDepth;
            stateElapsedTicks = fsm.StateElapsedTicks;
            pendingEventCount = fsm.PendingEventCount;
        }

        // --- Internal ---

        private static unsafe void EnterChain(ref HFSMComponent fsm, HFSMRoot root, int stateId, ref AIContext context)
        {
            var state = root.States[stateId];
            ExecuteActions(state.OnEnterActions, ref context);

            fsm.ActiveStateIds[fsm.ActiveDepth] = stateId;
            fsm.ActiveDepth++;

            if (state.DefaultChildId != -1)
                EnterChain(ref fsm, root, state.DefaultChildId, ref context);
        }

        private static unsafe void ExitChain(ref HFSMComponent fsm, HFSMRoot root, int fromDepth, int toDepth, ref AIContext context)
        {
            // OnExit in order from fromDepth-1 (leaf) → toDepth (just before LCA)
            for (int d = fromDepth - 1; d >= toDepth; d--)
            {
                var state = root.States[fsm.ActiveStateIds[d]];
                ExecuteActions(state.OnExitActions, ref context);
            }
        }

        private static unsafe void ChangeState(ref Frame frame, ref HFSMComponent fsm, HFSMRoot root, ref AIContext context, int targetStateId)
        {
            int fromLeafId = fsm.ActiveStateIds[fsm.ActiveDepth - 1];

            // CollectAncestors: leaf → root order (list of stateIds)
            Span<int> fromPath = stackalloc int[8];
            Span<int> toPath   = stackalloc int[8];
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
            for (int i = newBaseDepth; i < 8; i++) fsm.ActiveStateIds[i] = -1;
            fsm.ActiveDepth = newBaseDepth;
            for (int i = enterStartIdx; i >= 0; i--)
            {
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
        }

        private static int CollectAncestors(HFSMRoot root, int stateId, Span<int> path)
        {
            int len = 0;
            int cur = stateId;
            while (cur != -1)
            {
                path[len++] = cur;
                cur = root.States[cur].ParentId;
            }
            return len;
        }

        private static unsafe bool HasPendingEvent(ref HFSMComponent fsm, int eventId)
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
