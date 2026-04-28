using System;
using UnityEngine;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;
using System.Collections.Generic;


#if UNITY_EDITOR
using UnityEditor;
#endif

namespace xpTURN.Klotho.Unity.Physics
{
    public class FPStaticColliderVisualizer : MonoBehaviour
    {
        public TextAsset staticCollidersAsset;
        public bool showFace   = false;
        public bool showAABB    = true;
        public bool showShape   = true;
        public bool showIds     = false;

        public Color aabbColor     = new Color(0f, 1f, 0f, 0.3f);
        public Color shapeColor    = new Color(0f, 0.8f, 1f, 0.8f);
        public Color faceColor     = new Color(0f, 0.8f, 1f, 0.15f);
        public Color selectedColor = new Color(1f, 0.9f, 0f, 1f);
        public Color selectedFaceColor = new Color(1f, 0.9f, 0f, 0.3f);
        public int   selectedIndex = -1;

#if UNITY_EDITOR
        List<FPStaticCollider> _colliders;
        Mesh[]             _meshCache;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        void OnValidate() => Reload();

        void Reload()
        {
            if (staticCollidersAsset != null)
            {
                _colliders = FPStaticColliderSerializer.Load(staticCollidersAsset.bytes);
                BuildMeshCache();
            }
            else
            {
                _colliders = null;
                _meshCache = null;
            }
        }

        void BuildMeshCache()
        {
            _meshCache = new Mesh[_colliders.Count];
            for (int i = 0; i < _colliders.Count; i++)
            {
                if (_colliders[i].collider.type == ShapeType.Mesh && _colliders[i].meshData != null)
                    _meshCache[i] = ToUnityMesh(_colliders[i].meshData);
            }
        }

        void OnDrawGizmos()
        {
            if (_colliders == null && staticCollidersAsset != null) Reload();
            if (_colliders == null) return;
            EnsureLabelStyles();
            for (int i = 0; i < _colliders.Count; i++)
                DrawCollider(_colliders[i], i);
        }

        void DrawCollider(FPStaticCollider sc, int index)
        {
            bool isSelected = index == selectedIndex;

            if (showAABB)
            {
                Gizmos.color = isSelected ? selectedColor : aabbColor;
                var b = sc.collider.GetWorldBounds(sc.meshData);
                Gizmos.DrawWireCube(ToUnity(b.center), ToUnity(b.size));
            }

            if (showShape || isSelected)
            {
                Gizmos.color = isSelected ? selectedColor : shapeColor;
                switch (sc.collider.type)
                {
                    case ShapeType.Sphere:  DrawSphereWire(sc.collider.sphere);             break;
                    case ShapeType.Box:     DrawBoxWire(sc.collider.box);                   break;
                    case ShapeType.Capsule: DrawCapsuleWire(sc.collider.capsule);           break;
                    case ShapeType.Mesh:    DrawMeshWire(sc.collider.mesh, sc.meshData);    break;
                }
            }

            if (showFace || isSelected)
            {
                Gizmos.color = isSelected ? selectedFaceColor : faceColor;
                Mesh cachedMesh = (_meshCache != null && index < _meshCache.Length) ? _meshCache[index] : null;
                switch (sc.collider.type)
                {
                    case ShapeType.Sphere:  DrawSphereFace(sc.collider.sphere);                          break;
                    case ShapeType.Box:     DrawBoxFace(sc.collider.box);                                break;
                    case ShapeType.Capsule: DrawCapsuleFace(sc.collider.capsule);                        break;
                    case ShapeType.Mesh:    if (cachedMesh != null) DrawMeshFace(sc.collider.mesh, cachedMesh); break;
                }
            }

            if (showIds || isSelected)
            {
                var b = sc.collider.GetWorldBounds(sc.meshData);
                var style = isSelected ? _selectedLabelStyle : _normalLabelStyle;
                Handles.Label(ToUnity(b.center), $"id={sc.id}", style);
            }
        }

        static GUIStyle _selectedLabelStyle;
        static GUIStyle _normalLabelStyle;

        static void EnsureLabelStyles()
        {
            if (_normalLabelStyle == null)
            {
                _normalLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
                _normalLabelStyle.normal.textColor = Color.white;
            }
            if (_selectedLabelStyle == null)
            {
                _selectedLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
                _selectedLabelStyle.normal.textColor = new Color(1f, 0.9f, 0f);
            }
        }

        // --- Wire ---

        static void DrawSphereWire(FPSphereShape s)
        {
            Gizmos.DrawWireSphere(ToUnity(s.position), s.radius.ToFloat());
        }

        static void DrawBoxWire(FPBoxShape b)
        {
            Matrix4x4 prev = Gizmos.matrix;
            Gizmos.matrix  = Matrix4x4.TRS(ToUnity(b.position), ToUnity(b.rotation), Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, ToUnity(b.halfExtents) * 2f);
            Gizmos.matrix  = prev;
        }

