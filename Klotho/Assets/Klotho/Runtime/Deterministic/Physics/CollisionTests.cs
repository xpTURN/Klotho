using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    public static class CollisionTests
    {
        public static bool SphereSphere(ref FPSphereShape sa, ref FPSphereShape sb, out FPContact contact)
        {
            FPVector3 diff = sb.position - sa.position;
            FP64 sqrDist = diff.sqrMagnitude;
            FP64 radiusSum = sa.radius + sb.radius;

            if (sqrDist > radiusSum * radiusSum)
            {
                contact = default;
                return false;
            }

            FP64 dist = diff.magnitude;
            FPVector3 normal;
            if (dist == FP64.Zero)
            {
                normal = FPVector3.Up;
            }
            else
            {
                normal = diff / dist;
            }

            FP64 depth = radiusSum - dist;
            FPVector3 point = sa.position + normal * (sa.radius - depth * FP64.Half);

            contact = new FPContact(point, normal, depth, 0, 0);
            return true;
        }

        public static bool SphereBox(ref FPSphereShape sphere, ref FPBoxShape box, out FPContact contact)
        {
            FPVector3 closest = box.ClosestPoint(sphere.position);
            FPVector3 diff = sphere.position - closest;
            FP64 sqrDist = diff.sqrMagnitude;

            if (sqrDist > sphere.radius * sphere.radius)
            {
                contact = default;
                return false;
            }

            FPVector3 normal;
            FP64 dist;

            if (sqrDist == FP64.Zero)
            {
                FPVector3 localPos = sphere.position - box.position;
                box.GetAxes(out FPVector3 axisX, out FPVector3 axisY, out FPVector3 axisZ);

                FP64 px = FPVector3.Dot(localPos, axisX);
                FP64 py = FPVector3.Dot(localPos, axisY);
                FP64 pz = FPVector3.Dot(localPos, axisZ);

                FP64 dx = box.halfExtents.x - FP64.Abs(px);
                FP64 dy = box.halfExtents.y - FP64.Abs(py);
                FP64 dz = box.halfExtents.z - FP64.Abs(pz);

                if (dx <= dy && dx <= dz)
                {
                    normal = px >= FP64.Zero ? axisX : -axisX;
                    dist = dx;
                }
                else if (dy <= dz)
                {
                    normal = py >= FP64.Zero ? axisY : -axisY;
                    dist = dy;
                }
                else
                {
                    normal = pz >= FP64.Zero ? axisZ : -axisZ;
                    dist = dz;
                }

                FP64 depth = sphere.radius + dist;
                FPVector3 point = sphere.position - normal * (sphere.radius - depth * FP64.Half);
                contact = new FPContact(point, normal, depth, 0, 0);
                return true;
            }

            dist = FP64.Sqrt(sqrDist);
            normal = diff / dist;
            FP64 penetration = sphere.radius - dist;
            FPVector3 contactPoint = closest + normal * (penetration * FP64.Half);

            contact = new FPContact(contactPoint, normal, penetration, 0, 0);
            return true;
        }

        public static bool SphereCapsule(ref FPSphereShape sphere, ref FPCapsuleShape capsule, out FPContact contact)
        {
            FPCapsule fpCapsule = capsule.ToFPCapsule();
            FPVector3 closestOnSeg = fpCapsule.ClosestPointOnSegment(sphere.position);

            FPVector3 diff = sphere.position - closestOnSeg;
            FP64 sqrDist = diff.sqrMagnitude;
            FP64 radiusSum = sphere.radius + capsule.radius;

            if (sqrDist > radiusSum * radiusSum)
            {
                contact = default;
                return false;
            }

            FP64 dist = diff.magnitude;
            FPVector3 normal;
            if (dist == FP64.Zero)
            {
                normal = FPVector3.Up;
            }
            else
            {
                normal = diff / dist;
            }

            FP64 depth = radiusSum - dist;
            FPVector3 point = closestOnSeg + normal * (capsule.radius - depth * FP64.Half);

            contact = new FPContact(point, normal, depth, 0, 0);
            return true;
        }

        public static bool CapsuleCapsule(ref FPCapsuleShape ca, ref FPCapsuleShape cb, out FPContact contact)
        {
            FPCapsule capA = ca.ToFPCapsule();
            FPCapsule capB = cb.ToFPCapsule();

            ClosestPointsOnSegments(
                capA.pointA, capA.pointB,
                capB.pointA, capB.pointB,
                out FPVector3 closestA, out FPVector3 closestB
            );

            FPVector3 diff = closestB - closestA;
            FP64 sqrDist = diff.sqrMagnitude;
            FP64 radiusSum = ca.radius + cb.radius;

            if (sqrDist > radiusSum * radiusSum)
            {
                contact = default;
                return false;
            }

            FP64 dist = diff.magnitude;
            FPVector3 normal;
            if (dist == FP64.Zero)
            {
                normal = FPVector3.Up;
            }
            else
            {
                normal = diff / dist;
            }

            FP64 depth = radiusSum - dist;
            FPVector3 point = closestA + normal * (ca.radius - depth * FP64.Half);

            contact = new FPContact(point, normal, depth, 0, 0);
            return true;
        }

        public static bool BoxBox(ref FPBoxShape boxA, ref FPBoxShape boxB, out FPContact contact)
        {
            boxA.GetAxes(out FPVector3 axA0, out FPVector3 axA1, out FPVector3 axA2);
            boxB.GetAxes(out FPVector3 axB0, out FPVector3 axB1, out FPVector3 axB2);

            FPVector3 d = boxB.position - boxA.position;

            Span<FPVector3> axes = stackalloc FPVector3[15];
            axes[0] = axA0; axes[1] = axA1; axes[2] = axA2;
            axes[3] = axB0; axes[4] = axB1; axes[5] = axB2;

            axes[6] = FPVector3.Cross(axA0, axB0);
            axes[7] = FPVector3.Cross(axA0, axB1);
            axes[8] = FPVector3.Cross(axA0, axB2);
            axes[9] = FPVector3.Cross(axA1, axB0);
            axes[10] = FPVector3.Cross(axA1, axB1);
            axes[11] = FPVector3.Cross(axA1, axB2);
            axes[12] = FPVector3.Cross(axA2, axB0);
            axes[13] = FPVector3.Cross(axA2, axB1);
            axes[14] = FPVector3.Cross(axA2, axB2);

            FP64 minOverlap = FP64.MaxValue;
            FPVector3 minAxis = FPVector3.Up;

            FP64 epsilon = FP64.FromRaw(4295);

            for (int i = 0; i < 15; i++)
            {
                FPVector3 axis = axes[i];
                FP64 sqrLen = axis.sqrMagnitude;
                if (sqrLen < epsilon)
                    continue;

                axis = axis / FP64.Sqrt(sqrLen);

                FP64 projA = ProjectBox(ref boxA, axis);
                FP64 projB = ProjectBox(ref boxB, axis);
                FP64 dist = FP64.Abs(FPVector3.Dot(d, axis));
                FP64 overlap = projA + projB - dist;

                if (overlap < FP64.Zero)
                {
                    contact = default;
                    return false;
                }

                if (overlap < minOverlap)
                {
                    minOverlap = overlap;
                    minAxis = axis;
                }
            }

            if (FPVector3.Dot(d, minAxis) < FP64.Zero)
                minAxis = -minAxis;

            FPVector3 contactPoint = boxA.ClosestPoint(boxB.ClosestPoint(boxA.position));

            contact = new FPContact(contactPoint, minAxis, minOverlap, 0, 0);
            return true;
        }

        public static bool BoxCapsule(ref FPBoxShape box, ref FPCapsuleShape capsule, out FPContact contact)
        {
            FPCapsule fpCapsule = capsule.ToFPCapsule();

            FPVector3 closestOnSegToBox = fpCapsule.ClosestPointOnSegment(box.position);
            FPVector3 closestOnBox = box.ClosestPoint(closestOnSegToBox);
            FPVector3 closestOnSeg = fpCapsule.ClosestPointOnSegment(closestOnBox);

            FPVector3 diff = closestOnSeg - closestOnBox;
            FP64 sqrDist = diff.sqrMagnitude;

            if (sqrDist > capsule.radius * capsule.radius)
            {
                contact = default;
                return false;
            }

            FP64 dist = diff.magnitude;
            FPVector3 normal;

            if (dist == FP64.Zero)
            {
                FPVector3 localPos = closestOnSeg - box.position;
                box.GetAxes(out FPVector3 axisX, out FPVector3 axisY, out FPVector3 axisZ);

                FP64 px = FPVector3.Dot(localPos, axisX);
                FP64 py = FPVector3.Dot(localPos, axisY);
                FP64 pz = FPVector3.Dot(localPos, axisZ);

                FP64 dx = box.halfExtents.x - FP64.Abs(px);
                FP64 dy = box.halfExtents.y - FP64.Abs(py);
                FP64 dz = box.halfExtents.z - FP64.Abs(pz);

                if (dx <= dy && dx <= dz)
                    normal = px >= FP64.Zero ? axisX : -axisX;
                else if (dy <= dz)
                    normal = py >= FP64.Zero ? axisY : -axisY;
                else
                    normal = pz >= FP64.Zero ? axisZ : -axisZ;

                dist = FP64.Zero;
            }
            else
            {
                normal = diff / dist;
            }

            FP64 depth = capsule.radius - dist;
            FPVector3 point = closestOnBox + normal * (depth * FP64.Half);

            contact = new FPContact(point, normal, depth, 0, 0);
            return true;
        }

        public static bool SphereMesh(ref FPSphereShape sphere, ref FPMeshShape mesh,
            FPMeshData meshData, out FPContact contact)
        {
            contact = default;
            bool found = false;
            FP64 deepest = FP64.Zero;

            FPQuaternion rot = mesh.rotation;
            FPVector3 pos = mesh.position;

            for (int i = 0; i < meshData.TriangleCount; i++)
            {
                meshData.GetTriangle(i, out FPVector3 lv0, out FPVector3 lv1, out FPVector3 lv2);
                FPVector3 v0 = pos + rot * lv0;
                FPVector3 v1 = pos + rot * lv1;
                FPVector3 v2 = pos + rot * lv2;

                FPVector3 closest = ClosestPointOnTriangle(sphere.position, v0, v1, v2);
                FPVector3 diff = sphere.position - closest;
                FP64 sqrDist = diff.sqrMagnitude;

                if (sqrDist > sphere.radius * sphere.radius)
                    continue;

                FP64 dist = diff.magnitude;
                FP64 depth = sphere.radius - dist;

                if (!found || depth > deepest)
                {
                    FPVector3 normal;
                    if (dist == FP64.Zero)
                    {
                        FPVector3 edge1 = v1 - v0;
                        FPVector3 edge2 = v2 - v0;
                        normal = FPVector3.Cross(edge1, edge2);
                        FP64 nLen = normal.magnitude;
                        if (nLen > FP64.Epsilon)
                            normal = normal / nLen;
                        else
                            normal = FPVector3.Up;

                        if (FPVector3.Dot(sphere.position - v0, normal) < FP64.Zero)
                            normal = -normal;
                    }
                    else
                    {
                        normal = diff / dist;
                    }

                    deepest = depth;
                    FPVector3 point = closest + normal * (depth * FP64.Half);
                    contact = new FPContact(point, normal, depth, 0, 0);
                    found = true;
                }
            }

            return found;
        }

        public static bool CapsuleMesh(ref FPCapsuleShape capsule, ref FPMeshShape mesh,
            FPMeshData meshData, out FPContact contact)
        {
            contact = default;
            bool found = false;
            FP64 deepest = FP64.Zero;

            FPCapsule fpCapsule = capsule.ToFPCapsule();
            FPQuaternion rot = mesh.rotation;
            FPVector3 pos = mesh.position;

            for (int i = 0; i < meshData.TriangleCount; i++)
            {
                meshData.GetTriangle(i, out FPVector3 lv0, out FPVector3 lv1, out FPVector3 lv2);
                FPVector3 v0 = pos + rot * lv0;
                FPVector3 v1 = pos + rot * lv1;
                FPVector3 v2 = pos + rot * lv2;

                FPVector3 closestOnSeg = ClosestPointOnSegmentToTriangle(
                    fpCapsule.pointA, fpCapsule.pointB, v0, v1, v2);
                FPVector3 closestOnTri = ClosestPointOnTriangle(closestOnSeg, v0, v1, v2);

                FPVector3 diff = closestOnSeg - closestOnTri;
                FP64 sqrDist = diff.sqrMagnitude;

                if (sqrDist > capsule.radius * capsule.radius)
                    continue;

                FP64 dist = diff.magnitude;
                FP64 depth = capsule.radius - dist;

                if (!found || depth > deepest)
                {
                    FPVector3 normal;
                    if (dist == FP64.Zero)
                    {
                        FPVector3 edge1 = v1 - v0;
                        FPVector3 edge2 = v2 - v0;
                        normal = FPVector3.Cross(edge1, edge2);
                        FP64 nLen = normal.magnitude;
                        if (nLen > FP64.Epsilon)
                            normal = normal / nLen;
                        else
                            normal = FPVector3.Up;

                        if (FPVector3.Dot(closestOnSeg - v0, normal) < FP64.Zero)
                            normal = -normal;
                    }
                    else
                    {
                        normal = diff / dist;
                    }

                    deepest = depth;
                    FPVector3 point = closestOnTri + normal * (depth * FP64.Half);
                    contact = new FPContact(point, normal, depth, 0, 0);
                    found = true;
                }
            }

            return found;
        }

        public static bool BoxMesh(ref FPBoxShape box, ref FPMeshShape mesh,
            FPMeshData meshData, out FPContact contact)
        {
            contact = default;
            bool found = false;
            FP64 shallowest = FP64.MaxValue;

            FPQuaternion rot = mesh.rotation;
            FPVector3 pos = mesh.position;

            box.GetAxes(out FPVector3 boxAx0, out FPVector3 boxAx1, out FPVector3 boxAx2);

            FP64 epsilon = FP64.FromRaw(4295);

            Span<FPVector3> axes = stackalloc FPVector3[13];

            for (int i = 0; i < meshData.TriangleCount; i++)
            {
                meshData.GetTriangle(i, out FPVector3 lv0, out FPVector3 lv1, out FPVector3 lv2);
                FPVector3 v0 = pos + rot * lv0;
                FPVector3 v1 = pos + rot * lv1;
                FPVector3 v2 = pos + rot * lv2;

                FPVector3 triEdge0 = v1 - v0;
                FPVector3 triEdge1 = v2 - v0;
                FPVector3 triNormal = FPVector3.Cross(triEdge0, triEdge1);
                FP64 triNormalSqr = triNormal.sqrMagnitude;
                if (triNormalSqr < epsilon)
                    continue;
                triNormal = triNormal / FP64.Sqrt(triNormalSqr);

                FPVector3 triEdge2 = v2 - v1;

                axes[0] = boxAx0;
                axes[1] = boxAx1;
                axes[2] = boxAx2;
                axes[3] = triNormal;
                axes[4] = FPVector3.Cross(boxAx0, triEdge0);
                axes[5] = FPVector3.Cross(boxAx0, triEdge1);
                axes[6] = FPVector3.Cross(boxAx0, triEdge2);
                axes[7] = FPVector3.Cross(boxAx1, triEdge0);
                axes[8] = FPVector3.Cross(boxAx1, triEdge1);
                axes[9] = FPVector3.Cross(boxAx1, triEdge2);
                axes[10] = FPVector3.Cross(boxAx2, triEdge0);
                axes[11] = FPVector3.Cross(boxAx2, triEdge1);
                axes[12] = FPVector3.Cross(boxAx2, triEdge2);

                FPVector3 d = ((v0 + v1 + v2) / FP64.FromInt(3)) - box.position;

                FP64 minOverlap = FP64.MaxValue;
                FPVector3 minAxis = FPVector3.Up;
                bool separated = false;

                for (int j = 0; j < 13; j++)
                {
                    FPVector3 axis = axes[j];
                    FP64 sqrLen = axis.sqrMagnitude;
                    if (sqrLen < epsilon)
                        continue;

                    axis = axis / FP64.Sqrt(sqrLen);

                    FP64 projBox = ProjectBox(ref box, axis);

                    FP64 tv0 = FPVector3.Dot(v0, axis);
                    FP64 tv1 = FPVector3.Dot(v1, axis);
                    FP64 tv2 = FPVector3.Dot(v2, axis);
                    FP64 triMin = FP64.Min(tv0, FP64.Min(tv1, tv2));
                    FP64 triMax = FP64.Max(tv0, FP64.Max(tv1, tv2));

                    FP64 boxCenter = FPVector3.Dot(box.position, axis);
                    FP64 boxMin = boxCenter - projBox;
                    FP64 boxMax = boxCenter + projBox;

                    FP64 overlap = FP64.Min(boxMax - triMin, triMax - boxMin);

                    if (overlap < FP64.Zero)
                    {
                        separated = true;
                        break;
                    }

                    if (overlap < minOverlap)
                    {
                        minOverlap = overlap;
                        minAxis = axis;
                    }
                }

                if (separated)
                    continue;

                if (FPVector3.Dot(d, minAxis) < FP64.Zero)
                    minAxis = -minAxis;

                if (!found || minOverlap < shallowest)
                {
                    shallowest = minOverlap;

                    FPVector3 triCenter = (v0 + v1 + v2) / FP64.FromInt(3);
                    FPVector3 contactPoint = box.ClosestPoint(triCenter);

                    contact = new FPContact(contactPoint, minAxis, minOverlap, 0, 0);
                    found = true;
                }
            }

            return found;
        }

        // Returns true if the closest point lies only on an internal (concave) edge (candidate for contact removal).
        // On an edge: allowed if that edge slot is active, removed if inactive (concave).
        // Inside the triangle: always allowed.
        static bool IsBlockedByInactiveEdge(FPVector3 closest,
            FPVector3 v0, FPVector3 v1, FPVector3 v2,
            bool active01, bool active12, bool active20)
        {
            // Determine whether the point lies on an edge using barycentric coordinates.
            // On edge: one coordinate ≈ 0, the other two sum ≈ 1.
            FPVector3 e0 = v1 - v0, e1 = v2 - v0, ep = closest - v0;
            FP64 d00 = FPVector3.Dot(e0, e0);
            FP64 d01 = FPVector3.Dot(e0, e1);
            FP64 d11 = FPVector3.Dot(e1, e1);
            FP64 d20 = FPVector3.Dot(ep, e0);
            FP64 d21 = FPVector3.Dot(ep, e1);
            FP64 denom = d00 * d11 - d01 * d01;
            if (FP64.Abs(denom) < FP64.FromDouble(1e-8)) return false;  // Skip degenerate triangle (FP64 error can produce negative denominator)

            FP64 v = (d11 * d20 - d01 * d21) / denom;
            FP64 w = (d00 * d21 - d01 * d20) / denom;
            FP64 u = FP64.One - v - w;

            FP64 edgeTol = FP64.FromDouble(0.01);

            // On edge: the corresponding weight ≈ 0 (Abs < edgeTol), the other two are within range.
            bool onEdge01 = FP64.Abs(w) < edgeTol && u > -edgeTol && v > -edgeTol;
            bool onEdge12 = FP64.Abs(u) < edgeTol && v > -edgeTol && w > -edgeTol;
            bool onEdge20 = FP64.Abs(v) < edgeTol && u > -edgeTol && w > -edgeTol;

            // Interior: not on any edge → allowed.
            if (!onEdge01 && !onEdge12 && !onEdge20) return false;

            // On edge (or vertex): allowed if at least one of the related edges is active.
            if (onEdge01 && active01) return false;
            if (onEdge12 && active12) return false;
            if (onEdge20 && active20) return false;
            return true;
        }

        public static int SphereMeshMulti(ref FPSphereShape sphere, ref FPMeshShape mesh,
            FPMeshData meshData, FPContact[] buffer, int maxContacts)
        {
            int count = 0;
            FPQuaternion rot = mesh.rotation;
            FPVector3 pos = mesh.position;
            bool[] aeFlags = null;

            for (int i = 0; i < meshData.TriangleCount && count < maxContacts; i++)
            {
                meshData.GetTriangle(i, out FPVector3 lv0, out FPVector3 lv1, out FPVector3 lv2);
                FPVector3 v0 = pos + rot * lv0;
                FPVector3 v1 = pos + rot * lv1;
                FPVector3 v2 = pos + rot * lv2;

                FPVector3 closest = ClosestPointOnTriangle(sphere.position, v0, v1, v2);
                FPVector3 diff = sphere.position - closest;
                FP64 sqrDist = diff.sqrMagnitude;

                if (sqrDist > sphere.radius * sphere.radius)
                    continue;

                if (aeFlags != null)
                {
                    int b = i * 3;
                    if (IsBlockedByInactiveEdge(closest, v0, v1, v2, aeFlags[b], aeFlags[b + 1], aeFlags[b + 2]))
                        continue;
                }

                FP64 dist = diff.magnitude;
                FP64 depth = sphere.radius - dist;

                FPVector3 normal;
                if (dist == FP64.Zero)
                {
                    FPVector3 edge1 = v1 - v0;
                    FPVector3 edge2 = v2 - v0;
                    normal = FPVector3.Cross(edge1, edge2);
                    FP64 nLen = normal.magnitude;
                    normal = nLen > FP64.Epsilon ? normal / nLen : FPVector3.Up;
                    if (FPVector3.Dot(sphere.position - v0, normal) < FP64.Zero)
                        normal = -normal;
                }
                else
                {
                    normal = diff / dist;
                }

                buffer[count++] = new FPContact(closest + normal * (depth * FP64.Half), normal, depth, 0, 0);
            }

            return count;
        }

        public static int CapsuleMeshMulti(ref FPCapsuleShape capsule, ref FPMeshShape mesh,
            FPMeshData meshData, FPContact[] buffer, int maxContacts)
        {
            int count = 0;
            FPCapsule fpCapsule = capsule.ToFPCapsule();
            FPQuaternion rot = mesh.rotation;
            FPVector3 pos = mesh.position;
            bool[] aeFlags = null;

            for (int i = 0; i < meshData.TriangleCount && count < maxContacts; i++)
            {
                meshData.GetTriangle(i, out FPVector3 lv0, out FPVector3 lv1, out FPVector3 lv2);
                FPVector3 v0 = pos + rot * lv0;
                FPVector3 v1 = pos + rot * lv1;
                FPVector3 v2 = pos + rot * lv2;

                FPVector3 closestOnSeg = ClosestPointOnSegmentToTriangle(
                    fpCapsule.pointA, fpCapsule.pointB, v0, v1, v2);
                FPVector3 closestOnTri = ClosestPointOnTriangle(closestOnSeg, v0, v1, v2);

                FPVector3 diff = closestOnSeg - closestOnTri;
                FP64 sqrDist = diff.sqrMagnitude;

                if (sqrDist > capsule.radius * capsule.radius)
                    continue;

                if (aeFlags != null)
                {
                    int b = i * 3;
                    if (IsBlockedByInactiveEdge(closestOnTri, v0, v1, v2, aeFlags[b], aeFlags[b + 1], aeFlags[b + 2]))
                        continue;
                }

                FP64 dist = diff.magnitude;
                FP64 depth = capsule.radius - dist;

                FPVector3 normal;
                if (dist == FP64.Zero)
                {
                    FPVector3 edge1 = v1 - v0;
                    FPVector3 edge2 = v2 - v0;
                    normal = FPVector3.Cross(edge1, edge2);
                    FP64 nLen = normal.magnitude;
                    normal = nLen > FP64.Epsilon ? normal / nLen : FPVector3.Up;
                    if (FPVector3.Dot(closestOnSeg - v0, normal) < FP64.Zero)
                        normal = -normal;
                }
                else
                {
                    normal = diff / dist;
                }

                buffer[count++] = new FPContact(closestOnTri + normal * (depth * FP64.Half), normal, depth, 0, 0);
            }

            return count;
        }

        public static int BoxMeshMulti(ref FPBoxShape box, ref FPMeshShape mesh,
            FPMeshData meshData, FPContact[] buffer, int maxContacts)
        {
            int count = 0;
            FPQuaternion rot = mesh.rotation;
            FPVector3 pos = mesh.position;
            bool[] aeFlags = null;

            box.GetAxes(out FPVector3 boxAx0, out FPVector3 boxAx1, out FPVector3 boxAx2);
            FP64 epsilon = FP64.FromRaw(4295);

            Span<FPVector3> axes = stackalloc FPVector3[13];

            for (int i = 0; i < meshData.TriangleCount && count < maxContacts; i++)
            {
                meshData.GetTriangle(i, out FPVector3 lv0, out FPVector3 lv1, out FPVector3 lv2);
                FPVector3 v0 = pos + rot * lv0;
                FPVector3 v1 = pos + rot * lv1;
                FPVector3 v2 = pos + rot * lv2;

                FPVector3 triEdge0 = v1 - v0;
                FPVector3 triEdge1 = v2 - v0;
                FPVector3 triNormal = FPVector3.Cross(triEdge0, triEdge1);
                FP64 triNormalSqr = triNormal.sqrMagnitude;
                if (triNormalSqr < epsilon) continue;
                triNormal = triNormal / FP64.Sqrt(triNormalSqr);

                FPVector3 triEdge2 = v2 - v1;
                axes[0] = boxAx0; axes[1] = boxAx1; axes[2] = boxAx2; axes[3] = triNormal;
                axes[4]  = FPVector3.Cross(boxAx0, triEdge0);
                axes[5]  = FPVector3.Cross(boxAx0, triEdge1);
                axes[6]  = FPVector3.Cross(boxAx0, triEdge2);
                axes[7]  = FPVector3.Cross(boxAx1, triEdge0);
                axes[8]  = FPVector3.Cross(boxAx1, triEdge1);
                axes[9]  = FPVector3.Cross(boxAx1, triEdge2);
                axes[10] = FPVector3.Cross(boxAx2, triEdge0);
                axes[11] = FPVector3.Cross(boxAx2, triEdge1);
                axes[12] = FPVector3.Cross(boxAx2, triEdge2);

                FPVector3 d = ((v0 + v1 + v2) / FP64.FromInt(3)) - box.position;
                FP64 minOverlap = FP64.MaxValue;
                FPVector3 minAxis = FPVector3.Up;
                bool separated = false;

                for (int j = 0; j < 13; j++)
                {
                    FPVector3 axis = axes[j];
                    FP64 sqrLen = axis.sqrMagnitude;
                    if (sqrLen < epsilon) continue;
                    axis = axis / FP64.Sqrt(sqrLen);

                    FP64 projBox = ProjectBox(ref box, axis);
                    FP64 tv0 = FPVector3.Dot(v0, axis);
                    FP64 tv1 = FPVector3.Dot(v1, axis);
                    FP64 tv2 = FPVector3.Dot(v2, axis);
                    FP64 triMin = FP64.Min(tv0, FP64.Min(tv1, tv2));
                    FP64 triMax = FP64.Max(tv0, FP64.Max(tv1, tv2));
                    FP64 boxCenter = FPVector3.Dot(box.position, axis);
                    FP64 overlap = FP64.Min(boxCenter + projBox - triMin, triMax - (boxCenter - projBox));

                    if (overlap < FP64.Zero) { separated = true; break; }
                    if (overlap < minOverlap) { minOverlap = overlap; minAxis = axis; }
                }

                if (separated) continue;

                if (FPVector3.Dot(d, minAxis) < FP64.Zero)
                    minAxis = -minAxis;

                FPVector3 triCenter = (v0 + v1 + v2) / FP64.FromInt(3);
                FPVector3 contactPoint = box.ClosestPoint(triCenter);

                if (aeFlags != null)
                {
                    int b = i * 3;
                    // Project contactPoint (box surface) onto the triangle → edge classification uses the projected point.
                    FPVector3 closestOnTri = ClosestPointOnTriangle(contactPoint, v0, v1, v2);
                    if (IsBlockedByInactiveEdge(closestOnTri, v0, v1, v2, aeFlags[b], aeFlags[b + 1], aeFlags[b + 2]))
                        continue;
                }

                buffer[count++] = new FPContact(contactPoint, minAxis, minOverlap, 0, 0);
            }

            return count;
        }

        #region Distance Queries

        public static FP64 DistanceSphereSphere(
            ref FPSphereShape a, ref FPSphereShape b,
            out FPVector3 normal, out FPVector3 closestA, out FPVector3 closestB)
        {
            FPVector3 diff = b.position - a.position;
            FP64 dist = diff.magnitude;
            FP64 radiusSum = a.radius + b.radius;

            if (dist <= FP64.Epsilon)
            {
                normal = FPVector3.Up;
                closestA = a.position + FPVector3.Up * a.radius;
                closestB = b.position - FPVector3.Up * b.radius;
                return -radiusSum;
            }

            normal = diff / dist;
            closestA = a.position + normal * a.radius;
            closestB = b.position - normal * b.radius;
            return dist - radiusSum;
        }

        public static FP64 DistanceSphereBox(
            ref FPSphereShape sphere, ref FPBoxShape box,
            out FPVector3 normal, out FPVector3 closestA, out FPVector3 closestB)
        {
            FPVector3 closestOnBox = box.ClosestPoint(sphere.position);
            FPVector3 diff = closestOnBox - sphere.position;
            FP64 sqrDist = diff.sqrMagnitude;

            if (sqrDist <= FP64.Epsilon)
            {
                FPVector3 localPos = sphere.position - box.position;
                box.GetAxes(out FPVector3 axisX, out FPVector3 axisY, out FPVector3 axisZ);

                FP64 px = FPVector3.Dot(localPos, axisX);
                FP64 py = FPVector3.Dot(localPos, axisY);
                FP64 pz = FPVector3.Dot(localPos, axisZ);

                FP64 dx = box.halfExtents.x - FP64.Abs(px);
                FP64 dy = box.halfExtents.y - FP64.Abs(py);
                FP64 dz = box.halfExtents.z - FP64.Abs(pz);

                if (dx <= dy && dx <= dz)
                    normal = px >= FP64.Zero ? -axisX : axisX;
                else if (dy <= dz)
                    normal = py >= FP64.Zero ? -axisY : axisY;
                else
                    normal = pz >= FP64.Zero ? -axisZ : axisZ;

                closestA = sphere.position + normal * sphere.radius;
                closestB = closestOnBox;
                return -(sphere.radius + dx);
            }

            FP64 dist = FP64.Sqrt(sqrDist);
            normal = diff / dist;
            closestA = sphere.position + normal * sphere.radius;
            closestB = closestOnBox;
            return dist - sphere.radius;
        }

        public static FP64 DistanceSphereCapsule(
            ref FPSphereShape sphere, ref FPCapsuleShape capsule,
            out FPVector3 normal, out FPVector3 closestA, out FPVector3 closestB)
        {
            FPCapsule fpCapsule = capsule.ToFPCapsule();
            FPVector3 closestOnSeg = fpCapsule.ClosestPointOnSegment(sphere.position);

            FPVector3 diff = closestOnSeg - sphere.position;
            FP64 dist = diff.magnitude;
            FP64 radiusSum = sphere.radius + capsule.radius;

            if (dist <= FP64.Epsilon)
            {
                normal = FPVector3.Up;
                closestA = sphere.position + FPVector3.Up * sphere.radius;
                closestB = closestOnSeg - FPVector3.Up * capsule.radius;
                return -radiusSum;
            }

            normal = diff / dist;
            closestA = sphere.position + normal * sphere.radius;
            closestB = closestOnSeg - normal * capsule.radius;
            return dist - radiusSum;
        }

        public static FP64 DistanceCapsuleCapsule(
            ref FPCapsuleShape ca, ref FPCapsuleShape cb,
            out FPVector3 normal, out FPVector3 closestA, out FPVector3 closestB)
        {
            FPCapsule capA = ca.ToFPCapsule();
            FPCapsule capB = cb.ToFPCapsule();

            ClosestPointsOnSegments(
                capA.pointA, capA.pointB,
                capB.pointA, capB.pointB,
                out FPVector3 segClosestA, out FPVector3 segClosestB);

            FPVector3 diff = segClosestB - segClosestA;
            FP64 dist = diff.magnitude;
            FP64 radiusSum = ca.radius + cb.radius;

            if (dist <= FP64.Epsilon)
            {
                normal = FPVector3.Up;
                closestA = segClosestA + FPVector3.Up * ca.radius;
                closestB = segClosestB - FPVector3.Up * cb.radius;
                return -radiusSum;
            }

            normal = diff / dist;
            closestA = segClosestA + normal * ca.radius;
            closestB = segClosestB - normal * cb.radius;
            return dist - radiusSum;
        }

        public static FP64 DistanceBoxBox(
            ref FPBoxShape boxA, ref FPBoxShape boxB,
            out FPVector3 normal, out FPVector3 closestA, out FPVector3 closestB)
        {
            boxA.GetAxes(out FPVector3 axA0, out FPVector3 axA1, out FPVector3 axA2);
            boxB.GetAxes(out FPVector3 axB0, out FPVector3 axB1, out FPVector3 axB2);

            FPVector3 d = boxB.position - boxA.position;

            Span<FPVector3> axes = stackalloc FPVector3[15];
            axes[0] = axA0; axes[1] = axA1; axes[2] = axA2;
            axes[3] = axB0; axes[4] = axB1; axes[5] = axB2;
            axes[6] = FPVector3.Cross(axA0, axB0);
            axes[7] = FPVector3.Cross(axA0, axB1);
            axes[8] = FPVector3.Cross(axA0, axB2);
            axes[9] = FPVector3.Cross(axA1, axB0);
            axes[10] = FPVector3.Cross(axA1, axB1);
            axes[11] = FPVector3.Cross(axA1, axB2);
            axes[12] = FPVector3.Cross(axA2, axB0);
            axes[13] = FPVector3.Cross(axA2, axB1);
            axes[14] = FPVector3.Cross(axA2, axB2);

            FP64 epsilon = FP64.FromRaw(4295);
            FP64 maxSep = FP64.MinValue;
            FPVector3 sepAxis = FPVector3.Up;
            FP64 minOverlap = FP64.MaxValue;
            FPVector3 overlapAxis = FPVector3.Up;
            bool separated = false;

            for (int i = 0; i < 15; i++)
            {
                FPVector3 axis = axes[i];
                FP64 sqrLen = axis.sqrMagnitude;
                if (sqrLen < epsilon)
                    continue;

                axis = axis / FP64.Sqrt(sqrLen);

                FP64 projA = ProjectBox(ref boxA, axis);
                FP64 projB = ProjectBox(ref boxB, axis);
                FP64 dist = FP64.Abs(FPVector3.Dot(d, axis));
                FP64 overlap = projA + projB - dist;

                if (overlap < FP64.Zero)
                {
                    separated = true;
                    FP64 sep = -overlap;
                    if (maxSep == FP64.MinValue || sep < maxSep)
                    {
                        maxSep = sep;
                        sepAxis = axis;
                    }
                }
                else if (!separated && overlap < minOverlap)
                {
                    minOverlap = overlap;
                    overlapAxis = axis;
                }
            }

            if (separated)
            {
                if (FPVector3.Dot(d, sepAxis) < FP64.Zero)
                    sepAxis = -sepAxis;
                normal = sepAxis;
                closestA = boxA.ClosestPoint(boxB.position);
                closestB = boxB.ClosestPoint(boxA.position);
                return maxSep;
            }

            if (FPVector3.Dot(d, overlapAxis) < FP64.Zero)
                overlapAxis = -overlapAxis;
            normal = overlapAxis;
            closestA = boxA.ClosestPoint(boxB.ClosestPoint(boxA.position));
            closestB = closestA;
            return -minOverlap;
        }

        public static FP64 DistanceBoxCapsule(
            ref FPBoxShape box, ref FPCapsuleShape capsule,
            out FPVector3 normal, out FPVector3 closestA, out FPVector3 closestB)
        {
            FPCapsule fpCapsule = capsule.ToFPCapsule();

            FPVector3 closestOnSegToBox = fpCapsule.ClosestPointOnSegment(box.position);
            FPVector3 closestOnBox = box.ClosestPoint(closestOnSegToBox);
            FPVector3 closestOnSeg = fpCapsule.ClosestPointOnSegment(closestOnBox);

            FPVector3 diff = closestOnSeg - closestOnBox;
            FP64 sqrDist = diff.sqrMagnitude;

            if (sqrDist <= FP64.Epsilon)
            {
                FPVector3 localPos = closestOnSeg - box.position;
                box.GetAxes(out FPVector3 axisX, out FPVector3 axisY, out FPVector3 axisZ);

                FP64 px = FPVector3.Dot(localPos, axisX);
                FP64 py = FPVector3.Dot(localPos, axisY);
                FP64 pz = FPVector3.Dot(localPos, axisZ);

                FP64 dx = box.halfExtents.x - FP64.Abs(px);
                FP64 dy = box.halfExtents.y - FP64.Abs(py);
                FP64 dz = box.halfExtents.z - FP64.Abs(pz);

                if (dx <= dy && dx <= dz)
                    normal = px >= FP64.Zero ? axisX : -axisX;
                else if (dy <= dz)
                    normal = py >= FP64.Zero ? axisY : -axisY;
                else
                    normal = pz >= FP64.Zero ? axisZ : -axisZ;

                closestA = closestOnBox;
                closestB = closestOnSeg + normal * capsule.radius;
                return -(capsule.radius + FP64.Min(dx, FP64.Min(dy, dz)));
            }

            FP64 dist = FP64.Sqrt(sqrDist);
            normal = diff / dist;
            closestA = closestOnBox;
            closestB = closestOnSeg - normal * capsule.radius;
            return dist - capsule.radius;
        }

        #endregion

        #region RayCast

        public static bool RaySphere(FPRay3 ray, ref FPSphereShape sphere, out FP64 t, out FPVector3 normal)
        {
            FPVector3 oc = ray.origin - sphere.position;
            FP64 b = FPVector3.Dot(oc, ray.direction);
            FP64 c = FPVector3.Dot(oc, oc) - sphere.radius * sphere.radius;

            FP64 discriminant = b * b - c;
            if (discriminant < FP64.Zero)
            {
                t = default;
                normal = default;
                return false;
            }

            FP64 sqrtD = FP64.Sqrt(discriminant);
            FP64 t0 = -b - sqrtD;

            if (t0 < FP64.Zero)
            {
                t0 = -b + sqrtD;
                if (t0 < FP64.Zero)
                {
                    t = default;
                    normal = default;
                    return false;
                }
            }

            t = t0;
            FPVector3 hitPoint = ray.GetPoint(t);
            normal = (hitPoint - sphere.position) / sphere.radius;
            return true;
        }

        public static bool RayBox(FPRay3 ray, ref FPBoxShape box, out FP64 t, out FPVector3 normal)
        {
            FPQuaternion invRot = FPQuaternion.Inverse(box.rotation);
            FPVector3 localOrigin = invRot * (ray.origin - box.position);
            FPVector3 localDir = invRot * ray.direction;

            FP64 tMin = FP64.MinValue;
            FP64 tMax = FP64.MaxValue;
            FPVector3 tMinNormal = default;

            FP64 hx = box.halfExtents.x;
            FP64 hy = box.halfExtents.y;
            FP64 hz = box.halfExtents.z;

            // X slab
            if (!SlabTest(localOrigin.x, localDir.x, hx, 0, ref tMin, ref tMax, ref tMinNormal))
            { t = default; normal = default; return false; }
            // Y slab
            if (!SlabTest(localOrigin.y, localDir.y, hy, 1, ref tMin, ref tMax, ref tMinNormal))
            { t = default; normal = default; return false; }
            // Z slab
            if (!SlabTest(localOrigin.z, localDir.z, hz, 2, ref tMin, ref tMax, ref tMinNormal))
            { t = default; normal = default; return false; }

            FP64 tHit = tMin >= FP64.Zero ? tMin : tMax;
            if (tHit < FP64.Zero)
            {
                t = default;
                normal = default;
                return false;
            }

            t = tHit;
            if (tMin >= FP64.Zero)
                normal = box.rotation * tMinNormal;
            else
            {
                FPVector3 hitLocal = localOrigin + localDir * tMax;
                FP64 dx = hx - FP64.Abs(hitLocal.x);
                FP64 dy = hy - FP64.Abs(hitLocal.y);
                FP64 dz = hz - FP64.Abs(hitLocal.z);
                FPVector3 exitLocal;
                if (dx <= dy && dx <= dz)
                    exitLocal = new FPVector3(hitLocal.x >= FP64.Zero ? FP64.One : -FP64.One, FP64.Zero, FP64.Zero);
                else if (dy <= dz)
                    exitLocal = new FPVector3(FP64.Zero, hitLocal.y >= FP64.Zero ? FP64.One : -FP64.One, FP64.Zero);
                else
                    exitLocal = new FPVector3(FP64.Zero, FP64.Zero, hitLocal.z >= FP64.Zero ? FP64.One : -FP64.One);
                normal = box.rotation * exitLocal;
            }
            return true;
        }

        static bool SlabTest(FP64 origin, FP64 dir, FP64 halfExtent, int axis,
            ref FP64 tMin, ref FP64 tMax, ref FPVector3 tMinNormal)
        {
            FP64 epsilon = FP64.FromRaw(4295);
            if (FP64.Abs(dir) < epsilon)
            {
                if (origin < -halfExtent || origin > halfExtent)
                    return false;
                return true;
            }

            FP64 invD = FP64.One / dir;
            FP64 t1 = (-halfExtent - origin) * invD;
            FP64 t2 = ( halfExtent - origin) * invD;

            FP64 nSign = -FP64.One;
            if (t1 > t2)
            {
                FP64 tmp = t1; t1 = t2; t2 = tmp;
                nSign = FP64.One;
            }

            if (t1 > tMin)
            {
                tMin = t1;
                tMinNormal = default;
                if (axis == 0) tMinNormal = new FPVector3(nSign, FP64.Zero, FP64.Zero);
                else if (axis == 1) tMinNormal = new FPVector3(FP64.Zero, nSign, FP64.Zero);
                else tMinNormal = new FPVector3(FP64.Zero, FP64.Zero, nSign);
            }
            if (t2 < tMax)
                tMax = t2;

            return tMin <= tMax;
        }

        public static bool RayCapsule(FPRay3 ray, ref FPCapsuleShape capsule, out FP64 t, out FPVector3 normal)
        {
            capsule.GetWorldPoints(out FPVector3 pA, out FPVector3 pB);
            FPVector3 segDir = pB - pA;
            FP64 segLenSqr = segDir.sqrMagnitude;
            FP64 r = capsule.radius;

            FPVector3 m = ray.origin - pA;
            FP64 md = FPVector3.Dot(m, segDir);
            FP64 nd = FPVector3.Dot(ray.direction, segDir);
            FP64 dd = segLenSqr;
            FP64 mn = FPVector3.Dot(m, ray.direction);
            FP64 mm = FPVector3.Dot(m, m);

            // infinite cylinder: ||(O + tD - A) - ((O + tD - A)·S/|S|²) S||² = r²
            FP64 a = dd * FP64.One - nd * nd;
            FP64 b2 = dd * mn - nd * md;  // half of b
            FP64 c = dd * (mm - r * r) - md * md;

            t = FP64.MaxValue;
            normal = default;
            bool hit = false;

            FP64 epsilon = FP64.FromRaw(4295);

            if (a > epsilon)
            {
                FP64 discriminant = b2 * b2 - a * c;
                if (discriminant >= FP64.Zero)
                {
                    FP64 sqrtD = FP64.Sqrt(discriminant);

                    FP64 t0 = (-b2 - sqrtD) / a;
                    if (t0 >= FP64.Zero)
                    {
                        FP64 s0 = md + t0 * nd;
                        if (s0 >= FP64.Zero && s0 <= dd)
                        {
                            t = t0;
                            FPVector3 hitPt = ray.GetPoint(t);
                            FPVector3 proj = pA + segDir * (s0 / dd);
                            normal = (hitPt - proj) / r;
                            hit = true;
                        }
                    }

                    if (!hit)
                    {
                        FP64 t1 = (-b2 + sqrtD) / a;
                        if (t1 >= FP64.Zero)
                        {
                            FP64 s1 = md + t1 * nd;
                            if (s1 >= FP64.Zero && s1 <= dd)
                            {
                                t = t1;
                                FPVector3 hitPt = ray.GetPoint(t);
                                FPVector3 proj = pA + segDir * (s1 / dd);
                                normal = (hitPt - proj) / r;
                                hit = true;
                            }
                        }
                    }
                }
            }

            // sphere caps
            var capA = new FPSphereShape(r, pA);
            if (RaySphere(ray, ref capA, out FP64 tA, out FPVector3 nA) && tA < t)
            {
                t = tA;
                normal = nA;
                hit = true;
            }

            var capB = new FPSphereShape(r, pB);
            if (RaySphere(ray, ref capB, out FP64 tB, out FPVector3 nB) && tB < t)
            {
                t = tB;
                normal = nB;
                hit = true;
            }

            return hit;
        }

        public static bool RayMesh(FPRay3 ray, ref FPMeshShape mesh, FPMeshData meshData,
            out FP64 t, out FPVector3 normal)
        {
            t = FP64.MaxValue;
            normal = default;
            bool hit = false;

            FPQuaternion rot = mesh.rotation;
            FPVector3 pos = mesh.position;

            for (int i = 0; i < meshData.TriangleCount; i++)
            {
                meshData.GetTriangle(i, out FPVector3 lv0, out FPVector3 lv1, out FPVector3 lv2);
                FPVector3 v0 = pos + rot * lv0;
                FPVector3 v1 = pos + rot * lv1;
                FPVector3 v2 = pos + rot * lv2;

                if (RayTriangle(ray, v0, v1, v2, out FP64 tTri, out FPVector3 nTri) && tTri < t)
                {
                    t = tTri;
                    normal = nTri;
                    hit = true;
                }
            }

            return hit;
        }

        static bool RayTriangle(FPRay3 ray, FPVector3 v0, FPVector3 v1, FPVector3 v2,
            out FP64 t, out FPVector3 normal)
        {
            FPVector3 e1 = v1 - v0;
            FPVector3 e2 = v2 - v0;
            FPVector3 h = FPVector3.Cross(ray.direction, e2);
            FP64 a = FPVector3.Dot(e1, h);

            FP64 epsilon = FP64.FromRaw(4295);
            if (FP64.Abs(a) < epsilon)
            {
                t = default;
                normal = default;
                return false;
            }

            FP64 f = FP64.One / a;
            FPVector3 s = ray.origin - v0;
            FP64 u = f * FPVector3.Dot(s, h);

            if (u < FP64.Zero || u > FP64.One)
            {
                t = default;
                normal = default;
                return false;
            }

            FPVector3 q = FPVector3.Cross(s, e1);
            FP64 v = f * FPVector3.Dot(ray.direction, q);

            if (v < FP64.Zero || u + v > FP64.One)
            {
                t = default;
                normal = default;
                return false;
            }

            t = f * FPVector3.Dot(e2, q);
            if (t < FP64.Zero)
            {
                t = default;
                normal = default;
                return false;
            }

            normal = FPVector3.Cross(e1, e2);
            FP64 nLen = normal.magnitude;
            if (nLen > FP64.Epsilon)
                normal = normal / nLen;
            else
                normal = FPVector3.Up;

            if (FPVector3.Dot(normal, ray.direction) > FP64.Zero)
                normal = -normal;

            return true;
        }

        #endregion

        #region Helpers

        private static FP64 ProjectBox(ref FPBoxShape box, FPVector3 axis)
        {
            box.GetAxes(out FPVector3 axX, out FPVector3 axY, out FPVector3 axZ);
            return FP64.Abs(FPVector3.Dot(axX, axis)) * box.halfExtents.x
                 + FP64.Abs(FPVector3.Dot(axY, axis)) * box.halfExtents.y
                 + FP64.Abs(FPVector3.Dot(axZ, axis)) * box.halfExtents.z;
        }

        private static void ClosestPointsOnSegments(
            FPVector3 p1, FPVector3 q1,
            FPVector3 p2, FPVector3 q2,
            out FPVector3 closestA, out FPVector3 closestB)
        {
            FPVector3 d1 = q1 - p1;
            FPVector3 d2 = q2 - p2;
            FPVector3 r = p1 - p2;

            FP64 a = FPVector3.Dot(d1, d1);
            FP64 e = FPVector3.Dot(d2, d2);
            FP64 f = FPVector3.Dot(d2, r);

            FP64 epsilon = FP64.FromRaw(4295);

            if (a <= epsilon && e <= epsilon)
            {
                closestA = p1;
                closestB = p2;
                return;
            }

            FP64 s, t;

            if (a <= epsilon)
            {
                s = FP64.Zero;
                t = FP64.Clamp01(f / e);
            }
            else
            {
                FP64 c = FPVector3.Dot(d1, r);
                if (e <= epsilon)
                {
                    t = FP64.Zero;
                    s = FP64.Clamp01(-c / a);
                }
                else
                {
                    FP64 b = FPVector3.Dot(d1, d2);
                    FP64 denom = a * e - b * b;

                    if (denom != FP64.Zero)
                        s = FP64.Clamp01((b * f - c * e) / denom);
                    else
                        s = FP64.Zero;

                    t = (b * s + f) / e;

                    if (t < FP64.Zero)
                    {
                        t = FP64.Zero;
                        s = FP64.Clamp01(-c / a);
                    }
                    else if (t > FP64.One)
                    {
                        t = FP64.One;
                        s = FP64.Clamp01((b - c) / a);
                    }
                }
            }

            closestA = p1 + d1 * s;
            closestB = p2 + d2 * t;
        }

        private static FPVector3 ClosestPointOnTriangle(FPVector3 p, FPVector3 a, FPVector3 b, FPVector3 c)
        {
            FPVector3 ab = b - a;
            FPVector3 ac = c - a;
            FPVector3 ap = p - a;

            FP64 d1 = FPVector3.Dot(ab, ap);
            FP64 d2 = FPVector3.Dot(ac, ap);
            if (d1 <= FP64.Zero && d2 <= FP64.Zero)
                return a;

            FPVector3 bp = p - b;
            FP64 d3 = FPVector3.Dot(ab, bp);
            FP64 d4 = FPVector3.Dot(ac, bp);
            if (d3 >= FP64.Zero && d4 <= d3)
                return b;

            FP64 vc = d1 * d4 - d3 * d2;
            if (vc <= FP64.Zero && d1 >= FP64.Zero && d3 <= FP64.Zero)
            {
                FP64 v = d1 / (d1 - d3);
                return a + ab * v;
            }

            FPVector3 cp = p - c;
            FP64 d5 = FPVector3.Dot(ab, cp);
            FP64 d6 = FPVector3.Dot(ac, cp);
            if (d6 >= FP64.Zero && d5 <= d6)
                return c;

            FP64 vb = d5 * d2 - d1 * d6;
            if (vb <= FP64.Zero && d2 >= FP64.Zero && d6 <= FP64.Zero)
            {
                FP64 w = d2 / (d2 - d6);
                return a + ac * w;
            }

            FP64 va = d3 * d6 - d5 * d4;
            if (va <= FP64.Zero && (d4 - d3) >= FP64.Zero && (d5 - d6) >= FP64.Zero)
            {
                FP64 w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + (c - b) * w;
            }

            FP64 denom = FP64.One / (va + vb + vc);
            FP64 vf = vb * denom;
            FP64 wf = vc * denom;
            return a + ab * vf + ac * wf;
        }

        private static FPVector3 ClosestPointOnSegmentToTriangle(
            FPVector3 segA, FPVector3 segB,
            FPVector3 triV0, FPVector3 triV1, FPVector3 triV2)
        {
            FPVector3 bestPoint = segA;
            FP64 bestSqr = (ClosestPointOnTriangle(segA, triV0, triV1, triV2) - segA).sqrMagnitude;

            FP64 sqrB = (ClosestPointOnTriangle(segB, triV0, triV1, triV2) - segB).sqrMagnitude;
            if (sqrB < bestSqr)
            {
                bestSqr = sqrB;
                bestPoint = segB;
            }

            FPVector3 segDir = segB - segA;

            FPVector3 edge0 = triV1 - triV0;
            FPVector3 edge1 = triV2 - triV0;
            FPVector3 edge2 = triV2 - triV1;

            TestSegmentEdge(segA, segDir, triV0, edge0, ref bestPoint, ref bestSqr, triV0, triV1, triV2);
            TestSegmentEdge(segA, segDir, triV0, edge1, ref bestPoint, ref bestSqr, triV0, triV1, triV2);
            TestSegmentEdge(segA, segDir, triV1, edge2, ref bestPoint, ref bestSqr, triV0, triV1, triV2);

            return bestPoint;
        }

        private static void TestSegmentEdge(
            FPVector3 segA, FPVector3 segDir,
            FPVector3 edgeOrigin, FPVector3 edgeDir,
            ref FPVector3 bestPoint, ref FP64 bestSqr,
            FPVector3 triV0, FPVector3 triV1, FPVector3 triV2)
        {
            ClosestPointsOnSegments(
                segA, segA + segDir,
                edgeOrigin, edgeOrigin + edgeDir,
                out FPVector3 onSeg, out _);

            FPVector3 onTri = ClosestPointOnTriangle(onSeg, triV0, triV1, triV2);
            FP64 sqr = (onTri - onSeg).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                bestPoint = onSeg;
            }
        }

        #endregion
    }
}
