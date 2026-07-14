using xpTURN.Klotho.Logging;
#if KLOTHO_FAULT_INJECTION
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
#endif

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

        /// <summary>
        /// Loads and applies the JSON file at the given path. Returns true if any toggle was applied.
        /// Missing/null path or missing file → returns false without throwing (logs at Debug).
        /// When KLOTHO_FAULT_INJECTION is undefined, returns false immediately with no IO / logging.
        /// </summary>
        public static bool TryLoadAndApply(string path, IKLogger logger)
        {
#if KLOTHO_FAULT_INJECTION
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                logger?.KDebug($"[FaultInjectionLoader] No config file at '{path}' — using defaults");
                return false;
            }

            logger?.KInformation($"[FaultInjectionLoader] Loading from: {path}");

            Schema schema;
            try
            {
                string json = File.ReadAllText(path);
                schema = JsonConvert.DeserializeObject<Schema>(json);
            }
            catch (System.Exception ex)
            {
                logger?.KError($"[FaultInjectionLoader] Failed to parse '{path}': {ex.Message}");
                return false;
            }

            if (schema == null)
            {
                logger?.KWarning($"[FaultInjectionLoader] Empty/invalid JSON: {path}");
                return false;
            }

            ApplySchema(schema);
            logger?.KWarning(
                $"[FaultInjectionLoader] Applied: RTT={FaultInjection.EmulatedRttMs}ms, " +
                $"rttSchedule=[{FormatRttSchedule()}], " +
                $"disconnectSchedule=[{FormatDisconnectSchedule()}], " +
                $"GC={FaultInjection.ServerGcPauseMs}ms@tick{FaultInjection.ServerGcPauseAtTick}, " +
                $"dropSpawn=[{string.Join(",", FaultInjection.DropSpawnCommandPlayerIds)}], " +
                $"suppressAck=[{string.Join(",", FaultInjection.SuppressBootstrapAckPlayerIds)}], " +
                $"forceSpawnRetry=[{string.Join(",", FaultInjection.ForceSpawnRetryPlayerIds)}], " +
                $"forceTickOffsetDelta={FaultInjection.ForceTickOffsetDelta}, " +
                $"dropFullState=[{string.Join(",", FaultInjection.DropFullStateResponsePlayerIds)}], " +
                $"forceClientDesync={FaultInjection.ForceClientDesyncAtTick}@[{string.Join(",", FaultInjection.ForceClientDesyncPlayerIds)}], " +
                // No "mutator missing" check here: the game registers StateCorruptionMutator AFTER loading
                // the config, so at this point it is legitimately still null. The engine warns at arming
                // time, which is the first moment the answer is actually knowable.
                $"stateCorruption={FaultInjection.StateCorruptionTick}@[{string.Join(",", FaultInjection.StateCorruptionPlayerIds)}]");
            return true;
#else
            return false;
#endif
        }

