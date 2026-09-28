using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Seeded random-input driver for the fuzz tests. There is no native
    /// fuzzing engine in this toolchain (no libFuzzer or SharpFuzz on the
    /// project reference list), so the harnesses are randomized property
    /// tests: a fixed seed, a corpus of real manifests, and a mutation pass
    /// over each seed, all driven from <c>dotnet test</c>.
    ///
    /// Scale and reseed through the environment so a failing input is
    /// reproducible and a long soak does not need a second runner:
    ///   HORDEFORGE_FUZZ_SEED=<n>        rerun the same input sequence
    ///   HORDEFORGE_FUZZ_ITERATIONS=<n>  soak longer (default 3000 inputs)
    /// Every assertion failure prints the seed, the iteration index, and the
    /// exact input, so a crash becomes a one-line replay:
    ///   HORDEFORGE_FUZZ_SEED=<n> dotnet test --filter Fuzz
    /// </summary>
    internal static class FuzzDriver
    {
        /// <summary>Inputs generated per test when the environment says nothing.</summary>
        internal const int DefaultIterations = 3000;

        /// <summary>
        /// Wall-clock budget for a single hostile input. A manifest is a
        /// kilobyte-scale config file, so anything past this is a hang (a
        /// quadratic scan or an unbounded loop), which is the liveness bug
        /// this budget exists to surface.
        /// </summary>
        internal const long PerInputBudgetMs = 2000;

        private const int MaxReportedLength = 600;

        /// <summary>
        /// Fragments that break the parser's grammar in ways a byte flip
        /// rarely reaches on its own: quote and bracket imbalance, unknown
        /// and dangling escapes, surrogate halves, C0 and C1 controls, bidi
        /// overrides, line-ending variants, and numeric edge spellings.
        /// </summary>
        private static readonly string[] HostileTokens =
        {
            "\"", "'", "[", "]", "[[", "]]", "=", "#", "\\", "\\u", "\\uD83D", "\\uDE00",
            "\\n", "\\x", "\\U0001F600", "true", "false", ".", ",", " ", "  ", "\t",
            "\r\n", "\r", "\n", "\0", "\u0001", "\u007f", "\u0085", "\u009b", "\ufeff",
            "\u200b", "\u202e", "\u2066", "key = value", "[table]", "[a.b.c]", "\"\\u",
            "0", "-1", "1e999", "-0.0", "9223372036854775807", "9223372036854775808",
            "-9223372036854775808", "-9223372036854775809", "0x10", "1_0", "1.2.3", "007",
            "\ud800", "\udfff", "\ud83d\ude00", "\U0001F600", "settings", "limits",
        };

        /// <summary>Inputs to run when the environment sets no iteration count.</summary>
        internal static int Iterations()
        {
            string? raw = Environment.GetEnvironmentVariable("HORDEFORGE_FUZZ_ITERATIONS");
            if (int.TryParse(raw ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0)
            {
                return count;
            }
            return DefaultIterations;
        }

        /// <summary>Random seed, fixed unless the environment overrides it.</summary>
        internal static int Seed()
        {
            string? raw = Environment.GetEnvironmentVariable("HORDEFORGE_FUZZ_SEED");
            if (int.TryParse(raw ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
            {
                return seed;
            }
            return 20260928;
        }

        /// <summary>Failure text naming the seed, the iteration, and the input.</summary>
        internal static string Report(long seed, int iteration, string input, string because)
        {
            return because + Environment.NewLine
                + "  seed=" + seed.ToString(CultureInfo.InvariantCulture)
                + " iteration=" + iteration.ToString(CultureInfo.InvariantCulture)
                + Environment.NewLine
                + "  input=" + Escape(input)
                + Environment.NewLine
                + "  replay: HORDEFORGE_FUZZ_SEED=" + seed.ToString(CultureInfo.InvariantCulture) + " dotnet test --filter Fuzz";
        }

        /// <summary>Single-line, length-capped rendering of an input for a failure message.</summary>
        internal static string Escape(string text)
        {
            if (text == null)
            {
                return "(null)";
            }
            var sb = new StringBuilder();
            for (int i = 0; i < text.Length && i < MaxReportedLength; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (c < ' ' || c == '\x7f' || (c >= '\u0080' && c <= '\u009f') || char.IsSurrogate(c)
                            || c == '\u202e' || c == '\ufeff' || c == '\u200b')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            if (text.Length > MaxReportedLength)
            {
                sb.Append("... (").Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(" chars)");
            }
            return sb.ToString();
        }

        /// <summary>
        /// True when every surrogate in <paramref name="text"/> is part of a
        /// well-formed pair. A manifest value that fails this could not
        /// round-trip the guest string ABI as valid UTF-8.
        /// </summary>
        internal static bool IsWellFormedUnicode(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    {
                        return false;
                    }
                    i++;
                }
                else if (char.IsLowSurrogate(c))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Seeds drawn from manifests the project actually ships or serves.</summary>
        internal static IReadOnlyList<string> ManifestSeeds()
        {
            var seeds = new List<string>
            {
                // samples/guest-hello/wasm-mod.toml
                "# wasm-mod.toml\r\nname = \"hello\"\r\ndescription = \"Demo guest mod\"\r\nversion = \"0.1.0\"\r\n"
                    + "\r\n[limits]\r\nfuel_per_call = 1000000\r\nmax_memory_bytes = 33554432\r\n"
                    + "\r\n[settings]\r\ngreeting = \"hello survivor\"\r\n",
                // samples/wasm.toml.example
                "[limits]\nfuel_per_call = 1000000\nmax_memory_bytes = 4294967296\n"
                    + "\n[settings]\ngreeting = \"hello survivor\"\n",
                // samples/parachute/wasm-mod.toml
                "name = \"parachute\"\ndescription = \"glide exemption\"\nversion = \"0.1.0\"\n",
                // The key shapes the grammar turns on: escapes, arrays, tables,
                // comments in and out of strings, and an astral character.
                "k = \"a\\tb\"\nl = 'raw \\n'\nn = -1\nf = 6.0\nb = false\narr = [1, \"x, y\", 'z']\n"
                    + "[t]\nk = 1\n[t.u]\nk = \"\\uD83D\\uDE00\"\nq = \"tail # not a comment\"\n",
            };
            // tests/fixtures/parachute-config.toml: a real config file of the
            // sibling server, so the fuzzer starts from text that ships.
            string staged = Path.Combine(AppContext.BaseDirectory, "fixtures", "parachute-config.toml");
            if (File.Exists(staged))
            {
                seeds.Add(File.ReadAllText(staged));
            }
            return seeds;
        }

        /// <summary>
        /// One mutation of <paramref name="text"/>: a character flip, an
        /// insertion, a deletion, a duplicated span, a truncation, a hostile
        /// token, a line duplication, or a splice with another seed. The
        /// mutator is deliberately shallow so every input stays close to a
        /// manifest an operator would plausibly write.
        /// </summary>
        internal static string Mutate(Random rng, string text, IReadOnlyList<string> corpus)
        {
            if (text.Length == 0)
            {
                return HostileTokens[rng.Next(HostileTokens.Length)];
            }
            switch (rng.Next(9))
            {
                case 0:
                {
                    int at = rng.Next(text.Length);
                    return text.Substring(0, at) + HostileTokens[rng.Next(HostileTokens.Length)] + text.Substring(at);
                }
                case 1:
                {
                    int at = rng.Next(text.Length);
                    return text.Substring(0, at) + text[at];
                }
                case 2:
                {
                    int at = rng.Next(text.Length);
                    return text.Substring(0, at) + text.Substring(at + 1);
                }
                case 3:
                {
                    int start = rng.Next(text.Length);
                    int length = rng.Next(1, Math.Min(24, text.Length - start) + 1);
                    int at = rng.Next(text.Length);
                    return text.Substring(0, at) + text.Substring(start, length) + text.Substring(at);
                }
                case 4:
                    return text.Substring(0, rng.Next(text.Length + 1));
                case 5:
                {
                    char c = text[rng.Next(text.Length)];
                    char replacement = (char)rng.Next(0, 0x2100);
                    int at = rng.Next(text.Length);
                    return text.Substring(0, at) + replacement + text.Substring(at + 1);
                }
                case 6:
                {
                    int at = rng.Next(text.Length);
                    return text.Substring(0, at) + text.Substring(at) + text.Substring(at);
                }
                case 7:
                {
                    string other = corpus[rng.Next(corpus.Count)];
                    int a = rng.Next(text.Length);
                    int b = rng.Next(other.Length);
                    return text.Substring(0, a) + other.Substring(b);
                }
                default:
                {
                    // Deep array nesting: the documented stack-overflow guard.
                    int depth = 1 + rng.Next(600);
                    return "limits = " + new string('[', depth) + new string(']', depth) + "\n";
                }
            }
        }

        /// <summary>Milliseconds spent by <paramref name="action"/>.</summary>
        internal static long Timed(Action action)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
    }
}
