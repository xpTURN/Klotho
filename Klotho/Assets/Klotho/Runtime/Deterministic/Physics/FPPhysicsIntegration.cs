using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Physics integrator that integrates velocity and force into position and rotation.
    /// </summary>
    public static class FPPhysicsIntegration
    {
        public static void Integrate(
            ref FPRigidBody body,
            ref FPVector3 position,
            ref FPQuaternion rotation,
            FP64 dt)
        {
            if (body.isStatic || body.isKinematic)
                return;

            // --- Linear: semi-implicit Euler ---
            FPVector3 linearAccel = body.force * body.inverseMass;
            body.velocity = body.velocity + linearAccel * dt;
            position = position + body.velocity * dt;
            body.velocity = body.velocity * (FP64.One - body.linearDamping * dt);

            // --- Angular velocity: semi-implicit Euler ---
            FPVector3 angularAccel = body.torque * body.inverseMass;
            body.angularVelocity = body.angularVelocity + angularAccel * dt;

            FP64 angSpeed = body.angularVelocity.magnitude;
            if (angSpeed > FP64.Zero)
            {
                FPVector3 axis = body.angularVelocity / angSpeed;
                FP64 angleDeg = angSpeed * dt * FP64.Rad2Deg;
                FPQuaternion deltaRot = FPQuaternion.AngleAxis(angleDeg, axis);
                rotation = (deltaRot * rotation).normalized;
            }

            body.angularVelocity = body.angularVelocity * (FP64.One - body.angularDamping * dt);

            body.ClearForces();
        }
    }
}