#if KLOTHO_FAULT_INJECTION
        [JsonObject(MemberSerialization.OptIn)]
        private class Schema
        {
            [JsonProperty] public int? EmulatedRttMs { get; set; }
            [JsonProperty] public List<RttScheduleEntry> EmulatedRttSchedule { get; set; }
            [JsonProperty] public List<DisconnectScheduleEntry> EmulatedDisconnectSchedule { get; set; }
            [JsonProperty] public int? ServerGcPauseMs { get; set; }
            [JsonProperty] public int? ServerGcPauseAtTick { get; set; }
            [JsonProperty] public List<int> DropSpawnCommandPlayerIds { get; set; }
            [JsonProperty] public List<int> SuppressBootstrapAckPlayerIds { get; set; }
            [JsonProperty] public List<int> ForceSpawnRetryPlayerIds { get; set; }
            [JsonProperty] public int? ForceTickOffsetDelta { get; set; }
            [JsonProperty] public List<int> DropFullStateResponsePlayerIds { get; set; }
            [JsonProperty] public int? ForceClientDesyncAtTick { get; set; }
            [JsonProperty] public List<int> ForceClientDesyncPlayerIds { get; set; }

            // The positive control. Only the WHEN and WHO live here — the WHAT is a typed
            // mutation over the game's own components, so the game registers
            // FaultInjection.StateCorruptionMutator. Arming the tick without a mutator is inert, which
            // is the honest failure: core cannot invent a corruption for components it does not know.
            [JsonProperty] public int? StateCorruptionAtTick { get; set; }
            [JsonProperty] public List<int> StateCorruptionPlayerIds { get; set; }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class RttScheduleEntry
        {
            [JsonProperty("atSec")] public float AtSec { get; set; }
            [JsonProperty("rttMs")] public int RttMs { get; set; }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class DisconnectScheduleEntry
        {
            [JsonProperty("atSec")] public float AtSec { get; set; }
            [JsonProperty("durationSec")] public float DurationSec { get; set; }
            [JsonProperty("playerId")] public int? PlayerId { get; set; }
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

        private static string FormatDisconnectSchedule()
        {
            var schedule = FaultInjection.EmulatedDisconnectSchedule;
            if (schedule.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < schedule.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = schedule[i];
                sb.Append('(').Append(e.atSec.ToString("F1")).Append("s+")
                  .Append(e.durationSec.ToString("F1")).Append("s,peer=")
                  .Append(e.playerId.HasValue ? e.playerId.Value.ToString() : "all").Append(')');
            }
            return sb.ToString();
        }

        private static void ApplySchema(Schema s)
        {
            if (s.EmulatedRttMs.HasValue)        FaultInjection.EmulatedRttMs = s.EmulatedRttMs.Value;
            if (s.ServerGcPauseMs.HasValue)      FaultInjection.ServerGcPauseMs = s.ServerGcPauseMs.Value;
            if (s.ServerGcPauseAtTick.HasValue)  FaultInjection.ServerGcPauseAtTick = s.ServerGcPauseAtTick.Value;
            if (s.ForceTickOffsetDelta.HasValue) FaultInjection.ForceTickOffsetDelta = s.ForceTickOffsetDelta.Value;
            if (s.ForceClientDesyncAtTick.HasValue) FaultInjection.ForceClientDesyncAtTick = s.ForceClientDesyncAtTick.Value;

            if (s.EmulatedRttSchedule != null)
            {
                FaultInjection.EmulatedRttSchedule.Clear();
                foreach (var entry in s.EmulatedRttSchedule)
                    FaultInjection.EmulatedRttSchedule.Add((entry.AtSec, entry.RttMs));
                // Driver consumes sequentially — enforce ascending atSec.
                FaultInjection.EmulatedRttSchedule.Sort((a, b) => a.atSec.CompareTo(b.atSec));
            }

            if (s.EmulatedDisconnectSchedule != null)
            {
                FaultInjection.EmulatedDisconnectSchedule.Clear();
                foreach (var entry in s.EmulatedDisconnectSchedule)
                    FaultInjection.EmulatedDisconnectSchedule.Add((entry.AtSec, entry.DurationSec, entry.PlayerId));
                FaultInjection.EmulatedDisconnectSchedule.Sort((a, b) => a.atSec.CompareTo(b.atSec));
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
            if (s.DropFullStateResponsePlayerIds != null)
            {
                FaultInjection.DropFullStateResponsePlayerIds.Clear();
                foreach (int id in s.DropFullStateResponsePlayerIds)
                    FaultInjection.DropFullStateResponsePlayerIds.Add(id);
            }
            if (s.ForceClientDesyncPlayerIds != null)
            {
                FaultInjection.ForceClientDesyncPlayerIds.Clear();
                foreach (int id in s.ForceClientDesyncPlayerIds)
                    FaultInjection.ForceClientDesyncPlayerIds.Add(id);
            }

            if (s.StateCorruptionAtTick.HasValue) FaultInjection.StateCorruptionTick = s.StateCorruptionAtTick.Value;
            if (s.StateCorruptionPlayerIds != null)
            {
                FaultInjection.StateCorruptionPlayerIds.Clear();
                foreach (int id in s.StateCorruptionPlayerIds)
                    FaultInjection.StateCorruptionPlayerIds.Add(id);
            }
        }
#endif
    }
}
