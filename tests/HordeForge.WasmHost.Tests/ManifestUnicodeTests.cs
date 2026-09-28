using System;
using HordeForge.WasmHost;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Unicode mechanics of the manifest parser, exercised through the
    /// public ModManifest API: astral-plane characters must survive the
    /// string ABI round-trip (so escaped lone surrogates are rejected, not
    /// silently corrupted into replacement characters).
    /// </summary>
    public sealed class ManifestUnicodeTests
    {
        private const string Emoji = "\U0001F600";

        [Fact]
        public void RawAstralCharactersSurvive()
        {
            ModManifest manifest = ModManifest.ParseToml("[settings]\nboss_name = \"" + Emoji + "\"", "test");
            Assert.Equal(Emoji, manifest.Settings["boss_name"]);
        }

        [Fact]
        public void EscapedSurrogatePairSurvives()
        {
            ModManifest manifest = ModManifest.ParseToml("[settings]\nboss_name = \"\\uD83D\\uDE00\"", "test");
            Assert.Equal(Emoji, manifest.Settings["boss_name"]);
        }

        [Theory]
        [InlineData("\"\\uD83D\"")]          // high surrogate escape, string ends
        [InlineData("\"\\uD83Dx\"")]         // interrupted by a plain character
        [InlineData("\"\\uD83D\\n\"")]       // interrupted by another escape
        [InlineData("\"\\uD83D\\uD83D\"")]   // followed by a second high
        [InlineData("\"\\uDE00\"")]          // low surrogate with no leading high
        public void LoneSurrogateEscapesAreRejected(string value)
        {
            Assert.Throws<WasmManifestException>(
                () => ModManifest.ParseToml("[settings]\nboss_name = " + value, "test"));
        }

        [Theory]
        [InlineData("\"\\uD800\\uDC00\"", "\U00010000")]
        [InlineData("\"a\\uD801\\uDC37b\"", "a\U00010437b")]
        public void TomlEscapedSurrogatePairsDecode(string value, string expected)
        {
            ModManifest manifest = ModManifest.ParseToml("[settings]\nboss_name = " + value, "test");
            Assert.Equal(expected, manifest.Settings["boss_name"]);
        }

        [Fact]
        public void RawLoneSurrogatesAreRejected()
        {
            // A file read rejects invalid UTF-8 outright (ManifestFiles), so
            // these only reach the parser from a caller that decoded the text
            // itself. The ABI has no form for a lone surrogate, so the parser
            // rejects it here rather than handing the guest a value that
            // cannot round-trip. The cases are built in code because a lone
            // surrogate cannot survive test-data serialization.
            var cases = new[]
            {
                "\"" + "\uD800" + "\"",      // raw high surrogate, string ends
                "\"" + "\uD800" + "x\"",     // raw high surrogate, no low half
                "\"" + "\uDC00" + "\"",      // raw low surrogate, no high half
                "\"a\uDE00b\"",               // raw low surrogate after text
                "'\uD800'",                   // literal string, raw high surrogate
                "'\uDC00'",                   // literal string, raw low surrogate
                "\"\\uD83D\\uDE00\uD83D\"",  // valid escape pair, then a raw high
            };
            foreach (string value in cases)
            {
                Assert.Throws<WasmManifestException>(
                    () => ModManifest.ParseToml("[settings]\nboss_name = " + value, "test"));
            }
        }

        [Fact]
        public void RawSurrogatePairSurvives()
        {
            ModManifest manifest = ModManifest.ParseToml("[settings]\nboss_name = \"" + Emoji + "a\"", "test");
            Assert.Equal(Emoji + "a", manifest.Settings["boss_name"]);
        }

        [Fact]
        public void UnterminatedQuotedKeyIsRejected()
        {
            Assert.Throws<WasmManifestException>(
                () => ModManifest.ParseToml("[settings]\n\"boss_name = \"dave\"", "test"));
        }

        [Theory]
        [InlineData("\"\r\"")]         // a stray CR rides through
        [InlineData("\"a\rb\"")]       // CR in the middle of the value
        [InlineData("\"\u0000\"")]     // NUL
        [InlineData("\"a\u001bb\"")]   // ESC, which a terminal decodes
        [InlineData("\"\u0085\"")]     // NEL, a line break to some renderers
        [InlineData("\"\u007f\"")]     // DEL
        [InlineData("'a\rb'")]         // raw CR in a literal string
        public void RawControlCharactersInStringsAreRejected(string value)
        {
            // TOML allows no raw control character in a quoted string other
            // than the tab. One that got through would reach the guest
            // inside a setting value and, from there, a log line or a chat
            // message the host quotes it into.
            Assert.Throws<WasmManifestException>(
                () => ModManifest.ParseToml("[settings]\nboss_name = " + value, "test"));
        }

        [Fact]
        public void RawTabAndEscapedControlsSurvive()
        {
            ModManifest manifest = ModManifest.ParseToml("[settings]\nboss_name = \"a\tb\\nc\\rd\"", "test");
            Assert.Equal("a\tb\nc\rd", manifest.Settings["boss_name"]);
        }
    }
}
