using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NUnit.Framework;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Logging.Tests
{
    /// <summary>
    /// Covers the interpolated-string log handlers (structured message formatting): literal,
    /// primitive, format specifiers, alignment, enum, bool, null, brace escaping, plus level
    /// routing, level gating, and buffer reuse. Each case drives the real path
    /// handler -> CharBufferWriter -> KLogger -> sink and asserts on the finished message.
    /// </summary>
    [TestFixture]
    public class KLogFormattingTests
    {
        private sealed class CaptureSink : IKLogSink
        {
            public readonly List<(KLogLevel Level, string Message, Exception Exception)> Entries
                = new List<(KLogLevel, string, Exception)>();
            public void Write(KLogLevel level, string message, Exception exception)
                => Entries.Add((level, message ?? string.Empty, exception));
            public void Flush() { }
            public void Dispose() { }
        }

        private CaptureSink _sink;
        private IKLoggerFactory _factory;
        private IKLogger _logger;

        [SetUp]
        public void SetUp()
        {
            _sink = new CaptureSink();
            // Trace minimum so every level is enabled; formatting is what we assert here.
            _factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Trace).AddSink(_sink));
            _logger = _factory.CreateLogger("Test");
        }

        [TearDown]
        public void TearDown() => _factory?.Dispose();

        private string LastMessage => _sink.Entries[_sink.Entries.Count - 1].Message;

        /// <summary>Runs the action under the given culture and restores the previous culture when done.</summary>
        private static void RunUnderCulture(string cultureName, Action action)
        {
            var prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                action();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        private enum Phase { Idle = 0, Running = 1, Draining = 2 }

        [Flags]
        private enum Caps { None = 0, Read = 1, Write = 2 }

        // ── Literal / basic embedding ──

        [Test]
        public void Information_LiteralOnly_ReturnsLiteral()
        {
            _logger.KInformation($"Hello");
            Assert.That(LastMessage, Is.EqualTo("Hello"));
        }

        [Test]
        public void Information_EmptyLiteral_ReturnsEmpty()
        {
            _logger.KInformation($"");
            Assert.That(LastMessage, Is.EqualTo(""));
        }

        [Test]
        public void Information_SingleInt_EmbedsValue()
        {
            _logger.KInformation($"value={42}");
            Assert.That(LastMessage, Is.EqualTo("value=42"));
        }

        [Test]
        public void Information_SingleString_EmbedsValue()
        {
            _logger.KInformation($"name={"Alice"}");
            Assert.That(LastMessage, Is.EqualTo("name=Alice"));
        }

        [Test]
        public void Information_NegativeInt_EmbedsValue()
        {
            _logger.KInformation($"d={-7}");
            Assert.That(LastMessage, Is.EqualTo("d=-7"));
        }

        [Test]
        public void Information_MultipleValues_Concatenates()
        {
            _logger.KInformation($"a={1} b={2} c={3}");
            Assert.That(LastMessage, Is.EqualTo("a=1 b=2 c=3"));
        }

        [Test]
        public void Information_Unicode_PreservesCharacters()
        {
            _logger.KInformation($"한글-{1}-日本語-{2}");
            Assert.That(LastMessage, Is.EqualTo("한글-1-日本語-2"));
        }

        // ── null handling ──

        [Test]
        public void Information_NullString_EmbedsEmpty()
        {
            string nil = null;
            _logger.KInformation($"x={nil}y");
            Assert.That(LastMessage, Is.EqualTo("x=y"));
        }

        [Test]
        public void Information_NullObject_EmbedsEmpty()
        {
            object nil = null;
            _logger.KInformation($"[{nil}]");
            Assert.That(LastMessage, Is.EqualTo("[]"));
        }

        // ── primitives ──

        [Test]
        public void Information_Long_EmbedsValue()
        {
            _logger.KInformation($"n={9000000000L}");
            Assert.That(LastMessage, Is.EqualTo("n=9000000000"));
        }

        [Test]
        public void Information_Uint_EmbedsValue()
        {
            _logger.KInformation($"u={4000000000u}");
            Assert.That(LastMessage, Is.EqualTo("u=4000000000"));
        }

        [Test]
        public void Information_Bool_True()
        {
            _logger.KInformation($"f={true}");
            Assert.That(LastMessage, Is.EqualTo("f=True"));
        }

        [Test]
        public void Information_Bool_False()
        {
            _logger.KInformation($"f={false}");
            Assert.That(LastMessage, Is.EqualTo("f=False"));
        }

        // ── format specifiers (always InvariantCulture) ──

        [Test]
        public void Information_Float_F1_FormatsInvariant()
        {
            _logger.KInformation($"rtt={16.74f:F1}");
            Assert.That(LastMessage, Is.EqualTo("rtt=16.7"));
        }

        [Test]
        public void Information_Double_F2_FormatsInvariant()
        {
            _logger.KInformation($"x={3.14159:F2}");
            Assert.That(LastMessage, Is.EqualTo("x=3.14"));
        }

        [Test]
        public void Information_Int_Hex_Formats()
        {
            _logger.KInformation($"b={255:X}");
            Assert.That(LastMessage, Is.EqualTo("b=FF"));
        }

        [Test]
        public void Information_Ulong_Hex16_Formats()
        {
            _logger.KInformation($"hash=0x{0xDEADBEEFCAFEUL:X16}");
            Assert.That(LastMessage, Is.EqualTo("hash=0x0000DEADBEEFCAFE"));
        }

        [Test]
        public void Information_Long_Hex16_Formats()
        {
            _logger.KInformation($"h={1L:X16}");
            Assert.That(LastMessage, Is.EqualTo("h=0000000000000001"));
        }

        // ── enum ──

        [Test]
        public void Information_Enum_EmbedsName()
        {
            _logger.KInformation($"state={Phase.Draining}");
            Assert.That(LastMessage, Is.EqualTo("state=Draining"));
        }

        [Test]
        public void Information_Enum_Variable_EmbedsName()
        {
            Phase p = Phase.Running;
            _logger.KInformation($"{p}");
            Assert.That(LastMessage, Is.EqualTo("Running"));
        }

        [Test]
        public void Information_Enum_FormatD_EmbedsNumeric()
        {
            _logger.KInformation($"{Phase.Draining:D}");
            Assert.That(LastMessage, Is.EqualTo("2"));
        }

        [Test]
        public void Information_Enum_FormatX_EmbedsHex()
        {
            _logger.KInformation($"{Phase.Running:X}");
            Assert.That(LastMessage, Is.EqualTo("00000001"));
        }

        [Test]
        public void Information_Enum_Flags_Combination_EmbedsNames()
        {
            _logger.KInformation($"{Caps.Read | Caps.Write}");
            Assert.That(LastMessage, Is.EqualTo("Read, Write"));
        }

        [Test]
        public void Information_Enum_Multiple()
        {
            _logger.KInformation($"{Phase.Idle}->{Phase.Running}");
            Assert.That(LastMessage, Is.EqualTo("Idle->Running"));
        }

        // ── alignment ──

        [Test]
        public void Information_Int_RightAlign_PadsLeft()
        {
            _logger.KInformation($"[{42,5}]");
            Assert.That(LastMessage, Is.EqualTo("[   42]"));
        }

        [Test]
        public void Information_Int_LeftAlign_PadsRight()
        {
            _logger.KInformation($"[{42,-5}]");
            Assert.That(LastMessage, Is.EqualTo("[42   ]"));
        }

        [Test]
        public void Information_Int_AlignmentSmallerThanValue_NoPadding()
        {
            _logger.KInformation($"[{12345,3}]");
            Assert.That(LastMessage, Is.EqualTo("[12345]"));
        }

        [Test]
        public void Information_String_RightAlign_PadsLeft()
        {
            _logger.KInformation($"[{"hi",4}]");
            Assert.That(LastMessage, Is.EqualTo("[  hi]"));
        }

        [Test]
        public void Information_String_LeftAlign_PadsRight()
        {
            _logger.KInformation($"[{"hi",-4}]");
            Assert.That(LastMessage, Is.EqualTo("[hi  ]"));
        }

        [Test]
        public void Information_NullString_WithAlignment_PadsEmpty()
        {
            string nil = null;
            _logger.KInformation($"[{nil,4}]");
            Assert.That(LastMessage, Is.EqualTo("[    ]"));
        }

        [Test]
        public void Information_Enum_RightAlign_PadsLeft()
        {
            _logger.KInformation($"[{Phase.Idle,8}]");
            Assert.That(LastMessage, Is.EqualTo("[    Idle]"));
        }

        [Test]
        public void Information_AlignmentWithFormat_RightAlign()
        {
            _logger.KInformation($"[{3.14159,8:F2}]");
            Assert.That(LastMessage, Is.EqualTo("[    3.14]"));
        }

        [Test]
        public void Information_AlignmentWithFormat_LeftAlign()
        {
            _logger.KInformation($"[{3.14159,-8:F2}]");
            Assert.That(LastMessage, Is.EqualTo("[3.14    ]"));
        }

        [Test]
        public void Information_Mixed_AlignedColumns()
        {
            _logger.KInformation($"{"id",-4}{42,6}");
            Assert.That(LastMessage, Is.EqualTo("id      42"));
        }

        // ── brace escaping (compiler turns {{ }} into literal braces) ──

        [Test]
        public void Information_EscapedBraces_AroundValue()
        {
            _logger.KInformation($"{{{42}}}");
            Assert.That(LastMessage, Is.EqualTo("{42}"));
        }

        [Test]
        public void Information_EscapedBraces_LiteralOnly()
        {
            _logger.KInformation($"{{}}");
            Assert.That(LastMessage, Is.EqualTo("{}"));
        }

        // ── culture independence (handler formats via InvariantCulture) ──

        [Test]
        public void Information_Float_Format_IsCultureInvariant_deDE()
        {
            RunUnderCulture("de-DE", () =>
            {
                _logger.KInformation($"{1234.5:F1}");
                Assert.That(LastMessage, Is.EqualTo("1234.5"));
            });
        }

        [Test]
        public void Information_Double_Plain_IsCultureInvariant_koKR()
        {
            RunUnderCulture("ko-KR", () =>
            {
                _logger.KInformation($"{1.5}");
                Assert.That(LastMessage, Is.EqualTo("1.5"));
            });
        }

        // ── level routing ──

        [Test]
        public void KInformation_RoutesToInformation()
        {
            _logger.KInformation($"x");
            Assert.That(_sink.Entries[0].Level, Is.EqualTo(KLogLevel.Information));
        }

        [Test]
        public void KWarning_RoutesToWarning()
        {
            _logger.KWarning($"x");
            Assert.That(_sink.Entries[0].Level, Is.EqualTo(KLogLevel.Warning));
        }

        [Test]
        public void KError_RoutesToError_NoException()
        {
            _logger.KError($"boom={1}");
            Assert.That(_sink.Entries[0].Level, Is.EqualTo(KLogLevel.Error));
            Assert.That(_sink.Entries[0].Message, Is.EqualTo("boom=1"));
            Assert.That(_sink.Entries[0].Exception, Is.Null);
        }

        [Test]
        public void KError_WithException_CapturesExceptionAndMessage()
        {
            var ex = new InvalidOperationException("nope");
            _logger.KError(ex, $"failed at {7}");
            Assert.That(_sink.Entries[0].Level, Is.EqualTo(KLogLevel.Error));
            Assert.That(_sink.Entries[0].Message, Is.EqualTo("failed at 7"));
            Assert.That(_sink.Entries[0].Exception, Is.SameAs(ex));
        }

        [Test]
        public void KDebug_RoutesToDebug()
        {
            _logger.KDebug($"d={1}");
            Assert.That(_sink.Entries[0].Level, Is.EqualTo(KLogLevel.Debug));
            Assert.That(_sink.Entries[0].Message, Is.EqualTo("d=1"));
        }

        [Test]
        public void KTrace_RoutesToTrace()
        {
            _logger.KTrace($"t={1}");
            Assert.That(_sink.Entries[0].Level, Is.EqualTo(KLogLevel.Trace));
        }

        [Test]
        public void KLog_RuntimeLevel_RoutesToGivenLevel()
        {
            _logger.KLog(KLogLevel.Warning, $"w={5}");
            Assert.That(_sink.Entries[0].Level, Is.EqualTo(KLogLevel.Warning));
            Assert.That(_sink.Entries[0].Message, Is.EqualTo("w=5"));
        }

        // ── level gating: disabled level produces no entry ──

        [Test]
        public void Gating_BelowMinimum_DoesNotEmit()
        {
            using var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Warning).AddSink(_sink));
            var logger = factory.CreateLogger("Gated");

            logger.KInformation($"info={1}");
            logger.KDebug($"debug={2}");
            logger.KWarning($"warn={3}");

            Assert.That(_sink.Entries.Count, Is.EqualTo(1));
            Assert.That(_sink.Entries[0].Level, Is.EqualTo(KLogLevel.Warning));
            Assert.That(_sink.Entries[0].Message, Is.EqualTo("warn=3"));
        }

        [Test]
        public void Gating_KLog_BelowMinimum_DoesNotEmit()
        {
            using var factory = KLoggerFactory.Create(b => b.SetMinimumLevel(KLogLevel.Error).AddSink(_sink));
            var logger = factory.CreateLogger("Gated");

            logger.KLog(KLogLevel.Information, $"info={1}");
            Assert.That(_sink.Entries.Count, Is.EqualTo(0));

            logger.KLog(KLogLevel.Error, $"err={2}");
            Assert.That(_sink.Entries.Count, Is.EqualTo(1));
        }

        // ── null logger safety (extension on null this, handler guards) ──

        [Test]
        public void NullLogger_DoesNotThrow()
        {
            IKLogger nil = null;
            Assert.DoesNotThrow(() =>
            {
                nil.KInformation($"x={1}");
                nil.KError(new Exception(), $"y={2}");
                nil.KLog(KLogLevel.Warning, $"z={3}");
            });
        }

        // ── buffer reuse: sequential logs produce independent messages ──

        [Test]
        public void SequentialLogs_ProduceIndependentMessages()
        {
            _logger.KInformation($"first={1}");
            _logger.KInformation($"second-longer={222}");
            _logger.KInformation($"third={3}");

            Assert.That(_sink.Entries[0].Message, Is.EqualTo("first=1"));
            Assert.That(_sink.Entries[1].Message, Is.EqualTo("second-longer=222"));
            Assert.That(_sink.Entries[2].Message, Is.EqualTo("third=3"));
        }

        [Test]
        public void LongMessage_ExceedsInitialBuffer_FormatsFully()
        {
            // Forces the buffer to grow well past the 256-char initial capacity.
            var big = new string('x', 1000);
            _logger.KInformation($"{big}|{42}|{big}");
            Assert.That(LastMessage, Is.EqualTo(big + "|42|" + big));
            Assert.That(LastMessage.Length, Is.EqualTo(2000 + 4));
        }
    }
}
