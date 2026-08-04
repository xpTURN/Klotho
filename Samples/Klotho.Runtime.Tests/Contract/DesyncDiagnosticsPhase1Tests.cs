using System;
using System.Collections.Generic;
using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.ECS;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Pins the NEW behaviors introduced here (layered hash, history ring, command-digest
    /// preconditions). The existing suites only pin non-regression of the scalar hash; nothing else stops
    /// these properties from silently rotting:
    ///
    ///  - the layered fold structure (Total == fold of the exposed layers) that the online probe
    ///    and the offline diff tool will trust,
    ///  - localization: a component mutation moves exactly that component's layer hash, and a
    ///    snapshot-participant mutation moves exactly the system layer (the blind spot the old
    ///    per-component dump could not see at all),
    ///  - the resync-critical invariant GetStateHash() == SerializeFullStateWithHash().hash (the symmetric
    ///    fold — breaking one side makes every resync apply report HashMismatch),
    ///  - ring semantics: re-record overwrites (rollback-resim coherence) and wraparound
    ///    invalidates stale slots,
    ///  - the steady accumulation path allocates nothing (the zero-GC gate),
    ///  - command serialization round-trips byte-stably (the digest's canonical-form assumption
    ///    — the sort-order half is guaranteed by simulation determinism itself).
    ///
    /// Not covered here (needs the KLOTHO_FAULT_INJECTION define / engine harness): the salt negative
    /// control and the injection-driven acceptance flow.
    /// </summary>
    public sealed class DesyncDiagnosticsPhase1Tests
    {
        private const int MaxEntities = 32;
        private static readonly List<ICommand> NoCommands = new List<ICommand>();

        // ── fold structure ───────────────────────────────────────────

        [Test] // Total/FrameHash must equal the manual fold of the exposed layers — the probe/tool contract
        public void Breakdown_TotalEqualsFoldOfExposedLayers()
        {
            var (sim, system) = NewSimWithParticipant();
            sim.Tick(NoCommands);

            var b = new StateHashBreakdown();
            long total = sim.GetStateHash(b);

            // Frame level: FNV_OFFSET → Tick → EntityCount → fold of each per-type hash, in order.
            ulong frameFold = FPHash.FNV_OFFSET;
            frameFold = FPHash.Hash(frameFold, sim.Frame.Tick);
            frameFold = FPHash.Hash(frameFold, sim.Frame.Entities.Count);
            for (int i = 0; i < b.ComponentHashes.Length; i++)
                frameFold = FPHash.Hash(frameFold, b.ComponentHashes[i]);
            Assert.AreEqual(b.FrameHash, frameFold);

            // Total: frame hash folded with each participant hash, in registration order.
            ulong totalFold = b.FrameHash;
            for (int i = 0; i < b.SystemHashes.Length; i++)
                totalFold = FPHash.Hash(totalFold, b.SystemHashes[i]);
            Assert.AreEqual(b.Total, (long)totalFold);
            Assert.AreEqual(total, b.Total);
            Assert.IsTrue(b.SystemHashes.Length > 0, "harness must register a snapshot participant");
        }

        // ── localization ─────────────────────────────────────────────

        [Test] // mutating one component moves exactly that type's layer hash — nothing else
        public void ComponentMutation_LocalizesToThatTypeOnly()
        {
            var (sim1, _) = NewSimWithParticipant();
            var (sim2, _) = NewSimWithParticipant();
            var e1 = AddTransformEntity(sim1);
            var e2 = AddTransformEntity(sim2);
            sim1.Tick(NoCommands);
            sim2.Tick(NoCommands);

            var b1 = new StateHashBreakdown();
            var b2 = new StateHashBreakdown();
            sim1.GetStateHash(b1);
            sim2.GetStateHash(b2);
            Assert.AreEqual(b1.Total, b2.Total);   // identical setups agree end to end

            // Diverge one Transform field on sim2 only.
            ref var t = ref sim2.Frame.Get<TransformComponent>(e2);
            t.Position = new FPVector3(FP64.FromInt(7), FP64.Zero, FP64.Zero);
            sim2.GetStateHash(b2);

            int transformIdx = IndexOfType<TransformComponent>();
            Assert.AreNotEqual(b1.Total, b2.Total);
            Assert.AreNotEqual(b1.ComponentHashes[transformIdx], b2.ComponentHashes[transformIdx]);
            for (int i = 0; i < b1.ComponentHashes.Length; i++)
            {
                if (i == transformIdx) continue;
                Assert.AreEqual(b1.ComponentHashes[i], b2.ComponentHashes[i]);
                Assert.AreEqual(b1.ComponentCounts[i], b2.ComponentCounts[i]);
            }
            Assert.AreEqual(b1.SystemHashes, b2.SystemHashes);   // system layer untouched
        }

        [Test] // mutating only participant state moves ONLY the system layer — the blind spot pin:
               // every component hash stays equal, so the old component-only dump would report "no cause"
        public void SystemStateMutation_LocalizesToSystemLayerOnly()
        {
            var (sim1, _) = NewSimWithParticipant();
            var (sim2, sys2) = NewSimWithParticipant();
            AddTransformEntity(sim1);
            AddTransformEntity(sim2);
            sim1.Tick(NoCommands);
            sim2.Tick(NoCommands);

            var b1 = new StateHashBreakdown();
            var b2 = new StateHashBreakdown();
            sim1.GetStateHash(b1);
            sim2.GetStateHash(b2);
            Assert.AreEqual(b1.Total, b2.Total);

            sys2.State = 0xBADF00D;   // diverge system-internal state only (e.g. an RNG stream)
            sim2.GetStateHash(b2);

            Assert.AreNotEqual(b1.Total, b2.Total);
            Assert.AreEqual(b1.ComponentHashes, b2.ComponentHashes);   // component layer fully clean
            Assert.AreNotEqual(b1.SystemHashes[0], b2.SystemHashes[0]);
        }

        // ── resync-critical symmetry ─────────────────────────────────

        [Test] // GetStateHash() must equal the FullState serialization hash — the resync apply-verification
               // compares exactly these two; an asymmetric fold makes every resync report HashMismatch
        public void GetStateHash_EqualsFullStateSerializationHash()
        {
            // With a snapshot participant (per-participant fold branch).
            var (simP, _) = NewSimWithParticipant();
            AddTransformEntity(simP);
            simP.Tick(NoCommands);
            var (_, fullHashP) = simP.SerializeFullStateWithHash();
            Assert.AreEqual(simP.GetStateHash(), fullHashP);

            // Without participants (frame-only branch).
            var simF = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: 50);
            simF.Initialize();
            AddTransformEntity(simF);
            simF.Tick(NoCommands);
            var (_, fullHashF) = simF.SerializeFullStateWithHash();
            Assert.AreEqual(simF.GetStateHash(), fullHashF);
        }

        // ── ring semantics ───────────────────────────────────────────

        [Test] // re-record for the same tick overwrites the slot — the mechanism rollback resim relies on
               // to keep the ring on the corrected timeline
        public void HashHistory_ReRecord_OverwritesWithCurrentTimeline()
        {
            var (sim, _) = NewSimWithParticipant();
            sim.SetHashHistoryCapacity(8);
            var e = AddTransformEntity(sim);
            sim.Tick(NoCommands);

            sim.ComputeAndRecordHashHistory(5);
            Assert.IsTrue(sim.TryGetHashHistory(5, out long speculative, out _));

            // "Corrected timeline": state differs when tick 5 is re-executed after a rollback.
            ref var t = ref sim.Frame.Get<TransformComponent>(e);
            t.Position = new FPVector3(FP64.FromInt(3), FP64.Zero, FP64.Zero);
            sim.ComputeAndRecordHashHistory(5);

            Assert.IsTrue(sim.TryGetHashHistory(5, out long corrected, out _));
            Assert.AreNotEqual(speculative, corrected);
            Assert.AreEqual(sim.GetStateHash(), corrected);
        }

        [Test] // wraparound invalidates the overwritten tick instead of serving another tick's values
        public void HashHistory_Wraparound_InvalidatesStaleSlot()
        {
            var (sim, _) = NewSimWithParticipant();
            sim.SetHashHistoryCapacity(4);
            sim.Tick(NoCommands);

            for (int tick = 0; tick < 4; tick++)
                sim.ComputeAndRecordHashHistory(tick);
            Assert.IsTrue(sim.TryGetHashHistory(0, out _, out _));

            sim.ComputeAndRecordHashHistory(4);   // slot 0 reused by tick 4

            Assert.IsFalse(sim.TryGetHashHistory(0, out _, out _));
            Assert.IsTrue(sim.TryGetHashHistory(4, out _, out _));
        }

        // ── zero-GC gate ─────────────────────────────────────────────

        [Test] // the steady accumulation path (hash + breakdown fill + ring copy) must allocate nothing —
               // this is the always-on production path; allocations here are the zero-GC gate failing
        public void HashHistory_SteadyAccumulation_ZeroAlloc()
        {
            var (sim, _) = NewSimWithParticipant();
            sim.SetHashHistoryCapacity(16);
            AddTransformEntity(sim);
            sim.Tick(NoCommands);

            // Warmup: lazy ring build, breakdown sizing, participant hash buffer growth.
            for (int tick = 0; tick < 4; tick++)
                sim.ComputeAndRecordHashHistory(tick);
            sim.RecordHashHistory(3);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                sim.GetStateHash();                    // fills the reusable breakdown (history enabled)
                sim.RecordHashHistory(i % 16);         // check-tick shape: reuse the just-computed hash
                sim.ComputeAndRecordHashHistory(i % 16);   // per-tick shape
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0, allocated);
        }

        // ── digest precondition ──────────────────────────────────────

        [Test] // command serialization must round-trip byte-stably: the SD classification digests the
               // server's wire bytes against a re-serialization of locally deserialized commands
        public void CommandSerialization_RoundTrip_ByteStable()
        {
            var factory = new CommandFactory();
            var commands = new List<ICommand>
            {
                new PlayerJoinCommand { JoinedPlayerId = 2, PlayerId = 2, Tick = 41 },
                new EmptyCommand { PlayerId = 1, Tick = 41 },
                new StopCommand { PlayerId = 0, Tick = 41 },
            };

            byte[] first = Serialize(factory, commands);
            List<ICommand> roundTripped = new List<ICommand>(factory.DeserializeCommands(first));
            byte[] second = Serialize(factory, roundTripped);

            Assert.AreEqual(first, second);   // byte-identical ⇒ FNV digests identical on both ends
        }

        // ── harness ──────────────────────────────────────────────────

        // Minimal snapshot participant: system-internal state that is part of the total hash but
        // invisible to the component layer — the case (stands in for an RNG / accumulator system).
        private sealed class StatefulTestSystem : ISystem, ISnapshotParticipant
        {
            public long State;

            public void Update(ref Frame frame) { }
            public int GetSnapshotSize() => 8;
            public void SaveSnapshot(ref SpanWriter writer) => writer.WriteInt64(State);
            public void RestoreSnapshot(ref SpanReader reader) => State = reader.ReadInt64();
        }

        private static (EcsSimulation sim, StatefulTestSystem system) NewSimWithParticipant()
        {
            var sim = new EcsSimulation(MaxEntities, maxRollbackTicks: 8, deltaTimeMs: 50);
            sim.Initialize();
            var system = new StatefulTestSystem { State = 1 };
            sim.AddSystem(system, SystemPhase.Update);
            return (sim, system);
        }

        private static EntityRef AddTransformEntity(EcsSimulation sim)
        {
            var entity = sim.Frame.CreateEntity();
            sim.Frame.Add(entity, new TransformComponent
            {
                Position = FPVector3.Zero,
                Scale = FPVector3.One,
            });
            return entity;
        }

        // Breakdown arrays are positional over the ascending-typeId order; that internal array is not
        // public, but the position is derivable from the public surface: it equals the number of
        // registered typeIds smaller than this type's.
        private static int IndexOfType<T>() where T : unmanaged, IComponent
        {
            int typeId = ComponentStorageRegistry.GetTypeId(typeof(T));
            int index = 0;
            foreach (var type in ComponentStorageRegistry.RegisteredTypes)
                if (ComponentStorageRegistry.GetTypeId(type) < typeId) index++;
            return index;
        }

        private static byte[] Serialize(CommandFactory factory, List<ICommand> commands)
        {
            int size = factory.GetSerializedCommandsSize(commands);
            byte[] buf = new byte[size];
            int written = factory.SerializeCommandsTo(buf.AsSpan());
            return buf.AsSpan(0, written).ToArray();
        }
    }
}
