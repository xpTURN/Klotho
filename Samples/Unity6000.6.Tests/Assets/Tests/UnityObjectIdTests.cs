using NUnit.Framework;
using UnityEngine;

namespace xpTURN.Klotho.Unity6000_6.Tests
{
    /// <summary>
    /// UnityObjectId is the log-only identifier that replaced GetInstanceID() (obsolete-as-error on
    /// Unity 6000.6). It must be stable, distinct per object, zero only for null, and safe to read
    /// from a destroyed object — the view updater logs a view right after it may have been destroyed.
    /// </summary>
    [TestFixture]
    public class UnityObjectIdTests
    {
        [Test]
        public void Of_IsDistinctPerObject_AndStableForTheSameObject()
        {
            var a = new GameObject("a");
            var b = new GameObject("b");
            try
            {
                long ia = UnityObjectId.Of(a);
                long ib = UnityObjectId.Of(b);
                Assert.That(ia, Is.Not.EqualTo(0L));
                Assert.That(ib, Is.Not.EqualTo(0L));
                Assert.That(ia, Is.Not.EqualTo(ib));
                Assert.That(UnityObjectId.Of(a), Is.EqualTo(ia));
                Assert.That(UnityObjectId.Of(a.transform), Is.Not.EqualTo(ia), "component and its GameObject are different objects");
            }
            finally
            {
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        [Test]
        public void Of_NullReference_IsZero()
        {
            Assert.That(UnityObjectId.Of(null), Is.EqualTo(0L));
        }

        [Test]
        public void Of_DestroyedObject_DoesNotThrow_AndKeepsItsId()
        {
            var a = new GameObject("a");
            long before = UnityObjectId.Of(a);
            Object.DestroyImmediate(a);

            long after = 0;
            Assert.DoesNotThrow(() => after = UnityObjectId.Of(a));
            Assert.That(after, Is.EqualTo(before));
        }
    }
}
