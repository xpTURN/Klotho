using System;
using NUnit.Framework;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// A P2P host validating its own SimulationConfig.
    ///
    /// KlothoEngine.Initialize decides whether a caller is authoritative by reading
    /// IKlothoNetworkService.IsHost — but for a P2P host that flag only becomes true in
    /// HostGame -> CreateRoom, which runs after Initialize. The host therefore took the guest's
    /// log-and-proceed branch and shipped an invalid config to every guest that joined it.
    /// The fix validates on the host entry point, which knows it is hosting without asking.
    /// </summary>
    public class HostConfigValidationTests
    {
        /// <summary>
        /// Runs the field-range validation — the `ISimulationConfig` extension — and nothing else.
        ///
        /// ⚠️ The receiver type decides which method this is. `SimulationConfig` also declares an INSTANCE
        /// `Validate(IKLogger = null, bool throwOnError = false)`, which checks a different thing
        /// (ReactiveMax against the rollback budget) and defaults to clamp-and-warn rather than throw. An
        /// instance method beats an extension, so calling `.Validate()` on the concrete type silently gets
        /// that one — which is what the first version of these tests did, making them pass vacuously
        /// (2026-08-27). The framework's four call sites all hold `ISimulationConfig`, so only tests are
        /// exposed; this helper is the guard against writing it the wrong way again.
        /// </summary>
        static void RangeCheck(ISimulationConfig config) => config.Validate();

        static KlothoSessionFlow NewFlow(Action onCallbacksBuilt = null)
            => new KlothoSessionFlow(new KlothoFlowSetup
            {
                CallbacksFactory = (_, __) => { onCallbacksBuilt?.Invoke(); return default; }
            });

        [Test]
        public void StartHost_RejectsInvalidConfig()
        {
            // InterpolationDelayTicks is the field this review moved, and 0 is out of its [1,3] range.
            var cfg = new SimulationConfig { InterpolationDelayTicks = 0 };

            var ex = Assert.Throws<ArgumentException>(
                () => NewFlow().StartHost(cfg, new SessionConfig()),
                "a host must not start on a config it would then hand to every guest");
            Assert.That(ex.Message, Does.Contain("InterpolationDelayTicks"),
                "and the message has to name the field, or the host operator cannot act on it");
        }

        [Test]
        public void StartHost_ValidatesBeforeBuildingAnything()
        {
            bool built = false;
            var cfg = new SimulationConfig { InterpolationDelayTicks = 0 };

            Assert.Throws<ArgumentException>(() => NewFlow(() => built = true).StartHost(cfg, new SessionConfig()));
            Assert.IsFalse(built,
                "the check is only worth having if it fires before a session is half-constructed");
        }

        /// <summary>
        /// The upper bound moved 3 → 4. The frame-time budget is
        /// `(delay − 2) × tickMs`, so a 16 ms tick — which `SimulationConfigGuide` §4.2 recommends for
        /// 60 Hz fighting/action, and which both SD sample servers ship — cannot cover a 16.67 ms frame
        /// at 3. The old cap made that configuration unreachable, and the guide's own §4.1/§4.3
        /// adjustments ("+1", "+1–2" on a base of 3) named values its validation rejected.
        /// </summary>
        [Test]
        public void InterpolationDelay_AcceptsFour_AndStillRejectsFive()
        {
            Assert.DoesNotThrow(() => RangeCheck(new SimulationConfig { InterpolationDelayTicks = 4 }),
                "4 is what a 16ms tick needs; rejecting it made the shipped SD sample configs unsatisfiable");

            var ex = Assert.Throws<ArgumentException>(
                () => RangeCheck(new SimulationConfig { InterpolationDelayTicks = 5 }),
                "the bound moved, it did not go away — 5 is presentation latency nothing in the guide asks for");
            Assert.That(ex.Message, Does.Contain("[1, 4]"), "the message has to state the range that now applies");
        }

        [Test]
        public void ShippedHostConfigs_StillPass()
        {
            // The Godot P2P sample's host config. InputDelayTicks=0 is legal outside ServerDriven mode
            // (only the SD block requires >= 1), so adding the host check is a no-op for the samples —
            // the reason this fix needed no migration.
            var godotP2p = new SimulationConfig { EnableErrorCorrection = true, InputDelayTicks = 0 };
            Assert.AreNotEqual(NetworkMode.ServerDriven, godotP2p.Mode, "precondition: the SD-only rules are skipped");
            Assert.DoesNotThrow(() => RangeCheck(godotP2p));

            Assert.DoesNotThrow(() => RangeCheck(new SimulationConfig()), "defaults must be startable");
        }
    }
}
