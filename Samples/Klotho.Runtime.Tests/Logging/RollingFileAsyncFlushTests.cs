using System;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Logging.Tests
{
    /// <summary>
    /// Verifies the opt-in AsyncEvent flush mode of the rolling file sink: a background thread flushes
    /// shortly after a write, a burst is fully persisted, Dispose drains the tail, and the PerLine
    /// default never allocates the background thread or signal.
    /// </summary>
    [TestFixture]
    public class RollingFileAsyncFlushTests
    {
        private string _dir;

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "klog_async_" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

        private string ReadLogFile()
        {
            var files = Directory.GetFiles(_dir, "*.log");
            if (files.Length == 0) return string.Empty;
            using var fs = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }

        // Poll up to timeoutMs for the log file to contain the needle. The bound is a timing property,
        // so we retry at a short interval rather than sleep-then-assert once (avoids CI flakiness).
        private bool WaitForContent(string needle, int timeoutMs)
        {
            var deadline = Environment.TickCount + timeoutMs;
            do
            {
                if (ReadLogFile().Contains(needle)) return true;
                Thread.Sleep(5);
            } while (Environment.TickCount < deadline);
            return ReadLogFile().Contains(needle);
        }

        [Test]
        public void AsyncEvent_Write_VisibleWithinShortBound()
        {
            var sink = new RollingFileSink("Test", 1024 * 1024, _dir, KFlushMode.AsyncEvent);
            try
            {
                sink.Write(KLogLevel.Information, "async-visible", null);
                Assert.That(WaitForContent("async-visible", 2000), Is.True,
                    "AsyncEvent write should reach the file via the background flusher within the bound.");
            }
            finally { sink.Dispose(); }
        }

        [Test]
        public void AsyncEvent_Burst_NaturalBatching()
        {
            var sink = new RollingFileSink("Test", 1024 * 1024, _dir, KFlushMode.AsyncEvent);
            for (int i = 0; i < 1000; i++)
                sink.Write(KLogLevel.Debug, "burst-" + i, null);
            sink.Dispose(); // drains the tail

            var text = ReadLogFile();
            Assert.That(text, Does.Contain("burst-0"));
            Assert.That(text, Does.Contain("burst-999"));
            int count = 0;
            for (int idx = text.IndexOf("burst-", StringComparison.Ordinal); idx >= 0;
                 idx = text.IndexOf("burst-", idx + 1, StringComparison.Ordinal))
                count++;
            Assert.That(count, Is.EqualTo(1000), "all burst lines should be persisted");
        }

        [Test]
        public void AsyncEvent_Dispose_Drains()
        {
            var sink = new RollingFileSink("Test", 1024 * 1024, _dir, KFlushMode.AsyncEvent);
            sink.Write(KLogLevel.Information, "async-tail", null);
            sink.Dispose();
            Assert.That(ReadLogFile(), Does.Contain("async-tail"));
        }

        [Test]
        public void AsyncEvent_NoFlusherInPerLineMode()
        {
            var perLine = new RollingFileSink("Test", 1024 * 1024, _dir);
            try
            {
                Assert.That(GetField(perLine, "_flusher"), Is.Null, "PerLine must not start a flusher thread");
                Assert.That(GetField(perLine, "_flushSignal"), Is.Null, "PerLine must not allocate the flush signal");
            }
            finally { perLine.Dispose(); }

            var async = new RollingFileSink("Test", 1024 * 1024, _dir, KFlushMode.AsyncEvent);
            try
            {
                Assert.That(GetField(async, "_flusher"), Is.Not.Null, "AsyncEvent must start a flusher thread");
                Assert.That(GetField(async, "_flushSignal"), Is.Not.Null, "AsyncEvent must allocate the flush signal");
            }
            finally { async.Dispose(); }
        }

        private static object GetField(RollingFileSink sink, string name) =>
            typeof(RollingFileSink).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sink);
    }
}
