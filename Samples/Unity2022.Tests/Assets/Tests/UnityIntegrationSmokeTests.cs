using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Logging;
using xpTURN.Klotho.Unity;

namespace xpTURN.Klotho.Unity2022.Tests
{
    /// <summary>
    /// Unity 2022.3 LTS compatibility smoke for the engine-facing adapters — the parts that only
    /// exist inside Unity and therefore cannot be covered by the dotnet suites.
    /// </summary>
    [TestFixture]
    public class UnityIntegrationSmokeTests
    {
        // The logging extensions are built on interpolated string handlers
        // ([InterpolatedStringHandlerArgument]), a C# 10 feature the 2022.3 editor does not enable on
        // its own — it works only because the Polyfill package raises the language version and supplies
        // the attributes. If that wiring regresses, this file stops compiling; if the handler's gate
        // regresses, the expected log never arrives. Both are 2022-specific failure modes.
        [Test]
        public void Logging_InterpolatedHandlerReachesUnityDebugSink()
        {
            var factory = KLoggerFactory.Create(logging => logging
                .SetMinimumLevel(KLogLevel.Trace)
                .AddUnityDebug());
            var logger = factory.CreateLogger("Unity2022Smoke");

            LogAssert.Expect(LogType.Log, "smoke information 42");
            logger.KInformation($"smoke information {42}");

            LogAssert.Expect(LogType.Warning, "smoke warning 7");
            logger.KWarning($"smoke warning {7}");

            factory.Dispose();
        }

        [Test]
        public void Logging_LevelGateSuppressesBelowMinimum()
        {
            var factory = KLoggerFactory.Create(logging => logging
                .SetMinimumLevel(KLogLevel.Warning)
                .AddUnityDebug());
            var logger = factory.CreateLogger("Unity2022Smoke");

            // Below the floor: must not reach UnityEngine.Debug at all (LogAssert.NoUnexpectedReceived
            // at teardown would otherwise flag it).
            logger.KInformation($"suppressed {0}");
            Assert.IsFalse(logger.IsEnabled(KLogLevel.Information));
            Assert.IsTrue(logger.IsEnabled(KLogLevel.Warning));

            factory.Dispose();
        }

        [Test]
        public void FPVector3_ConvertsToAndFromUnityVector3()
        {
            var source = new Vector3(1.5f, -2.25f, 3.75f);
            var fp = source.ToFPVector3();

            // 1.5 / -2.25 / 3.75 are exact in both float and 32.32 fixed point, so this is exact.
            Assert.AreEqual(source, fp.ToVector3());
            Assert.AreEqual(FP64.FromDouble(1.5).RawValue, fp.x.RawValue);
            Assert.AreEqual(FP64.FromDouble(-2.25).RawValue, fp.y.RawValue);
            Assert.AreEqual(FP64.FromDouble(3.75).RawValue, fp.z.RawValue);
        }

        [Test]
        public void FPQuaternion_ConvertsToAndFromUnityQuaternion()
        {
            Assert.AreEqual(Quaternion.identity, FPQuaternion.Identity.ToQuaternion());

            var roundTripped = Quaternion.identity.ToFPQuaternion().ToQuaternion();
            Assert.AreEqual(Quaternion.identity, roundTripped);
        }

        // USimulationConfig exposes its inspector surface as [field: SerializeField] auto-properties.
        // Unity's serializer only picks those up through the compiler-generated backing field, so this
        // asserts the 2022.3 serializer really sees them (and that [field: Header] targets the field
        // rather than the property).
        [Test]
        public void USimulationConfig_AutoPropertyBackingFieldsAreSerialized()
        {
            var config = ScriptableObject.CreateInstance<USimulationConfig>();
            try
            {
                Assert.AreEqual(25, config.TickIntervalMs, "default TickIntervalMs");
                Assert.AreEqual(256, config.MaxEntities, "default MaxEntities");

                var serialized = new UnityEditor.SerializedObject(config);
                Assert.IsNotNull(serialized.FindProperty("<TickIntervalMs>k__BackingField"),
                    "[field: SerializeField] auto-property is not serialized by this editor");
                Assert.IsNotNull(serialized.FindProperty("<Mode>k__BackingField"),
                    "[field: SerializeField] enum auto-property is not serialized by this editor");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void KlothoSessionDriver_AttachesAsMonoBehaviour()
        {
            var host = new GameObject("Unity2022SmokeDriver");
            try
            {
                var driver = host.AddComponent<KlothoSessionDriver>();

                Assert.IsNotNull(driver);
                Assert.IsNull(driver.Session, "a fresh driver holds no session");

                // Idle teardown must be safe with nothing attached (OnDestroy runs DetachAndStop).
                Assert.DoesNotThrow(() => driver.DetachAndStop());
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
