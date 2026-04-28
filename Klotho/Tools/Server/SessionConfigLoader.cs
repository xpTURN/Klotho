using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ZLogger;

namespace xpTURN.Klotho.Core
{
    public static class SessionConfigLoader
    {
        private const string FileName = "sessionconfig.json";

        private static readonly JsonSerializerSettings s_settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static SessionConfig Load(string[] args, ILogger logger)
        {
            string path = ConfigPathResolver.Resolve(FileName, args);

            if (path == null)
            {
                logger.ZLogWarning(
                    $"[SessionConfigLoader] {FileName} not found, using defaults.");
                return new SessionConfig();
            }

            logger.ZLogInformation(
                $"[SessionConfigLoader] Loading from: {path}");

            try
            {
                string json = File.ReadAllText(path);
                var config = JsonConvert.DeserializeObject<SessionConfig>(json, s_settings)
                             ?? new SessionConfig();

                int clampedMinPlayers = Math.Clamp(config.MinPlayers, 1, config.MaxPlayers);
                if (clampedMinPlayers != config.MinPlayers)
                {
                    logger.ZLogWarning(
                        $"[SessionConfigLoader] MinPlayers clamped: {config.MinPlayers} -> {clampedMinPlayers} (range: 1..{config.MaxPlayers})");
                    config.MinPlayers = clampedMinPlayers;
                }

                logger.ZLogInformation(
                    $"[SessionConfigLoader] AllowLateJoin={config.AllowLateJoin}, ReconnectTimeoutMs={config.ReconnectTimeoutMs}, MinPlayers={config.MinPlayers}, MaxPlayers={config.MaxPlayers}");

                return config;
            }
            catch (JsonException ex)
            {
                logger.ZLogError(
                    $"[SessionConfigLoader] Failed to parse '{path}': {ex.Message}");
                throw;
            }
        }
    }
}
