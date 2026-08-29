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
        /// Clamped below <see cref="ISimulationConfig.MaxRollbackTicks"/>: the SD client's hard
        /// prediction throttle equals MaxRollbackTicks (snapshot-ring bound), so a target lead at or
        /// above it would park the client at the throttle before ever reaching its target.
        /// </summary>
        public static int GetEffectiveSDInputLeadTicks(this ISimulationConfig config)
        {
            int lead = config.SDInputLeadTicks > 0 ? config.SDInputLeadTicks : SDInputLeadTicksDefault;
            int max = config.MaxRollbackTicks - 1;
            return lead < max ? lead : max;
        }

        /// <summary>
        /// Snapshot/event ring capacity: MaxRollbackTicks + 2 headroom (one-tick prediction lead
        /// and event-diff timing). Single source for the engine snapshot ring, the EventBuffer,
        /// and the ECS frame ring.
        /// </summary>
        public static int GetSnapshotCapacity(this ISimulationConfig config)
            => config.MaxRollbackTicks + 2;

        /// <summary>
        /// Effective sync-check interval, clamped to MaxRollbackTicks / 2: the last matched anchor
        /// can be up to 2×interval − 1 ticks old at desync time, so 2×interval ≤ MaxRollbackTicks
        /// must hold for the first rung-1 rollback to stay inside the rollback window.
        /// Clamp instead of hard-throw validation: 30/50 ships in serialized assets and user
        /// projects, and tests use degenerate 1/1 configs — a throw would break them all.
        /// </summary>
        public static int GetEffectiveSyncCheckInterval(this ISimulationConfig config)
        {
            int max = Math.Max(1, config.MaxRollbackTicks / 2);
            return config.SyncCheckInterval < max ? config.SyncCheckInterval : max;
        }

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

            // Upper bound raised 3 -> 4 (IMP103). The sawtooth the render clock rides has amplitude one
            // tick plus one render frame, and the slack that absorbs it is (delay - 1) ticks, so the
            // condition is frameMs <= (delay - 2) * tickMs. At a 16ms tick that needs 4 to cover a 60fps
            // frame at all — and 3 was the cap, which is why every 16ms config in this repo and the
            // console/global rows of SimulationConfigGuide were unsatisfiable. The guide also instructed
            // "+1" on a base of 3, i.e. a value its own validation rejected.
            if (config.InterpolationDelayTicks < 1 || config.InterpolationDelayTicks > 4)
                throw new ArgumentException(
                    $"InterpolationDelayTicks must be in [1, 4] (got {config.InterpolationDelayTicks})");

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
