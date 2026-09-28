using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HordeForge.WasmHost.Abi;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Randomized fuzz harness for the guest string cut used by the config
    /// and query imports. Both sides of the call are untrusted: the buffer
    /// holds a file an operator edited, and the capacity is an i32 the guest
    /// chose, so it arrives as any value from a garbage register. A cut that
    /// returns more than the capacity, or one that lands inside a multi-byte
    /// sequence, hands the guest a buffer that does not hold what the host
    /// promised, and the guest decodes the tail as U+FFFD.
    ///
    /// The fuzzer feeds two input families, well-formed UTF-8 cut at a
    /// random point and raw hostile bytes (overlong forms, truncated
    /// sequences, lone continuations, embedded NULs), against a capacity
    /// that is often one byte short of a real cut, and asserts:
    ///
    ///   * the cut never exceeds the capacity, the buffer, or int.MaxValue,
    ///   * on well-formed input the cut decodes to a clean prefix of the
    ///     decoded text with no replacement character, so a guest that grows
    ///     its buffer and retries ends up with the whole value,
    ///   * a larger capacity never shortens the cut, and an unchanged
    ///     capacity always gives the same answer,
    ///   * a negative or zero capacity yields no bytes rather than an
    ///     index error, and one call stays inside a wall-clock budget.
    /// </summary>
    public sealed class Utf8PrefixFuzzTests
    {
        /// <summary>Byte runs that break UTF-8 decoding in the ways operators and guests produce.</summary>
        private static readonly byte[][] HostileByteRuns =
        {
            new byte[] { 0x00 },
            new byte[] { 0xC0, 0x80 },                              // overlong NUL
            new byte[] { 0xC1, 0xBF },                              // overlong solidus
            new byte[] { 0xE0, 0x80, 0xAF },                        // overlong three-byte
            new byte[] { 0xF0, 0x80, 0x80, 0xAF },                  // overlong four-byte
            new byte[] { 0xF5, 0x80, 0x80, 0x80 },                  // beyond U+10FFFF
            new byte[] { 0x80 },                                    // lone continuation
            new byte[] { 0xBF },
            new byte[] { 0xC2 },                                    // truncated lead
            new byte[] { 0xE2, 0x82 },                              // truncated three-byte
            new byte[] { 0xF0, 0x9F, 0x98 },                        // truncated four-byte
            new byte[] { 0xED, 0xA0, 0x80 },                        // UTF-16 surrogate half
            new byte[] { 0xFE, 0xFF },
            new byte[] { 0xFF, 0xFE },                              // UTF-16 BOM
            new byte[] { 0xEF, 0xBB, 0xBF },                        // UTF-8 BOM
            new byte[] { 0xC2, 0xA0 },                              // non-breaking space
            new byte[] { 0xE2, 0x80, 0x8B },                        // zero-width space
            new byte[] { 0xE2, 0x80, 0xAE },                        // right-to-left override
            new byte[] { 0xF0, 0x9F, 0x92, 0xA9 },                  // emoji
        };

        /// <summary>Text fragments that go into the well-formed half of the corpus.</summary>
        private static readonly string[] HostileTexts =
        {
            "", "a", "key = value\n", "greeting = \"hello survivor\"\n", "€", "\u00a0", "\u200b",
            "\u202e", "\ufeff", "\ud83d\ude00", "\U0001F600", "caf\u00e9", "\t\r\n", "\\",
            "9223372036854775808", "[settings]\n",
        };

        [Fact]
        public void GuestSizedCutsStayInsideTheCapacity()
        {
            int iterations = FuzzDriver.Iterations();
            long seed = FuzzDriver.Seed();
            var rng = new Random(unchecked((int)seed));
            for (int i = 0; i < iterations; i++)
            {
                byte[] bytes = Generate(rng);
                int capacity = PickCapacity(rng, bytes.Length);
                string because = FuzzDriver.Escape(Describe(bytes, capacity));

                int cut = 0;
                long elapsed = FuzzDriver.Timed(() => cut = Utf8Prefix.Length(bytes, capacity));

                Assert.True(elapsed <= FuzzDriver.PerInputBudgetMs, FuzzDriver.Report(seed, i, because,
                    "one cut took " + elapsed.ToString(CultureInfo.InvariantCulture) + " ms"));
                Assert.True(cut >= 0, FuzzDriver.Report(seed, i, because, "negative cut " + cut));
                Assert.True(cut <= bytes.Length, FuzzDriver.Report(seed, i, because,
                    "cut " + cut + " past a " + bytes.Length + " byte buffer"));
                Assert.True(capacity <= 0 || cut <= capacity, FuzzDriver.Report(seed, i, because,
                    "cut " + cut + " past capacity " + capacity));
                Assert.Equal(cut, Utf8Prefix.Length(bytes, capacity));
                if (capacity >= 0 && capacity < int.MaxValue && capacity + 1 <= bytes.Length)
                {
                    Assert.True(Utf8Prefix.Length(bytes, capacity + 1) >= cut, FuzzDriver.Report(seed, i, because,
                        "a larger capacity shortened the cut"));
                }
            }
        }

        /// <summary>
        /// The property the guest ABI depends on: on a well-formed buffer,
        /// every cut lands between characters, so the guest never decodes a
        /// truncated sequence into U+FFFD. Only well-formed input can carry
        /// the claim; raw hostile bytes may already be undecodable, which is
        /// the guest's problem to handle, not a cut that made it worse.
        /// </summary>
        [Fact]
        public void CutsOnWellFormedTextNeverSplitACharacter()
        {
            int iterations = FuzzDriver.Iterations();
            long seed = FuzzDriver.Seed() + 1;
            var rng = new Random(unchecked((int)seed));
            for (int i = 0; i < iterations; i++)
            {
                string text = GenerateText(rng);
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                string whole = Encoding.UTF8.GetString(bytes);
                Assert.Equal(text, whole);
                for (int capacity = 0; capacity <= bytes.Length; capacity++)
                {
                    int cut = Utf8Prefix.Length(bytes, capacity);
                    string decoded = Encoding.UTF8.GetString(bytes, 0, cut);
                    Assert.True(whole.StartsWith(decoded, StringComparison.Ordinal),
                        FuzzDriver.Report(seed, i, FuzzDriver.Escape(text),
                            "cut " + cut + " is not a prefix of the whole value"));
                    Assert.True(decoded.IndexOf('\ufffd') < 0,
                        FuzzDriver.Report(seed, i, FuzzDriver.Escape(text),
                            "cut " + cut + " of " + bytes.Length + " decodes a truncated character"));
                    // A cut that fits the whole buffer is the whole buffer.
                    if (cut == bytes.Length)
                    {
                        Assert.Equal(whole, decoded);
                    }
                    AssertCutIsMaximal(seed, i, FuzzDriver.Escape(text), bytes, cut, capacity);
                }
            }
        }

        /// <summary>
        /// The capacities a guest can actually deliver: zero, negative, and
        /// the int boundaries, on an empty and on a non-empty buffer. None
        /// may throw or report bytes.
        /// </summary>
        [Fact]
        public void GuestCapacityExtremesYieldNoBytes()
        {
            long seed = FuzzDriver.Seed();
            byte[][] buffers = { new byte[0], new byte[] { 0x61 }, new byte[] { 0xF0, 0x9F, 0x92, 0xA9 } };
            int[] capacities = { int.MinValue, -1, 0, 1, int.MaxValue - 1, int.MaxValue };
            for (int b = 0; b < buffers.Length; b++)
            {
                for (int c = 0; c < capacities.Length; c++)
                {
                    int cut = Utf8Prefix.Length(buffers[b], capacities[c]);
                    string because = FuzzDriver.Escape("buffer=" + buffers[b].Length.ToString(CultureInfo.InvariantCulture)
                        + " capacity=" + capacities[c].ToString(CultureInfo.InvariantCulture));
                    Assert.True(cut >= 0 && cut <= buffers[b].Length, FuzzDriver.Report(seed, b * capacities.Length + c, because,
                        "cut " + cut.ToString(CultureInfo.InvariantCulture) + " is outside the buffer"));
                    if (capacities[c] <= 0)
                    {
                        Assert.Equal(0, cut);
                    }
                }
            }
        }

        /// <summary>
        /// The cut is the longest prefix that fits: a guest that sized its
        /// buffer to the reported length must not be sent a shorter value.
        /// A cut that stops short of the capacity has to be because the
        /// character starting there does not fit in what is left, not
        /// because the walk gave up early.
        /// </summary>
        private static void AssertCutIsMaximal(long seed, int iteration, string because, byte[] bytes, int cut, int capacity)
        {
            if (capacity > bytes.Length)
            {
                capacity = bytes.Length;
            }
            if (cut >= capacity)
            {
                return;
            }
            int characterBytes = CharacterBytes(bytes[cut]);
            Assert.True(cut + characterBytes > capacity, FuzzDriver.Report(seed, iteration, because,
                "cut " + cut + " of " + bytes.Length + " left a " + characterBytes.ToString(CultureInfo.InvariantCulture)
                + " byte character out of " + capacity + " usable bytes"));
        }

        /// <summary>Bytes the character starting at <paramref name="lead"/> occupies.</summary>
        private static int CharacterBytes(byte lead)
        {
            if (lead < 0x80)
            {
                return 1;
            }
            if ((lead & 0xE0) == 0xC0)
            {
                return 2;
            }
            if ((lead & 0xF0) == 0xE0)
            {
                return 3;
            }
            return 4;
        }

        /// <summary>Mixes well-formed text with hostile byte runs, then trims it anywhere.</summary>
        private static byte[] Generate(Random rng)
        {
            var bytes = new List<byte>();
            int parts = rng.Next(6);
            for (int p = 0; p < parts; p++)
            {
                if (rng.Next(2) == 0)
                {
                    byte[] run = HostileByteRuns[rng.Next(HostileByteRuns.Length)];
                    bytes.AddRange(run);
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(HostileTexts[rng.Next(HostileTexts.Length)]));
                }
            }
            if (rng.Next(3) == 0)
            {
                // A wholly random tail reaches byte values no curated run
                // holds, including the 0xF8..0xFF forms no decoder accepts.
                int tail = rng.Next(8);
                for (int t = 0; t < tail; t++)
                {
                    bytes.Add((byte)rng.Next(0, 256));
                }
            }
            var result = new byte[bytes.Count];
            bytes.CopyTo(result);
            return result;
        }

        private static string GenerateText(Random rng)
        {
            var text = new StringBuilder();
            int parts = 1 + rng.Next(5);
            for (int p = 0; p < parts; p++)
            {
                text.Append(HostileTexts[rng.Next(HostileTexts.Length)]);
            }
            return text.ToString();
        }

        /// <summary>
        /// Mostly small capacities, weighted toward the two values that
        /// matter (one below a real cut, and a cut that lands mid-character)
        /// rather than uniformly over a range a real buffer never spans.
        /// </summary>
        private static int PickCapacity(Random rng, int length)
        {
            switch (rng.Next(6))
            {
                case 0: return 0;
                case 1: return -rng.Next(1, 1000);
                case 2: return rng.Next(0, Math.Min(length, 512) + 1);
                case 3: return length - 1;
                case 4: return length;
                default: return int.MaxValue - rng.Next(0, 4);
            }
        }

        private static string Describe(byte[] bytes, int capacity)
        {
            var sb = new StringBuilder();
            sb.Append("capacity=").Append(capacity.ToString(CultureInfo.InvariantCulture)).Append(" bytes=[");
            for (int i = 0; i < bytes.Length && i < 32; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            if (bytes.Length > 32)
            {
                sb.Append(" ... (").Append(bytes.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes)");
            }
            return sb.Append(']').ToString();
        }
    }
}
