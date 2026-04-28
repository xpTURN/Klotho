using System.Collections.Generic;

namespace xpTURN.Klotho.ECS.FSM
{
    public class HFSMRoot
    {
        public int RootId;
        public int DefaultStateId;
        public HFSMStateNode[] States;

        private static readonly Dictionary<int, HFSMRoot> _registry = new Dictionary<int, HFSMRoot>();

        public static void Register(HFSMRoot root) => _registry[root.RootId] = root;
        public static bool Has(int rootId) => _registry.ContainsKey(rootId);
        public static HFSMRoot Get(int rootId) => _registry.TryGetValue(rootId, out var root)
            ? root : throw new System.ArgumentException($"Unknown HFSMRoot: {rootId}");
    }

    public class HFSMStateNode
    {
        public int StateId;
        public int ParentId;
        public int DefaultChildId;

        public AIAction[] OnEnterActions;
        public AIAction[] OnUpdateActions;
        public AIAction[] OnExitActions;

        public HFSMTransitionNode[] Transitions;
    }

    public class HFSMTransitionNode
    {
        public int Priority;
        public int TargetStateId;
        public HFSMDecision Decision;
        public int EventId;
    }
}
