using System;
using System.Collections.Generic;
using UnityEngine;
using xpTURN.Klotho.ECS.FSM;

namespace xpTURN.Klotho.Editor.FSM
{
    /// <summary>
    /// Renders the HFSM state tree in an editor window.
    /// </summary>
    internal static class HFSMStateTreeRenderer
    {
        private static readonly Color ActiveColor = new Color(0.2f, 0.8f, 0.2f, 1f);
        private static readonly Color SelectedColor = new Color(0.3f, 0.5f, 0.9f, 0.3f);

        public static int Render(
            HFSMRoot root,
            Dictionary<int, List<int>> childMap,
            Dictionary<int, string> stateNameMap,
            int[] activeIds,
            int activeDepth,
            int selectedStateId,
            Vector2 scrollPos,
            out Vector2 newScrollPos)
        {
            int newSelected = selectedStateId;
            newScrollPos = GUILayout.BeginScrollView(scrollPos);

            // Find root nodes: ParentId == -1
            for (int i = 0; i < root.States.Length; i++)
            {
                var state = root.States[i];
                if (state.ParentId == -1)
                    RenderNode(root, state.StateId, 0, childMap, stateNameMap, activeIds, activeDepth, ref newSelected);
            }

            GUILayout.EndScrollView();
            return newSelected;
        }

        private static void RenderNode(
            HFSMRoot root,
            int stateId,
            int depth,
            Dictionary<int, List<int>> childMap,
            Dictionary<int, string> stateNameMap,
            int[] activeIds,
            int activeDepth,
            ref int selectedStateId)
        {
            bool isActive = IsActive(stateId, activeIds, activeDepth);
            bool isSelected = stateId == selectedStateId;
            bool hasChildren = childMap.ContainsKey(stateId);

            string stateName = stateNameMap.TryGetValue(stateId, out var name) ? name : stateId.ToString();
            string label = hasChildren
                ? $"  {stateName}  {(isActive ? "●" : "")}"
                : $"    {stateName}  {(isActive ? "●" : "")}";

            GUILayout.BeginHorizontal();
            GUILayout.Space(depth * 16f);

            var prevColor = GUI.backgroundColor;
            if (isSelected) GUI.backgroundColor = SelectedColor;
            if (isActive)
            {
                var prevContentColor = GUI.contentColor;
                GUI.contentColor = ActiveColor;
                if (GUILayout.Button(label, GUILayout.ExpandWidth(false)))
                    selectedStateId = stateId;
                GUI.contentColor = prevContentColor;
            }
            else
            {
                if (GUILayout.Button(label, GUILayout.ExpandWidth(false)))
                    selectedStateId = stateId;
            }
            GUI.backgroundColor = prevColor;

            GUILayout.EndHorizontal();

            if (hasChildren)
            {
                var children = childMap[stateId];
                for (int i = 0; i < children.Count; i++)
                    RenderNode(root, children[i], depth + 1, childMap, stateNameMap, activeIds, activeDepth, ref selectedStateId);
            }
        }

        private static bool IsActive(int stateId, int[] activeIds, int activeDepth)
        {
            for (int i = 0; i < activeDepth; i++)
            {
                if (activeIds[i] == stateId) return true;
            }
            return false;
        }
    }
}
