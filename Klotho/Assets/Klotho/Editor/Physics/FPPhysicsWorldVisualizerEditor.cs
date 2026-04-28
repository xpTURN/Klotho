#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Unity.Physics;

namespace xpTURN.Klotho.Editor.Physics
{
    /// <summary>
    /// Custom inspector for FPPhysicsWorldVisualizer.
    /// </summary>
    [CustomEditor(typeof(FPPhysicsWorldVisualizer))]
    public class FPPhysicsWorldVisualizerEditor : UnityEditor.Editor
    {
        Vector2 _contactScrollPos;

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var vis = (FPPhysicsWorldVisualizer)target;
            if (!Application.isPlaying) return;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Runtime Info", EditorStyles.boldLabel);
            if (vis.currentBodies == null && vis.Provider != null)
            {
                vis.Provider.GetBodies(out vis.currentBodies, out vis.bodyCount);
                vis.Provider.GetStaticColliders(out vis.currentStatics, out vis.staticCount);
            }

            EditorGUILayout.LabelField($"Bodies: {vis.bodyCount}   StaticColliders: {vis.staticCount}");

            if (vis.currentBodies == null) return;

            // Tab
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(vis.viewingBodies, "Bodies", EditorStyles.miniButtonLeft) != vis.viewingBodies)
            {
                vis.viewingBodies = true;
                vis.selectedIndex = 0;
            }
            if (GUILayout.Toggle(!vis.viewingBodies, "StaticColliders", EditorStyles.miniButtonRight) == vis.viewingBodies)
            {
                vis.viewingBodies = false;
                vis.selectedIndex = 0;
            }
            EditorGUILayout.EndHorizontal();

            // Navigation
            int total = vis.viewingBodies ? vis.bodyCount : vis.staticCount;
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = vis.selectedIndex > 0;
            if (GUILayout.Button("<", GUILayout.Width(30)))
                vis.selectedIndex--;
            GUI.enabled = true;
            EditorGUILayout.LabelField($"{vis.selectedIndex + 1} / {total}", EditorStyles.centeredGreyMiniLabel);
            GUI.enabled = vis.selectedIndex < total - 1;
            if (GUILayout.Button(">", GUILayout.Width(30)))
                vis.selectedIndex++;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (total == 0) return;
            vis.selectedIndex = Mathf.Clamp(vis.selectedIndex, 0, Mathf.Max(0, total - 1));

            // Detail
            EditorGUILayout.Space(4);
            if (vis.viewingBodies)
                DrawBodyInfo(vis);
            else
                DrawStaticInfo(vis);

