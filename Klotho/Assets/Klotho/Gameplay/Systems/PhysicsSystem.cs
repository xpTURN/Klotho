using System;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Geometry;
using xpTURN.Klotho.Deterministic.Physics;
using xpTURN.Klotho.Serialization;
using System.Collections.Generic;

namespace xpTURN.Klotho.ECS.Systems
{
    /// <summary>
    /// ECS physics adapter system: without modifying FPPhysicsWorld,
    /// synchronize data between ECS Components and FPPhysicsBody[] and then call Step().
    ///
    /// 1. ECS(TransformComponent, VelocityComponent, PhysicsBodyComponent) → FPPhysicsBody[] copy
    /// 2. FPPhysicsWorld.Step() execution
    /// 3. FPPhysicsBody[] result → ECS Component reverse sync
    /// </summary>
    public class PhysicsSystem : ISystem, IFPPhysicsWorldProvider, IStaticColliderService, IPhysicsRayCaster, ISnapshotParticipant
    {
        private FPPhysicsWorld _world;
        private readonly FPVector3 _gravity;

        private FPPhysicsBody[] _bodies;
        private EntityRef[] _bodyEntities;
        private int _bodyCount;

        // contact/trigger snapshot buffers — copied immediately after Step()
        private FPContact[] _contactBuf       = new FPContact[256];
        private FPContact[] _staticContactBuf = new FPContact[256];
        private (int, int)[] _triggerBuf      = new (int, int)[64];
        private int _contactCount, _staticContactCount, _triggerCount;

        // Structured trigger callbacks — dynamic×static
        public Action<EntityRef, int> OnStaticTriggerEnter;
        public Action<EntityRef, int> OnStaticTriggerStay;
        public Action<EntityRef, int> OnStaticTriggerExit;

        // Structured trigger callbacks — dynamic×dynamic
        public Action<EntityRef, EntityRef> OnEntityTriggerEnter;
        public Action<EntityRef, EntityRef> OnEntityTriggerStay;
        public Action<EntityRef, EntityRef> OnEntityTriggerExit;

        public PhysicsSystem(int maxEntities, FPVector3 gravity)
        {
            _world = new FPPhysicsWorld(FP64.FromInt(10));
            _gravity = gravity;
            _bodies = new FPPhysicsBody[maxEntities];
            _bodyEntities = new EntityRef[maxEntities];
        }

        public PhysicsSystem(int maxEntities)
            : this(maxEntities, new FPVector3(FP64.Zero, FP64.FromInt(-10), FP64.Zero))
        {
        }

        public void SetSkipStaticGroundResponse(bool skip)
        {
            _world.SetSkipStaticGroundResponse(skip);
        }

        public void GetBodies(out FPPhysicsBody[] bodies, out int count)
        {
            bodies = _bodies;
            count  = _bodyCount;
        }

        public void GetStaticColliders(out FPStaticCollider[] colliders, out int count)
        {
            _world.GetStaticColliders(out colliders, out count);
        }

        public void GetContacts(out FPContact[] contacts, out int contactCount,
                                out FPContact[] staticContacts, out int staticContactCount)
        {
            contacts            = _contactBuf;
            contactCount        = _contactCount;
            staticContacts      = _staticContactBuf;
            staticContactCount  = _staticContactCount;
        }

        public void GetTriggerPairs(out (int, int)[] pairs, out int count)
        {
            pairs = _triggerBuf;
            count = _triggerCount;
        }

        public void LoadStaticColliders(string sceneKey, List<FPStaticCollider> colliders)
        {
            _world.LoadStaticColliders(sceneKey, colliders);
            RebuildStaticBVH();
        }

        public void LoadStaticColliders(string sceneKey, FPStaticCollider[] colliders, int count)
        {
            _world.LoadStaticColliders(sceneKey, colliders, count);
            RebuildStaticBVH();
        }

        public void UnloadStaticColliders(string sceneKey)
        {
            _world.UnloadStaticColliders(sceneKey);
            RebuildStaticBVH();
        }

        public void RebuildStaticBVH()
        {
            _world.RebuildStaticBVH(_bodies, _bodyCount);
        }

        public bool RayCastStatic(FPRay3 ray, FP64 maxDistance, out FPVector3 hitPoint, out FPVector3 hitNormal, out FP64 hitDistance)
            => _world.RayCastStatic(ray, _bodies, _bodyCount, maxDistance, out hitPoint, out hitNormal, out hitDistance, out _);

        public bool RayCastStatic(FPRay3 ray, FP64 maxDistance, out FPVector3 hitPoint, out FPVector3 hitNormal, out FP64 hitDistance, out int hitLeafIndex)
            => _world.RayCastStatic(ray, _bodies, _bodyCount, maxDistance, out hitPoint, out hitNormal, out hitDistance, out hitLeafIndex);

        // --- ISnapshotParticipant ---

        public int GetSnapshotSize() => _world.GetSerializedSize();
        public void SaveSnapshot(ref SpanWriter writer) => _world.Serialize(ref writer);
        public void RestoreSnapshot(ref SpanReader reader) => _world.Deserialize(ref reader);

        // --- ID lookup ---

        public bool TryGetEntityRef(int bodyId, out EntityRef entity)
        {
            for (int i = 0; i < _bodyCount; i++)
            {
                if (_bodies[i].id == bodyId)
                {
                    entity = _bodyEntities[i];
                    return true;
                }
            }
            entity = EntityRef.None;
            return false;
        }

        private bool IsStaticColliderId(int id)
        {
            _world.GetStaticColliders(out var colliders, out var count);
            for (int i = 0; i < count; i++)
            {
                if (colliders[i].id == id)
                    return true;
            }
            return false;
        }

