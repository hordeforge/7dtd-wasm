using System;
using HordeForge.WasmHost;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Unicode mechanics of the manifest parsers, exercised through the
    /// public ModManifest API: astral-plane characters must survive the
    /// string ABI round-trip (so escaped lone surrogates are rejected, not
    /// silently corrupted into replacement characters), and quoted TOML
    /// keys must define the same identity as their unquoted spelling.
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
            Assert.Throws<WasmModLoadException>(
                () => ModManifest.ParseToml("[settings]\nboss_name = " + value, "test"));
        }

        [Theory]
        [InlineData("\"\\uD800\\uDC00\"")]
        [InlineData("\"a\\uD801\\uDC37b\"")]
        public void JsonEscapedSurrogatePairsDecode(string value)
        {
            string json = "{\"settings\": {\"boss_name\": " + value + "}}";
            ModManifest manifest = ModManifest.Parse(json, "test");
            // The deprecated JSON manifest binds only limits, but parsing
            // the string must succeed without throwing.
            Assert.NotNull(manifest);
        }

        [Fact]
        public void JsonLoneLowSurrogateEscapeIsRejected()
        {
            Assert.Throws<WasmModLoadException>(
                () => ModManifest.Parse("{\"limits\": {\"note\": \"\\uDE00\"}}", "test"));
        }

        [Fact]
        public void QuotedKeyDefinesTheUnquotedIdentity()
        {
            ModManifest manifest = ModManifest.ParseToml(
                "[settings]\n\"boss_name\" = \"maci\"\n'other' = 7", "test");
            Assert.True(manifest.Settings.ContainsKey("boss_name"));
            Assert.True(manifest.Settings.ContainsKey("other"));
            Assert.False(manifest.Settings.ContainsKey("\"boss_name\""));
        }

        [Fact]
        public void QuotedHeaderPartNamesTheSameTable()
        {
            ModManifest manifest = ModManifest.ParseToml(
                "[\"settings\"]\nboss_name = \"maci\"", "test");
            Assert.Equal("maci", manifest.Settings["boss_name"]);
        }

        [Fact]
        public void UnterminatedQuotedKeyIsRejected()
        {
            Assert.Throws<WasmModLoadException>(
                () => ModManifest.ParseToml("[settings]\n\"boss_name = \"maci\"", "test"));
        }
    }
}
