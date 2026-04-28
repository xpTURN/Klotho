using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Rendering;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;

namespace xpTURN.Klotho.Unity.Physics
{
    public class FPPhysicsWorldVisualizer : MonoBehaviour
    {
        // ---- Inspector ----

        [Header("PhysicsBody Display")]
        public bool  showBodies          = true;
        public bool  showBodyShape       = true;
        public bool  showBodyAABB        = false;
        public bool  showBodyVelocity    = true;
        public float velocityArrowScale  = 0.2f;

        [Header("StaticCollider Display")]
        public bool showStaticColliders  = true;
        public bool showStaticShape      = true;
        public bool showStaticAABB       = false;

        [Header("Collision Visualization")]
        public bool  showContacts           = true;
        public bool  showContactNormals     = true;
        public bool  showCollisionHighlight = true;
        public float contactNormalScale     = 0.5f;
        public float contactPointRadius     = 0.05f;

        [Header("Colors — Body")]
        public Color dynamicShapeColor    = new Color(1f,   0.8f, 0f,   0.9f);
        public Color staticBodyShapeColor = new Color(0.6f, 0.6f, 0.6f, 0.9f);
        public Color kinematicShapeColor  = new Color(0f,   0.6f, 1f,   0.9f);
        public Color sceneStaticColor     = new Color(0f,   1f,   0.5f, 0.9f);
        public Color aabbColor            = new Color(1f,   1f,   1f,   0.25f);
        public Color velocityColor        = new Color(1f,   0.3f, 0.3f, 1f);

        [Header("Colors — Selection")]
        public Color selectedShapeColor = new Color(1f, 0.9f, 0f, 1f);
        public Color selectedAABBColor  = new Color(1f, 0.9f, 0f, 0.6f);

        [Header("Colors — Collision")]
        public Color contactPointColor       = new Color(1f, 0f,   0f,   1f);
        public Color contactNormalColor      = new Color(1f, 0.5f, 0f,   1f);
        public Color collisionHighlightColor = new Color(1f, 0f,   0f,   0.5f);
        public Color triggerHighlightColor   = new Color(1f, 0f,   1f,   0.5f);

        [Header("Rendering")]
        public Camera targetCamera = null;
        public bool   alwaysOnTop  = true;
        public bool   showInSceneView = true;
        public bool   showInGameView  = false;

        // ---- Internal state (accessed from the editor) ----

        public IFPPhysicsWorldProvider Provider { get; set; }

        [System.NonSerialized] public int  selectedIndex    = 0;
        [System.NonSerialized] public bool viewingBodies    = true;

        [System.NonSerialized] public int               bodyCount      = 0;
        [System.NonSerialized] public int               staticCount    = 0;
        [System.NonSerialized] public FPPhysicsBody[]   currentBodies  = null;
        [System.NonSerialized] public FPStaticCollider[] currentStatics = null;

        [System.NonSerialized] public FPContact[] currentContacts       = null;
        [System.NonSerialized] public int         currentContactCount   = 0;
        [System.NonSerialized] public FPContact[] currentSContacts      = null;
        [System.NonSerialized] public int         currentSContactCount  = 0;

        bool[] _collidingMark;
        bool[] _triggerMark;
        bool[] _triggerStaticMark;

        Dictionary<int, int> _idToIndex       = new Dictionary<int, int>();
        Dictionary<int, int> _staticIdToIndex = new Dictionary<int, int>();

#if UNITY_EDITOR || DEVELOPMENT_BUILD

        // ---- Lifecycle ----

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            if (targetCamera == null) targetCamera = Camera.main;
        }

        void OnEnable()
        {
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        }

