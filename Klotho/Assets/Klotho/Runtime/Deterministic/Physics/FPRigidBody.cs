using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Fixed-point rigid body properties. Defines mass, velocity, damping, restitution, and similar attributes.
    /// </summary>
    [Serializable]
    public struct FPRigidBody : IEquatable<FPRigidBody>
    {
        public FP64 mass;
        public FP64 inverseMass;

        public FPVector3 velocity;
        public FPVector3 force;

        public FPVector3 angularVelocity;
        public FPVector3 torque;

        public FP64 linearDamping;
        public FP64 angularDamping;

        public FP64 restitution;
        public FP64 friction;

        public bool isKinematic;
        public bool isStatic;

        public static FPRigidBody CreateDynamic(FP64 mass)
        {
            var body = new FPRigidBody();
            body.mass = mass;
            body.inverseMass = FP64.One / mass;
            return body;
        }

        public static FPRigidBody CreateStatic()
        {
            var body = new FPRigidBody();
            body.isStatic = true;
            return body;
        }

        public static FPRigidBody CreateKinematic()
        {
            var body = new FPRigidBody();
            body.isKinematic = true;
            return body;
        }

        public void AddForce(FPVector3 f)
        {
            force = force + f;
        }

        public void AddTorque(FPVector3 t)
        {
            torque = torque + t;
        }

        public void ClearForces()
        {
            force = FPVector3.Zero;
            torque = FPVector3.Zero;
        }

        public bool Equals(FPRigidBody other)
        {
            return mass == other.mass
                && inverseMass == other.inverseMass
                && velocity == other.velocity
                && force == other.force
                && angularVelocity == other.angularVelocity
                && torque == other.torque
                && linearDamping == other.linearDamping
                && angularDamping == other.angularDamping
                && restitution == other.restitution
                && friction == other.friction
                && isKinematic == other.isKinematic
                && isStatic == other.isStatic;
        }

        public override bool Equals(object obj) => obj is FPRigidBody other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = mass.GetHashCode();
                hash = hash * 397 ^ inverseMass.GetHashCode();
                hash = hash * 397 ^ velocity.GetHashCode();
                hash = hash * 397 ^ force.GetHashCode();
                hash = hash * 397 ^ angularVelocity.GetHashCode();
                hash = hash * 397 ^ torque.GetHashCode();
                hash = hash * 397 ^ linearDamping.GetHashCode();
                hash = hash * 397 ^ angularDamping.GetHashCode();
                hash = hash * 397 ^ restitution.GetHashCode();
                hash = hash * 397 ^ friction.GetHashCode();
                hash = hash * 397 ^ isKinematic.GetHashCode();
                hash = hash * 397 ^ isStatic.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(FPRigidBody a, FPRigidBody b) => a.Equals(b);
        public static bool operator !=(FPRigidBody a, FPRigidBody b) => !a.Equals(b);

        public override string ToString()
        {
            return $"FPRigidBody(mass:{mass}, vel:{velocity}, angVel:{angularVelocity}, e:{restitution}, f:{friction}, static:{isStatic}, kinematic:{isKinematic})";
        }
    }
}
