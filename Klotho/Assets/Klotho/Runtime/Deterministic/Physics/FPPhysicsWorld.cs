using System;
using System.Collections.Generic;
using xpTURN.Klotho.Serialization;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Fixed-point physics simulation world. Manages bodies, collision detection, and joint resolution.
    /// </summary>
    public struct FPPhysicsWorld
    {
        FPSpatialGrid _grid;
        FPTriggerSystem _triggerSystem;
        List<(int, int)> _broadPairs;
        List<(int, int)> _triggerPairs;
        List<FPContact> _contacts;
        List<(int, int)> _sweepPairs;
        FP64[] _sweepAdvanced;

        FPStaticBVH _staticBVH;
        bool _staticBVHBuilt;
        List<int> _staticOverlapBuffer;
        List<(int, int)> _staticPairs;
        List<FPContact> _staticContacts;
        FPStaticCollider[] _staticColliders;
        int _staticColliderCount;
        List<(string key, List<FPStaticCollider> colliders)> _staticScenes;
        FPRigidBody _staticSentinel;
        FPContact[] _meshContactBuffer;
        bool _skipStaticGroundResponse;

        static readonly IntPairComparer PairComparer = default;

        public FPPhysicsWorld(FP64 cellSize)
        {
            _grid = new FPSpatialGrid(cellSize);
            _triggerSystem = new FPTriggerSystem(true);
            _broadPairs = new List<(int, int)>();
            _triggerPairs = new List<(int, int)>();
            _contacts = new List<FPContact>();
            _sweepPairs = new List<(int, int)>();
            _sweepAdvanced = null;
            _staticOverlapBuffer = new List<int>();
            _staticPairs = new List<(int, int)>();
            _staticContacts = new List<FPContact>();
            _staticColliders = null;
            _staticColliderCount = 0;
            _staticScenes = new List<(string, List<FPStaticCollider>)>();
            _staticSentinel = new FPRigidBody { isStatic = true };
            _meshContactBuffer = new FPContact[32];
            _skipStaticGroundResponse = false;
        }

        public void SetSkipStaticGroundResponse(bool skip)
        {
            _skipStaticGroundResponse = skip;
        }

        // On level load — physics body static only (path A)
        // When using LoadStaticColliders, RebuildStaticBVH must be called instead
        public void BuildStatic(FPPhysicsBody[] bodies, int count)
        {
            var staticIndices = new List<int>();
            for (int i = 0; i < count; i++)
                if (bodies[i].rigidBody.isStatic)
                    staticIndices.Add(i);

            _staticBVH = FPStaticBVH.Build(bodies, staticIndices.ToArray());
            _staticBVHBuilt = staticIndices.Count > 0;
        }

        // On level load — register scene export data (call before RebuildStaticBVH)
        // Backward compatibility: register a single scene with sceneKey = ""
        public void LoadStaticColliders(List<FPStaticCollider> colliders)
            => LoadStaticColliders("", colliders);

        // Array-based overload (compatibility for tests/external callers)
        public void LoadStaticColliders(FPStaticCollider[] colliders, int count)
            => LoadStaticColliders("", colliders, count);

        public void LoadStaticColliders(string sceneKey, FPStaticCollider[] colliders, int count)
        {
            var list = new List<FPStaticCollider>(count);
            for (int i = 0; i < count; i++)
                list.Add(colliders[i]);
            LoadStaticColliders(sceneKey, list);
        }

        // Per-scene additive registration — re-calling with the same key overwrites
        public void LoadStaticColliders(string sceneKey, List<FPStaticCollider> colliders)
        {
            for (int i = 0; i < _staticScenes.Count; i++)
            {
                if (_staticScenes[i].key == sceneKey)
                {
                    _staticScenes[i] = (sceneKey, colliders);
                    _staticColliders = FlattenSortedColliders(out _staticColliderCount);
                    return;
                }
            }
            _staticScenes.Add((sceneKey, colliders));
            _staticColliders = FlattenSortedColliders(out _staticColliderCount);
        }

        // Additive scene unload
        public void UnloadStaticColliders(string sceneKey)
        {
            for (int i = 0; i < _staticScenes.Count; i++)
            {
                if (_staticScenes[i].key == sceneKey)
                {
                    _staticScenes.RemoveAt(i);
                    _staticColliders = FlattenSortedColliders(out _staticColliderCount);
                    return;
                }
            }
        }

        // On level load — build BVH mixing both sources (path C)
        // Precondition: after this call, isStatic body indices in bodies[] must be immutable
        // When LoadStaticColliders is not called, _staticColliders=null and _staticColliderCount=0 —
        //   Build(path C) does not iterate colliders when count=0, so it is null-safe
        public void RebuildStaticBVH(FPPhysicsBody[] bodies, int bodyCount)
        {
            var staticIndices = new List<int>();
            for (int i = 0; i < bodyCount; i++)
                if (bodies[i].rigidBody.isStatic)
                    staticIndices.Add(i);

            _staticColliders = FlattenSortedColliders(out _staticColliderCount);

            _staticBVH = FPStaticBVH.Build(bodies, staticIndices.ToArray(),
                                           _staticColliders, _staticColliderCount);
            _staticBVHBuilt = staticIndices.Count > 0 || _staticColliderCount > 0;
        }

        public void GetStaticColliders(out FPStaticCollider[] colliders, out int count)
        {
            colliders = _staticColliders;
            count = _staticColliderCount;
        }

        public bool RayCastStatic(FPRay3 ray, FPPhysicsBody[] bodies, int bodyCount,
            FP64 maxDistance,
            out FPVector3 hitPoint, out FPVector3 hitNormal, out FP64 hitDistance,
            out int hitLeafIndex)
        {
            hitPoint = hitNormal = default;
            hitDistance = maxDistance;
            hitLeafIndex = 0;
            if (!_staticBVHBuilt)
                return false;

            TraverseRayCastStatic(_staticBVH.rootIndex, ray, bodies, bodyCount,
                ref hitPoint, ref hitNormal, ref hitDistance, ref hitLeafIndex);
            return hitDistance < maxDistance;
        }

        void TraverseRayCastStatic(int nodeIdx, FPRay3 ray, FPPhysicsBody[] bodies, int bodyCount,
            ref FPVector3 bestPoint, ref FPVector3 bestNormal, ref FP64 bestT, ref int bestLeafIndex)
        {
            ref FPBVHNode node = ref _staticBVH.nodes[nodeIdx];
            if (!node.bounds.IntersectRay(ray, out FP64 tHit, out FP64 tHitMin))
                return;
            // tHitMin < 0 means ray origin is inside bounds — skip early-exit to avoid false culling
            if (tHitMin >= FP64.Zero && tHit >= bestT)
                return;

            if (node.left == -1)
            {
                int leafIndex = node.leafIndex;
                FPCollider collider;
                FPMeshData meshData;

                if (leafIndex >= 0)
                {
                    if (leafIndex >= bodyCount) return;
                    collider = bodies[leafIndex].collider;
                    meshData = bodies[leafIndex].meshData;
                }
                else
                {
                    int ci = ~leafIndex;
                    if (_staticColliders == null || ci >= _staticColliderCount) return;
                    collider = _staticColliders[ci].collider;
                    meshData = _staticColliders[ci].meshData;
                }

                if (NarrowphaseDispatch.RayCast(ray, ref collider, meshData, out FP64 shapeT, out FPVector3 shapeN))
                {
                    if (shapeT < bestT)
                    {
                        bestT = shapeT;
                        bestPoint = ray.GetPoint(shapeT);
                        bestNormal = shapeN;
                        bestLeafIndex = leafIndex;
                    }
                }
                return;
            }

            TraverseRayCastStatic(node.left, ray, bodies, bodyCount, ref bestPoint, ref bestNormal, ref bestT, ref bestLeafIndex);
            TraverseRayCastStatic(node.right, ray, bodies, bodyCount, ref bestPoint, ref bestNormal, ref bestT, ref bestLeafIndex);
        }

        // Sort scene keys in alphabetical ascending order, then merge into a single array — guarantees determinism regardless of load order
        FPStaticCollider[] FlattenSortedColliders(out int total)
        {
            _staticScenes.Sort((a, b) => string.Compare(a.key, b.key, StringComparison.Ordinal));
            total = 0;
            foreach (var s in _staticScenes) total += s.colliders.Count;
            var flat = new FPStaticCollider[total];
            int offset = 0;
            foreach (var s in _staticScenes)
            {
                for (int i = 0; i < s.colliders.Count; i++)
                    flat[offset + i] = s.colliders[i];
                offset += s.colliders.Count;
            }
            return flat;
        }

        public void Step(
            FPPhysicsBody[] bodies,
            int count,
            FP64 dt,
            FPVector3 gravity,
            Action<int, int> onTriggerEnter,
            Action<int, int> onTriggerStay,
            Action<int, int> onTriggerExit)
        {
            Step(bodies, count, dt, gravity, null, 0, null, 0, 1,
                default, onTriggerEnter, onTriggerStay, onTriggerExit);
        }

        public void Step(
            FPPhysicsBody[] bodies,
            int count,
            FP64 dt,
            FPVector3 gravity,
            FPDistanceJoint[] distanceJoints,
            int distanceJointCount,
            FPHingeJoint[] hingeJoints,
            int hingeJointCount,
            int solverIterations,
            Action<int, int> onTriggerEnter,
            Action<int, int> onTriggerStay,
            Action<int, int> onTriggerExit)
        {
            Step(bodies, count, dt, gravity, distanceJoints, distanceJointCount,
                hingeJoints, hingeJointCount, solverIterations,
                default, onTriggerEnter, onTriggerStay, onTriggerExit);
        }

        public void Step(
            FPPhysicsBody[] bodies,
            int count,
            FP64 dt,
            FPVector3 gravity,
            FPDistanceJoint[] distanceJoints,
            int distanceJointCount,
            FPHingeJoint[] hingeJoints,
            int hingeJointCount,
            int solverIterations,
            FPCCDConfig ccdConfig,
            Action<int, int> onTriggerEnter,
            Action<int, int> onTriggerStay,
            Action<int, int> onTriggerExit)
        {
            if (count == 0)
            {
                _triggerPairs.Clear();
                _triggerSystem.ProcessCallbacks(_triggerPairs, onTriggerEnter, onTriggerStay, onTriggerExit);
                return;
            }

            // 1. Apply gravity
            for (int i = 0; i < count; i++)
            {
                if (!bodies[i].rigidBody.isStatic && !bodies[i].rigidBody.isKinematic)
                    bodies[i].rigidBody.AddForce(gravity * bodies[i].rigidBody.mass);
            }

            // 2. Sync collider positions
            for (int i = 0; i < count; i++)
                SyncColliderPosition(ref bodies[i]);

            // 3. Broadphase collision detection
            _grid.Clear();
            _staticPairs.Clear();
            _staticContacts.Clear();
            bool buildStatic = _staticBVHBuilt;
            for (int i = 0; i < count; i++)
            {
                if (buildStatic && bodies[i].rigidBody.isStatic) continue;

                FPBounds3 bounds = bodies[i].collider.GetWorldBounds(bodies[i].meshData);

                if (ccdConfig.enabled && (bodies[i].useCCD || bodies[i].useSweep) && !bodies[i].rigidBody.isStatic
                    && bodies[i].rigidBody.velocity.sqrMagnitude > ccdConfig.velocityThreshold * ccdConfig.velocityThreshold)
                {
                    FPVector3 displacement = bodies[i].rigidBody.velocity * dt;
                    FPVector3 bMin = bounds.min;
                    FPVector3 bMax = bounds.max;

                    if (displacement.x > FP64.Zero) bMax = new FPVector3(bMax.x + displacement.x, bMax.y, bMax.z);
                    else bMin = new FPVector3(bMin.x + displacement.x, bMin.y, bMin.z);
                    if (displacement.y > FP64.Zero) bMax = new FPVector3(bMax.x, bMax.y + displacement.y, bMax.z);
                    else bMin = new FPVector3(bMin.x, bMin.y + displacement.y, bMin.z);
                    if (displacement.z > FP64.Zero) bMax = new FPVector3(bMax.x, bMax.y, bMax.z + displacement.z);
                    else bMin = new FPVector3(bMin.x, bMin.y, bMin.z + displacement.z);

                    bounds.SetMinMax(bMin, bMax);

                    FP64 angSpeed = bodies[i].rigidBody.angularVelocity.magnitude;
                    if (angSpeed > FP64.Zero)
                    {
                        FP64 maxExtent = bounds.extents.magnitude;
                        FP64 angExpand = angSpeed * maxExtent * dt;
                        bounds = new FPBounds3(bounds.center, bounds.size + new FPVector3(angExpand, angExpand, angExpand) * FP64.FromInt(2));
                    }
                }

                _grid.Insert(i, bounds);

                if (buildStatic)
                {
                    _staticOverlapBuffer.Clear();
                    _staticBVH.OverlapAABB(bounds, _staticOverlapBuffer);

                    for (int s = 0; s < _staticOverlapBuffer.Count; s++)
                        _staticPairs.Add((i, _staticOverlapBuffer[s]));
                }
            }
            _grid.GetPairs(_broadPairs);

            if (buildStatic)
            {
                _staticPairs.Sort(PairComparer);

                int write = 0;
                for (int i = 0; i < _staticPairs.Count; i++)
                {
                    if (i == 0 || _staticPairs[i] != _staticPairs[i - 1])
                        _staticPairs[write++] = _staticPairs[i];
                }
                _staticPairs.RemoveRange(write, _staticPairs.Count - write);
            }

            // 4. Narrowphase collision detection
            _contacts.Clear();
            _triggerPairs.Clear();
            _sweepPairs.Clear();
            for (int p = 0; p < _broadPairs.Count; p++)
            {
                int idxA = _broadPairs[p].Item1;
                int idxB = _broadPairs[p].Item2;
                bool isTriggerPair = bodies[idxA].isTrigger || bodies[idxB].isTrigger;

                bool isSweepPair = false;
                if (ccdConfig.enabled && ccdConfig.maxSweepIterations > 0 && !isTriggerPair)
                {
                    bool sweepA = bodies[idxA].useSweep && bodies[idxA].collider.type == ShapeType.Sphere && !bodies[idxA].rigidBody.isStatic;
                    bool sweepB = bodies[idxB].useSweep && bodies[idxB].collider.type == ShapeType.Sphere && !bodies[idxB].rigidBody.isStatic;
                    if (sweepA || sweepB)
                    {
                        _sweepPairs.Add((idxA, idxB));
                        isSweepPair = true;
                    }
                }

                if (NarrowphaseDispatch.Test(
                    ref bodies[idxA].collider, bodies[idxA].meshData,
                    ref bodies[idxB].collider, bodies[idxB].meshData,
                    out FPContact contact))
                {
                    if (isTriggerPair)
                    {
                        int a = bodies[idxA].id;
                        int b = bodies[idxB].id;
                        if (a > b) { int tmp = a; a = b; b = tmp; }
                        _triggerPairs.Add((a, b));
                    }
                    else
                    {
                        contact = new FPContact(contact.point, contact.normal, contact.depth, idxA, idxB);
                        _contacts.Add(contact);
                    }
                }
                else if (!isSweepPair && ccdConfig.enabled && (bodies[idxA].useCCD || bodies[idxB].useCCD))
                {
                    FPVector3 relVel = bodies[idxA].rigidBody.velocity - bodies[idxB].rigidBody.velocity;
                    if (relVel.sqrMagnitude <= ccdConfig.velocityThreshold * ccdConfig.velocityThreshold)
                        continue;

                    FP64 dist = NarrowphaseDispatch.Distance(
                        ref bodies[idxA].collider,
                        ref bodies[idxB].collider,
                        out FPVector3 normal, out FPVector3 closestA, out FPVector3 closestB);

                    if (dist > FP64.Zero && dist < FP64.MaxValue)
                    {
                        FP64 approachSpeed = FPVector3.Dot(relVel, normal);
                        if (approachSpeed > FP64.Zero && dist < approachSpeed * dt)
                        {
                            if (isTriggerPair)
                            {
                                int a = bodies[idxA].id;
                                int b = bodies[idxB].id;
                                if (a > b) { int tmp = a; a = b; b = tmp; }
                                _triggerPairs.Add((a, b));
                            }
                            else
                            {
                                FPVector3 midPoint = (closestA + closestB) * FP64.Half;
                                contact = new FPContact(midPoint, normal, -dist, idxA, idxB)
                                {
                                    isSpeculative = true
                                };
                                _contacts.Add(contact);
                            }
                        }
                    }
                }
            }

            // Dynamic×Static narrowphase
            for (int p = 0; p < _staticPairs.Count; p++)
            {
                int dynIdx = _staticPairs[p].Item1;
                int leafIndex = _staticPairs[p].Item2;

                FPMeshData staticMesh;
                bool staticIsTrigger;
                FPContact contact;
                bool hit;

                if (leafIndex >= 0)
                {
                    staticMesh = bodies[leafIndex].meshData;
                    staticIsTrigger = bodies[leafIndex].isTrigger;
                    hit = NarrowphaseDispatch.Test(
                        ref bodies[dynIdx].collider, bodies[dynIdx].meshData,
                        ref bodies[leafIndex].collider, staticMesh,
                        out contact);
                }
                else
                {
                    int ci = ~leafIndex;
                    staticMesh = _staticColliders[ci].meshData;
                    staticIsTrigger = _staticColliders[ci].isTrigger;

                    if (_staticColliders[ci].collider.type == ShapeType.Mesh)
                    {
                        int hitCount = NarrowphaseDispatch.TestMulti(
                            ref bodies[dynIdx].collider, bodies[dynIdx].meshData,
                            ref _staticColliders[ci].collider, staticMesh,
                            _meshContactBuffer, _meshContactBuffer.Length);

                        bool isTriggerPairMulti = bodies[dynIdx].isTrigger || staticIsTrigger;
                        if (isTriggerPairMulti)
                        {
                            if (hitCount > 0)
                            {
                                int dynId = bodies[dynIdx].id;
                                int staticId = _staticColliders[ci].id;
                                int a = dynId, b = staticId;
                                if (a > b) { int tmp = a; a = b; b = tmp; }
                                _triggerPairs.Add((a, b));
                            }
                        }
                        else
                        {
                            for (int h = 0; h < hitCount; h++)
                            {
                                var mc = _meshContactBuffer[h];
                                _staticContacts.Add(new FPContact(mc.point, mc.normal, mc.depth, dynIdx, leafIndex));
                            }
                        }
                        continue;
                    }

                    hit = NarrowphaseDispatch.Test(
                        ref bodies[dynIdx].collider, bodies[dynIdx].meshData,
                        ref _staticColliders[ci].collider, staticMesh,
                        out contact);
                }

                if (!hit) continue;

                bool isTriggerPair = bodies[dynIdx].isTrigger || staticIsTrigger;
                if (isTriggerPair)
                {
                    int dynId = bodies[dynIdx].id;
                    int staticId = leafIndex >= 0
                        ? bodies[leafIndex].id
                        : _staticColliders[~leafIndex].id;
                    int a = dynId, b = staticId;
                    if (a > b) { int tmp = a; a = b; b = tmp; }
                    _triggerPairs.Add((a, b));
                }
                else
                {
                    contact = new FPContact(contact.point, contact.normal, contact.depth, dynIdx, leafIndex);
                    _staticContacts.Add(contact);
                }
            }

            // 5. Trigger callbacks
            _triggerPairs.Sort(PairComparer);
            _triggerSystem.ProcessCallbacks(_triggerPairs, onTriggerEnter, onTriggerStay, onTriggerExit);

            // 6. Collision response
            for (int i = 0; i < _contacts.Count; i++)
            {
                FPContact c = _contacts[i];
                FPCollisionResponse.ResolveContact(
                    ref bodies[c.entityA].rigidBody, ref bodies[c.entityA].position,
                    ref bodies[c.entityB].rigidBody, ref bodies[c.entityB].position,
                    in c, dt);
            }

            // 6-b. Dynamic×Static
            MergeStaticContacts();
            for (int i = 0; i < _staticContacts.Count; i++)
            {
                FPContact c = _staticContacts[i];

                if (c.entityB >= 0)
                {
                    FPCollisionResponse.ResolveContact(
                        ref bodies[c.entityA].rigidBody, ref bodies[c.entityA].position,
                        ref bodies[c.entityB].rigidBody, ref bodies[c.entityB].position,
                        in c, dt);
                }
                else
                {
                    ref var sc = ref _staticColliders[~c.entityB];

                    // Skip slope band ground contact
                    // Box collider: an edge contact's normal can differ from the face normal, so
                    // correct contact normal → nearest face normal before judging
                    if (_skipStaticGroundResponse)
                    {
                        FP64 checkNY = c.normal.y;  // preserve sign (skip positive only)

                        if (sc.collider.type == ShapeType.Box)
                        {
                            FPQuaternion invRot = FPQuaternion.Inverse(sc.collider.box.rotation);
                            FPVector3 localN = invRot * c.normal;
                            FP64 ax = FP64.Abs(localN.x);
                            FP64 ay = FP64.Abs(localN.y);
                            FP64 az = FP64.Abs(localN.z);

                            FPVector3 faceDir;
                            if (ay >= ax && ay >= az)
                                faceDir = localN.y >= FP64.Zero ? FPVector3.Up : -FPVector3.Up;
                            else if (ax >= az)
                                faceDir = localN.x >= FP64.Zero ? FPVector3.Right : -FPVector3.Right;
                            else
                                faceDir = localN.z >= FP64.Zero ? FPVector3.Forward : -FPVector3.Forward;

                            FPVector3 worldFaceN = sc.collider.box.rotation * faceDir;
                            // Preserve sign: positive Y = upward-facing surface (slope), negative Y = downward-facing surface (ceiling)
                            checkNY = worldFaceN.y;
                        }

                        // Skip positive Y only (slope = walkable surface)
                        // Keep negative Y (ceiling/underside = prevent penetration from below)
                        if (checkNY >= FP64.Half && checkNY < FP64.FromDouble(0.95))
                            continue;
                    }

                    _staticSentinel.restitution = sc.restitution;
                    _staticSentinel.friction = sc.friction;
                    var dummyPos = FPVector3.Zero;

                    if (_skipStaticGroundResponse
                        && (c.normal.y > FP64.Half || c.normal.y < -FP64.Half))
                    {
                        ref var bodyA = ref bodies[c.entityA];
                        FP64 speedSqr = bodyA.rigidBody.velocity.x * bodyA.rigidBody.velocity.x
                                      + bodyA.rigidBody.velocity.z * bodyA.rigidBody.velocity.z;

                        if (speedSqr > FP64.Epsilon)
                        {
                            // While moving: keep Y response, drop XZ push-back/friction
                            FP64 saveX = bodyA.position.x;
                            FP64 saveZ = bodyA.position.z;
                            FP64 saveVx = bodyA.rigidBody.velocity.x;
                            FP64 saveVz = bodyA.rigidBody.velocity.z;

                            FPCollisionResponse.ResolveContact(
                                ref bodyA.rigidBody, ref bodyA.position,
                                ref _staticSentinel, ref dummyPos,
                                in c, dt);

                            bodyA.position.x = saveX;
                            bodyA.position.z = saveZ;
                            bodyA.rigidBody.velocity.x = saveVx;
                            bodyA.rigidBody.velocity.z = saveVz;
                        }
                        // While idle: skip all ground/ceiling response (prevents Y push oscillation)
                    }
                    else
                    {
                        FPCollisionResponse.ResolveContact(
                            ref bodies[c.entityA].rigidBody, ref bodies[c.entityA].position,
                            ref _staticSentinel, ref dummyPos,
                            in c, dt);
                    }
                }
            }

            // 6.5 Sweep TOI resolution
            if (_sweepPairs.Count > 0)
            {
                if (_sweepAdvanced == null || _sweepAdvanced.Length < count)
                    _sweepAdvanced = new FP64[count];
                for (int i = 0; i < count; i++)
                    _sweepAdvanced[i] = FP64.Zero;

                ResolveSweepBodies(bodies, count, dt, ccdConfig);
            }

            // 7. Solve constraints
            bool hasDistanceJoints = distanceJoints != null && distanceJointCount > 0;
            bool hasHingeJoints = hingeJoints != null && hingeJointCount > 0;

            if (hasDistanceJoints || hasHingeJoints)
            {
                for (int iter = 0; iter < solverIterations; iter++)
                {
                    if (hasDistanceJoints)
                    {
                        for (int j = 0; j < distanceJointCount; j++)
                        {
                            FPDistanceJoint dj = distanceJoints[j];
                            FPConstraintSolver.SolveDistanceJoint(
                                ref bodies[dj.bodyIndexA].rigidBody,
                                ref bodies[dj.bodyIndexA].position,
                                in bodies[dj.bodyIndexA].rotation,
                                ref bodies[dj.bodyIndexB].rigidBody,
                                ref bodies[dj.bodyIndexB].position,
                                in bodies[dj.bodyIndexB].rotation,
                                in dj, dt);
                        }
                    }

                    if (hasHingeJoints)
                    {
                        for (int j = 0; j < hingeJointCount; j++)
                        {
                            FPHingeJoint hj = hingeJoints[j];
                            FPConstraintSolver.SolveHingeJoint(
                                ref bodies[hj.bodyIndexA].rigidBody,
                                ref bodies[hj.bodyIndexA].position,
                                in bodies[hj.bodyIndexA].rotation,
                                ref bodies[hj.bodyIndexB].rigidBody,
                                ref bodies[hj.bodyIndexB].position,
                                in bodies[hj.bodyIndexB].rotation,
                                in hj, dt);
                        }
                    }
                }
            }

            // 8. Integrate
            for (int i = 0; i < count; i++)
            {
                FP64 bodyDt = dt;
                if (_sweepAdvanced != null && i < _sweepAdvanced.Length && _sweepAdvanced[i] > FP64.Zero)
                    bodyDt = dt - _sweepAdvanced[i];
                FPPhysicsIntegration.Integrate(
                    ref bodies[i].rigidBody,
                    ref bodies[i].position,
                    ref bodies[i].rotation,
                    bodyDt);
            }
        }

        void ResolveSweepBodies(FPPhysicsBody[] bodies, int count, FP64 dt, FPCCDConfig config)
        {
            FP64 remainingDt = dt;

            for (int iter = 0; iter < config.maxSweepIterations && remainingDt > FP64.Epsilon; iter++)
            {
                FP64 minToi = remainingDt + FP64.One;
                int minPairIdx = -1;
                FPVector3 minNormal = FPVector3.Up;

                for (int p = 0; p < _sweepPairs.Count; p++)
                {
                    int idxA = _sweepPairs[p].Item1;
                    int idxB = _sweepPairs[p].Item2;

                    bool hit = DispatchSweep(ref bodies[idxA], ref bodies[idxB],
                        remainingDt, out FP64 toi, out FPVector3 normal);

                    if (hit && toi < minToi)
                    {
                        minToi = toi;
                        minPairIdx = p;
                        minNormal = normal;
                    }
                }

                if (minPairIdx < 0)
                    break;

                int a = _sweepPairs[minPairIdx].Item1;
                int b = _sweepPairs[minPairIdx].Item2;

                if (!bodies[a].rigidBody.isStatic && !bodies[a].rigidBody.isKinematic)
                {
                    bodies[a].position = bodies[a].position + bodies[a].rigidBody.velocity * minToi;
                    _sweepAdvanced[a] = _sweepAdvanced[a] + minToi;
                }
                if (!bodies[b].rigidBody.isStatic && !bodies[b].rigidBody.isKinematic)
                {
                    bodies[b].position = bodies[b].position + bodies[b].rigidBody.velocity * minToi;
                    _sweepAdvanced[b] = _sweepAdvanced[b] + minToi;
                }

                SyncColliderPosition(ref bodies[a]);
                SyncColliderPosition(ref bodies[b]);

                FPVector3 contactPoint;
                if (bodies[a].useSweep && bodies[a].collider.type == ShapeType.Sphere)
                    contactPoint = bodies[a].position + minNormal * bodies[a].collider.sphere.radius;
                else
                    contactPoint = bodies[b].position - minNormal * bodies[b].collider.sphere.radius;

                var toiContact = new FPContact(contactPoint, minNormal, FP64.Zero, a, b);
                FPCollisionResponse.ResolveContact(
                    ref bodies[a].rigidBody, ref bodies[a].position,
                    ref bodies[b].rigidBody, ref bodies[b].position,
                    in toiContact);

                remainingDt = remainingDt - minToi;

                _sweepPairs.RemoveAt(minPairIdx);
            }
        }

        static bool DispatchSweep(ref FPPhysicsBody bodyA, ref FPPhysicsBody bodyB,
            FP64 dt, out FP64 toi, out FPVector3 normal)
        {
            if (bodyA.useSweep && bodyA.collider.type == ShapeType.Sphere && !bodyA.rigidBody.isStatic)
                return DispatchSweepOrdered(ref bodyA, ref bodyB, dt, out toi, out normal);

            if (bodyB.useSweep && bodyB.collider.type == ShapeType.Sphere && !bodyB.rigidBody.isStatic)
            {
                bool hit = DispatchSweepOrdered(ref bodyB, ref bodyA, dt, out toi, out normal);
                if (hit) normal = -normal;
                return hit;
            }

            toi = FP64.Zero;
            normal = FPVector3.Up;
            return false;
        }

        static bool DispatchSweepOrdered(ref FPPhysicsBody sphere, ref FPPhysicsBody other,
            FP64 dt, out FP64 toi, out FPVector3 normal)
        {
            FPVector3 otherVel = (other.rigidBody.isStatic || other.rigidBody.isKinematic)
                ? FPVector3.Zero : other.rigidBody.velocity;
            FPVector3 relVel = sphere.rigidBody.velocity - otherVel;

            switch (other.collider.type)
            {
                case ShapeType.Sphere:
                    return FPSweepTests.SweptSphereSphere(
                        sphere.position, sphere.collider.sphere.radius, sphere.rigidBody.velocity,
                        other.position, other.collider.sphere.radius, otherVel,
                        dt, out toi, out normal);
                case ShapeType.Box:
                    return FPSweepTests.SweptSphereBox(
                        sphere.position, sphere.collider.sphere.radius, relVel,
                        ref other.collider.box, dt, out toi, out normal);
                case ShapeType.Capsule:
                    return FPSweepTests.SweptSphereCapsule(
                        sphere.position, sphere.collider.sphere.radius, relVel,
                        ref other.collider.capsule, dt, out toi, out normal);
                default:
                    toi = FP64.Zero;
                    normal = FPVector3.Up;
                    return false;
            }
        }

        static void SyncColliderPosition(ref FPPhysicsBody body)
        {
            FPVector3 worldPos = body.position + body.rotation * body.colliderOffset;
            switch (body.collider.type)
            {
                case ShapeType.Sphere:
                    body.collider.sphere.position = worldPos;
                    break;
                case ShapeType.Box:
                    body.collider.box.position = worldPos;
                    body.collider.box.rotation = body.rotation;
                    break;
                case ShapeType.Capsule:
                    body.collider.capsule.position = worldPos;
                    body.collider.capsule.rotation = body.rotation;
                    break;
                case ShapeType.Mesh:
                    body.collider.mesh.position = worldPos;
                    body.collider.mesh.rotation = body.rotation;
                    break;
            }
        }

        public void CopyContactsTo(FPContact[] buffer, out int count)
        {
            count = _contacts.Count;
            for (int i = 0; i < count; i++) buffer[i] = _contacts[i];
        }

        public void CopyStaticContactsTo(FPContact[] buffer, out int count)
        {
            count = _staticContacts.Count;
            for (int i = 0; i < count; i++) buffer[i] = _staticContacts[i];
        }

        public void CopyTriggerPairsTo((int, int)[] buffer, out int count)
        {
            count = _triggerPairs.Count;
            for (int i = 0; i < count; i++) buffer[i] = _triggerPairs[i];
        }

        public int GetSerializedSize() => _triggerSystem.GetSerializedSize();

        public void Serialize(ref SpanWriter writer)
        {
            _triggerSystem.Serialize(ref writer);
        }

        public void Deserialize(ref SpanReader reader)
        {
            _triggerSystem.Deserialize(ref reader);
        }

        public void Clear()
        {
            _grid.Clear();
            _triggerSystem.Clear();
            _broadPairs.Clear();
            _triggerPairs.Clear();
            _contacts.Clear();
            _sweepPairs.Clear();
        }

        static readonly FP64 MergeCosThreshold = FP64.FromDouble(0.8);

        void MergeStaticContacts()
        {
            for (int i = 0; i < _staticContacts.Count; i++)
            {
                FPContact ci = _staticContacts[i];
                for (int j = _staticContacts.Count - 1; j > i; j--)
                {
                    FPContact cj = _staticContacts[j];
                    if (ci.entityA != cj.entityA || ci.entityB != cj.entityB)
                        continue;

                    FP64 dot = FPVector3.Dot(ci.normal, cj.normal);
                    if (dot > MergeCosThreshold)
                    {
                        FPVector3 merged = ci.normal * ci.depth + cj.normal * cj.depth;
                        FP64 nLen = merged.magnitude;
                        FPVector3 mergedNormal = nLen > FP64.Epsilon ? merged / nLen : ci.normal;

                        FP64 mergedDepth = ci.depth >= cj.depth ? ci.depth : cj.depth;
                        FPVector3 mergedPoint = ci.depth >= cj.depth ? ci.point : cj.point;

                        _staticContacts[i] = new FPContact(mergedPoint, mergedNormal, mergedDepth,
                            ci.entityA, ci.entityB);
                        _staticContacts.RemoveAt(j);

                        ci = _staticContacts[i];
                    }
                }
            }
        }
    }
}
