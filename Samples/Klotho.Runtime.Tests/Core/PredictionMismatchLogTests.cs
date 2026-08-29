using System.Reflection;
using NUnit.Framework;
using xpTURN.Klotho.Helper.Tests;
using xpTURN.Klotho.Input;
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// What the PredictionMismatch line says when a prediction is rejected.
    ///
    /// The rollback decision is byte equality of the two serialized commands, but the log printed
    /// CommandTypeId alone — which is equal in every case except a type change, so the interesting
    /// mismatch (same command, different input) printed the same number twice and named nothing.
    /// A live P2P host produced 50 of these lines and 19 of them read "predicted=113, actual=113".
    ///
    /// Asserted on the message rather than on a helper's return value: the defect was that the line
    /// had drifted away from the decision it was reporting, and only the line shows that.
    ///
    /// Scope: these invoke the formatter directly, so they pin the SHAPE of the line, not the fact that
    /// the reconcile path calls it. Reaching that path needs a seeded pending prediction and a live
    /// rollback, which is a harness this claim does not justify — the call site is a one-line
    /// substitution with no other caller.
    /// </summary>
    [TestFixture]
    public class PredictionMismatchLogTests
    {
        private static readonly MethodInfo LogMethod = typeof(KlothoEngine)
            .GetMethod("LogPredictionMismatch", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo LoggerField = typeof(KlothoEngine)
            .GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance);

        private KlothoEngine NewEngine(LogCapture log)
        {
            var engine = new KlothoEngine(new SimulationConfig(), new SessionConfig());
            LoggerField.SetValue(engine, log);
            return engine;
        }

        private static string Emit(KlothoEngine engine, LogCapture log, ICommand predicted, ICommand actual)
        {
            LogMethod.Invoke(engine, new object[] { predicted, actual });
            foreach (var entry in log.Entries)
                if (entry.Message.Contains("PredictionMismatch")) return entry.Message;
            return null;
        }

        [Test]
        public void SameCommandDifferentInput_NamesTheByteThatDiffers()
        {
            var log = new LogCapture();
            var engine = NewEngine(log);

            // Same type, same tick, same player — only the payload differs. This is the case that used
            // to print one number twice, and it is the common one: a mispredicted input, not a
            // mispredicted command.
            var predicted = new PlayerJoinCommand { PlayerId = 1, Tick = 7, JoinedPlayerId = 3 };
            var actual    = new PlayerJoinCommand { PlayerId = 1, Tick = 7, JoinedPlayerId = 9 };

            string line = Emit(engine, log, predicted, actual);

            Assert.That(line, Is.Not.Null, "a rejected prediction still has to be reported");
            Assert.That(line, Does.Contain("reason=payload"),
                "the decision was byte equality, so the line has to say the bytes differed — not repeat the type id");
            Assert.That(line, Does.Contain("firstDiff@"),
                "and where: an offset is what turns this line into something a game can act on");
            Assert.That(line, Does.Contain("tick=7").And.Contain("player=1"));
        }

        [Test]
        public void DifferentCommand_StillNamesBothTypes()
        {
            var log = new LogCapture();
            var engine = NewEngine(log);

            var predicted = new EmptyCommand { PlayerId = 2, Tick = 4 };
            var actual    = new PlayerJoinCommand { PlayerId = 2, Tick = 4, JoinedPlayerId = 1 };

            string line = Emit(engine, log, predicted, actual);

            Assert.That(line, Does.Contain("reason=type"), "a type change is the one case the old line did report");
            Assert.That(line, Does.Contain($"predicted={predicted.CommandTypeId}")
                          .And.Contain($"actual={actual.CommandTypeId}"),
                "and both ids stay, because here they are the answer");
        }
    }
}
