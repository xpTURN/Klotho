using System;
using System.Collections.Generic;
using System.Text;

namespace xpTURN.Klotho.ECS.Diagnostics
{
    // Per-type ECS component-storage memory stat (per frame). Read-only measurement.
    // "Reserve vs live" split: reservedBytes = layout.TotalSize (slotCapacity-based reservation);
    // liveBytes/wasteBytes measure the variable footprint (components + dense int, both shrink with
    // maxCount) at (memSize + 4) per slot. serSize is reported separately (wire/snapshot), never used
    // for the memory/waste math.
    public readonly struct ComponentMemoryStat
    {
        public readonly int TypeId;
        public readonly string Name;
        public readonly bool Singleton;
        public readonly int MemSize;        // in-memory size (Unsafe.SizeOf), memory-saving basis
        public readonly int SerSize;        // serialized size (wire/snapshot), not used for waste
        public readonly int SlotCapacity;   // dense/components length (singleton 1, else maxEntities)
        public readonly int Capacity;       // sparse domain = maxEntities (fixed, non-shrinkable)
        public readonly int LiveCount;
        public readonly int PeakCount;      // -1 = not measured (sampler off)
        public readonly int ReservedBytes;  // layout.TotalSize (per frame)
        public readonly int LiveBytes;      // liveCount * (memSize + 4)  — components + dense
        public readonly int WasteBytes;     // (slotCapacity - liveCount) * (memSize + 4)

        public ComponentMemoryStat(int typeId, string name, bool singleton,
                                   int memSize, int serSize, int slotCapacity, int capacity,
                                   int liveCount, int peakCount,
                                   int reservedBytes, int liveBytes, int wasteBytes)
        {
            TypeId = typeId;
            Name = name;
            Singleton = singleton;
            MemSize = memSize;
            SerSize = serSize;
            SlotCapacity = slotCapacity;
            Capacity = capacity;
            LiveCount = liveCount;
            PeakCount = peakCount;
            ReservedBytes = reservedBytes;
            LiveBytes = liveBytes;
            WasteBytes = wasteBytes;
        }

        public double Util => SlotCapacity > 0 ? LiveCount / (double)SlotCapacity : 0.0;

        // Singletons are structurally single-slot (maxCount cannot shrink below/grow above 1) -> always 1.
        // Else: N/A (-1) when peak was not measured; otherwise ceil(peak * margin).
        public int RecommendedMaxCount(double margin)
            => Singleton ? 1 : (PeakCount < 0 ? -1 : (int)Math.Ceiling(PeakCount * margin));
    }

    // Reusable report (call Reset+Add via ComponentMemoryAnalyzer.Capture). Per-frame totals are int
    // (bounded by the byte[] heap size); the x-ring aggregates are long to avoid overflow on large
    // configs. ToText() is the only allocating path.
    public sealed class ComponentMemoryReport
    {
        private readonly List<ComponentMemoryStat> _stats = new List<ComponentMemoryStat>();

        public IReadOnlyList<ComponentMemoryStat> Stats => _stats;
        public int MaxEntities { get; private set; }
        public int RingHeaps { get; private set; }
        public int ObservedTicks { get; set; }   // "N ticks observed" meta (set by the caller from the sampler)

        public int TotalReservedPerFrame { get; private set; }
        public int TotalLivePerFrame { get; private set; }
        public int TotalWastePerFrame { get; private set; }
        public int TotalFixedPerFrame { get; private set; }      // sparse sum (non-shrinkable)
        public int TotalVariablePerFrame { get; private set; }   // dense + components sum (shrinkable)

        public long TotalReservedAllHeaps => (long)TotalReservedPerFrame * RingHeaps;
        public long TotalLiveAllHeaps => (long)TotalLivePerFrame * RingHeaps;
        public long TotalWasteAllHeaps => (long)TotalWastePerFrame * RingHeaps;

        internal void Reset(int maxEntities, int ringHeaps)
        {
            _stats.Clear();
            MaxEntities = maxEntities;
            RingHeaps = ringHeaps;
            ObservedTicks = 0;
            TotalReservedPerFrame = 0;
            TotalLivePerFrame = 0;
            TotalWastePerFrame = 0;
            TotalFixedPerFrame = 0;
            TotalVariablePerFrame = 0;
        }

        internal void Add(in ComponentMemoryStat stat)
        {
            _stats.Add(stat);
            TotalReservedPerFrame += stat.ReservedBytes;
            TotalLivePerFrame += stat.LiveBytes;
            TotalWastePerFrame += stat.WasteBytes;
            TotalFixedPerFrame += stat.Capacity * 4;                          // sparse
            TotalVariablePerFrame += stat.SlotCapacity * (stat.MemSize + 4);  // dense + components
        }

