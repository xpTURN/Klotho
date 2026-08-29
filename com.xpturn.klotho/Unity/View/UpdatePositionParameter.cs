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

        /// <summary>
        /// Tick-quantized position before interpolation — the root transform's value under
        /// <c>_interpolationTarget</c>, and the snap target of the CSP teleport branch.
        ///
        /// Which tick it comes from depends on the render path: the CSP path uses the live Predicted
        /// frame, the snapshot path uses the render window's alpha=0 endpoint (a Verified snapshot, at
        /// most one tick behind the interpolated child). For a verified-rendered entity the live pose is
        /// a prediction — the one thing that path exists to avoid — so it is not used there (IMP103 D-2).
        /// </summary>
        public Vector3 UninterpolatedPosition;

        /// <summary>Tick-quantized rotation before interpolation. Same per-path sourcing as <see cref="UninterpolatedPosition"/>.</summary>
        public Quaternion UninterpolatedRotation;

        // DeltaTime (Unity Time.deltaTime) was removed here. Nothing read it, and unlike the fields above
        // it was never an extension point worth keeping: the value is a global static, so an ApplyTransform
        // override reads Time.deltaTime directly rather than being handed it (IMP103 D-4). The Godot adapter
        // has no counterpart to this struct at all — IMP52 decided the parameter/ApplyTransform split is
        // unnecessary there because it applies the transform inline.

        /// <summary>
        /// Whether the render interval this frame sits on crosses a teleport, so the view must jump rather
        /// than interpolate. The source is per path, and none of them is <c>HasEntityTeleported</c> alone:
        /// <list type="bullet">
        /// <item>CSP: the game's own stamp — <c>TransformComponent.TeleportTick == CurrentTick - 1</c>
        /// (0 means "never teleported", so it never counts), OR-ed with
        /// <see cref="Core.IKlothoEngine.HasEntityTeleported"/> only when error correction is on and a
        /// rollback ran this frame — that one means "re-simulation introduced a teleport".</item>
        /// <item>Snapshot: the interpolator's verdict, i.e. the two render-window endpoints carry different
        /// <c>TeleportTick</c> values. A window with only one endpoint occupied reports false.</item>
        /// </list>
        /// <see cref="EntityView.ApplyTransform"/> acts on it for CSP views only; the snapshot path puts the
        /// jump on the interpolation boundary instead (IMP103 V-5).
        /// </summary>
        public bool Teleported;
    }
}
