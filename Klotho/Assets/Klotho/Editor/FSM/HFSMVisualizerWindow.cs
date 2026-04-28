using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.ECS.FSM;
using xpTURN.Klotho.Unity;

namespace xpTURN.Klotho.Editor.FSM
{
    /// <summary>
    /// Editor window that shows the HFSM state tree in real time.
    /// </summary>
    public class HFSMVisualizerWindow : EditorWindow
    {
        [MenuItem("Tools/Klotho/Visualizer/HFSM")]
        static void Open() => GetWindow<HFSMVisualizerWindow>("HFSM Debug");

        private int _targetEntityId;
        private int _selectedStateId = -1;
        private Vector2 _scrollPosTree;
        private Vector2 _scrollPosInfo;

        private HFSMRoot _cachedRoot;
        private int _cachedRootId = -1;
        private Dictionary<int, List<int>> _childMap;
        private Dictionary<int, string> _stateNameMap;

        private readonly int[] _activeIdsBuf = new int[8];
        private int _activeDepth;

        private readonly int[] _pendingEventsBuf = new int[4];
        private int _pendingEventCount;

        // Current stage: BotStateId is specified directly
        private Type _stateIdType;

        void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += OnEditorUpdate;
            ResolveStateIdType();
        }

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.update -= OnEditorUpdate;
        }

        void OnEditorUpdate()
        {
            if (Application.isPlaying)
                Repaint();
        }

        void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                HFSMReflectionCache.ClearSessionCache();
                _cachedRoot = null;
                _cachedRootId = -1;
                _childMap = null;
                _stateNameMap = null;
            }
        }

        void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Play mode only", MessageType.Info);
                return;
            }

            var bridge = EcsDebugBridge.Instance;
            if (bridge == null || bridge.Simulation == null)
            {
                EditorGUILayout.HelpBox("EcsDebugBridge not registered", MessageType.Warning);
                return;
            }

            DrawToolbar();
            EditorGUILayout.Space(4);

            var frame = bridge.Simulation.Frame;
            if (!bridge.AllEntityIndexMap.TryGetValue(_targetEntityId, out var entity))
            {
                EditorGUILayout.HelpBox($"No HFSM entity found for EntityIndex {_targetEntityId}.", MessageType.Warning);
                return;
            }

            if (!frame.Has<HFSMComponent>(entity))
            {
                EditorGUILayout.HelpBox($"Entity({entity}) has no HFSMComponent.", MessageType.Warning);
                return;
            }

            // Read active states, events and debug info
            _activeDepth = HFSMManager.GetActiveStateIds(ref frame, entity, new Span<int>(_activeIdsBuf));
            _pendingEventCount = HFSMManager.GetPendingEventIds(ref frame, entity, new Span<int>(_pendingEventsBuf));
            HFSMManager.GetDebugInfo(ref frame, entity,
                out int rootId, out int activeDepth, out int stateElapsedTicks, out int pendingEventCount);

            // Root cache
            if (_cachedRootId != rootId || _cachedRoot == null)
            {
                if (HFSMReflectionCache.TryGetRoot(rootId, out _cachedRoot))
                {
                    _cachedRootId = rootId;
                    _childMap = HFSMReflectionCache.GetChildMap(rootId, _cachedRoot);
                    if (_stateIdType != null)
                        _stateNameMap = HFSMReflectionCache.GetStateNameMap(rootId, _stateIdType);
                    else
                        _stateNameMap = new Dictionary<int, string>();
                }
                else
                {
                    EditorGUILayout.HelpBox($"No HFSMRoot found for RootId {rootId}.", MessageType.Error);
                    return;
                }
            }

            // Split left/right
            EditorGUILayout.BeginHorizontal();

            // Left: state tree
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.4f));
            EditorGUILayout.LabelField("State Graph", EditorStyles.boldLabel);
            _selectedStateId = HFSMStateTreeRenderer.Render(
                _cachedRoot, _childMap, _stateNameMap,
                _activeIdsBuf, _activeDepth, _selectedStateId,
                _scrollPosTree, out _scrollPosTree);
            EditorGUILayout.EndVertical();

            // Right: runtime info
            EditorGUILayout.BeginVertical();
            _scrollPosInfo = EditorGUILayout.BeginScrollView(_scrollPosInfo);
            DrawRuntimeInfo(rootId, activeDepth, stateElapsedTicks);
            DrawActivePath();
            DrawSelectedState();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Entity Id:", GUILayout.Width(60));
            _targetEntityId = EditorGUILayout.IntField(_targetEntityId, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRuntimeInfo(int rootId, int activeDepth, int stateElapsedTicks)
        {
            EditorGUILayout.LabelField("Runtime Info", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("RootId", rootId.ToString());
            EditorGUILayout.LabelField("ActiveDepth", activeDepth.ToString());
            EditorGUILayout.LabelField("StateElapsedTicks", stateElapsedTicks.ToString());
            EditorGUILayout.LabelField("PendingEvents", FormatPendingEvents());
            EditorGUI.indentLevel--;
        }

        private string FormatPendingEvents()
        {
            if (_pendingEventCount == 0) return "[]";
            var parts = new string[_pendingEventCount];
            for (int i = 0; i < _pendingEventCount; i++)
                parts[i] = _pendingEventsBuf[i].ToString();
            return "[" + string.Join(", ", parts) + "]";
        }

        private void DrawActivePath()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Active Path", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            for (int i = 0; i < _activeDepth; i++)
            {
                int id = _activeIdsBuf[i];
                string name = (_stateNameMap != null && _stateNameMap.TryGetValue(id, out var n)) ? n : id.ToString();
                string suffix = (i == _activeDepth - 1) ? "  <-- leaf" : "";
                EditorGUILayout.LabelField($"[{i}] {name}{suffix}");
            }
            EditorGUI.indentLevel--;
        }

        private void DrawSelectedState()
        {
            if (_cachedRoot == null || _selectedStateId < 0 || _selectedStateId >= _cachedRoot.States.Length)
                return;

            var state = _cachedRoot.States[_selectedStateId];

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Selected State", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("StateId", state.StateId.ToString());
            EditorGUILayout.LabelField("ParentId", state.ParentId.ToString());
            EditorGUILayout.LabelField("DefaultChildId", state.DefaultChildId.ToString());
            EditorGUILayout.LabelField("OnEnter", FormatActions(state.OnEnterActions));
            EditorGUILayout.LabelField("OnUpdate", FormatActions(state.OnUpdateActions));
            EditorGUILayout.LabelField("OnExit", FormatActions(state.OnExitActions));

            // Transitions
            if (state.Transitions != null && state.Transitions.Length > 0)
            {
                EditorGUILayout.LabelField("Transitions:");
                EditorGUI.indentLevel++;
                for (int i = 0; i < state.Transitions.Length; i++)
                {
                    var t = state.Transitions[i];
                    string targetName = (_stateNameMap != null && _stateNameMap.TryGetValue(t.TargetStateId, out var tn)) ? tn : t.TargetStateId.ToString();
                    string decisionName = HFSMReflectionCache.GetDecisionName(t.Decision);
                    string eventStr = t.EventId != 0 ? $" E{t.EventId}" : "";
                    EditorGUILayout.LabelField($"P{t.Priority} -> {targetName}  [{decisionName}]{eventStr}");
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.indentLevel--;
        }

        private static string FormatActions(AIAction[] actions)
        {
            if (actions == null || actions.Length == 0) return "[]";
            var names = new string[actions.Length];
            for (int i = 0; i < actions.Length; i++)
                names[i] = HFSMReflectionCache.GetActionName(actions[i]);
            return "[" + string.Join(", ", names) + "]";
        }

        private void ResolveStateIdType()
        {
            // Current stage: search BotStateId directly
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType("Brawler.BotStateId");
                if (type != null)
                {
                    _stateIdType = type;
                    return;
                }
            }
        }
    }
}