        void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (!enabled) return;
            if (Provider == null) return;
            bool isScene  = showInSceneView && cam.cameraType == CameraType.SceneView;
            bool isGame   = showInGameView  && (cam == targetCamera || cam.cameraType == CameraType.Game);
            if (!isScene && !isGame) return;
            DrawAll(cam);
        }

        // ---- DrawAll ----

        void DrawAll(Camera cam)
        {
            Provider.GetBodies(out var bodies, out int bc);
            Provider.GetStaticColliders(out var statics, out int sc);
            Provider.GetContacts(out var contacts, out int cCount,
                                  out var sContacts, out int scCount);
            Provider.GetTriggerPairs(out var triggerPairs, out int tCount);

            // 0. Refresh cache
            bodyCount             = bc;
            staticCount           = sc;
            currentBodies         = bodies;
            currentStatics        = statics;
            currentContacts       = contacts;
            currentContactCount   = cCount;
            currentSContacts      = sContacts;
            currentSContactCount  = scCount;

            // 1. Rebuild id→index reverse mapping (every frame)
            RebuildIdMaps(bodies, bc, statics, sc);

            // 2. Collision/trigger marking
            MarkCollidingBodies(bodies, bc, contacts, cCount, sContacts, scCount);
            MarkTriggerBodies(bc, statics, sc, triggerPairs, tCount);

            // 3. GL pass
            FPPhysicsGLDrawer.BeginPass(cam, alwaysOnTop);

            // 4. StaticCollider
            if (showStaticColliders)
                for (int i = 0; i < sc; i++)
                    DrawStaticCollider(ref statics[i]);

            // 5. PhysicsBody
            if (showBodies)
                for (int i = 0; i < bc; i++)
                    DrawBody(ref bodies[i], i);

            // 6. Collision/trigger highlight overlay
            if (showCollisionHighlight
                && _collidingMark != null && _collidingMark.Length >= bc
                && _triggerMark   != null && _triggerMark.Length   >= bc)
            {
                for (int i = 0; i < bc; i++)
                {
                    if (_collidingMark[i])
                        FPPhysicsGLDrawer.DrawBodyShape(ref bodies[i], collisionHighlightColor);
                    if (_triggerMark[i])
                        FPPhysicsGLDrawer.DrawBodyShape(ref bodies[i], triggerHighlightColor);
                }
                if (_triggerStaticMark != null && _triggerStaticMark.Length >= sc)
                    for (int i = 0; i < sc; i++)
                        if (_triggerStaticMark[i])
                            FPPhysicsGLDrawer.DrawStaticColliderShape(ref statics[i], triggerHighlightColor);
            }

            // 7. Contact points + normals
            if (showContacts)
            {
                for (int i = 0; i < cCount; i++)
                    FPPhysicsGLDrawer.DrawContact(ref contacts[i],
                        contactPointColor, contactNormalColor,
                        contactNormalScale, contactPointRadius, showContactNormals);
                for (int i = 0; i < scCount; i++)
                    FPPhysicsGLDrawer.DrawContact(ref sContacts[i],
                        contactPointColor, contactNormalColor,
                        contactNormalScale, contactPointRadius, showContactNormals);
            }

            // 8. Highlight selected item (final pass — overlaid on top of all rendering)
            DrawSelectedHighlight(bodies, bc, statics, sc);

            FPPhysicsGLDrawer.EndPass();
        }

        void DrawSelectedHighlight(FPPhysicsBody[] bodies, int bc,
                                    FPStaticCollider[] statics, int sc)
        {
            FPPhysicsGLDrawer.SetBold(0.015f);
            if (viewingBodies && selectedIndex >= 0 && selectedIndex < bc)
            {
                FPPhysicsGLDrawer.DrawBodyShape(ref bodies[selectedIndex], selectedShapeColor);
                FPPhysicsGLDrawer.DrawAABB(bodies[selectedIndex].collider.GetWorldBounds(bodies[selectedIndex].meshData), selectedAABBColor);
            }
            else if (!viewingBodies && selectedIndex >= 0 && selectedIndex < sc)
            {
                FPPhysicsGLDrawer.DrawStaticColliderShape(ref statics[selectedIndex], selectedShapeColor);
                FPPhysicsGLDrawer.DrawAABB(statics[selectedIndex].collider.GetWorldBounds(statics[selectedIndex].meshData), selectedAABBColor);
            }
            FPPhysicsGLDrawer.SetBold(0f);
        }

        // ---- Draw helpers ----

        void DrawStaticCollider(ref FPStaticCollider sc)
        {
            if (showStaticShape)
                FPPhysicsGLDrawer.DrawStaticColliderShape(ref sc, sceneStaticColor);
            if (showStaticAABB)
                FPPhysicsGLDrawer.DrawAABB(sc.collider.GetWorldBounds(sc.meshData), aabbColor);
        }

        void DrawBody(ref FPPhysicsBody body, int index)
        {
            Color color = BodyColor(ref body);

            if (showBodyShape)
                FPPhysicsGLDrawer.DrawBodyShape(ref body, color);
            if (showBodyAABB)
                FPPhysicsGLDrawer.DrawAABB(body.collider.GetWorldBounds(body.meshData), aabbColor);
            if (showBodyVelocity && !body.rigidBody.isStatic && !body.rigidBody.isKinematic)
            {
                Vector3 origin = ToV3(body.position);
                Vector3 vel    = ToV3(body.rigidBody.velocity);
                GL.Color(velocityColor);
                FPPhysicsGLDrawer.DrawArrowFromVelocity(origin, vel, velocityArrowScale);
            }
        }

        Color BodyColor(ref FPPhysicsBody body)
        {
            if (body.isTrigger)
            {
                Color base_ = body.rigidBody.isStatic    ? staticBodyShapeColor
                            : body.rigidBody.isKinematic  ? kinematicShapeColor
                                                          : dynamicShapeColor;
                return new Color(base_.r, base_.g, base_.b, 0.4f);
            }
            if (body.rigidBody.isStatic)    return staticBodyShapeColor;
            if (body.rigidBody.isKinematic) return kinematicShapeColor;
            return dynamicShapeColor;
        }

        // ---- Collision marking ----

        void MarkCollidingBodies(FPPhysicsBody[] bodies, int bodyCount,
                                  FPContact[] contacts, int cCount,
                                  FPContact[] sContacts, int scCount)
        {
            if (_collidingMark == null || _collidingMark.Length < bodyCount)
                _collidingMark = new bool[bodyCount];
            for (int i = 0; i < bodyCount; i++) _collidingMark[i] = false;

            for (int i = 0; i < cCount; i++)
            {
                if (contacts[i].isSpeculative) continue;
                int a = contacts[i].entityA;
                int b = contacts[i].entityB;
                if (a >= 0 && a < bodyCount) _collidingMark[a] = true;
                if (b >= 0 && b < bodyCount) _collidingMark[b] = true;
            }

            for (int i = 0; i < scCount; i++)
            {
                if (sContacts[i].isSpeculative) continue;
                int a = sContacts[i].entityA;
                int b = sContacts[i].entityB;
                if (a >= 0 && a < bodyCount) _collidingMark[a] = true;
                if (b >= 0 && b < bodyCount) _collidingMark[b] = true;
            }
        }

        void MarkTriggerBodies(int bodyCount, FPStaticCollider[] statics, int staticCount,
                                (int, int)[] pairs, int count)
        {
            if (_triggerMark == null || _triggerMark.Length < bodyCount)
                _triggerMark = new bool[bodyCount];
            for (int i = 0; i < bodyCount; i++) _triggerMark[i] = false;

            if (_triggerStaticMark == null || _triggerStaticMark.Length < staticCount)
                _triggerStaticMark = new bool[staticCount];
            for (int i = 0; i < staticCount; i++) _triggerStaticMark[i] = false;

            for (int i = 0; i < count; i++)
            {
                TryMarkTriggerId(pairs[i].Item1, bodyCount, staticCount);
                TryMarkTriggerId(pairs[i].Item2, bodyCount, staticCount);
            }
        }

        void TryMarkTriggerId(int id, int bodyCount, int staticCount)
        {
            if (_idToIndex.TryGetValue(id, out int bi) && bi < bodyCount)
                _triggerMark[bi] = true;
            else if (_staticIdToIndex.TryGetValue(id, out int si) && si < staticCount)
                _triggerStaticMark[si] = true;
        }

        void RebuildIdMaps(FPPhysicsBody[] bodies, int bodyCount,
                           FPStaticCollider[] statics, int staticCount)
        {
            _idToIndex.Clear();
            for (int i = 0; i < bodyCount; i++)
                _idToIndex[bodies[i].id] = i;

            _staticIdToIndex.Clear();
            for (int i = 0; i < staticCount; i++)
                _staticIdToIndex[statics[i].id] = i;
        }

        // ---- Format helpers (also used by the editor) ----

        public static string FmtV3(FPVector3 v)
            => $"({v.x.ToFloat():F2}, {v.y.ToFloat():F2}, {v.z.ToFloat():F2})";

        public static string FmtEuler(FPQuaternion q)
        {
            var e = new Quaternion(q.x.ToFloat(), q.y.ToFloat(), q.z.ToFloat(), q.w.ToFloat()).eulerAngles;
            return $"({e.x:F0}°, {e.y:F0}°, {e.z:F0}°)";
        }

        public static string BodyTypeStr(ref FPPhysicsBody b)
        {
            if (b.rigidBody.isStatic)    return "Static";
            if (b.rigidBody.isKinematic) return "Kinematic";
            return "Dynamic";
        }

        static Vector3 ToV3(FPVector3 v) => new Vector3(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat());

#endif

    }
}