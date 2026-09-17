using System.Collections.Generic;

namespace xpTURN.Klotho.ECS.FSM
{
    public class HFSMRoot
    {
        public int RootId;
        public int DefaultStateId;
        public HFSMStateNode[] States;

        private static readonly Dictionary<int, HFSMRoot> _registry = new Dictionary<int, HFSMRoot>();

        /// <summary>Registers a root. Re-registering the same instance is a no-op; registering a
        /// different instance under an already-used id throws (call <see cref="Clear"/> first).</summary>
        public static void Register(HFSMRoot root)
        {
            if (_registry.TryGetValue(root.RootId, out var existing))
            {
                if (ReferenceEquals(existing, root)) return; // idempotent re-register
                throw new System.InvalidOperationException(
                    $"HFSMRoot {root.RootId} already registered with a different instance. Call Clear() first.");
            }
            _registry[root.RootId] = root;
        }

        public static bool Has(int rootId) => _registry.ContainsKey(rootId);
        public static HFSMRoot Get(int rootId) => _registry.TryGetValue(rootId, out var root)
            ? root : throw new System.ArgumentException($"Unknown HFSMRoot: {rootId}");

        /// <summary>Empties the registry. For tests / editor domain-reload teardown — not for runtime use.</summary>
        public static void Clear() => _registry.Clear();
    }

    public class HFSMStateNode
    {
        public int StateId;
        public int ParentId;
        public int DefaultChildId;

        /// <summary>Display name declared with <see cref="HFSMBuilder.StateBuilder.Named"/>; null when none was.
        /// Display only — tools and logs read it, the runtime never does. It is not part of any snapshot or hash,
        /// so logic must not branch on it: renaming a state must never change behaviour.</summary>
        public string Name;

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