            // Copy
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Copy to Clipboard"))
            {
                string text = vis.viewingBodies
                    ? BuildBodyText(vis)
                    : BuildStaticText(vis);
                EditorGUIUtility.systemCopyBuffer = text;
            }
        }

        void DrawBodyInfo(FPPhysicsWorldVisualizer vis)
        {
            if (vis.selectedIndex >= vis.bodyCount) return;
            ref FPPhysicsBody b = ref vis.currentBodies[vis.selectedIndex];
            var rb = b.rigidBody;

            EditorGUILayout.IntField("EntityIndex", b.id);
            EditorGUILayout.LabelField("Type", FPPhysicsWorldVisualizer.BodyTypeStr(ref b));
            EditorGUILayout.LabelField("Position", FPPhysicsWorldVisualizer.FmtV3(b.position));
            EditorGUILayout.LabelField("Rotation", FPPhysicsWorldVisualizer.FmtEuler(b.rotation));
            EditorGUILayout.LabelField("Shape", b.collider.type.ToString());
            EditorGUILayout.LabelField("Mass", $"{rb.mass.ToFloat():F2}  invM: {rb.inverseMass.ToFloat():F2}");
            EditorGUILayout.LabelField("Velocity", FPPhysicsWorldVisualizer.FmtV3(rb.velocity));
            EditorGUILayout.LabelField("AngVelocity", FPPhysicsWorldVisualizer.FmtV3(rb.angularVelocity));
            EditorGUILayout.LabelField("Damping", $"lin={rb.linearDamping.ToFloat():F2}  ang={rb.angularDamping.ToFloat():F2}");
            EditorGUILayout.LabelField("Material", $"rest={rb.restitution.ToFloat():F2}  fric={rb.friction.ToFloat():F2}");
            EditorGUILayout.LabelField("Flags", $"Static={rb.isStatic}  Kin={rb.isKinematic}  Trigger={b.isTrigger}");

            DrawContactList(vis);
        }

        void DrawStaticInfo(FPPhysicsWorldVisualizer vis)
        {
            if (vis.selectedIndex >= vis.staticCount) return;
            ref FPStaticCollider sc = ref vis.currentStatics[vis.selectedIndex];
            var bounds = sc.collider.GetWorldBounds(sc.meshData);

            EditorGUILayout.LabelField("Type", $"id={sc.id}  Shape: {sc.collider.type}");
            EditorGUILayout.LabelField("Material", $"rest={sc.restitution.ToFloat():F2}  fric={sc.friction.ToFloat():F2}");
            EditorGUILayout.LabelField("Trigger", sc.isTrigger.ToString());
            EditorGUILayout.LabelField("AABB center", FPPhysicsWorldVisualizer.FmtV3(bounds.center));
            EditorGUILayout.LabelField("AABB size", FPPhysicsWorldVisualizer.FmtV3(bounds.size));
        }

        string BuildBodyText(FPPhysicsWorldVisualizer vis)
        {
            if (vis.selectedIndex >= vis.bodyCount) return string.Empty;
            ref FPPhysicsBody b = ref vis.currentBodies[vis.selectedIndex];
            var rb = b.rigidBody;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"EntityIndex: {b.id}");
            sb.AppendLine($"Type: {FPPhysicsWorldVisualizer.BodyTypeStr(ref b)}");
            sb.AppendLine($"Position: {FPPhysicsWorldVisualizer.FmtV3(b.position)}");
            sb.AppendLine($"Rotation: {FPPhysicsWorldVisualizer.FmtEuler(b.rotation)}");
            sb.AppendLine($"Shape: {b.collider.type}");
            sb.AppendLine($"Mass: {rb.mass.ToFloat():F2}  invM: {rb.inverseMass.ToFloat():F2}");
            sb.AppendLine($"Velocity: {FPPhysicsWorldVisualizer.FmtV3(rb.velocity)}");
            sb.AppendLine($"AngVelocity: {FPPhysicsWorldVisualizer.FmtV3(rb.angularVelocity)}");
            sb.AppendLine($"Damping: lin={rb.linearDamping.ToFloat():F2}  ang={rb.angularDamping.ToFloat():F2}");
            sb.AppendLine($"Material: rest={rb.restitution.ToFloat():F2}  fric={rb.friction.ToFloat():F2}");
            sb.AppendLine($"Flags: Static={rb.isStatic}  Kin={rb.isKinematic}  Trigger={b.isTrigger}");
            return sb.ToString();
        }

        string BuildStaticText(FPPhysicsWorldVisualizer vis)
        {
            if (vis.selectedIndex >= vis.staticCount) return string.Empty;
            ref FPStaticCollider sc = ref vis.currentStatics[vis.selectedIndex];
            var bounds = sc.collider.GetWorldBounds(sc.meshData);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Type: id={sc.id}  Shape: {sc.collider.type}");
            sb.AppendLine($"Material: rest={sc.restitution.ToFloat():F2}  fric={sc.friction.ToFloat():F2}");
            sb.AppendLine($"Trigger: {sc.isTrigger}");
            sb.AppendLine($"AABB center: {FPPhysicsWorldVisualizer.FmtV3(bounds.center)}");
            sb.AppendLine($"AABB size: {FPPhysicsWorldVisualizer.FmtV3(bounds.size)}");
            return sb.ToString();
        }

        void DrawContactList(FPPhysicsWorldVisualizer vis)
        {
            int bodyIdx = vis.selectedIndex;
            int dynCount = 0, staCount = 0;

            if (vis.currentContacts != null)
                for (int i = 0; i < vis.currentContactCount; i++)
                    if (vis.currentContacts[i].entityA == bodyIdx || vis.currentContacts[i].entityB == bodyIdx)
                        dynCount++;
            if (vis.currentSContacts != null)
                for (int i = 0; i < vis.currentSContactCount; i++)
                    if (vis.currentSContacts[i].entityA == bodyIdx || vis.currentSContacts[i].entityB == bodyIdx)
                        staCount++;

            if (dynCount + staCount == 0) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Contacts  Dyn:{dynCount}  Static:{staCount}", EditorStyles.boldLabel);
            _contactScrollPos = EditorGUILayout.BeginScrollView(_contactScrollPos, GUILayout.Height(80));

            if (vis.currentContacts != null)
            {
                for (int i = 0; i < vis.currentContactCount; i++)
                {
                    ref FPContact c = ref vis.currentContacts[i];
                    if (c.entityA != bodyIdx && c.entityB != bodyIdx) continue;
                    int other = c.entityA == bodyIdx ? c.entityB : c.entityA;
                    string tag  = c.isSpeculative ? " [CCD]" : "";
                    string peer = (other >= 0 && other < vis.bodyCount) ? $"entity={vis.currentBodies[other].id}" : "?";
                    EditorGUILayout.LabelField($"{peer}  d={Mathf.Abs(c.depth.ToFloat()):F3}  n={FPPhysicsWorldVisualizer.FmtV3(c.normal)}{tag}");
                }
            }

            if (vis.currentSContacts != null)
            {
                for (int i = 0; i < vis.currentSContactCount; i++)
                {
                    ref FPContact c = ref vis.currentSContacts[i];
                    if (c.entityA != bodyIdx && c.entityB != bodyIdx) continue;
                    string tag  = c.isSpeculative ? " [CCD]" : "";
                    string peer = c.entityB < 0
                        ? $"static[{~c.entityB}]"
                        : (c.entityB >= 0 && c.entityB < vis.bodyCount ? $"entity={vis.currentBodies[c.entityB].id}" : "?");
                    EditorGUILayout.LabelField($"{peer}  d={Mathf.Abs(c.depth.ToFloat()):F3}  n={FPPhysicsWorldVisualizer.FmtV3(c.normal)}{tag}");
                }
            }

            EditorGUILayout.EndScrollView();
        }
    }
}

#endif
