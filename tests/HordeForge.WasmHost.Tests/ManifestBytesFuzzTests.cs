using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Randomized fuzz harness for the manifest file decoder. Every other
    /// fuzz harness in this suite drives the parser from a C# string, which
    /// is already decoded UTF-16; the bytes an operator's file actually
    /// holds reach the host through <see cref="ManifestFiles.TryRead"/>,
    /// and that path is where the encodings a string literal cannot express
    /// live: overlong forms, bare continuations, CESU-8 surrogate halves,
    /// sequences past U+10FFFF, a UTF-16 or UTF-32 BOM that a permissive
    /// reader would silently switch encodings on, and a leading UTF-8 BOM.
    ///
    /// The decoder promises strict UTF-8 (a byte sequence it cannot decode
    /// fails the load with a reason rather than handing guests U+FFFD), a
    /// hard size bound, and a BOM strip, so the harness asserts each of
    /// those against payloads drawn from a real file rather than from a
    /// string, and then feeds every accepted payload to the manifest parser
    /// so a byte sequence that decodes but does not parse still leaves the
    /// module rejected through the typed load exception.
    /// </summary>
    public sealed class ManifestBytesFuzzTests : IDisposable
    {
        /// <summary>
        /// Upper bound on the random passes, because every pass writes and
        /// re-reads a file. A soak raises this through
        /// HORDEFORGE_FUZZ_ITERATIONS, which is the escape hatch for a long
        /// run; the default keeps the suite at a few hundred file writes.
        /// </summary>
        private const int MaxFileIterations = 500;

        /// <summary>Largest hostile payload built, kept under the size bound.</summary>
        private const int MaxPayloadBytes = 4096;

        private readonly string _dir;
        private readonly string _path;

        public ManifestBytesFuzzTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "manifest-bytes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "wasm-mod.toml");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Byte runs that break the UTF-8 grammar in ways a byte flip rarely
        /// reaches: overlong encodings of ASCII, lone continuation bytes,
        /// CESU-8 and WTF-8 surrogate halves, sequences past the last code
        /// point, truncated multi-byte tails, and the BOMs of the encodings
        /// this decoder must refuse.
        /// </summary>
        private static readonly byte[][] HostileByteRuns =
        {
            new byte[] { 0xC0, 0x80 },                                  // overlong NUL
            new byte[] { 0xC0, 0xAF },                                  // overlong '/'
            new byte[] { 0xE0, 0x80, 0xAF },                             // overlong 3-byte
            new byte[] { 0xF0, 0x80, 0x80, 0xAF },                       // overlong 4-byte
            new byte[] { 0x80 },                                        // lone continuation
            new byte[] { 0xBF },                                        // lone continuation
            new byte[] { 0xC2 },                                        // truncated 2-byte
            new byte[] { 0xE2, 0x82 },                                  // truncated 3-byte
            new byte[] { 0xF0, 0x9F, 0x98 },                            // truncated 4-byte
            new byte[] { 0xED, 0xA0, 0x80 },                            // CESU-8 high surrogate
            new byte[] { 0xED, 0xB0, 0x80 },                            // CESU-8 low surrogate
            new byte[] { 0xF4, 0x90, 0x80, 0x80 },                       // past U+10FFFF
            new byte[] { 0xF5, 0x80, 0x80, 0x80 },                       // out of range lead
            new byte[] { 0xFE },                                        // never a UTF-8 lead
            new byte[] { 0xFF },                                        // never a UTF-8 byte
            new byte[] { 0xEF, 0xBB, 0xBF },                            // UTF-8 BOM
            new byte[] { 0xEF, 0xBB },                                  // truncated BOM
            new byte[] { 0xFF, 0xFE },                                  // UTF-16 LE BOM
            new byte[] { 0xFE, 0xFF },                                  // UTF-16 BE BOM
            new byte[] { 0xFF, 0xFE, 0x00, 0x00 },                      // UTF-32 LE BOM
            new byte[] { 0x00, 0x00, 0xFE, 0xFF },                      // UTF-32 BE BOM
            new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF },          // doubled BOM
            new byte[] { 0x00 },                                        // NUL
            new byte[] { 0x00, 0x00, 0x00 },                            // NUL padding
            new byte[] { 0xEF, 0xBF, 0xBE },                            // U+FFFE
            new byte[] { 0xEF, 0xBF, 0xBF },                            // U+FFFF
            Encoding.UTF8.GetBytes("name = \"\U0001F600\""),
            Encoding.UTF8.GetBytes("greeting = \"caf\u00e9 \u4e2d\u6587\""),
        };

        /// <summary>File payloads a real deployment ships, re-encoded byte by byte.</summary>
        private static IReadOnlyList<string> SeedManifests() => FuzzDriver.ManifestSeeds();

        [Fact]
        public void ArbitraryBytesNeverEscapeTheStrictDecodeContract()
        {
            int iterations = Math.Min(FuzzDriver.Iterations(), MaxFileIterations);
            long seed = FuzzDriver.Seed();
            var rng = new Random(unchecked((int)seed));
            IReadOnlyList<string> corpus = SeedManifests();
            for (int i = 0; i < iterations; i++)
            {
                byte[] payload = Generate(rng, corpus);
                Check(seed, i, payload);
            }
        }

        [Fact]
        public void HostileEncodingsAreRejectedWithAReason()
        {
            long seed = FuzzDriver.Seed() + 1;
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            for (int i = 0; i < HostileByteRuns.Length; i++)
            {
                byte[] run = HostileByteRuns[i];
                // Every run on its own, and again surrounded by legal text,
                // so a decoder that only checks the first character passes
                // neither shape.
                Check(seed, i, run);
                Check(seed, i, Concat(utf8.GetBytes("name = \"hello\"\ngreeting = \""), run,
                    utf8.GetBytes("\"\n[limits]\nfuel_per_call = 1\n")));
            }
        }

        [Fact]
        public void OverSizeFilesAreRefusedBeforeTheContentIsRead()
        {
            // One byte past the bound. The stat is what rejects it, so this
            // pins the documented limit rather than the parser.
            var bytes = new byte[ManifestFiles.MaxBytes + 1];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)'a';
            }
            File.WriteAllBytes(_path, bytes);
            Assert.False(ManifestFiles.TryRead(_path, out string content, out string reason));
            Assert.Equal(string.Empty, content);
            Assert.True(reason.Length > 0, "an oversize manifest must report why it was refused");
        }

        [Fact]
        public void AMissingFileIsRefusedNotThrown()
        {
            Assert.False(ManifestFiles.TryRead(Path.Combine(_dir, "absent.toml"), out string content, out string reason));
            Assert.Equal(string.Empty, content);
            Assert.True(reason.Length > 0, "a missing manifest must report why it was refused");
        }

        /// <summary>
        /// Decodes one payload through the real file path and asserts the
        /// contract every caller downstream depends on.
        /// </summary>
        private void Check(long seed, int iteration, byte[] payload)
        {
            File.WriteAllBytes(_path, payload);
            bool ok = false;
            string content = string.Empty;
            string reason = string.Empty;
            long ms = FuzzDriver.Timed(() => ok = ManifestFiles.TryRead(_path, out content, out reason));
            Assert.True(ms < FuzzDriver.PerInputBudgetMs,
                FuzzDriver.Report(seed, iteration, Describe(payload),
                    "decoding " + payload.Length.ToString(CultureInfo.InvariantCulture) + " bytes took "
                    + ms.ToString(CultureInfo.InvariantCulture) + " ms"));

            if (!ok)
            {
                Assert.Equal(string.Empty, content);
                Assert.True(reason.Length > 0, FuzzDriver.Report(seed, iteration, Describe(payload),
                    "a refused manifest returned no reason"));
                return;
            }

            // Accepted bytes are strict UTF-8 by definition of the decoder,
            // so the result must be well-formed.
            Assert.True(FuzzDriver.IsWellFormedUnicode(content), FuzzDriver.Report(seed, iteration, Describe(payload),
                "accepted bytes decoded to a string with a lone surrogate"));
            // Differential: the host decoder and an independently
            // constructed strict decoder must agree on every accepted file,
            // one leading BOM stripped and nothing else touched.
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            string expected = strict.GetString(payload);
            if (expected.Length > 0 && expected[0] == '\uFEFF')
            {
                expected = expected.Substring(1);
            }
            Assert.Equal(expected, content);

            // A payload that decodes is still untrusted input to the parser,
            // and the parser has one documented failure type.
            string outcome = "accepted";
            ModManifest? manifest = null;
            FuzzDriver.Timed(() =>
            {
                try
                {
                    manifest = ModManifest.ParseToml(content, "fuzz");
                }
                catch (WasmModLoadException)
                {
                    outcome = "rejected";
                }
                catch (Exception ex)
                {
                    outcome = "rejected with " + ex.GetType().FullName + ": " + ex.Message;
                }
            });
            Assert.True(outcome == "accepted" || outcome == "rejected", FuzzDriver.Report(seed, iteration, Describe(payload),
                "manifest parse left the module as '" + outcome + "'"));
            if (outcome == "rejected")
            {
                Assert.Null(manifest);
            }
        }

        private static byte[] Generate(Random rng, IReadOnlyList<string> corpus)
        {
            int shape = rng.Next(4);
            if (shape == 0)
            {
                // Raw random bytes: most are undecodable, which is the point.
                var bytes = new byte[rng.Next(0, 512)];
                rng.NextBytes(bytes);
                return bytes;
            }
            if (shape == 1)
            {
                // A hostile run spliced into a seed manifest.
                byte[] run = HostileByteRuns[rng.Next(HostileByteRuns.Length)];
                byte[] seed = Encoding.UTF8.GetBytes(corpus[rng.Next(corpus.Count)]);
                int at = rng.Next(seed.Length + 1);
                return Concat(Slice(seed, 0, at), run, Slice(seed, at, seed.Length - at));
            }
            if (shape == 2)
            {
                // A single run repeated, so truncation and adjacency show up
                // where a decoder's lookahead would trip.
                byte[] run = HostileByteRuns[rng.Next(HostileByteRuns.Length)];
                int count = 1 + rng.Next(Math.Max(1, MaxPayloadBytes / run.Length));
                return Concat(run, run, run, run, run, run, run, run);
            }
            // A seed manifest re-encoded after a byte flip, the closest a
            // file on disk gets to the mutation the text harnesses perform.
            byte[] text = Encoding.UTF8.GetBytes(corpus[rng.Next(corpus.Count)]);
            if (text.Length == 0)
            {
                return text;
            }
            int flip = rng.Next(text.Length);
            text[flip] = (byte)rng.Next(0, 256);
            return text;
        }

        private static byte[] Slice(byte[] bytes, int start, int count)
        {
            var slice = new byte[Math.Max(0, count)];
            Array.Copy(bytes, start, slice, 0, slice.Length);
            return slice;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (byte[] part in parts)
            {
                total += part.Length;
            }
            var joined = new byte[total];
            int at = 0;
            foreach (byte[] part in parts)
            {
                Array.Copy(part, 0, joined, at, part.Length);
                at += part.Length;
            }
            return joined;
        }

        /// <summary>Hex rendering of a payload, capped, for a failure message.</summary>
        private static string Describe(byte[] bytes)
        {
            var sb = new StringBuilder();
            int shown = Math.Min(bytes.Length, 96);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            if (bytes.Length > shown)
            {
                sb.Append(" ... (").Append(bytes.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes)");
            }
            if (sb.Length == 0)
            {
                return "(empty file)";
            }
            return sb.ToString();
        }
    }
}
