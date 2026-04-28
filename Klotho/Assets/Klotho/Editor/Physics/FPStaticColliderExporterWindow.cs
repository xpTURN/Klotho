using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;

namespace xpTURN.Klotho.Editor
{
    /// <summary>
    /// Editor window that converts and exports the scene's Unity Colliders into FPStaticCollider.
    /// </summary>
    internal class FPStaticColliderExporterWindow : EditorWindow
    {
        const string OutputDir = "Assets";

        [MenuItem("Tools/Klotho/Export Static Colliders")]
        public static void ShowWindow()
        {
            GetWindow<FPStaticColliderExporterWindow>("Static Collider Exporter");
        }

        private List<FPStaticCollider> _preview;
        private List<string> _previewTags;
        private Vector2 _scrollPos;
        private string _lastError;

        private void OnGUI()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            int staticCount = CountTag("FPStatic");
            int triggerCount = CountTag("FPTrigger");

            EditorGUILayout.LabelField("Scene", sceneName);
            EditorGUILayout.LabelField("FPStatic", $"{staticCount}  /  FPTrigger: {triggerCount}");
            EditorGUILayout.LabelField("Total", $"{staticCount + triggerCount}");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output Path", OutputDir);
            EditorGUILayout.LabelField("File Name", $"{sceneName}.StaticColliders");

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Preview")) BuildPreview();
            if (GUILayout.Button("Export")) Export(sceneName);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_lastError))
                EditorGUILayout.HelpBox(_lastError, MessageType.Error);

            DrawPreviewPanel();
        }

        void DrawPreviewPanel()
        {
            if (_preview == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.Height(300));

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("ID", GUILayout.Width(40));
            EditorGUILayout.LabelField("Tag", GUILayout.Width(70));
            EditorGUILayout.LabelField("Shape", GUILayout.Width(130));
            EditorGUILayout.LabelField("Position", GUILayout.Width(160));
            EditorGUILayout.LabelField("Trigger", GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < _preview.Count; i++)
            {
                var sc = _preview[i];
                var tag = _previewTags[i];
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(sc.id.ToString(), GUILayout.Width(40));
                EditorGUILayout.LabelField(tag, GUILayout.Width(70));
                EditorGUILayout.LabelField(ShapeLabel(sc), GUILayout.Width(130));
                EditorGUILayout.LabelField(PosLabel(sc.collider), GUILayout.Width(160));
                EditorGUILayout.LabelField(sc.isTrigger ? "true" : "false", GUILayout.Width(50));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        void BuildPreview()
        {
            _lastError = null;
            try
            {
                var list = Collect(out var tags);
                AssignIds(list);
                _preview = list;
                _previewTags = tags;
            }
            catch (Exception e)
            {
                _lastError = e.Message;
                _preview = null;
            }
        }

        void Export(string sceneName)
        {
            _lastError = null;

            string defaultDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", OutputDir));
            string defaultName = $"{sceneName}.StaticColliders";
            string fullPath = EditorUtility.SaveFilePanel(
                "Export Static Colliders", defaultDir, defaultName, "bytes");

            if (string.IsNullOrEmpty(fullPath)) return;

            try
            {
                var list = Collect(out _);
                AssignIds(list);

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                FPStaticColliderSerializer.Save(list.ToArray(), fullPath);

                string jsonPath = Path.ChangeExtension(fullPath, ".json");
                File.WriteAllText(jsonPath, ToJson(list), Encoding.UTF8);

                AssetDatabase.Refresh();
                Debug.Log($"[FPStaticColliderExporter] Saved: {fullPath}  ({list.Count})");
                Debug.Log($"[FPStaticColliderExporter] Saved JSON: {jsonPath}");
            }
            catch (Exception e)
            {
                _lastError = e.Message;
                Debug.LogError($"[FPStaticColliderExporter] Export failed: {e.Message}");
            }
        }

        static string ToJson(List<FPStaticCollider> list)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[");
            for (int i = 0; i < list.Count; i++)
            {
                var sc = list[i];
                var c = sc.collider;
                sb.Append("  {");
                sb.Append($"\"id\":{sc.id}");
                sb.Append($",\"isTrigger\":{(sc.isTrigger ? "true" : "false")}");
                sb.Append($",\"restitution\":{sc.restitution.ToFloat()}");
                sb.Append($",\"friction\":{sc.friction.ToFloat()}");
                sb.Append($",\"shape\":\"{c.type}\"");

                switch (c.type)
                {
                    case ShapeType.Sphere:
                        sb.Append($",\"position\":{Vec3(c.sphere.position)}");
                        sb.Append($",\"radius\":{c.sphere.radius.ToFloat()}");
                        break;
                    case ShapeType.Box:
                        sb.Append($",\"position\":{Vec3(c.box.position)}");
                        sb.Append($",\"rotation\":{Quat(c.box.rotation)}");
                        sb.Append($",\"halfExtents\":{Vec3(c.box.halfExtents)}");
                        break;
                    case ShapeType.Capsule:
                        sb.Append($",\"position\":{Vec3(c.capsule.position)}");
                        sb.Append($",\"rotation\":{Quat(c.capsule.rotation)}");
                        sb.Append($",\"halfHeight\":{c.capsule.halfHeight.ToFloat()}");
                        sb.Append($",\"radius\":{c.capsule.radius.ToFloat()}");
                        break;
                    case ShapeType.Mesh:
                        sb.Append($",\"position\":{Vec3(c.mesh.position)}");
                        sb.Append($",\"rotation\":{Quat(c.mesh.rotation)}");
                        if (sc.meshData != null)
                        {
                            sb.Append(",\"vertices\":[");
                            for (int v = 0; v < sc.meshData.vertices.Length; v++)
                            {
                                if (v > 0) sb.Append(',');
                                sb.Append(Vec3(sc.meshData.vertices[v]));
                            }
                            sb.Append(']');
                            sb.Append(",\"indices\":[");
                            for (int idx = 0; idx < sc.meshData.indices.Length; idx++)
                            {
                                if (idx > 0) sb.Append(',');
                                sb.Append(sc.meshData.indices[idx]);
                            }
                            sb.Append(']');
                        }
                        break;
                }

                sb.Append('}');
                if (i < list.Count - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.Append(']');
            return sb.ToString();
        }

        static string Vec3(Deterministic.Math.FPVector3 v)
            => $"[{v.x.ToFloat()},{v.y.ToFloat()},{v.z.ToFloat()}]";

        static string Quat(Deterministic.Math.FPQuaternion q)
            => $"[{q.x.ToFloat()},{q.y.ToFloat()},{q.z.ToFloat()},{q.w.ToFloat()}]";

        List<FPStaticCollider> Collect(out List<string> tags)
        {
            var list = new List<FPStaticCollider>();
            tags = new List<string>();
            CollectTag("FPStatic", false, list, tags);
            CollectTag("FPTrigger", true, list, tags);
            return list;
        }

        static void CollectTag(string tag, bool isTrigger, List<FPStaticCollider> list, List<string> tags)
        {
            foreach (var go in GameObject.FindGameObjectsWithTag(tag))
            {
                var col = go.GetComponent<Collider>();
                if (col == null)
                {
                    Debug.LogWarning($"[FPStaticColliderExporter] '{go.name}': no Collider — skipped");
                    continue;
                }
                list.Add(FPStaticColliderConverter.Convert(col, isTrigger));
                tags.Add(tag);
            }
        }

        static void AssignIds(List<FPStaticCollider> list)
        {
            int next = 1;
            foreach (var sc in list)
                if (sc.id > 0 && sc.id >= next) next = sc.id + 1;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].id == -1)
                {
                    var sc = list[i];
                    sc.id = next++;
                    list[i] = sc;
                }
            }
        }

        static int CountTag(string tag)
        {
            try { return GameObject.FindGameObjectsWithTag(tag).Length; }
            catch { return 0; }
        }

        static string ShapeLabel(FPStaticCollider sc)
        {
            var c = sc.collider;
            return c.type switch
            {
                ShapeType.Sphere => $"Sphere r={c.sphere.radius.ToFloat():F2}",
                ShapeType.Box => $"Box {c.box.halfExtents.x.ToFloat():F1},{c.box.halfExtents.y.ToFloat():F1},{c.box.halfExtents.z.ToFloat():F1}",
                ShapeType.Capsule => $"Capsule r={c.capsule.radius.ToFloat():F2}",
                ShapeType.Mesh => $"Mesh ({sc.meshData?.TriangleCount ?? 0}tri)",
                _ => c.type.ToString()
            };
        }

        static string PosLabel(FPCollider c)
        {
            var p = c.type switch
            {
                ShapeType.Sphere => c.sphere.position,
                ShapeType.Box => c.box.position,
                ShapeType.Capsule => c.capsule.position,
                ShapeType.Mesh => c.mesh.position,
                _ => default
            };
            return $"({p.x.ToFloat():F2}, {p.y.ToFloat():F2}, {p.z.ToFloat():F2})";
        }
    }
}
