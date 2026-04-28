using System;
using UnityEngine;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Unity.Physics;

namespace xpTURN.Klotho.Editor
{
    /// <summary>
    /// Converts a Unity Collider into a fixed-point static collider.
    /// </summary>
    public static class FPStaticColliderConverter
    {
        // isTrigger: determined by tag ("FPStatic"=false, "FPTrigger"=true)
        // id, restitution, friction: read from FPStaticColliderOverride if present, otherwise defaults
        public static FPStaticCollider Convert(Collider col, bool isTrigger)
        {
            Vector3      lossyScale = col.transform.lossyScale;
            FPVector3    worldPos   = ToFP(col.transform.position);
            FPQuaternion worldRot   = ToFP(col.transform.rotation);

            if (lossyScale.x < 0 || lossyScale.y < 0 || lossyScale.z < 0)
                throw new InvalidOperationException($"'{col.name}': Negative scale not supported");

            FPCollider fpCollider = col switch
            {
                SphereCollider  sc => ConvertSphere(sc, lossyScale),
                BoxCollider     bc => ConvertBox(bc, worldRot, lossyScale),
                CapsuleCollider cc => ConvertCapsule(cc, lossyScale),
                MeshCollider    mc => ConvertMesh(mc, worldPos, worldRot),
                _ => throw new NotSupportedException($"Unsupported collider type: {col.GetType()}")
            };

            FPMeshData meshData = col is MeshCollider meshCol ? BakeMeshData(meshCol, lossyScale) : null;

            var ov = col.GetComponent<FPStaticColliderOverride>();
            return new FPStaticCollider
            {
                id          = ov != null && ov.id != 0 ? ov.id : -1,
                collider    = fpCollider,
                meshData    = meshData,
                isTrigger   = isTrigger,
                restitution = ov != null ? FP64.FromFloat(ov.restitution) : FP64.Zero,
                friction    = ov != null ? FP64.FromFloat(ov.friction)    : FP64.Zero,
            };
        }

        static FPCollider ConvertSphere(SphereCollider sc, Vector3 lossyScale)
        {
            if (!Mathf.Approximately(lossyScale.x, lossyScale.y) || !Mathf.Approximately(lossyScale.x, lossyScale.z))
                throw new InvalidOperationException($"'{sc.name}': Non-uniform scale not supported for SphereCollider (ellipsoid)");

            FPVector3 center = ToFP(sc.transform.TransformPoint(sc.center));
            FP64      radius = FP64.FromFloat(sc.radius * lossyScale.x);
            return FPCollider.FromSphere(new FPSphereShape(radius, center));
        }

        static FPCollider ConvertBox(BoxCollider bc, FPQuaternion worldRot, Vector3 lossyScale)
        {
            FPVector3 center = ToFP(bc.transform.TransformPoint(bc.center));
            FPVector3 halfExtents = new FPVector3(
                FP64.FromFloat(bc.size.x * 0.5f * lossyScale.x),
                FP64.FromFloat(bc.size.y * 0.5f * lossyScale.y),
                FP64.FromFloat(bc.size.z * 0.5f * lossyScale.z));
            return FPCollider.FromBox(new FPBoxShape(halfExtents, center, worldRot));
        }

        static FPCollider ConvertCapsule(CapsuleCollider cc, Vector3 lossyScale)
        {
            FPVector3 center = ToFP(cc.transform.TransformPoint(cc.center));

            float        heightScale, radialScale;
            Quaternion   axisRot;
            switch (cc.direction)
            {
                case 0:  // X-axis
                    heightScale = lossyScale.x;
                    radialScale = Mathf.Max(lossyScale.y, lossyScale.z);
                    axisRot     = Quaternion.FromToRotation(Vector3.up, Vector3.right);
                    break;
                case 2:  // Z-axis
                    heightScale = lossyScale.z;
                    radialScale = Mathf.Max(lossyScale.x, lossyScale.y);
                    axisRot     = Quaternion.FromToRotation(Vector3.up, Vector3.forward);
                    break;
                default: // 1 = Y-axis
                    heightScale = lossyScale.y;
                    radialScale = Mathf.Max(lossyScale.x, lossyScale.z);
                    axisRot     = Quaternion.identity;
                    break;
            }

            FP64         radius     = FP64.FromFloat(cc.radius * radialScale);
            FP64         halfHeight = FP64.FromFloat(Mathf.Max(0f, cc.height * 0.5f * heightScale - cc.radius * radialScale));
            FPQuaternion rot        = ToFP(cc.transform.rotation * axisRot);
            return FPCollider.FromCapsule(new FPCapsuleShape(halfHeight, radius, center, rot));
        }

        // FPMeshShape: stores world position/rotation only (scale baked into vertices)
        static FPCollider ConvertMesh(MeshCollider mc, FPVector3 worldPos, FPQuaternion worldRot)
            => FPCollider.FromMesh(new FPMeshShape(worldPos, worldRot));

        // Converts vertices to FP64 after applying local scale (rotation/position delegated to FPMeshShape)
        static FPMeshData BakeMeshData(MeshCollider mc, Vector3 lossyScale)
        {
            Mesh      mesh     = mc.sharedMesh;
            Vector3[] srcVerts = mesh.vertices;
            int[]     srcIdx   = mesh.triangles;

            var fpVerts = new FPVector3[srcVerts.Length];
            for (int i = 0; i < srcVerts.Length; i++)
            {
                Vector3 scaled = Vector3.Scale(srcVerts[i], lossyScale);
                fpVerts[i] = ToFP(scaled);
            }
            return new FPMeshData(fpVerts, srcIdx);
        }

        // float → FP64 conversion (once at export time)
        static FPVector3    ToFP(Vector3    v) => new(FP64.FromFloat(v.x), FP64.FromFloat(v.y), FP64.FromFloat(v.z));
        static FPQuaternion ToFP(Quaternion q) => new(FP64.FromFloat(q.x), FP64.FromFloat(q.y), FP64.FromFloat(q.z), FP64.FromFloat(q.w));
    }
}
