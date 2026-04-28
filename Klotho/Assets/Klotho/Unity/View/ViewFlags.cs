using System;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Flags that control view behavior. The prefab Inspector values are the defaults; Factory can override them at runtime.
    /// </summary>
    [Flags]
    public enum ViewFlags
    {
        None                          = 0,
        /// <summary>Skips InternalUpdateView / InternalLateUpdateView entirely.</summary>
        DisableUpdate                 = 1 << 0,
        /// <summary>Skips position update inside ApplyTransform (e.g. when only rotation should be applied).</summary>
        DisablePositionUpdate         = 1 << 1,
        /// <summary>Uses cached transform values. Skips per-tick recalculation to reduce cost.</summary>
        UseCachedTransform            = 1 << 2,
        /// <summary>Uses VerifiedFrame-based snapshot interpolation (VerifiedFrameInterpolator path).</summary>
        EnableSnapshotInterpolation   = 1 << 3,
    }
}
