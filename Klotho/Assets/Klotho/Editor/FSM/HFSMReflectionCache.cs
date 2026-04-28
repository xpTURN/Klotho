using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.Editor.FSM
{
    /// <summary>
    /// HFSM state tree structure reflection cache.
    /// </summary>
    internal static class HFSMReflectionCache
    {
        // ── FieldInfo (app lifetime, looked up once) ──────────────────────────
        static readonly FieldInfo s_registryField =
            typeof(HFSMRoot).GetField("_registry", BindingFlags.NonPublic | BindingFlags.Static);

        // ── Per-RootId derived cache (immutable within a session) ────────────
        static readonly Dictionary<int, HFSMRoot> s_roots = new();
        static readonly Dictionary<int, Dictionary<int, List<int>>> s_childMaps = new();
        static readonly Dictionary<int, Dictionary<int, string>> s_stateNames = new();

        // ── Per-instance name cache (based on object identity) ────────────────
        static readonly ConditionalWeakTable<AIAction, StrongBox<string>> s_actionNames = new();
        static readonly ConditionalWeakTable<HFSMDecision, StrongBox<string>> s_decisionNames = new();

        // ── Public API ────────────────────────────────────────────────────────

        public static Dictionary<int, HFSMRoot> GetRegistry()
            => s_registryField?.GetValue(null) as Dictionary<int, HFSMRoot>;

        public static bool TryGetRoot(int rootId, out HFSMRoot root)
        {
            if (s_roots.TryGetValue(rootId, out root)) return true;

            var reg = GetRegistry();
            if (reg != null && reg.TryGetValue(rootId, out root))
            {
                s_roots[rootId] = root;
                return true;
            }
            return false;
        }

        public static Dictionary<int, List<int>> GetChildMap(int rootId, HFSMRoot root)
        {
            if (s_childMaps.TryGetValue(rootId, out var map)) return map;

            map = new Dictionary<int, List<int>>();
            foreach (var state in root.States)
            {
                if (state.ParentId == -1) continue;
                if (!map.TryGetValue(state.ParentId, out var list))
                    map[state.ParentId] = list = new List<int>();
                list.Add(state.StateId);
            }
            s_childMaps[rootId] = map;
            return map;
        }

        public static Dictionary<int, string> GetStateNameMap(int rootId, Type stateIdType)
        {
            if (s_stateNames.TryGetValue(rootId, out var map)) return map;

            map = new Dictionary<int, string>();
            foreach (var field in stateIdType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(int))
                    map[(int)field.GetValue(null)] = field.Name;
            }
            s_stateNames[rootId] = map;
            return map;
        }

        public static string GetActionName(AIAction action)
        {
            if (!s_actionNames.TryGetValue(action, out var box))
                s_actionNames.Add(action, box = new StrongBox<string>(action.GetType().Name));
            return box.Value;
        }

        public static string GetDecisionName(HFSMDecision decision)
        {
            if (decision == null) return "(unconditional)";
            if (!s_decisionNames.TryGetValue(decision, out var box))
                s_decisionNames.Add(decision, box = new StrongBox<string>(decision.GetType().Name));
            return box.Value;
        }

        public static void ClearSessionCache()
        {
            s_roots.Clear();
            s_childMaps.Clear();
            s_stateNames.Clear();
        }
    }
}
