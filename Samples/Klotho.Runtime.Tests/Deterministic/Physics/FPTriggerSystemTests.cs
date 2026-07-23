using System;
using System.Collections.Generic;
using NUnit.Framework;
using xpTURN.Klotho.Serialization;

namespace xpTURN.Klotho.Deterministic.Physics.Tests
{
    [TestFixture]
    public class FPTriggerSystemTests
    {
        #region Enter

        [Test]
        public void NoPreviousPairs_AllEnter()
        {
            var sys = new FPTriggerSystem(true);
            var curr = new List<(int, int)> { (1, 3), (2, 5) };
            var entered = new List<(int, int)>();

            sys.ProcessCallbacks(curr,
                (a, b) => entered.Add((a, b)),
                null, null);

            Assert.AreEqual(2, entered.Count);
            Assert.AreEqual((1, 3), entered[0]);
            Assert.AreEqual((2, 5), entered[1]);
        }

        #endregion

        #region Stay

        [Test]
        public void SamePairs_AllStay()
        {
            var sys = new FPTriggerSystem(true);
            var pairs = new List<(int, int)> { (1, 3), (2, 5) };
            sys.ProcessCallbacks(pairs, null, null, null);

            var stayed = new List<(int, int)>();
            sys.ProcessCallbacks(pairs, null,
                (a, b) => stayed.Add((a, b)),
                null);

            Assert.AreEqual(2, stayed.Count);
            Assert.AreEqual((1, 3), stayed[0]);
            Assert.AreEqual((2, 5), stayed[1]);
        }

        #endregion

        #region Exit

        [Test]
        public void RemovedPairs_AllExit()
        {
            var sys = new FPTriggerSystem(true);
            var tick1 = new List<(int, int)> { (1, 3), (2, 5) };
            sys.ProcessCallbacks(tick1, null, null, null);

            var tick2 = new List<(int, int)>();
            var exited = new List<(int, int)>();
            sys.ProcessCallbacks(tick2, null, null,
                (a, b) => exited.Add((a, b)));

            Assert.AreEqual(2, exited.Count);
            Assert.AreEqual((1, 3), exited[0]);
            Assert.AreEqual((2, 5), exited[1]);
        }

        #endregion

        #region Mixed

