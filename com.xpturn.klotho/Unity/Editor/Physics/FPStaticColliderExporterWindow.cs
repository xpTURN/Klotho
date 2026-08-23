using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
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

        // Audit of what the export would MISS. Kept entirely out of Collect/AssignIds: that path decides
        // ids from enumeration order, so touching it would change the exported bytes (and with them the
        // static fingerprint peers compare). Cached because a full Collider sweep must not run per repaint.
        //
        // NAMES, not GameObjects: holding references across a scene change left the window drawing
        // destroyed objects (MissingReferenceException from `.name` inside OnGUI). The audit only ever
        // renders text, so the names are all it needs and nothing here can dangle.
        private List<string> _untagged;        // has a Collider, tagged neither FPStatic nor FPTrigger
        private List<string> _taggedInactive;  // tagged, but inactive — FindGameObjectsWithTag skips it

        private void OnEnable()
        {
            // Opening or creating a scene replaces the objects the counts were taken from — drop the
            // cache so the next repaint re-audits instead of showing the previous scene's numbers.
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.newSceneCreated += OnNewSceneCreated;
        }

        private void OnDisable()
        {
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.newSceneCreated -= OnNewSceneCreated;
        }

        private void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode mode) => InvalidateAudit();
        private void OnSceneClosed(UnityEngine.SceneManagement.Scene scene) => InvalidateAudit();
        private void OnNewSceneCreated(UnityEngine.SceneManagement.Scene scene, NewSceneSetup setup, NewSceneMode mode) => InvalidateAudit();

        private void InvalidateAudit()
        {
            _untagged = null;
            _taggedInactive = null;
            _preview = null;      // preview rows hold converted copies, but their ids belong to the old scene
            _previewTags = null;
            Repaint();
        }

        private void OnFocus() => Audit();

        private void OnGUI()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            int staticCount = CountTag("FPStatic");
            int triggerCount = CountTag("FPTrigger");
            if (_untagged == null || _taggedInactive == null) Audit();

            EditorGUILayout.LabelField("Scene", sceneName);
            EditorGUILayout.LabelField("FPStatic", $"{staticCount}  /  FPTrigger: {triggerCount}");
            EditorGUILayout.LabelField("Total", $"{staticCount + triggerCount}");
            EditorGUILayout.LabelField("Excluded", $"untagged: {_untagged.Count}  /  tagged-but-inactive: {_taggedInactive.Count}");

            DrawExclusionWarnings();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output Path", OutputDir);
            EditorGUILayout.LabelField("File Name", $"{sceneName}.StaticColliders");

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Rescan")) Audit();
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
            Audit();
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
                Audit();
                LogExclusions();
                var list = Collect(out _);
                AssignIds(list);

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                FPStaticColliderSerializer.Save(list.ToArray(), fullPath);

                string jsonPath = Path.ChangeExtension(fullPath, ".json");
                File.WriteAllText(jsonPath, FPStaticColliderSerializer.ToJson(list), Encoding.UTF8);

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

        // Enumerates every Collider in the open scenes — inactive ones included — and splits out the two
        // ways a Collider silently misses the export:
        //   1) it carries neither tag, so CollectTag never sees it;
        //   2) it carries a tag but its GameObject is inactive, and FindGameObjectsWithTag returns only
        //      active objects, so CollectTag never sees it either.
        // Read-only: nothing here feeds Collect/AssignIds.
        void Audit()
        {
            _untagged = new List<string>();
            _taggedInactive = new List<string>();
            var seen = new HashSet<int>();   // one entry per GameObject, not per Collider

            foreach (var col in AllColliders())
            {
                if (col == null) continue;
                var go = col.gameObject;
                if (go == null || !seen.Add(go.GetInstanceID())) continue;

                // go.tag is a plain string read — unlike CompareTag/FindGameObjectsWithTag it cannot throw
                // when a tag is missing from the project's tag list.
                bool tagged = go.tag == "FPStatic" || go.tag == "FPTrigger";
                if (!tagged)
                    _untagged.Add(go.name);
                else if (!go.activeInHierarchy)
                    _taggedInactive.Add(go.name);
            }
        }

        static Collider[] AllColliders()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            return UnityEngine.Object.FindObjectsOfType<Collider>(true);
#endif
        }

        const int MaxListed = 20;

        void DrawExclusionWarnings()
        {
            if (_untagged.Count > 0)
                EditorGUILayout.HelpBox(
                    $"{_untagged.Count} Collider(s) carry neither FPStatic nor FPTrigger and will NOT be exported:\n{NameList(_untagged)}",
                    MessageType.Warning);

            if (_taggedInactive.Count > 0)
                EditorGUILayout.HelpBox(
                    $"{_taggedInactive.Count} tagged Collider(s) are inactive and will NOT be exported:\n{NameList(_taggedInactive)}",
                    MessageType.Warning);
        }

        void LogExclusions()
        {
            if (_untagged.Count > 0)
                Debug.LogWarning($"[FPStaticColliderExporter] untagged Colliders excluded ({_untagged.Count}): {NameList(_untagged)}");
            if (_taggedInactive.Count > 0)
                Debug.LogWarning($"[FPStaticColliderExporter] tagged-but-inactive Colliders excluded ({_taggedInactive.Count}): {NameList(_taggedInactive)}");
        }

        static string NameList(List<string> names)
        {
            var sb = new StringBuilder();
            int shown = Math.Min(names.Count, MaxListed);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(names[i]);
            }
            if (names.Count > shown) sb.Append($", … (+{names.Count - shown})");
            return sb.ToString();
        }

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