        // --- raw callback → structured callback conversion ---
        // Since entity.Index and staticCollider.id can share the same value,
        // even when body lookup succeeds, cross-validate against the static side to classify correctly.

        private void ClassifyTriggerPair(int idA, int idB,
            out bool aIsBody, out EntityRef entityA,
            out bool bIsBody, out EntityRef entityB)
        {
            aIsBody = TryGetEntityRef(idA, out entityA);
            bIsBody = TryGetEntityRef(idB, out entityB);

            // Both sides recognized as body: possible ID collision — cross-check the static side
            if (aIsBody && bIsBody)
            {
                bool aIsStatic = IsStaticColliderId(idA);
                bool bIsStatic = IsStaticColliderId(idB);

                // If only one side is static, exclude that side from body
                if (aIsStatic && !bIsStatic) { aIsBody = false; entityA = EntityRef.None; }
                else if (bIsStatic && !aIsStatic) { bIsBody = false; entityB = EntityRef.None; }
                // If both are static or neither is, keep the original classification
            }
        }

        private void HandleRawTriggerEnter(int idA, int idB)
        {
            ClassifyTriggerPair(idA, idB, out bool aIsBody, out var entityA, out bool bIsBody, out var entityB);

            if (aIsBody && bIsBody)
                OnEntityTriggerEnter?.Invoke(entityA, entityB);
            else if (aIsBody && !bIsBody)
                OnStaticTriggerEnter?.Invoke(entityA, idB);
            else if (!aIsBody && bIsBody)
                OnStaticTriggerEnter?.Invoke(entityB, idA);
        }

        private void HandleRawTriggerStay(int idA, int idB)
        {
            ClassifyTriggerPair(idA, idB, out bool aIsBody, out var entityA, out bool bIsBody, out var entityB);

            if (aIsBody && bIsBody)
                OnEntityTriggerStay?.Invoke(entityA, entityB);
            else if (aIsBody && !bIsBody)
                OnStaticTriggerStay?.Invoke(entityA, idB);
            else if (!aIsBody && bIsBody)
                OnStaticTriggerStay?.Invoke(entityB, idA);
        }

        private void HandleRawTriggerExit(int idA, int idB)
        {
            ClassifyTriggerPair(idA, idB, out bool aIsBody, out var entityA, out bool bIsBody, out var entityB);

            if (aIsBody && bIsBody)
                OnEntityTriggerExit?.Invoke(entityA, entityB);
            else if (aIsBody && !bIsBody)
                OnStaticTriggerExit?.Invoke(entityA, idB);
            else if (!aIsBody && bIsBody)
                OnStaticTriggerExit?.Invoke(entityB, idA);
        }

        public void Update(ref Frame frame)
        {
            _bodyCount = 0;

            // 1. ECS → FPPhysicsBody[] sync
            var filter = frame.Filter<TransformComponent, PhysicsBodyComponent>();
            while (filter.Next(out var entity))
            {
                ref readonly var transform = ref frame.GetReadOnly<TransformComponent>(entity);
                ref readonly var physBody = ref frame.GetReadOnly<PhysicsBodyComponent>(entity);

                _bodies[_bodyCount].id = entity.Index;
                _bodies[_bodyCount].position = transform.Position;
                _bodies[_bodyCount].rotation = FPQuaternion.Euler(FPVector3.Up * transform.Rotation);
                _bodies[_bodyCount].rigidBody = physBody.RigidBody;
                _bodies[_bodyCount].collider = physBody.Collider;
                _bodies[_bodyCount].colliderOffset = physBody.ColliderOffset;
                _bodies[_bodyCount].isTrigger = false;
                _bodies[_bodyCount].useCCD = false;
                _bodies[_bodyCount].useSweep = false;

                // If a VelocityComponent exists, reflect it onto the RigidBody velocity
                if (frame.Has<VelocityComponent>(entity))
                {
                    ref readonly var velocity = ref frame.GetReadOnly<VelocityComponent>(entity);
                    _bodies[_bodyCount].rigidBody.velocity = velocity.Velocity;
                }

                _bodyEntities[_bodyCount] = entity;
                _bodyCount++;
            }

            if (_bodyCount == 0)
                return;

            // 2. Physics simulation
            FP64 dt = FP64.FromInt(frame.DeltaTimeMs) / FP64.FromInt(1000);
            _world.Step(_bodies, _bodyCount, dt, _gravity,
                HandleRawTriggerEnter, HandleRawTriggerStay, HandleRawTriggerExit);

            // 2-b. Snapshot copy for the visualizer (immediately after Step)
            _world.CopyContactsTo(_contactBuf, out _contactCount);
            _world.CopyStaticContactsTo(_staticContactBuf, out _staticContactCount);
            _world.CopyTriggerPairsTo(_triggerBuf, out _triggerCount);

            // 3. FPPhysicsBody[] → ECS Component reverse sync
            for (int i = 0; i < _bodyCount; i++)
            {
                var entity = _bodyEntities[i];

                ref var transform = ref frame.Get<TransformComponent>(entity);
                transform.Position = _bodies[i].position;

                ref var physBody = ref frame.Get<PhysicsBodyComponent>(entity);
                physBody.RigidBody = _bodies[i].rigidBody;
                physBody.Collider = _bodies[i].collider;

                if (frame.Has<VelocityComponent>(entity))
                {
                    ref var velocity = ref frame.Get<VelocityComponent>(entity);
                    velocity.Velocity = _bodies[i].rigidBody.velocity;
                }
            }
        }
    }
}
