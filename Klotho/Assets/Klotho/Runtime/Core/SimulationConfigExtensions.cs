using System;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// <see cref="ISimulationConfig"/> extension helpers.
    /// Centralizes default-value resolution for 0 (auto) values.
    /// </summary>
    public static class SimulationConfigExtensions
    {
        /// <summary>
        /// Default value returned when <see cref="ISimulationConfig.SDInputLeadTicks"/> is 0.
        /// </summary>
        public const int SDInputLeadTicksDefault = 10;

        /// <summary>
        /// Returns the effective input-lead tick count, substituting <see cref="SDInputLeadTicksDefault"/> when the configured value is 0.
        /// </summary>
        public static int GetEffectiveSDInputLeadTicks(this ISimulationConfig config)
            => config.SDInputLeadTicks > 0 ? config.SDInputLeadTicks : SDInputLeadTicksDefault;

        /// <summary>
        /// Validates SimulationConfig for substantively-broken conditions; throws on violation.
        /// Called during Initialize to catch config misconfiguration early.
        /// </summary>
        public static void Validate(this ISimulationConfig config)
        {
            if (config.TickIntervalMs <= 0)
                throw new ArgumentException("TickIntervalMs must be > 0");

            if (config.MaxRollbackTicks <= 0)
                throw new ArgumentException("MaxRollbackTicks must be > 0");

            if (config.SyncCheckInterval <= 0 || config.SyncCheckInterval > config.MaxRollbackTicks)
                throw new ArgumentException(
                    "SyncCheckInterval must be in [1, MaxRollbackTicks]");

            if (config.InputDelayTicks < 0)
                throw new ArgumentException("InputDelayTicks must be >= 0");

            if (config.MaxEntities <= 0)
                throw new ArgumentException("MaxEntities must be > 0");

            if (config.InterpolationDelayTicks < 1 || config.InterpolationDelayTicks > 3)
                throw new ArgumentException(
                    $"InterpolationDelayTicks must be in [1, 3] (got {config.InterpolationDelayTicks})");

            // SD only
            if (config.Mode == NetworkMode.ServerDriven)
            {
                // Structural past-tick race in SD mode requires at least 1 input-delay tick.
                if (config.InputDelayTicks < 1)
                    throw new ArgumentException(
                        $"InputDelayTicks={config.InputDelayTicks} is unsafe in ServerDriven mode (must be >= 1)");

                if (config.SDInputLeadTicks < 0)
                    throw new ArgumentException("SDInputLeadTicks must be >= 0");

                if (config.HardToleranceMs < 0)
                    throw new ArgumentException("HardToleranceMs must be >= 0");

                if (config.InputResendIntervalMs <= 0)
                    throw new ArgumentException("InputResendIntervalMs must be > 0");

                if (config.MaxUnackedInputs <= 0)
                    throw new ArgumentException("MaxUnackedInputs must be > 0");

                if (config.ServerSnapshotRetentionTicks < 0)
                    throw new ArgumentException("ServerSnapshotRetentionTicks must be >= 0");
            }
        }

        /// <summary>
        /// Non-throwing variant of <see cref="Validate"/>. Returns false with the error message on violation.
        /// Use from non-authoritative callers (SD client / P2P guest) that should log and proceed instead of failing.
        /// </summary>
        public static bool TryValidate(this ISimulationConfig config, out string error)
        {
            try { config.Validate(); error = null; return true; }
            catch (ArgumentException ex) { error = ex.Message; return false; }
        }
    }
}