        static void DrawCapsuleWire(FPCapsuleShape c)
        {
            Vector3    center     = ToUnity(c.position);
            float      radius     = c.radius.ToFloat();
            float      halfHeight = c.halfHeight.ToFloat();
            Quaternion rot        = ToUnity(c.rotation);
            Vector3    axis       = rot * Vector3.up;

            Vector3 top    = center + axis * halfHeight;
            Vector3 bottom = center - axis * halfHeight;

            DrawWireCircle(top,    axis, radius);
            DrawWireCircle(bottom, axis, radius);

            Vector3 perp = Vector3.Cross(axis, Vector3.up).normalized;
            if (perp.sqrMagnitude < 0.001f) perp = Vector3.right;
            Vector3 perp2 = Vector3.Cross(axis, perp);
            Gizmos.DrawLine(top + perp  * radius, bottom + perp  * radius);
            Gizmos.DrawLine(top - perp  * radius, bottom - perp  * radius);
            Gizmos.DrawLine(top + perp2 * radius, bottom + perp2 * radius);
            Gizmos.DrawLine(top - perp2 * radius, bottom - perp2 * radius);
        }

        static void DrawMeshWire(FPMeshShape m, FPMeshData data)
        {
            if (data == null) return;

            Matrix4x4 trs = Matrix4x4.TRS(ToUnity(m.position), ToUnity(m.rotation), Vector3.one);
            var verts = data.vertices;
            var idx   = data.indices;
            for (int i = 0; i < idx.Length; i += 3)
            {
                Vector3 v0 = trs.MultiplyPoint3x4(ToUnity(verts[idx[i]]));
                Vector3 v1 = trs.MultiplyPoint3x4(ToUnity(verts[idx[i + 1]]));
                Vector3 v2 = trs.MultiplyPoint3x4(ToUnity(verts[idx[i + 2]]));
                Gizmos.DrawLine(v0, v1);
                Gizmos.DrawLine(v1, v2);
                Gizmos.DrawLine(v2, v0);
            }
        }

        // --- Face ---

        static void DrawSphereFace(FPSphereShape s)
        {
            Gizmos.DrawSphere(ToUnity(s.position), s.radius.ToFloat());
        }

        static void DrawBoxFace(FPBoxShape b)
        {
            Matrix4x4 prev = Gizmos.matrix;
            Gizmos.matrix  = Matrix4x4.TRS(ToUnity(b.position), ToUnity(b.rotation), Vector3.one);
            Gizmos.DrawCube(Vector3.zero, ToUnity(b.halfExtents) * 2f);
            Gizmos.matrix  = prev;
        }

        static void DrawCapsuleFace(FPCapsuleShape c)
        {
            float      radius = c.radius.ToFloat();
            float      halfH  = c.halfHeight.ToFloat();
            Quaternion rot    = ToUnity(c.rotation);
            Vector3    axis   = rot * Vector3.up;
            Vector3    center = ToUnity(c.position);

            Gizmos.DrawSphere(center + axis * halfH, radius);
            Gizmos.DrawSphere(center - axis * halfH, radius);
        }

        static void DrawMeshFace(FPMeshShape m, Mesh mesh)
        {
            Gizmos.DrawMesh(mesh, ToUnity(m.position), ToUnity(m.rotation));
        }

        // --- Unity Mesh cache build ---

        static Mesh ToUnityMesh(FPMeshData data)
        {
            var verts = new Vector3[data.vertices.Length];
            for (int i = 0; i < verts.Length; i++)
                verts[i] = ToUnity(data.vertices[i]);
            var mesh = new Mesh { vertices = verts, triangles = data.indices };
            mesh.RecalculateNormals();
            return mesh;
        }

        static void DrawWireCircle(Vector3 center, Vector3 normal, float radius, int segments = 16)
        {
            Vector3 tangent = Vector3.Cross(normal, Vector3.up).normalized;
            if (tangent.sqrMagnitude < 0.001f) tangent = Vector3.right;
            Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;

            Vector3 prev = center + tangent * radius;
            for (int i = 1; i <= segments; i++)
            {
                float   angle = i * 2f * Mathf.PI / segments;
                Vector3 curr  = center + (Mathf.Cos(angle) * tangent + Mathf.Sin(angle) * bitangent) * radius;
                Gizmos.DrawLine(prev, curr);
                prev = curr;
            }
        }

        static Vector3    ToUnity(FPVector3    v) => new Vector3(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat());
        static Quaternion ToUnity(FPQuaternion q) => new Quaternion(q.x.ToFloat(), q.y.ToFloat(), q.z.ToFloat(), q.w.ToFloat());
#endif
    }
}
