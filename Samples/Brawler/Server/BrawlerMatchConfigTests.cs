using System;

using Brawler; // BrawlerMatchConfig
using xpTURN.Klotho.ECS; // FixedString64

namespace xpTURN.Klotho.BrawlerDedicatedServer.Tests
{
    /// <summary>
    /// BrawlerMatchConfig codec round-trip: the per-match dynamic config (BotCount, MaxPlayers) serialized
    /// into the opaque SimulationConfig.MatchConfigData byte[] and restored on every peer.
    /// Run: dotnet run -- --test
    /// </summary>
    public static class BrawlerMatchConfigTests
    {
        private static int _passed;
        private static int _failed;

        public static int RunAll()
        {
            _passed = 0;
            _failed = 0;
            Console.WriteLine("\n=== BrawlerMatchConfig Tests ===\n");

            // Round-trip preserves BotCount for a range of values.
            foreach (int n in new[] { 0, 1, 3, 4, 42 })
            {
                byte[] bytes = BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = n });
                int back = BrawlerMatchConfig.Decode(bytes).BotCount;
                Assert($"roundtrip BotCount={n}", back == n);
            }

            // MaxPlayers travels with it: bot ids are numbered past the room capacity, so it is a tick-0
            // state input rather than a display hint — a replay verifier rebuilding tick 0 reads it here.
            var both = BrawlerMatchConfig.Decode(
                BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = 2, MaxPlayers = 4 }));
            Assert("roundtrip MaxPlayers", both.MaxPlayers == 4);
            Assert("roundtrip keeps BotCount alongside it", both.BotCount == 2);

            // Null / empty / malformed buffers → default, so an unset MatchConfigData is a no-op.
            Assert("null → 0", BrawlerMatchConfig.Decode(null).BotCount == 0);
            Assert("empty → 0", BrawlerMatchConfig.Decode(Array.Empty<byte>()).BotCount == 0);
            Assert("malformed(<size) → 0", BrawlerMatchConfig.Decode(new byte[] { 1, 2 }).BotCount == 0);

            // 0 is "not stamped", and it has to be distinguishable from a stamped capacity: consumers fall
            // back to their local value on 0, and the verifier refuses to rebuild tick 0 without one.
            Assert("unstamped MaxPlayers is 0", BrawlerMatchConfig.Decode(
                BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = 1 })).MaxPlayers == 0);

            // A blob from before MaxPlayers existed is exactly 4 bytes, and the reader needs the full record —
            // it decodes to default rather than reading BotCount and stopping. That is why the verifier treats
            // a capacity of 0 as "re-record", not as "0 players".
            Assert("pre-MaxPlayers blob → default", BrawlerMatchConfig.Decode(new byte[] { 7, 0, 0, 0 }).BotCount == 0);

            // The match instance id — the only key that joins an uploaded replay to the lobby's record of the
            // match. It rides here because the payload is what reaches every peer and lands in every file.
            var withId = BrawlerMatchConfig.Decode(BrawlerMatchConfig.Encode(new BrawlerMatchConfigData
            {
                BotCount = 1, MaxPlayers = 4, MatchInstanceId = FixedString64.FromString("match-7#t3"),
            }));
            Assert("roundtrip MatchInstanceId", withId.MatchInstanceId.ToString() == "match-7#t3");
            Assert("identity does not disturb its neighbours", withId.BotCount == 1 && withId.MaxPlayers == 4);

            // Empty is the "no lobby issued one" signal (a P2P host or solo authors its own config), and it
            // must survive as empty rather than becoming whatever the previous field left behind.
            Assert("unstamped identity is empty", BrawlerMatchConfig.Decode(
                BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = 1, MaxPlayers = 4 }))
                .MatchInstanceId.Length == 0);

            // 62 UTF-8 bytes is the budget and FixedString64 CUTS SILENTLY past it — two ids cut to the same
            // value would merge two different matches. This is the check the lobby runs before stamping, so
            // the failure is a refused issue rather than a corrupted record.
            string tooLong = new string('x', 63);
            Assert("over-budget id does not round-trip (issuer must refuse)",
                FixedString64.FromString(tooLong).ToString() != tooLong);
            string atBudget = new string('x', 62);
            Assert("62 bytes still fits", FixedString64.FromString(atBudget).ToString() == atBudget);

            // Encode is exactly the struct's serialized size (two int32 + FixedString64).
            Assert("Encode length == 72", BrawlerMatchConfig.Encode(new BrawlerMatchConfigData { BotCount = 7 }).Length == 72);

            Console.WriteLine($"\n=== BrawlerMatchConfig results: {_passed} passed, {_failed} failed ===");
            return _failed;
        }

        static void Assert(string name, bool condition)
        {
            if (condition) { Console.WriteLine($"  PASS: {name}"); _passed++; }
            else           { Console.WriteLine($"  FAIL: {name}"); _failed++; }
        }
    }
}
