using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace xpTURN.Klotho.Helper.Tests
{
    /// <summary>
    /// Test-only ILogger that captures every Log call as (level, message) entries.
    /// Compatible with ZLogger extension methods (ZLogWarning/ZLogError/...).
    /// </summary>
    public class LogCapture : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new List<(LogLevel, string)>();

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            string message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;
            Entries.Add((logLevel, message));
        }

        public int CountAt(LogLevel level)
        {
            int count = 0;
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i].Level == level) count++;
            return count;
        }

        public bool Contains(LogLevel level, string substring)
        {
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i].Level == level && Entries[i].Message.Contains(substring)) return true;
            return false;
        }

        public void Clear() => Entries.Clear();

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}
