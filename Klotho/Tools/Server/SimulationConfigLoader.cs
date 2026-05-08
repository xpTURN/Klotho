using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using ZLogger;

namespace xpTURN.Klotho.Core
{
    public static class SimulationConfigLoader
    {
        private const string FileName = "simulationconfig.json";

        private static readonly JsonSerializerSettings s_settings = new()
        {
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static SimulationConfig Load(string[] args, ILogger logger)
        {
            string path = ConfigPathResolver.Resolve(FileName, args);

            if (path == null)
            {
                logger.ZLogWarning(
                    $"[SimulationConfigLoader] {FileName} not found, using defaults.");
                return new SimulationConfig();
            }

            logger.ZLogInformation(
                $"[SimulationConfigLoader] Loading from: {path}");

            try
            {
                string json = File.ReadAllText(path);
                var config = JsonConvert.DeserializeObject<SimulationConfig>(json, s_settings)
                             ?? new SimulationConfig();

                logger.ZLogInformation(
                    $"[SimulationConfigLoader] Mode={config.Mode}, TickIntervalMs={config.TickIntervalMs}, MaxRollbackTicks={config.MaxRollbackTicks}");

                if (config.Mode == NetworkMode.ServerDriven && config.InputDelayTicks < 2)
                    logger.ZLogWarning(
                        $"[SimulationConfigLoader] InputDelayTicks={config.InputDelayTicks} below recommended minimum of 2 — increased jitter risk.");

                return config;
            }
            catch (JsonException ex)
            {
                logger.ZLogError(
                    $"[SimulationConfigLoader] Failed to parse '{path}': {ex.Message}");
                throw;
            }
        }
    }
}