        [Test]
        public void MixedEnterStayExit()
        {
            var sys = new FPTriggerSystem(true);
            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 3), (2, 5) },
                null, null, null);

            var entered = new List<(int, int)>();
            var stayed = new List<(int, int)>();
            var exited = new List<(int, int)>();

            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 3), (1, 7) },
                (a, b) => entered.Add((a, b)),
                (a, b) => stayed.Add((a, b)),
                (a, b) => exited.Add((a, b)));

            Assert.AreEqual(1, entered.Count);
            Assert.AreEqual((1, 7), entered[0]);

            Assert.AreEqual(1, stayed.Count);
            Assert.AreEqual((1, 3), stayed[0]);

            Assert.AreEqual(1, exited.Count);
            Assert.AreEqual((2, 5), exited[0]);
        }

        #endregion

        #region CallbackOrder

        [Test]
        public void CallbackOrder_EnterBeforeStayBeforeExit()
        {
            var sys = new FPTriggerSystem(true);
            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 3), (2, 5) },
                null, null, null);

            var order = new List<string>();
            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 3), (1, 7) },
                (a, b) => order.Add("enter"),
                (a, b) => order.Add("stay"),
                (a, b) => order.Add("exit"));

            Assert.AreEqual(3, order.Count);
            Assert.AreEqual("enter", order[0]);
            Assert.AreEqual("stay", order[1]);
            Assert.AreEqual("exit", order[2]);
        }

        [Test]
        public void PairsSortedWithinCategory()
        {
            var sys = new FPTriggerSystem(true);
            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 2), (3, 4) },
                null, null, null);

            var entered = new List<(int, int)>();
            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 2), (3, 4), (5, 6), (7, 8) },
                (a, b) => entered.Add((a, b)),
                null, null);

            Assert.AreEqual(2, entered.Count);
            Assert.AreEqual((5, 6), entered[0]);
            Assert.AreEqual((7, 8), entered[1]);
        }

        #endregion

        #region Empty

        [Test]
        public void EmptyCurrentPairs_AllExit()
        {
            var sys = new FPTriggerSystem(true);
            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 2), (3, 4) },
                null, null, null);

            var exited = new List<(int, int)>();
            sys.ProcessCallbacks(
                new List<(int, int)>(),
                null, null,
                (a, b) => exited.Add((a, b)));

            Assert.AreEqual(2, exited.Count);
            Assert.AreEqual((1, 2), exited[0]);
            Assert.AreEqual((3, 4), exited[1]);
        }

        [Test]
        public void EmptyBothPairs_NoCallbacks()
        {
            var sys = new FPTriggerSystem(true);
            bool called = false;
            sys.ProcessCallbacks(
                new List<(int, int)>(),
                (a, b) => called = true,
                (a, b) => called = true,
                (a, b) => called = true);

            Assert.IsFalse(called);
        }

        #endregion

        #region Serialization

        [Test]
        public void SerializeDeserialize_RestoresState()
        {
            var sys = new FPTriggerSystem(true);
            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 3), (2, 5) },
                null, null, null);

            int size = sys.GetSerializedSize();
            var buf = new byte[size];
            var writer = new SpanWriter(buf);
            sys.Serialize(ref writer);

            var sys2 = new FPTriggerSystem(true);
            var reader = new SpanReader(new ReadOnlySpan<byte>(buf, 0, writer.Position));
            sys2.Deserialize(ref reader);

            var entered = new List<(int, int)>();
            var stayed = new List<(int, int)>();
            var exited = new List<(int, int)>();
            sys2.ProcessCallbacks(
                new List<(int, int)> { (1, 3), (1, 7) },
                (a, b) => entered.Add((a, b)),
                (a, b) => stayed.Add((a, b)),
                (a, b) => exited.Add((a, b)));

            Assert.AreEqual(1, entered.Count);
            Assert.AreEqual((1, 7), entered[0]);
            Assert.AreEqual(1, stayed.Count);
            Assert.AreEqual((1, 3), stayed[0]);
            Assert.AreEqual(1, exited.Count);
            Assert.AreEqual((2, 5), exited[0]);
        }

        #endregion

        #region Clear

        [Test]
        public void Clear_ResetsPrevPairs()
        {
            var sys = new FPTriggerSystem(true);
            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 3), (2, 5) },
                null, null, null);

            sys.Clear();

            var entered = new List<(int, int)>();
            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 3), (2, 5) },
                (a, b) => entered.Add((a, b)),
                null, null);

            Assert.AreEqual(2, entered.Count);
        }

        #endregion

        #region Determinism

        [Test]
        public void Determinism_ProcessOrderIndependent()
        {
            var sysA = new FPTriggerSystem(true);
            var sysB = new FPTriggerSystem(true);

            var tick1 = new List<(int, int)> { (1, 2), (3, 4), (5, 6) };
            var tick2 = new List<(int, int)> { (1, 2), (5, 6), (7, 8) };

            var enterA = new List<(int, int)>();
            var stayA = new List<(int, int)>();
            var exitA = new List<(int, int)>();

            sysA.ProcessCallbacks(tick1, null, null, null);
            sysA.ProcessCallbacks(tick2,
                (a, b) => enterA.Add((a, b)),
                (a, b) => stayA.Add((a, b)),
                (a, b) => exitA.Add((a, b)));

            var enterB = new List<(int, int)>();
            var stayB = new List<(int, int)>();
            var exitB = new List<(int, int)>();

            sysB.ProcessCallbacks(tick1, null, null, null);
            sysB.ProcessCallbacks(tick2,
                (a, b) => enterB.Add((a, b)),
                (a, b) => stayB.Add((a, b)),
                (a, b) => exitB.Add((a, b)));

            Assert.AreEqual(enterA.Count, enterB.Count);
            for (int i = 0; i < enterA.Count; i++)
                Assert.AreEqual(enterA[i], enterB[i]);

            Assert.AreEqual(stayA.Count, stayB.Count);
            for (int i = 0; i < stayA.Count; i++)
                Assert.AreEqual(stayA[i], stayB[i]);

            Assert.AreEqual(exitA.Count, exitB.Count);
            for (int i = 0; i < exitA.Count; i++)
                Assert.AreEqual(exitA[i], exitB[i]);
        }

        #endregion

        #region NullCallbacks

        [Test]
        public void NullCallbacks_NoException()
        {
            var sys = new FPTriggerSystem(true);
            sys.ProcessCallbacks(
                new List<(int, int)> { (1, 3) },
                null, null, null);

            Assert.DoesNotThrow(() =>
                sys.ProcessCallbacks(
                    new List<(int, int)> { (1, 3), (2, 5) },
                    null, null, null));
        }

        #endregion
    }
}
