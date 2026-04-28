using System;
using System.IO;

namespace xpTURN.Klotho.Core
{
    public static class ConfigPathResolver
    {
        /// <summary>
        /// Resolves the config file path.
        /// 1. Directory specified via the --config-dir &lt;dir&gt; CLI argument
        /// 2. CWD
        /// 3. AppContext.BaseDirectory
        /// </summary>
        public static string Resolve(string fileName, string[] args)
        {
            // 1. --config-dir <dir>
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--config-dir")
                {
                    string explicitPath = Path.Combine(args[i + 1], fileName);
                    if (!File.Exists(explicitPath))
                        throw new FileNotFoundException(
                            $"Config file not found in --config-dir: {explicitPath}");
                    return explicitPath;
                }
            }

            // 2. CWD
            string cwdPath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            if (File.Exists(cwdPath))
                return cwdPath;

            // 3. AppContext.BaseDirectory
            string basePath = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(basePath))
                return basePath;

            return null;
        }
    }
}
