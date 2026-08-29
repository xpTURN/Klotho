// BindBehaviour / ViewFlags for the Godot view layer.
using System;

namespace xpTURN.Klotho.Godot
{
    // Whether a view tracks the Verified frame or the (predicted) NonVerified frame.
    public enum BindBehaviour
    {
        NonVerified,
        Verified,
    }

    [Flags]
    public enum ViewFlags
    {
        None                          = 0,
        DisableUpdate                 = 1 << 0,
        DisablePositionUpdate         = 1 << 1,
        // 1 << 2 is deliberately vacant. UseCachedTransform lived here: it promised to "skip per-tick
        // recalculation", but IMP46-E moved the transform pipeline to per-frame, so there has been no
        // per-tick recalculation to skip and nothing ever read the flag. Renumbering to close the gap
        // would silently change what an existing asset means (IMP103 D-3).
        EnableSnapshotInterpolation   = 1 << 3,
    }
}
