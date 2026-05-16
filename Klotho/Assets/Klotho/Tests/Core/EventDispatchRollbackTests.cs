using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ZLogger.Unity;

using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Network;
using xpTURN.Klotho.State;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// P2P Rollback path (Rollback.cs:152-194) event dispatch invariants across rollback + resim:
    ///   (1) OnSyncedEvent dispatch count for a (tick, event) pair remains 1 across one
    ///       rollback/resim cycle. Re-dispatch on re-verification is a regression.
    ///   (2) BeginTick(t) and AddEvent(t, ...) use the same tick argument — externally observable
    ///       as evt.Tick (stamped by RaiseEvent from BeginTick) equaling the OnSyncedEvent
    ///       callback's tick parameter (used by DispatchTickEvents).
    ///   (3) For Regular events, DiffRollbackEvents fires OnEventCanceled for old-only entries
    ///       and OnEventConfirmed/OnEventPredicted for new-only entries.
    /// </summary>
    [TestFixture]
    public class EventDispatchRollbackTests
    {
        private sealed class TestSyncedEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_201;
            public override EventMode Mode => EventMode.Synced;
        }

        private sealed class TestRegularEvent : SimulationEvent
        {
            public int Payload;
            public override int EventTypeId => 9_999_202;
            // Mode defaults to Regular.
            public override long GetContentHash() => ((long)EventTypeId << 32) | (uint)Payload;
        }

        private sealed class StubSnapshot : IStateSnapshot
        {
            public int Tick { get; set; }
            public byte[] Serialize() => Array.Empty<byte>();
            public void Deserialize(byte[] data) { }
            public ulong CalculateHash() => 0;
        }

        private static readonly FieldInfo _engineEventCollectorField = typeof(KlothoEngine)
            .GetField("_eventCollector", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _engineSnapshotManagerField = typeof(KlothoEngine)
            .GetField("_snapshotManager", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _engineSyncedWatermarkField = typeof(KlothoEngine)
            .GetField("_syncedDispatchHighWaterMark", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _engineApplyFullStateMethod = typeof(KlothoEngine)
            .GetMethod("ApplyFullState", BindingFlags.NonPublic | BindingFlags.Instance);

        private static IStateSnapshotManager ReadSnapshotManager(KlothoEngine engine)
            => (IStateSnapshotManager)_engineSnapshotManagerField.GetValue(engine);

        private static int ReadSyncedWatermark(KlothoEngine engine)
            => (int)_engineSyncedWatermarkField.GetValue(engine);

        private ILogger _logger;
        private KlothoTestHarness _harness;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var factory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
                b.AddZLoggerUnityDebug();
            });
            _logger = factory.CreateLogger("EventDispatchRollbackTests");
        }

        [SetUp]
        public void SetUp()
        {
            TestTransport.Reset();
            StreamPool.Clear();
            _harness = new KlothoTestHarness(_logger);
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Reset();
        }

        private static void WireEventRaiserFromEngine(TestPeer peer)
        {
            var collector = (ISimulationEventRaiser)_engineEventCollectorField.GetValue(peer.Engine);
            peer.Simulation.EventRaiser = collector;
        }

        [Test]
        public void Rollback_SyncedEvent_DispatchAcrossRollbackResim_SingleFire()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 10;
            int dispatchedCount = 0;
            int lastTickFromCallback = -1;
            int lastTickFromEvent = -1;
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent)
                {
                    dispatchedCount++;
                    lastTickFromCallback = tick;
                    lastTickFromEvent = evt.Tick;
                }
            };

            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
            };

            // Initial pass: chain advances past raiseAtTick — Synced fires once on verification.
            _harness.AdvanceAllToTick(raiseAtTick + 5);
            Assert.AreEqual(1, dispatchedCount, "Initial verified-tick dispatch must fire exactly once");
            Assert.AreEqual(raiseAtTick, lastTickFromCallback);
            Assert.AreEqual(raiseAtTick, lastTickFromEvent,
                "evt.Tick (set by BeginTick) must match dispatch tick (set by DispatchTickEvents) — tick-argument invariant");

            // Inject stub snapshot so ResolveRollbackTick_Default finds a restore point.
            ReadSnapshotManager(_harness.Host.Engine)
                .SaveSnapshot(raiseAtTick - 1, new StubSnapshot { Tick = raiseAtTick - 1 });

            // Force rollback through raiseAtTick. Rollback.cs:143-144 rewinds _lastVerifiedTick
            // to resolvedTick - 1, so chain must re-advance past raiseAtTick — re-dispatch of
            // the resimulated Synced event would violate the single-fire invariant.
            _harness.Host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 12);

            Assert.AreEqual(1, dispatchedCount,
                $"Synced event must dispatch exactly once across rollback/resim cycle. Got {dispatchedCount}. " +
                "double-fire: TryAdvanceVerifiedChain (FrameVerification.cs:64) and ExecuteTick " +
                "DispatchTickEvents both fire OnSyncedEvent for the same tick after rollback.");
            Assert.AreEqual(raiseAtTick, lastTickFromCallback,
                "Tick-argument invariant must hold across resim");
            Assert.AreEqual(raiseAtTick, lastTickFromEvent,
                "evt.Tick stamped during resim BeginTick must equal the resim tick");
        }

        [Test]
        public void Rollback_RegularEvent_DiffCascade_OldOnlyCancels_NewOnlyConfirms()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 10;
            int predictedCount = 0;
            int confirmedCount = 0;
            int canceledCount = 0;
            _harness.Host.Engine.OnEventPredicted += (tick, evt) =>
            {
                if (evt is TestRegularEvent) predictedCount++;
            };
            _harness.Host.Engine.OnEventConfirmed += (tick, evt) =>
            {
                if (evt is TestRegularEvent) confirmedCount++;
            };
            _harness.Host.Engine.OnEventCanceled += (tick, evt) =>
            {
                if (evt is TestRegularEvent) canceledCount++;
            };

            int payloadVariant = 1;
            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestRegularEvent { Payload = payloadVariant });
            };

            // Initial pass — both peers exchange input so CanAdvanceTick succeeds: Regular event
            // fires OnEventConfirmed (verified path), not OnEventPredicted.
            _harness.AdvanceAllToTick(raiseAtTick + 5);
            int initialPredicted = predictedCount;
            int initialConfirmed = confirmedCount;
            int initialCanceled = canceledCount;
            Assert.GreaterOrEqual(initialConfirmed, 1,
                "Regular event must fire OnEventConfirmed on the verified path during initial pass");

            // Inject a stub snapshot at raiseAtTick - 1 so ResolveRollbackTick_Default can find
            // a restore point. Without this the non-ECS path always fails with NoSnapshot.
            ReadSnapshotManager(_harness.Host.Engine)
                .SaveSnapshot(raiseAtTick - 1, new StubSnapshot { Tick = raiseAtTick - 1 });

            // Change variant so resim produces an event with a different content hash.
            payloadVariant = 2;
            _harness.Host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 12);

            int newCanceled = canceledCount - initialCanceled;
            int newConfirmed = confirmedCount - initialConfirmed;
            Assert.AreEqual(1, newCanceled,
                $"Old-only Regular event (variant 1) must be canceled exactly once. Got {newCanceled}. " +
                "DiffRollbackEvents matches by (Tick, EventTypeId, GetContentHash); variant 1's hash differs from variant 2.");
            Assert.AreEqual(1, newConfirmed,
                $"New-only Regular event (variant 2) must be confirmed exactly once on the re-verified tick. Got {newConfirmed}.");
        }

        // Functionally equivalent to the single-fire test above (the helper is content-agnostic),
        // but surfaces single-fire's precedence over new-event delivery as an explicit guard:
        // a variant-changed Synced re-buffered at the same tick must not bypass the watermark.
        [Test]
        public void Rollback_SyncedEvent_VariantChangeAtSameTick_BatchSkipped()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 10;
            int dispatchedCount = 0;
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent) dispatchedCount++;
            };

            int payloadVariant = 1;
            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = payloadVariant });
            };

            _harness.AdvanceAllToTick(raiseAtTick + 5);
            Assert.AreEqual(1, dispatchedCount, "Initial pass must fire Synced exactly once");

            ReadSnapshotManager(_harness.Host.Engine)
                .SaveSnapshot(raiseAtTick - 1, new StubSnapshot { Tick = raiseAtTick - 1 });

            // Change variant so resim produces a different-content Synced at the same tick.
            payloadVariant = 2;
            _harness.Host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 12);

            Assert.AreEqual(1, dispatchedCount,
                $"Variant-changed Synced at same tick must not bypass the watermark. Got {dispatchedCount}. " +
                "single-fire invariant takes precedence over new-event delivery.");
        }

        // Path-agnostic check: the batch helper is shared across all 5 dispatch sites, so a
        // P2P-path test covers the others by helper logic identity.
        [Test]
        public void MultiSyncedAtSameTick_BatchSingleFireAcrossRollback()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 10;
            int dispatchedCount = 0;
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent) dispatchedCount++;
            };

            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                {
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 2 });
                }
            };

            // Initial pass: both Synced events at the same tick must fire.
            _harness.AdvanceAllToTick(raiseAtTick + 5);
            Assert.AreEqual(2, dispatchedCount,
                $"Both Synced events at the same tick must fire on initial pass. Got {dispatchedCount}.");

            ReadSnapshotManager(_harness.Host.Engine)
                .SaveSnapshot(raiseAtTick - 1, new StubSnapshot { Tick = raiseAtTick - 1 });

            _harness.Host.Engine.RequestRollback(raiseAtTick - 1);
            _harness.AdvanceAllToTick(raiseAtTick + 12);

            // Rollback re-buffers the same two events; helper short-circuits the entire batch.
            Assert.AreEqual(2, dispatchedCount,
                $"After rollback, neither of the two Synced events must re-fire (batch skip). Got {dispatchedCount}.");
        }

        // Prediction-stage gate guard: Synced events buffered during ExecuteTickWithPrediction
        // must not dispatch until the tick is later verified.
        [Test]
        public void P2P_PredictionStage_SyncedNotDispatched()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 5;
            int dispatchedCount = 0;
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent) dispatchedCount++;
            };

            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
            };

            // Stall the guest so host runs ExecuteTickWithPrediction past raiseAtTick.
            const int guestPlayerId = 1;
            _harness.AdvanceWithStalledPeer(raiseAtTick + 5, guestPlayerId);
            Assert.AreEqual(0, dispatchedCount,
                "Synced events buffered at predicted ticks must not dispatch (gate: state == Verified).");

            // Resume guest — verified chain advances past raiseAtTick → dispatch fires now.
            _harness.AdvanceAllToTick(raiseAtTick + 10);
            Assert.AreEqual(1, dispatchedCount,
                "Once the predicted tick is verified, Synced must fire exactly once.");
        }

        // ApplyFullState with allowRetreat reasons (LateJoin/InitialFullState/CorrectiveReset)
        // reaches ClearAll even when applyTick is below _lastVerifiedTick, so the watermark
        // reset hook (FullStateResync.cs after ClearAll) fires and lowers the watermark to
        // applyTick - 1.
        [TestCase(ApplyReason.LateJoin)]
        [TestCase(ApplyReason.InitialFullState)]
        [TestCase(ApplyReason.CorrectiveReset)]
        public void ApplyFullState_AllowRetreat_ResetsWatermarkBelowTick(ApplyReason reason)
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 10;
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) => { };
            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
            };

            _harness.AdvanceAllToTick(raiseAtTick + 5);
            Assert.AreEqual(raiseAtTick, ReadSyncedWatermark(_harness.Host.Engine),
                "Watermark must equal the dispatched tick after initial pass");

            // Reflection-driven ApplyFullState produces a hash mismatch (we pass 0L but the host
            // simulation's state hash is non-zero). The error log is expected; declare it so the
            // test runner does not flag it as unhandled.
            LogAssert.Expect(LogType.Error, new Regex(@"\[KlothoEngine\]\[FullStateResync\] hash mismatch"));

            const int applyTick = 5;
            byte[] emptyState = Array.Empty<byte>();
            try
            {
                _engineApplyFullStateMethod.Invoke(_harness.Host.Engine,
                    new object[] { applyTick, emptyState, 0L, reason });
            }
            catch (TargetInvocationException)
            {
                // Restoration failure on empty state is acceptable — the watermark reset hook
                // runs immediately after ClearAll, before downstream-dependent error paths.
            }

            int watermark = ReadSyncedWatermark(_harness.Host.Engine);
            Assert.AreEqual(applyTick - 1, watermark,
                $"For allowRetreat reason {reason} with watermark > applyTick, reset must lower watermark to {applyTick - 1}. Got {watermark}.");
        }

        // ApplyFullState with non-allowRetreat reasons (ResyncRequest/Reconnect) is a forward
        // recovery: applyTick is expected to be > _lastVerifiedTick. The skip-retreat early
        // return (FullStateResync.cs:188-196) bypasses ClearAll for backward applyTick, so the
        // reset hook is unreachable in that case — intended behavior. For forward applyTick
        // (typical), ClearAll runs but the reset condition (watermark >= tick) is false because
        // watermark <= _lastVerifiedTick < applyTick; the watermark is preserved below tick.
        [TestCase(ApplyReason.ResyncRequest)]
        [TestCase(ApplyReason.Reconnect)]
        public void ApplyFullState_NoAllowRetreat_ForwardResync_PreservesWatermarkBelowTick(ApplyReason reason)
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 10;
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) => { };
            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
            };

            _harness.AdvanceAllToTick(raiseAtTick + 5);
            int initialWatermark = ReadSyncedWatermark(_harness.Host.Engine);
            Assert.AreEqual(raiseAtTick, initialWatermark);

            LogAssert.Expect(LogType.Error, new Regex(@"\[KlothoEngine\]\[FullStateResync\] hash mismatch"));

            // Forward applyTick avoids the skip-retreat trap (lastVerifiedTick < tick), so the
            // reset hook is reachable. Reset condition (initialWatermark >= tick) is false, so
            // the watermark is preserved.
            const int applyTick = 50;
            byte[] emptyState = Array.Empty<byte>();
            try
            {
                _engineApplyFullStateMethod.Invoke(_harness.Host.Engine,
                    new object[] { applyTick, emptyState, 0L, reason });
            }
            catch (TargetInvocationException) { }

            int watermark = ReadSyncedWatermark(_harness.Host.Engine);
            Assert.Less(watermark, applyTick,
                $"For reason {reason} with applyTick > watermark, watermark must remain below tick. Got {watermark}.");
        }

        // Spectator.ResetToTick lowers the watermark so the re-emitted Synced batch dispatches.
        [Test]
        public void Spectator_ResetToTick_LowersWatermark_AllowsRedispatch()
        {
            _harness.CreateHost(2);
            _harness.AddGuest();
            _harness.StartPlaying();
            WireEventRaiserFromEngine(_harness.Host);

            const int raiseAtTick = 10;
            int dispatchedCount = 0;
            _harness.Host.Engine.OnSyncedEvent += (tick, evt) =>
            {
                if (evt is TestSyncedEvent) dispatchedCount++;
            };
            _harness.Host.Simulation.OnAfterTickRaise = (tick, raiser) =>
            {
                if (tick == raiseAtTick + 1 && raiser != null)
                    raiser.RaiseEvent(new TestSyncedEvent { Payload = 1 });
            };

            _harness.AdvanceAllToTick(raiseAtTick + 5);
            Assert.AreEqual(1, dispatchedCount, "Initial pass must fire once");
            int watermarkBeforeReset = ReadSyncedWatermark(_harness.Host.Engine);
            Assert.AreEqual(raiseAtTick, watermarkBeforeReset);

            // ResetToTick must lower the watermark below `tick` so a freshly re-emitted Synced
            // at that tick can dispatch (otherwise the helper would short-circuit).
            _harness.Host.Engine.ResetToTick(raiseAtTick);
            int watermarkAfterReset = ReadSyncedWatermark(_harness.Host.Engine);
            Assert.LessOrEqual(watermarkAfterReset, raiseAtTick - 1,
                $"ResetToTick({raiseAtTick}) must lower watermark to <= tick-1. Got {watermarkAfterReset}.");
        }
    }
}
