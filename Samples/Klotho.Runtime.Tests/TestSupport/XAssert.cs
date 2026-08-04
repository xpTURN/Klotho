using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Small compatibility helper for the handful of xUnit assertions that have no clean 1:1 NUnit
    /// equivalent, used by the tests migrated from the former Klotho.Core.Tests (xUnit) project:
    ///   - Assert.Single that RETURNS the single element (NUnit has no returning form)
    ///   - Assert.Contains / DoesNotContain with a predicate, and their value-in-collection forms
    /// Every other xUnit assert was converted to its native NUnit call (Equal→AreEqual, True→IsTrue, …).
    /// </summary>
    internal static class XAssert
    {
        // Assert.Single(coll) — exactly one element; returns it (xUnit semantics).
        public static T Single<T>(IEnumerable<T> collection)
        {
            var list = collection as IList<T> ?? collection.ToList();
            Assert.That(list.Count, Is.EqualTo(1), "Expected exactly one element");
            return list[0];
        }

        // Assert.Contains(collection, predicate)
        public static void Contains<T>(IEnumerable<T> collection, Func<T, bool> predicate)
            => Assert.That(collection.Any(predicate), Is.True, "Expected an element matching the predicate");

        // Assert.Contains(expected, collection)
        public static void Contains<T>(T expected, IEnumerable<T> collection)
            => Assert.That(collection, Does.Contain(expected));

        // Assert.Contains(substring, string)
        public static void Contains(string expectedSubstring, string actual)
            => Assert.That(actual, Does.Contain(expectedSubstring));

        // Assert.DoesNotContain(collection, predicate)
        public static void DoesNotContain<T>(IEnumerable<T> collection, Func<T, bool> predicate)
            => Assert.That(collection.Any(predicate), Is.False, "Expected no element matching the predicate");

        // Assert.DoesNotContain(expected, collection)
        public static void DoesNotContain<T>(T expected, IEnumerable<T> collection)
            => Assert.That(collection, Does.Not.Contain(expected));

        // Assert.DoesNotContain(substring, string)
        public static void DoesNotContain(string expectedSubstring, string actual)
            => Assert.That(actual, Does.Not.Contain(expectedSubstring));
    }
}
