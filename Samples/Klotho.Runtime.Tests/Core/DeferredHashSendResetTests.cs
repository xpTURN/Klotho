using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

using xpTURN.Klotho.Logging;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Helper.Tests;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// ResetTransientSyncStateAfterFullState must clear the deferred-hash-send
    /// set so a FullState apply does not leave orphan check-tick entries.
    ///
    /// Without the clear, a backward FullState jump leaves entries the chain re-crosses on forward
    /// re-prediction (duplicate sync-hash send, caller 2) or below the new verified floor (memory
    /// leak, caller 1 = host corrective-reset self-apply). The sent value is always fresh (prediction
    /// strictly precedes verification), so the harm is duplicate-send / orphan-leak, not a stale send.
    /// This pins the fix directly: after the reset the set is empty regardless of seeded ticks.
    /// </summary>
    [TestFixture]
    public class DeferredHashSendResetTests
    {
        private static readonly FieldInfo DeferredHashSendTicksField = typeof(KlothoEngine)
            .GetField("_deferredHashSendTicks", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LastVerifiedTickField = typeof(KlothoEngine)
            .GetField("_lastVerifiedTick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo ResetTransientMethod = typeof(KlothoEngine)
            .GetMethod("ResetTransientSyncStateAfterFullState", BindingFlags.NonPublic | BindingFlags.Instance);

        private KlothoEngine _engine;
        private IKLogger _logger;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning));
            _logger = factory.CreateLogger("DeferredHashSendResetTests");
        }

        [SetUp]
        public void SetUp()
        {
            _engine = new KlothoEngine(
                new SimulationConfig { InputDelayTicks = 4, SyncCheckInterval = 10, MaxRollbackTicks = 50 },
                new SessionConfig());
            // Network-less init — ResetTransientSyncStateAfterFullState's only network call
            // (InvalidateSyncHashes) is null-conditional, so no service is needed.
            _engine.Initialize(new TestSimulation { UseDeterministicHash = true }, _logger);
        }

        private HashSet<int> DeferredSet => (HashSet<int>)DeferredHashSendTicksField.GetValue(_engine);
        private void InvokeReset(int tick) => ResetTransientMethod.Invoke(_engine, new object[] { tick });

        /// <summary>
        /// backward-jump shape (tick &lt; old head): the seeded set mixes entries at/above
        /// the apply tick (would re-cross → duplicate) and below it (would leak). The reset clears all.
        /// pre-fix witness: without the `.Clear()` line the set retains every entry.
        /// </summary>
        [Test]
        public void Reset_ClearsDeferredHashSendTicks_BackwardJumpMix()
        {
            var set = DeferredSet;
            set.Clear();
            set.Add(10); // below apply tick 20 → orphan leak
            set.Add(20); // at apply tick   → re-crossed (duplicate)
            set.Add(30); // above apply tick → re-crossed (duplicate)
            LastVerifiedTickField.SetValue(_engine, 40);

            InvokeReset(20);

            Assert.AreEqual(0, DeferredSet.Count,
                "ResetTransientSyncStateAfterFullState must clear _deferredHashSendTicks (D-F10).");
        }

        /// <summary>
        /// host corrective-reset self-apply shape (tick == CurrentTick, no retreat): all
        /// staged entries are below the new verified floor (tick-1) → never re-crossed → pure leak.
        /// The reset clears them.
        /// </summary>
        [Test]
        public void Reset_ClearsDeferredHashSendTicks_SelfApplyBelowFloor()
        {
            var set = DeferredSet;
            set.Clear();
            set.Add(50);
            set.Add(60);
            LastVerifiedTickField.SetValue(_engine, 70);

            InvokeReset(70); // self-apply at CurrentTick == 70

            Assert.AreEqual(0, DeferredSet.Count,
                "Self-apply reset must clear below-floor orphan entries (D-F10 caller 1).");
        }
    }
}
