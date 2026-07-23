using System;
using xpTURN.Klotho.Logging;
using System.Collections.Generic;

namespace xpTURN.Klotho.Helper.Tests
{
    /// <summary>
    /// Test-only logger that captures every Log call as (level, message) entries.
    /// Copied from the Brawler test harness (Helpers/LogCapture.cs) so the moved navigation tests
    /// can assert on logged errors under `dotnet test`, where Unity's LogAssert is unavailable.
    /// </summary>
    public class LogCapture : IKLogger
    {
        public readonly List<(KLogLevel Level, string Message)> Entries = new List<(KLogLevel, string)>();

        public bool IsEnabled(KLogLevel level) => true;

        public void Log(KLogLevel level, string message, Exception exception)
        {
            Entries.Add((level, message ?? string.Empty));
        }

        public int CountAt(KLogLevel level)
        {
            int count = 0;
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i].Level == level) count++;
            return count;
        }

        public bool Contains(KLogLevel level, string substring)
        {
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i].Level == level && Entries[i].Message.Contains(substring)) return true;
            return false;
        }

        public void Clear() => Entries.Clear();
    }
}
