using System;
using xpTURN.Klotho.Deterministic.Math;

namespace xpTURN.Klotho.Deterministic.Physics
{
    /// <summary>
    /// Continuous collision detection (CCD) configuration. Defines enable flag, velocity threshold, and maximum sweep iterations.
    /// </summary>
    [Serializable]
    public struct FPCCDConfig
    {
        public bool enabled;
        public FP64 velocityThreshold;
        public int maxSweepIterations;
    }
}
