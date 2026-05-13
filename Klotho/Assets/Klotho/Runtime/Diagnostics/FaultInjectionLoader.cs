#if KLOTHO_FAULT_INJECTION
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace xpTURN.Klotho.Diagnostics
{
    /// <summary>
    /// Loads fault-injection toggles from a JSON file and applies them to the static FaultInjection
    /// fields. Missing fields are left at their current value (merge semantics) — explicit empty
    /// arrays clear the corresponding HashSet. Caller resolves the file path (e.g. via the existing
    /// ConfigPathResolver on the dedicated server, or StreamingAssets on the Unity client).
    /// </summary>
    public static class FaultInjectionLoader
    {
        public const string DefaultFileName = "faultinjectionconfig.json";

        [JsonObject(MemberSerialization.OptIn)]
        private class Schema
        {
            [JsonProperty] public int? EmulatedRttMs { get; set; }
            [JsonProperty] public List<RttScheduleEntry> EmulatedRttSchedule { get; set; }
            [JsonProperty] public int? ServerGcPauseMs { get; set; }
            [JsonProperty] public int? ServerGcPauseAtTick { get; set; }
            [JsonProperty] public List<int> DropSpawnCommandPlayerIds { get; set; }
            [JsonProperty] public List<int> SuppressBootstrapAckPlayerIds { get; set; }
            [JsonProperty] public List<int> ForceSpawnRetryPlayerIds { get; set; }
            [JsonProperty] public int? ForceTickOffsetDelta { get; set; }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class RttScheduleEntry
        {
            [JsonProperty("atSec")] public float AtSec { get; set; }
            [JsonProperty("rttMs")] public int RttMs { get; set; }
        }

        /// <summary>
        /// Loads and applies the JSON file at the given path. Returns true if any toggle was applied.
        /// Missing/null path or missing file → returns false without throwing (logs at Debug).
        /// </summary>
        public static bool TryLoadAndApply(string path, ILogger logger)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                logger?.ZLogDebug($"[FaultInjectionLoader] No config file at '{path}' — using defaults");
                return false;
            }

            logger?.ZLogInformation($"[FaultInjectionLoader] Loading from: {path}");

            Schema schema;
            try
            {
                string json = File.ReadAllText(path);
                schema = JsonConvert.DeserializeObject<Schema>(json);
            }
            catch (System.Exception ex)
            {
                logger?.ZLogError($"[FaultInjectionLoader] Failed to parse '{path}': {ex.Message}");
                return false;
            }

            if (schema == null)
            {
                logger?.ZLogWarning($"[FaultInjectionLoader] Empty/invalid JSON: {path}");
                return false;
            }

            ApplySchema(schema);
            logger?.ZLogWarning(
                $"[FaultInjectionLoader] Applied: RTT={FaultInjection.EmulatedRttMs}ms, " +
                $"rttSchedule=[{FormatRttSchedule()}], " +
                $"GC={FaultInjection.ServerGcPauseMs}ms@tick{FaultInjection.ServerGcPauseAtTick}, " +
                $"dropSpawn=[{string.Join(",", FaultInjection.DropSpawnCommandPlayerIds)}], " +
                $"suppressAck=[{string.Join(",", FaultInjection.SuppressBootstrapAckPlayerIds)}], " +
                $"forceSpawnRetry=[{string.Join(",", FaultInjection.ForceSpawnRetryPlayerIds)}], " +
                $"forceTickOffsetDelta={FaultInjection.ForceTickOffsetDelta}");
            return true;
        }

        private static string FormatRttSchedule()
        {
            var schedule = FaultInjection.EmulatedRttSchedule;
            if (schedule.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < schedule.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('(').Append(schedule[i].atSec.ToString("F1")).Append("s,")
                  .Append(schedule[i].rttMs).Append("ms)");
            }
            return sb.ToString();
        }

        private static void ApplySchema(Schema s)
        {
            if (s.EmulatedRttMs.HasValue)        FaultInjection.EmulatedRttMs = s.EmulatedRttMs.Value;
            if (s.ServerGcPauseMs.HasValue)      FaultInjection.ServerGcPauseMs = s.ServerGcPauseMs.Value;
            if (s.ServerGcPauseAtTick.HasValue)  FaultInjection.ServerGcPauseAtTick = s.ServerGcPauseAtTick.Value;
            if (s.ForceTickOffsetDelta.HasValue) FaultInjection.ForceTickOffsetDelta = s.ForceTickOffsetDelta.Value;

            if (s.EmulatedRttSchedule != null)
            {
                FaultInjection.EmulatedRttSchedule.Clear();
                foreach (var entry in s.EmulatedRttSchedule)
                    FaultInjection.EmulatedRttSchedule.Add((entry.AtSec, entry.RttMs));
                // Driver consumes sequentially — enforce ascending atSec.
                FaultInjection.EmulatedRttSchedule.Sort((a, b) => a.atSec.CompareTo(b.atSec));
            }

            if (s.DropSpawnCommandPlayerIds != null)
            {
                FaultInjection.DropSpawnCommandPlayerIds.Clear();
                foreach (int id in s.DropSpawnCommandPlayerIds)
                    FaultInjection.DropSpawnCommandPlayerIds.Add(id);
            }
            if (s.SuppressBootstrapAckPlayerIds != null)
            {
                FaultInjection.SuppressBootstrapAckPlayerIds.Clear();
                foreach (int id in s.SuppressBootstrapAckPlayerIds)
                    FaultInjection.SuppressBootstrapAckPlayerIds.Add(id);
            }
            if (s.ForceSpawnRetryPlayerIds != null)
            {
                FaultInjection.ForceSpawnRetryPlayerIds.Clear();
                foreach (int id in s.ForceSpawnRetryPlayerIds)
                    FaultInjection.ForceSpawnRetryPlayerIds.Add(id);
            }
        }
    }
}
#endif
