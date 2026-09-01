using System.IO;

using UnityEngine;

using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Unity
{
    public static class KlothoLogger
    {
        // Default IKLogger with UnityDebug + RollingFile sinks.
        // Callers needing custom sinks should build their own factory.
        public static IKLogger CreateDefault(
            KLogLevel level = KLogLevel.Information,
            string filePrefix = "Client",
            string categoryName = "Client",
            int rollingSizeKB = 1024 * 1024,
            string directory = null,  // null → DefaultLogDirectory()
            string timestampFormat = null)
        {
            string logDir = directory ?? DefaultLogDirectory();

            var loggerFactory = KLoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(level);
                builder.AddUnityDebug();
                builder.AddRollingFile(options =>
                {
                    options.FilePrefix = filePrefix;
                    options.RollingSizeKB = rollingSizeKB;
                    options.Directory = logDir;
                    options.TimestampFormat = timestampFormat;
                });
            });

            return loggerFactory.CreateLogger(categoryName);
        }

        /// <summary>
        /// Absolute log directory for the running platform.
        /// <para>The sink's own default ("Logs") is a RELATIVE path, so it resolves against the process
        /// working directory: the project root in the Editor, but a read-only install folder (Windows) or
        /// a sandboxed bundle root (iOS/Android) in a build. There the directory cannot be created, and
        /// since the sink opens its file on first write, that surfaces as an exception thrown out of the
        /// first log call. Anchoring to an absolute writable root removes that failure mode.</para>
        /// </summary>
        private static string DefaultLogDirectory()
        {
#if UNITY_EDITOR
            // Project root (parent of Assets) — alongside the Editor's own logs, where a developer looks.
            string root = Path.Combine(Application.dataPath, "..");
#else
            string root = Application.persistentDataPath;
#endif
            return Path.GetFullPath(Path.Combine(root, "Logs"));
        }
    }
}
