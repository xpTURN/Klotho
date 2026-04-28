using UnityEngine;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Bundle of interpolated transform parameters passed to EntityView.ApplyTransform.
    /// Values are calculated and populated inside EntityView.InternalUpdateView.
    /// </summary>
    public struct UpdatePositionParameter
    {
        /// <summary>Interpolated current position (ready to use for rendering).</summary>
        public Vector3 NewPosition;

        /// <summary>Interpolated current rotation.</summary>
        public Quaternion NewRotation;

        /// <summary>Position offset caused by rollback. Intermediate value damped by ErrorVisualState.</summary>
        public Vector3 ErrorVisualVector;

        /// <summary>Rotation-axis rollback offset.</summary>
        public Quaternion ErrorVisualQuaternion;

        /// <summary>Pure current position before interpolation, used for snap on Teleport.</summary>
        public Vector3 UninterpolatedPosition;

        /// <summary>Pure current rotation before interpolation, used for snap on Teleport.</summary>
        public Quaternion UninterpolatedRotation;

        /// <summary>Unity Time.deltaTime.</summary>
        public float DeltaTime;

        /// <summary>Engine-confirmed teleport on this frame (<see cref="Core.IKlothoEngine.HasEntityTeleported"/>).</summary>
        public bool Teleported;
    }
}
