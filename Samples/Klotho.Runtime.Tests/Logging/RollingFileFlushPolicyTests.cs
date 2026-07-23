using System;
using System.IO;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Logging.Tests
{
    /// <summary>
    /// Verifies that the rolling file sink flushes every line to disk so logs are immediately
    /// visible (no buffering window), and that Dispose pushes the final state out cleanly. Each
    /// case writes to a temp directory and reads the file back.
    /// </summary>
    [TestFixture]
    public class RollingFileFlushPolicyTests
    {
        private string _dir;

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "klog_" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

        private string ReadLogFile()
        {
            var file = Directory.GetFiles(_dir, "*.log")[0];
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }

        [Test]
        public void EveryLine_FlushedImmediately_AcrossLevels()
        {
            var sink = new RollingFileSink("Test", 1024 * 1024, _dir);
            try
            {
                sink.Write(KLogLevel.Information, "info-1", null);
                Assert.That(ReadLogFile(), Does.Contain("info-1"));

                sink.Write(KLogLevel.Warning, "warn-1", null);
                Assert.That(ReadLogFile(), Does.Contain("warn-1"));

                sink.Write(KLogLevel.Debug, "dbg-1", null);
                var text = ReadLogFile();
                Assert.That(text, Does.Contain("info-1"));
                Assert.That(text, Does.Contain("warn-1"));
                Assert.That(text, Does.Contain("dbg-1"));
            }
            finally { sink.Dispose(); }
        }

        [Test]
        public void Error_FlushedImmediately_WithException()
        {
            var sink = new RollingFileSink("Test", 1024 * 1024, _dir);
            try
            {
                sink.Write(KLogLevel.Error, "boom", new InvalidOperationException("why"));
                var text = ReadLogFile();
                Assert.That(text, Does.Contain("boom"));
                Assert.That(text, Does.Contain("why"));
            }
            finally { sink.Dispose(); }
        }

        [Test]
        public void Dispose_FlushesAndCloses()
        {
            var sink = new RollingFileSink("Test", 1024 * 1024, _dir);
            sink.Write(KLogLevel.Information, "tail", null);
            sink.Dispose();
            Assert.That(ReadLogFile(), Does.Contain("tail"));
        }
    }
}
