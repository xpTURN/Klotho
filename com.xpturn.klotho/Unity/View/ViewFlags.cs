using System;

namespace xpTURN.Klotho
{
    /// <summary>
    /// Flags that control view behavior.
    ///
    /// Authoring is split. The prefab Inspector owns every flag except the ones a Factory claims through
    /// <c>EntityViewFactory.FactoryOwnedFlags</c>; on spawn the two are merged rather than one replacing
    /// the other (<c>EntityViewFactory.ComposeViewFlags</c>). So a flag ticked on the prefab survives
    /// unless the Factory owns that bit, and a Factory-owned bit ignores whatever the prefab said.
    /// </summary>
    [Flags]
    public enum ViewFlags
    {
        None                          = 0,
        /// <summary>Skips InternalUpdateView / InternalLateUpdateView entirely.</summary>
        DisableUpdate                 = 1 << 0,
        /// <summary>Skips position update inside ApplyTransform (e.g. when only rotation should be applied).</summary>
        DisablePositionUpdate         = 1 << 1,
        // 1 << 2 is deliberately vacant. UseCachedTransform lived here: it promised to "skip per-tick
        // recalculation", but the transform pipeline moved from per-tick (InternalUpdateView) to
        // per-frame (InternalLateUpdateView), so there has been no per-tick recalculation to skip and
        // nothing ever read the flag. Renumbering the flags below to close the gap would silently change
        // what an existing asset means — _viewFlags is serialized as an int on the prefab.
        /// <summary>
        /// Uses VerifiedFrame-based snapshot interpolation (VerifiedFrameInterpolator path).
        /// <b>Factory-owned by default</b> — it follows from network mode and local ownership, which the
        /// asset cannot know, so ticking it in the Inspector has no effect. Forcing it on a locally-owned
        /// entity would render the local player several ticks late; that is why the Factory keeps it.
        /// </summary>
        EnableSnapshotInterpolation   = 1 << 3,
    }
}
