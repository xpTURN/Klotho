using UnityEngine;
using UnityEngine.Rendering;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;

namespace xpTURN.Klotho.Unity.Physics
{
    public static class FPPhysicsGLDrawer
    {
        static Material _mat;
        static Camera   _currentCamera;
        static float    _boldThickness;

        static void EnsureMaterial()
        {
            if (_mat != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored");
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",     (int)CullMode.Off);
            _mat.SetInt("_ZWrite",   0);
        }

        public static void BeginPass(Camera cam, bool alwaysOnTop)
        {
            EnsureMaterial();
            _mat.SetInt("_ZTest", alwaysOnTop
                ? (int)CompareFunction.Always
                : (int)CompareFunction.LessEqual);
            _mat.SetPass(0);

            GL.PushMatrix();
            GL.LoadProjectionMatrix(GL.GetGPUProjectionMatrix(cam.projectionMatrix, false));
            GL.modelview = cam.worldToCameraMatrix;
            GL.Begin(GL.LINES);
            _currentCamera  = cam;
            _boldThickness  = 0f;
        }

        public static void SetBold(float thickness)
        {
            _boldThickness = thickness;
        }

        public static void EndPass()
        {
            GL.End();
            GL.PopMatrix();
        }

        // --- Shape dispatchers ---

        public static void DrawStaticColliderShape(ref FPStaticCollider sc, Color color)
        {
            GL.Color(color);
            switch (sc.collider.type)
            {
                case ShapeType.Sphere:
                    DrawSphereWire(ToV3(sc.collider.sphere.position),
                                   sc.collider.sphere.radius.ToFloat());
                    break;
                case ShapeType.Box:
                    DrawBoxWire(ToV3(sc.collider.box.position),
                                ToQuat(sc.collider.box.rotation),
                                ToV3(sc.collider.box.halfExtents));
                    break;
                case ShapeType.Capsule:
                    DrawCapsuleWire(ToV3(sc.collider.capsule.position),
                                    ToQuat(sc.collider.capsule.rotation),
                                    sc.collider.capsule.radius.ToFloat(),
                                    sc.collider.capsule.halfHeight.ToFloat());
                    break;
                case ShapeType.Mesh:
                    DrawMeshWire(ToV3(sc.collider.mesh.position),
                                 ToQuat(sc.collider.mesh.rotation),
                                 sc.meshData);
                    break;
            }
        }

        public static void DrawBodyShape(ref FPPhysicsBody body, Color color)
        {
            GL.Color(color);
            switch (body.collider.type)
            {
                case ShapeType.Sphere:
                    DrawSphereWire(ToV3(body.collider.sphere.position),
                                   body.collider.sphere.radius.ToFloat());
                    break;
                case ShapeType.Box:
                    DrawBoxWire(ToV3(body.collider.box.position),
                                ToQuat(body.collider.box.rotation),
                                ToV3(body.collider.box.halfExtents));
                    break;
                case ShapeType.Capsule:
                    DrawCapsuleWire(ToV3(body.collider.capsule.position),
                                    ToQuat(body.collider.capsule.rotation),
                                    body.collider.capsule.radius.ToFloat(),
                                    body.collider.capsule.halfHeight.ToFloat());
                    break;
                case ShapeType.Mesh:
                    DrawMeshWire(ToV3(body.collider.mesh.position),
                                 ToQuat(body.collider.mesh.rotation),
                                 body.meshData);
                    break;
            }
        }

        public static void DrawAABB(FPBounds3 bounds, Color color)
        {
            GL.Color(color);
            Vector3 center = ToV3(bounds.center);
            Vector3 half   = ToV3(bounds.extents);
            DrawBoxWire(center, Quaternion.identity, half);
        }

        // --- Primitive wire drawers ---

        static void DrawSphereWire(Vector3 center, float radius)
        {
            // XZ plane
            DrawCircle(center, Vector3.right, Vector3.forward, radius);
            // XY plane
            DrawCircle(center, Vector3.right, Vector3.up, radius);
            // YZ plane
            DrawCircle(center, Vector3.up, Vector3.forward, radius);
        }

        static void DrawCircle(Vector3 center, Vector3 tangent, Vector3 bitangent, float radius, int segments = 16)
        {
            Vector3 prev = center + tangent * radius;
            for (int i = 1; i <= segments; i++)
            {
                float   angle = i * 2f * Mathf.PI / segments;
                Vector3 curr  = center + (Mathf.Cos(angle) * tangent + Mathf.Sin(angle) * bitangent) * radius;
                GL.Vertex(prev);
                GL.Vertex(curr);
                prev = curr;
            }
        }

        static void DrawBoxWire(Vector3 position, Quaternion rotation, Vector3 halfExtents)
        {
            float hx = halfExtents.x, hy = halfExtents.y, hz = halfExtents.z;

            // c[0]=(-hx,-hy,-hz) c[1]=(+hx,-hy,-hz)
            // c[2]=(+hx,+hy,-hz) c[3]=(-hx,+hy,-hz)
            // c[4]=(-hx,-hy,+hz) c[5]=(+hx,-hy,+hz)
            // c[6]=(+hx,+hy,+hz) c[7]=(-hx,+hy,+hz)
            var c = new Vector3[8];
            c[0] = new Vector3(-hx, -hy, -hz);
            c[1] = new Vector3(+hx, -hy, -hz);
            c[2] = new Vector3(+hx, +hy, -hz);
            c[3] = new Vector3(-hx, +hy, -hz);
            c[4] = new Vector3(-hx, -hy, +hz);
            c[5] = new Vector3(+hx, -hy, +hz);
            c[6] = new Vector3(+hx, +hy, +hz);
            c[7] = new Vector3(-hx, +hy, +hz);

            var trs = Matrix4x4.TRS(position, rotation, Vector3.one);
            for (int i = 0; i < 8; i++)
                c[i] = trs.MultiplyPoint3x4(c[i]);

            // Bottom face (-hz)
            DrawLine(c[0], c[1]); DrawLine(c[1], c[2]); DrawLine(c[2], c[3]); DrawLine(c[3], c[0]);
            // Top face (+hz)
            DrawLine(c[4], c[5]); DrawLine(c[5], c[6]); DrawLine(c[6], c[7]); DrawLine(c[7], c[4]);
            // 4 vertical edges
            DrawLine(c[0], c[4]); DrawLine(c[1], c[5]); DrawLine(c[2], c[6]); DrawLine(c[3], c[7]);
        }

        static void DrawCapsuleWire(Vector3 position, Quaternion rotation, float radius, float halfHeight)
        {
            Vector3 axis   = rotation * Vector3.up;
            Vector3 top    = position + axis * halfHeight;
            Vector3 bottom = position - axis * halfHeight;

            Vector3 perp = Vector3.Cross(axis, Vector3.up).normalized;
            if (perp.sqrMagnitude < 0.001f) perp = Vector3.right;
            Vector3 perp2 = Vector3.Cross(axis, perp);

            DrawCircle(top,    perp, perp2, radius);
            DrawCircle(bottom, perp, perp2, radius);

            DrawLine(top + perp  * radius, bottom + perp  * radius);
            DrawLine(top - perp  * radius, bottom - perp  * radius);
            DrawLine(top + perp2 * radius, bottom + perp2 * radius);
            DrawLine(top - perp2 * radius, bottom - perp2 * radius);

            DrawHemiArc(top,    axis, perp,  radius);
            DrawHemiArc(top,    axis, perp2, radius);
            DrawHemiArc(bottom, -axis, perp,  radius);
            DrawHemiArc(bottom, -axis, perp2, radius);
        }

        static void DrawHemiArc(Vector3 center, Vector3 poleDir, Vector3 tangent, float radius, int segments = 8)
        {
            Vector3 prev = center + tangent * radius;
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * Mathf.PI / segments;
                Vector3 curr = center + (Mathf.Cos(angle) * tangent + Mathf.Sin(angle) * poleDir) * radius;
                GL.Vertex(prev);
                GL.Vertex(curr);
                prev = curr;
            }
        }

