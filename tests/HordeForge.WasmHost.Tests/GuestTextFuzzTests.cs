using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Randomized fuzz harness for the host functions that take guest- or
    /// client-supplied text: a guest can return any UTF-8 string, and the
    /// host routes it through the log sanitizer and the mod id validator
    /// before it reaches a log source tag, a trap message, or a module path.
    /// The fuzzer generates hostile text (control ranges, bidi controls,
    /// lone surrogates, path separators, drive-relative colon forms, mixed
    /// line endings) and asserts the properties the rest of the host relies
    /// on:
    ///
    ///   * Clean preserves length and every kept character at its original
    ///     index, leaves no strippable character behind, and is idempotent,
    ///     so no second pass can change a line that has already been logged,
    ///   * an id the validator accepts carries no control or invisible
    ///     formatting character, and Path.Combine of it with a module root
    ///     cannot escape that root, so a guest-chosen id can never point the
    ///     loader outside Mods/Wasm.
    /// </summary>
    public sealed class GuestTextFuzzTests
    {
        private static readonly string[] HostileRuns =
        {
            "\0", "\n", "\r", "\t", "\u0001", "\u007f", "\u0085", "\u009b", "\u200b",
            "\u202a", "\u202e", "\u2066", "\u2069", "\ufeff", "\ufe0f", "\ud83d\ude00",
            "\u2028", "\u2029",
            "\ud800", "\udfff", "/", "\\", ":", ".", "..", " ", "id", "wasm/hello", "C:name",
            "\u0301", "€", "\U0001F600", "0", "-", "#", "=", "0.0",
        };

        private static readonly string[] SeedIds = { "hello", "parachute", "fps-bot", "a", "boss_zig" };

        [Fact]
        public void SanitizedTextKeepsItsShapeAndLosesNothing()
        {
            int iterations = FuzzDriver.Iterations();
            long seed = FuzzDriver.Seed();
            var rng = new Random(unchecked((int)seed));
            for (int i = 0; i < iterations; i++)
            {
                string text = Generate(rng, HostileRuns);
                string cleaned = TextSanitizer.Clean(text);
                Assert.Equal(text.Length, cleaned.Length);
                for (int c = 0; c < text.Length; c++)
                {
                    Assert.True(!IsStripped(text[c]) || cleaned[c] == '?', FuzzDriver.Report(seed, i, text,
                        "control at index " + c.ToString(CultureInfo.InvariantCulture) + " survived sanitization"));
                    Assert.True(IsStripped(text[c]) || cleaned[c] == text[c], FuzzDriver.Report(seed, i, text,
                        "kept character at index " + c.ToString(CultureInfo.InvariantCulture) + " was rewritten"));
                }
                Assert.Equal(cleaned, TextSanitizer.Clean(cleaned));
            }
        }

        [Fact]
        public void AcceptedModIdsCannotEscapeTheModuleRoot()
        {
            int iterations = FuzzDriver.Iterations();
            long seed = FuzzDriver.Seed() + 1;
            var rng = new Random(unchecked((int)seed));
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fuzz-mods"));
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            for (int i = 0; i < iterations; i++)
            {
                string id = rng.Next(3) == 0 ? SeedIds[rng.Next(SeedIds.Length)] : Generate(rng, HostileRuns);
                if (id.Length == 0 || !FuzzDriver.IsWellFormedUnicode(id))
                {
                    continue;
                }
                if (!ModId.IsValid(id))
                {
                    continue;
                }
                foreach (char c in id)
                {
                    Assert.True(!IsStripped(c), FuzzDriver.Report(seed, i, id,
                        "accepted id contains a control or invisible formatting character U+"
                        + ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
                }
                string combined = Path.GetFullPath(Path.Combine(root, id));
                Assert.True(
                    combined.StartsWith(rootWithSeparator, StringComparison.Ordinal)
                        || string.Equals(combined, root, StringComparison.Ordinal),
                    FuzzDriver.Report(seed, i, id, "accepted id resolves outside the module root: " + combined));
            }
        }

        [Fact]
        public void MalformedTextReachesTheHostAsDataNotAsControl()
        {
            // A representative set of live-shaped inputs, so the property
            // stays checked even when the randomized passes are scaled down.
            var cases = new List<string>
            {
                "hello survivor",
                "boss\u202edead\u202c",
                "line one\nline two\r\nline three",
                "esc\u009b31mred",
                "nul\0byte",
                "wasm/hello",
                "C:name",
                "..",
                string.Empty,
            };
            foreach (string text in cases)
            {
                string cleaned = TextSanitizer.Clean(text);
                Assert.Equal(text.Length, cleaned.Length);
                foreach (char c in cleaned)
                {
                    Assert.True(!IsStripped(c), "sanitized text kept a strippable character: " + FuzzDriver.Escape(cleaned));
                }
            }
        }

        private static string Generate(Random rng, IReadOnlyList<string> parts)
        {
            int count = rng.Next(5);
            if (count == 0)
            {
                return string.Empty;
            }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (rng.Next(8) == 0)
                {
                    // Below the surrogate block: the generator must not
                    // manufacture a malformed pair, or the invariant it
                    // checks would be testing the generator, not the host.
                    sb.Append((char)rng.Next(0, 0xD7FF));
                }
                sb.Append(parts[rng.Next(parts.Count)]);
            }
            return sb.ToString();
        }

        private static bool IsStripped(char c)
        {
            bool control = c < ' ' || c == '\x7f' || (c >= '\u0080' && c <= '\u009f');
            bool invisible = (c >= '\u202a' && c <= '\u202e') || (c >= '\u2066' && c <= '\u2069') || c == '\ufeff';
            bool lineBreak = c == '\u2028' || c == '\u2029';
            return control || invisible || lineBreak;
        }
    }
}
