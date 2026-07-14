using NUnit.Framework;

using xpTURN.Klotho.Core;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core.Tests
{
    /// <summary>
    /// Pins the coupling between the authority's FullState serve cooldown and the requester's
    /// resync retry interval. Both the P2P host and the SD server drop a FullStateRequest that
    /// lands inside the cooldown, and they send no reject — the requester is expected to re-send
    /// on its own timeout. That only works while the cooldown is strictly shorter than the retry
    /// interval; at or above it, every retry lands inside a fresh window and resync starves
    /// silently (the peer never gets a state, and nothing logs a rejection).
    ///
    /// The two constants live in different classes, so nothing but this test stops a future
    /// cooldown bump from crossing the line.
    /// </summary>
    [TestFixture]
    public class ResyncPolicyInvariantTests
    {
        [Test]
        public void ServeCooldown_IsShorterThan_RequesterRetryInterval()
        {
            Assert.That(
                ResyncPolicy.RESYNC_RESPONSE_COOLDOWN_MS,
                Is.LessThan((long)KlothoEngine.RESYNC_TIMEOUT_MS),
                "FullState serve cooldown must stay below the resync retry interval — otherwise a " +
                "legitimate retry always lands inside the cooldown window and is dropped forever.");
        }
    }
}