        static void DrawMeshWire(Vector3 position, Quaternion rotation, FPMeshData data)
        {
            if (data == null) return;
            var trs  = Matrix4x4.TRS(position, rotation, Vector3.one);
            var verts = data.vertices;
            var idx   = data.indices;
            for (int i = 0; i < idx.Length; i += 3)
            {
                Vector3 v0 = trs.MultiplyPoint3x4(ToV3(verts[idx[i]]));
                Vector3 v1 = trs.MultiplyPoint3x4(ToV3(verts[idx[i + 1]]));
                Vector3 v2 = trs.MultiplyPoint3x4(ToV3(verts[idx[i + 2]]));
                DrawLine(v0, v1);
                DrawLine(v1, v2);
                DrawLine(v2, v0);
            }
        }

        // --- Arrow ---

        public static void DrawArrowFromVelocity(Vector3 origin, Vector3 dir, float scale)
        {
            if (dir.sqrMagnitude < 0.0001f) return;
            Vector3 dn      = dir.normalized;
            Vector3 end     = origin + dir * scale;
            float   headLen = dir.magnitude * scale * 0.2f;
            DrawArrowHead(origin, end, dn, headLen);
        }

        public static void DrawArrowFromNormal(Vector3 origin, Vector3 dir, float length)
        {
            Vector3 end     = origin + dir * length;
            float   headLen = length * 0.2f;
            DrawArrowHead(origin, end, dir, headLen);
        }

