using System;
using System.Collections.Generic;
using System.Globalization;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Randomized fuzz harness for the manifest parser, the highest-risk
    /// untrusted-input surface in the host: a mod directory is a place a
    /// player or a dropped modlet can write to, and every byte of
    /// wasm-mod.toml reaches the hand-written TOML parser before the module
    /// is rejected. The fuzzer covers two input families, a structure-aware
    /// generator that reaches the accepted deep paths and a mutation pass
    /// over the manifests the project actually ships, and asserts the
    /// contract the loader relies on rather than only "it did not crash":
    ///
    ///   * the only exception that may leave ParseToml is
    ///     WasmModLoadException, so a malformed manifest can never surface
    ///     as a raw FormatException, an index error, or a
    ///     NullReferenceException in the mod load path,
    ///   * one input stays inside a wall-clock budget, so a hostile file
    ///     cannot wedge the loader,
    ///   * an accepted manifest binds the fields inside their documented
    ///     ranges and yields well-formed Unicode values (a lone surrogate
    ///     could not round-trip the guest string ABI),
    ///   * parsing is deterministic: the same bytes bind the same values.
    /// </summary>
    public sealed class ManifestFuzzTests
    {
        private const int MaxFuelPerCallCeiling = 50_000_000;

        private static readonly string[] Keys = { "fuel_per_call", "max_memory_bytes", "greeting", "k", "a-b", "x_1" };
        private static readonly string[] Tables = { "limits", "settings", "t", "a.b.c", "a.b", "" };
        private static readonly string[] Scalars =
        {
            "1", "-1", "0", "9223372036854775807", "9223372036854775808", "-9223372036854775809",
            "0.0", "-6.0", "1e999", "true", "false", "\"\"", "\"text\"", "'raw'", "\"a\\tb\\n\"",
            "\"\\uD83D\\uDE00\"", "\"\\uD83D\"", "\"\\ud800\"", "\"tail # hash\"", "'has \" quote'",
            "[1, 2, 3]", "[\"a, b\", 'c']", "[]", "garbage", "\"unterminated", "0x1f", "1_0",
        };

        [Fact]
        public void GeneratedManifestsHonorTheLoadContract()
        {
            int iterations = FuzzDriver.Iterations();
            long seed = FuzzDriver.Seed();
            var rng = new Random(unchecked((int)seed));
            var corpus = FuzzDriver.ManifestSeeds();
            for (int i = 0; i < iterations; i++)
            {
                string input = Generate(rng, corpus);
                AssertHonorsLoadContract(seed, i, input, rng.Next(2) == 0 ? "fuzz" : FuzzDriver.Mutate(rng, input, corpus));
            }
        }

        [Fact]
        public void MutatedShippedManifestsHonorTheLoadContract()
        {
            int iterations = FuzzDriver.Iterations();
            long seed = FuzzDriver.Seed() + 1;
            var rng = new Random(unchecked((int)seed));
            IReadOnlyList<string> corpus = FuzzDriver.ManifestSeeds();
            for (int i = 0; i < iterations; i++)
            {
                string input = FuzzDriver.Mutate(rng, corpus[rng.Next(corpus.Count)], corpus);
                AssertHonorsLoadContract(seed, i, input, FuzzDriver.Mutate(rng, input, corpus));
            }
        }

        /// <summary>
        /// The deep-array and deep-table shapes an attacker reaches for are
        /// few and cheap, so they run as their own fixed-shape test rather
        /// than waiting for the random pass to stumble on them. Both
        /// directions are budgeted: a shape that must be rejected has to
        /// fail with the load exception, and a huge but legal shape has to
        /// parse without falling over.
        /// </summary>
        [Fact]
        public void HostileNestingIsRejectedFast()
        {
            var rejected = new List<string>
            {
                "x = " + new string('[', 100_000) + new string(']', 100_000) + "\n",
                "x = " + new string('[', 100_000),
                "[" + string.Join(".", new string[100_000]) + "]\n",
                new string('[', 200_000) + "\n",
                new string('=', 200_000) + "\n",
                "k = \"" + string.Concat(System.Linq.Enumerable.Repeat("\\uD83D", 20_000)) + "\"\n",
                "k = \"" + new string('\\', 200_001) + "\"\n",
                "k = '" + new string('\'', 200_000) + "x\n",
                "k = \"" + new string('#', 200_000) + "\n",
                "k = \"" + new string('=', 200_000) + "\n",
            };
            for (int i = 0; i < rejected.Count; i++)
            {
                string input = rejected[i];
                string outcome = "accepted";
                long ms = FuzzDriver.Timed(() => outcome = OutcomeOf(() => ModManifest.ParseToml(input, "fuzz")));
                Assert.True(
                    outcome == "rejected",
                    FuzzDriver.Report(0, i, input, "hostile shape " + i.ToString(CultureInfo.InvariantCulture) + " was " + outcome));
                AssertInBudget(i, input, ms);
            }

            var accepted = new List<string>
            {
                "k = \"" + new string('F', 200_000) + "\"\n",
                "k = \"" + string.Concat(System.Linq.Enumerable.Repeat("\\uD83D\\uDE00", 20_000)) + "\"\n",
                "k = '" + new string('x', 200_000) + "'\n",
                "k = \"" + new string('x', 200_000) + "\" # tail\n",
            };
            for (int i = 0; i < accepted.Count; i++)
            {
                string input = accepted[i];
                ModManifest? parsed = null;
                long ms = FuzzDriver.Timed(() => parsed = ModManifest.ParseToml(input, "fuzz"));
                AssertInBudget(rejected.Count + i, input, ms);
                Assert.True(parsed != null, FuzzDriver.Report(0, i, input, "legal large manifest was rejected"));
            }
        }

        private static string OutcomeOf(Func<ModManifest> parse)
        {
            try
            {
                parse();
                return "accepted";
            }
            catch (WasmModLoadException)
            {
                return "rejected";
            }
            catch (Exception ex)
            {
                return "rejected with " + ex.GetType().FullName + ": " + ex.Message;
            }
        }

        private static void AssertInBudget(int shape, string input, long ms)
        {
            Assert.True(
                ms < FuzzDriver.PerInputBudgetMs,
                FuzzDriver.Report(0, shape, input, "shape " + shape.ToString(CultureInfo.InvariantCulture)
                    + " took " + ms.ToString(CultureInfo.InvariantCulture) + " ms, budget is "
                    + FuzzDriver.PerInputBudgetMs.ToString(CultureInfo.InvariantCulture) + " ms"));
        }

        private static void AssertHonorsLoadContract(long seed, int iteration, string primary, string secondary)
        {
            Check(seed, iteration, primary);
            Check(seed, iteration, secondary);
        }

        private static void Check(long seed, int iteration, string input)
        {
            ModManifest? parsed = null;
            long ms = FuzzDriver.Timed(() =>
            {
                try
                {
                    parsed = ModManifest.ParseToml(input, "fuzz");
                }
                catch (WasmModLoadException)
                {
                    // The documented rejection path.
                }
                catch (Exception ex)
                {
                    Assert.Fail(FuzzDriver.Report(seed, iteration, input,
                        "ParseToml leaked " + ex.GetType().FullName + " instead of WasmModLoadException: " + ex.Message));
                }
            });
            Assert.True(
                ms < FuzzDriver.PerInputBudgetMs,
                FuzzDriver.Report(seed, iteration, input, "parse took " + ms.ToString(CultureInfo.InvariantCulture)
                    + " ms, budget is " + FuzzDriver.PerInputBudgetMs.ToString(CultureInfo.InvariantCulture) + " ms"));

            if (parsed == null)
            {
                return;
            }

            AssertBound(seed, iteration, input, parsed, "first parse");
            if (parsed.FuelPerCall.HasValue)
            {
                Assert.InRange(parsed.FuelPerCall.Value, 1UL, (ulong)MaxFuelPerCallCeiling);
            }
            if (parsed.MaxMemoryBytes.HasValue)
            {
                Assert.True(parsed.MaxMemoryBytes.Value >= 1UL, FuzzDriver.Report(seed, iteration, input, "max_memory_bytes bound below 1"));
            }
            foreach (KeyValuePair<string, string> setting in parsed.Settings)
            {
                Assert.True(setting.Key.Length > 0, FuzzDriver.Report(seed, iteration, input, "empty settings key"));
                Assert.True(setting.Value != null, FuzzDriver.Report(seed, iteration, input, "null settings value for " + setting.Key));
                Assert.True(
                    FuzzDriver.IsWellFormedUnicode(setting.Value),
                    FuzzDriver.Report(seed, iteration, input, "settings value for " + setting.Key
                        + " is not well-formed Unicode: " + FuzzDriver.Escape(setting.Value)));
            }

            // Determinism: the loader re-reads a manifest on every load and on
            // a shared-file change, so the same bytes must bind identically.
            ModManifest again;
            try
            {
                again = ModManifest.ParseToml(input, "fuzz");
            }
            catch (Exception ex)
            {
                Assert.Fail(FuzzDriver.Report(seed, iteration, input,
                    "second parse threw " + ex.GetType().FullName + " for input the first parse accepted: " + ex.Message));
                return;
            }
            AssertBound(seed, iteration, input, again, "second parse");
            Assert.Equal(parsed.FuelPerCall, again.FuelPerCall);
            Assert.Equal(parsed.MaxMemoryBytes, again.MaxMemoryBytes);
            Assert.Equal(parsed.Settings.Count, again.Settings.Count);
            foreach (KeyValuePair<string, string> setting in parsed.Settings)
            {
                Assert.True(again.Settings.TryGetValue(setting.Key, out string? value), FuzzDriver.Report(seed, iteration, input, "settings key " + setting.Key + " vanished on reparse"));
                Assert.Equal(setting.Value, value);
            }
        }

        private static void AssertBound(long seed, int iteration, string input, ModManifest manifest, string phase)
        {
            Assert.True(manifest.Settings != null, FuzzDriver.Report(seed, iteration, input, phase + ": null settings table"));
        }

        /// <summary>
        /// Structure-aware generator: lines drawn from the grammar the
        /// parser accepts plus the shapes it must reject, assembled with the
        /// shipped manifests as fragments.
        /// </summary>
        private static string Generate(Random rng, IReadOnlyList<string> corpus)
        {
            var sb = new System.Text.StringBuilder();
            int lines = 1 + rng.Next(12);
            for (int i = 0; i < lines; i++)
            {
                switch (rng.Next(10))
                {
                    case 0:
                    {
                        string table = Tables[rng.Next(Tables.Length)];
                        if (table.Length > 0)
                        {
                            sb.Append('[').Append(table).Append(']').Append(rng.Next(4) == 0 ? " # c" : string.Empty).Append('\n');
                        }
                        break;
                    }
                    case 1:
                    {
                        string fragment = corpus[rng.Next(corpus.Count)];
                        int lines2 = fragment.Split('\n').Length;
                        sb.Append(fragment.Split('\n')[rng.Next(lines2)]).Append('\n');
                        break;
                    }
                    default:
                    {
                        string key = Keys[rng.Next(Keys.Length)];
                        string value = Scalars[rng.Next(Scalars.Length)];
                        string quoted = rng.Next(6) == 0 ? "\"" + key + "\"" : key;
                        sb.Append(quoted).Append(rng.Next(8) == 0 ? "==" : "=").Append(value).Append('\n');
                        break;
                    }
                }
            }
            if (rng.Next(6) == 0)
            {
                return sb.ToString().Replace("\n", "\r\n", StringComparison.Ordinal);
            }
            if (rng.Next(8) == 0)
            {
                sb.Append('\uFEFF');
            }
            if (rng.Next(8) == 0)
            {
                // A bare carriage return: not a line separator here, so it
                // rides along inside whatever value it lands in.
                sb.Append("k = \"a\rb\"\n");
            }
            return sb.ToString();
        }
    }
}