        // Aligned table dump (LogComponentHashes style). Peak/->maxCount render "-" when unmeasured.
        public string ToText(double margin = 1.5)
        {
            var sb = new StringBuilder();
            sb.Append("[Mem] maxEntities=").Append(MaxEntities)
              .Append(" ringHeaps=").Append(RingHeaps)
              .Append(" observedTicks=").Append(ObservedTicks)
              .Append(" margin=").Append(margin).AppendLine();
            // Name column widened to the longest "Name(typeId)" so the numeric columns stay aligned.
            int nameWidth = 4; // "type"
            for (int i = 0; i < _stats.Count; i++)
            {
                int len = (_stats[i].Name + "(" + _stats[i].TypeId + ")").Length;
                if (len > nameWidth) nameWidth = len;
            }

            sb.Append("[Mem] ").Append("type".PadRight(nameWidth))
              .Append("mem".PadLeft(5))
              .Append("slot".PadLeft(6))
              .Append("live".PadLeft(7))
              .Append("peak".PadLeft(7))
              .Append("reserved".PadLeft(13))
              .Append("liveB".PadLeft(13))
              .Append("waste".PadLeft(13))
              .Append("util".PadLeft(6))
              .Append("->maxCount".PadLeft(11))
              .AppendLine();
            for (int i = 0; i < _stats.Count; i++)
            {
                ComponentMemoryStat s = _stats[i];
                string peakStr = s.PeakCount < 0 ? "-" : s.PeakCount.ToString();
                int rec = s.RecommendedMaxCount(margin);
                string recStr = rec < 0 ? "-" : rec.ToString();
                string nameId = s.Name + "(" + s.TypeId + ")";
                string util = ((int)(s.Util * 100)) + "%";
                sb.Append("[Mem] ")
                  .Append(nameId.PadRight(nameWidth))
                  .Append(s.MemSize.ToString().PadLeft(5))
                  .Append(s.SlotCapacity.ToString().PadLeft(6))
                  .Append(s.LiveCount.ToString().PadLeft(7))
                  .Append(peakStr.PadLeft(7))
                  .Append(s.ReservedBytes.ToString("N0").PadLeft(13))
                  .Append(s.LiveBytes.ToString("N0").PadLeft(13))
                  .Append(s.WasteBytes.ToString("N0").PadLeft(13))
                  .Append(util.PadLeft(6))
                  .Append(recStr.PadLeft(11))
                  .AppendLine();
            }
            sb.Append("[Mem] -- per-frame total: reserved ").Append(TotalReservedPerFrame.ToString("N0"))
              .Append(" / live ").Append(TotalLivePerFrame.ToString("N0"))
              .Append(" / waste ").Append(TotalWastePerFrame.ToString("N0"))
              .Append("  (fixed sparse ").Append(TotalFixedPerFrame.ToString("N0"))
              .Append(" / variable ").Append(TotalVariablePerFrame.ToString("N0")).Append(')').AppendLine();
            sb.Append("[Mem] -- x").Append(RingHeaps).Append(" heaps: reserved ").Append(TotalReservedAllHeaps.ToString("N0"))
              .Append(" / live ").Append(TotalLiveAllHeaps.ToString("N0"))
              .Append(" / waste ").Append(TotalWasteAllHeaps.ToString("N0")).AppendLine();
            return sb.ToString();
        }
    }

    // Pure function: registry (reserve) + frame (live) + ringHeaps (multiplier) -> report.
    // For a live simulation pass ringHeaps = simulation.RollbackCapacity + 1 (the resident frame heaps:
    // the rollback ring plus the one live frame) — read from the actual ring so it is construction-path
    // independent.
    public static class ComponentMemoryAnalyzer
    {
        public static ComponentMemoryReport Capture(Frame frame, int ringHeaps, int[] peakCounts = null)
        {
            var report = new ComponentMemoryReport();
            Capture(frame, ringHeaps, peakCounts, report);
            return report;
        }

        // No-alloc overload (after warmup): fills a caller-owned report.
        public static void Capture(Frame frame, int ringHeaps, int[] peakCounts, ComponentMemoryReport report)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (report == null) throw new ArgumentNullException(nameof(report));

            report.Reset(frame.MaxEntities, ringHeaps);

            ReadOnlySpan<int> typeIds = ComponentStorageRegistry.RegisteredTypeIdsSorted;
            for (int i = 0; i < typeIds.Length; i++)
            {
                int typeId = typeIds[i];
                ref readonly var layout = ref ComponentStorageRegistry.GetLayout(typeId);

                int memSize = layout.ComponentSize;
                int slot = layout.SlotCapacity;
                int live = frame.GetLiveCount(typeId);
                int peak = (peakCounts != null && typeId < peakCounts.Length) ? peakCounts[typeId] : -1;
                int variableUnit = memSize + 4;   // component slot + dense int (both shrink with maxCount)

                report.Add(new ComponentMemoryStat(
                    typeId,
                    ComponentStorageRegistry.GetType(typeId)?.Name ?? "?",
                    ComponentStorageRegistry.IsSingleton(typeId),
                    memSize,
                    ComponentStorageRegistry.PerComponentSize[typeId],
                    slot,
                    layout.Capacity,
                    live,
                    peak,
                    layout.TotalSize,
                    live * variableUnit,
                    (slot - live) * variableUnit));
            }
        }
    }
}