        static void DrawArrowHead(Vector3 origin, Vector3 end, Vector3 dn, float headLen)
        {
            Vector3 right = Vector3.Cross(dn, Vector3.up).normalized;
            if (right.sqrMagnitude < 0.001f) right = Vector3.right;

            GL.Vertex(origin); GL.Vertex(end);
            GL.Vertex(end); GL.Vertex(end - dn * headLen + right * headLen * 0.5f);
            GL.Vertex(end); GL.Vertex(end - dn * headLen - right * headLen * 0.5f);
        }

        // --- Contact ---

        public static void DrawContact(ref FPContact c, Color pointColor, Color normalColor,
                                        float normalScale, float pointRadius, bool drawNormal)
        {
            Vector3 pt    = ToV3(c.point);
            Vector3 n     = ToV3(c.normal);
            float   d     = Mathf.Abs(c.depth.ToFloat());
            bool    isCCD = c.isSpeculative;

            GL.Color(pointColor);
            float r = pointRadius;
            GL.Vertex(pt + Vector3.right   * r); GL.Vertex(pt - Vector3.right   * r);
            GL.Vertex(pt + Vector3.up      * r); GL.Vertex(pt - Vector3.up      * r);
            GL.Vertex(pt + Vector3.forward * r); GL.Vertex(pt - Vector3.forward * r);

            if (drawNormal && n.sqrMagnitude > 0.001f)
            {
                GL.Color(isCCD ? Color.Lerp(normalColor, Color.cyan, 0.5f) : normalColor);
                float len = Mathf.Max(d * normalScale, 0.1f);
                DrawArrowFromNormal(pt, n, len);
            }
        }

        // --- Helpers ---

        static void DrawLine(Vector3 a, Vector3 b)
        {
            GL.Vertex(a);
            GL.Vertex(b);
            if (_boldThickness > 0f && _currentCamera != null)
            {
                Vector3 lineDir = (b - a).normalized;
                Vector3 camDir  = (_currentCamera.transform.position - (a + b) * 0.5f).normalized;
                Vector3 offset  = Vector3.Cross(lineDir, camDir).normalized * _boldThickness;
                if (offset.sqrMagnitude > 0.0001f)
                {
                    GL.Vertex(a + offset); GL.Vertex(b + offset);
                    GL.Vertex(a - offset); GL.Vertex(b - offset);
                }
            }
        }

        static Vector3    ToV3(FPVector3    v) => new Vector3(v.x.ToFloat(), v.y.ToFloat(), v.z.ToFloat());
        static Quaternion ToQuat(FPQuaternion q) => new Quaternion(q.x.ToFloat(), q.y.ToFloat(), q.z.ToFloat(), q.w.ToFloat());
    }
}
