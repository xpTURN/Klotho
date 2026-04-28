using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Unity;

namespace xpTURN.Klotho.Editor.ECS
{
    /// <summary>
    /// Editor window that shows entity component field values in real time.
    /// </summary>
    public class EntityComponentVisualizerWindow : EditorWindow
    {
        [MenuItem("Tools/Klotho/Visualizer/Entity Component")]
        static void Open() => GetWindow<EntityComponentVisualizerWindow>("Entity Component Visualizer");

        private int _targetEntityId;
        private Vector2 _scrollPos;
        private readonly Dictionary<Type, bool> _foldouts = new();

        private Type[] _registeredTypes;

        void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += OnEditorUpdate;
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
                _registeredTypes = null;
        }

        private void RefreshRegisteredTypesIfNeeded()
        {
            int current = ComponentStorageRegistry.RegisteredTypes.Count;
            if (_registeredTypes == null || _registeredTypes.Length != current)
                _registeredTypes = ComponentStorageRegistry.RegisteredTypes.ToArray();
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

            RefreshRegisteredTypesIfNeeded();

            DrawToolbar();
            EditorGUILayout.Space(4);

            var frame = bridge.Simulation.Frame;

            if (!bridge.AllEntityIndexMap.TryGetValue(_targetEntityId, out var entity))
            {
                EditorGUILayout.HelpBox($"No entity found for EntityIndex {_targetEntityId}.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField($"EntityRef: {entity}  Tick: {frame.Tick}", EditorStyles.miniLabel);
            EditorGUILayout.Space(4);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            foreach (Type type in _registeredTypes)
            {
                if (!frame.TryGetReflectableStorage(type, out var storage)) continue;
                if (!storage.Has(entity.Index)) continue;

                object boxed = storage.GetBoxed(entity.Index);

                if (!_foldouts.TryGetValue(type, out bool expanded))
                    expanded = true;
                _foldouts[type] = EditorGUILayout.Foldout(expanded, type.Name, true, EditorStyles.foldoutHeader);
                if (!_foldouts[type]) continue;

                DrawComponentFields(type, boxed);
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Entity Index", GUILayout.Width(80));
            _targetEntityId = EditorGUILayout.IntField(_targetEntityId, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawComponentFields(Type type, object boxed)
        {
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 240f;
            EditorGUI.indentLevel++;
            DrawFields(type, boxed, prefix: null);
            EditorGUI.indentLevel--;
            EditorGUIUtility.labelWidth = prevLabelWidth;
        }

        private void DrawFields(Type type, object boxed, string prefix)
        {
            foreach (var field in ComponentReflectionCache.GetFields(type))
            {
                if (field.IsStatic) continue;

                string label = prefix != null ? $"{prefix}.{field.Name}" : field.Name;

                // fixed array — when a Reader is registered
                if (ComponentStorageRegistry.TryGetFixedArrayReader(type, field.Name, out var reader))
                {
                    var arr = reader(boxed);
                    for (int i = 0; i < arr.Length; i++)
                        EditorGUILayout.LabelField($"{label}[{i}]", arr[i].ToString());
                    continue;
                }

                // fixed array — Reader not registered
                if (ComponentReflectionCache.IsFixedField(type, field.Name))
                {
                    EditorGUILayout.LabelField(label, "(fixed, no reader registered)");
                    continue;
                }

                object val = field.GetValue(boxed);

                // Recursive expansion — struct with public instance fields
                // Types marked with [Primitive] (FP64, FPVector*, EntityRef, etc.) are shown on one line via ToString()
                if (val != null
                    && field.FieldType.IsValueType
                    && !field.FieldType.IsPrimitive
                    && !field.FieldType.IsEnum
                    && !ComponentReflectionCache.IsPrimitive(field.FieldType)
                    && ComponentReflectionCache.HasPublicInstanceFields(field.FieldType))
                {
                    EditorGUI.indentLevel++;
                    DrawFields(field.FieldType, val, label);
                    EditorGUI.indentLevel--;
                    continue;
                }

                EditorGUILayout.LabelField(label, val?.ToString() ?? "null");
            }
        }
    }
}
