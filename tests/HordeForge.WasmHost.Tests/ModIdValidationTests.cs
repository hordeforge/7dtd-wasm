using System;
using HordeForge.WasmHost.Core;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Mod id validation: ids become registry keys, log source tags, trap
    /// message fragments, and module paths. Path separators must never let an
    /// id escape Mods/Wasm, and control characters (C0, DEL, C1) must never
    /// reach log output where they could forge lines or drive terminals.
    /// </summary>
    public sealed class ModIdValidationTests
    {
        [Theory]
        [InlineData("hello")]
        [InlineData("boss-zig")]
        [InlineData("fps_bot")]
        [InlineData("Mod.Name")]
        [InlineData("a b")]
        // First non-control code point after DEL and the C1 block.
        [InlineData("\u00a0nbsp")]
        [InlineData("café")]
        // Only a whole device name is reserved: a longer stem is a folder.
        [InlineData("console")]
        [InlineData("com10")]
        [InlineData("boss.dat2")]
        [InlineData("a.b")]
        public void PlainFolderNamesAreValid(string id)
        {
            Assert.True(ModId.IsValid(id));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("../evil")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        // Windows drive-relative ids are rooted for Path.Combine and would
        // move the module path off Mods/Wasm.
        [InlineData("C:temp")]
        [InlineData("C:")]
        [InlineData("C:\\temp")]
        [InlineData("x\ny")]
        [InlineData("x\ty")]
        [InlineData("x\ry")]
        [InlineData("\u001b[31mred")]
        // C1 range boundaries: lowest and highest control code both rejected.
        [InlineData("\u0080")]
        [InlineData("\u009b31mcsi")]
        [InlineData("trailing\u009f")]
        [InlineData("trailing\u007f")]
        // Unicode line and paragraph separators: not controls, but they
        // break a log line wherever the id is rendered.
        [InlineData("bo\u2028ss")]
        [InlineData("bo\u2029ss")]
        // Invisible format characters (Cf): zero-width space, joiners, word
        // joiner, bidi overrides/isolates, soft hyphen, U+FEFF. All render
        // as nothing, so "bo" + one of these + "ss" must not coexist with a
        // plain "boss" as two distinct registry keys.
        [InlineData("bo\u200bss")]
        [InlineData("bo\u200css")]
        [InlineData("bo\u200dss")]
        [InlineData("bo\u2060ss")]
        [InlineData("bo\u202ess")]
        [InlineData("bo\u2066ss")]
        [InlineData("bo\u00adss")]
        [InlineData("\ufeffboss")]
        // Variation selectors: BMP block and plane-14 (high surrogate 0xDB40).
        [InlineData("boss\ufe0f")]
        [InlineData("boss\udb40\udd00")]
        // A folder name that is not valid UTF-8 (legal on Linux) reaches
        // .NET as U+FFFD; the id no longer re-encodes to its own directory.
        [InlineData("bo\ufffdss")]
        [InlineData("\ufffd")]
        // Windows names a device, not a directory, so a folder with one of
        // these names cannot exist there while the id still validates.
        [InlineData("con")]
        [InlineData("CON")]
        [InlineData("nul")]
        [InlineData("aux.wasm")]
        [InlineData("PRN")]
        [InlineData("com1")]
        [InlineData("LPT9")]
        // The three superscript forms are reserved as well and render like
        // the ASCII ones in a console listing.
        [InlineData("COM\u00b9")]
        [InlineData("lpt\u00b3")]
        // Windows stores a name without its trailing period or space, so the
        // id would name a different directory than the one it spells.
        [InlineData("boss.")]
        [InlineData("boss ")]
        [InlineData("a.b.")]
        // Win32 refuses these in a file name outright, so a folder carrying
        // one exists on Linux and cannot be created on Windows at all.
        [InlineData("bo?ss")]
        [InlineData("bo*ss")]
        [InlineData("bo|ss")]
        [InlineData("bo<ss")]
        [InlineData("bo>ss")]
        [InlineData("bo\"ss")]
        public void UnsafeIdsAreRejected(string? id)
        {
            Assert.False(ModId.IsValid(id));
        }

        [Fact]
        public void LoadModuleRejectsUnsafeId()
        {
            var api = new TestGameHostApi();
            using var host = new WasmModHost(api, new HordeForge.WasmHost.Config.WasmHostConfig());
            byte[] wasm = FixtureBytes();
            foreach (string id in new[] { "../escape", "bad\nid", "" })
            {
                WasmModLoadException ex = Assert.Throws<WasmModLoadException>(() => host.LoadModule(id, wasm));
                Assert.Equal(id, ex.ModId);
                Assert.Empty(host.ModIds);
            }
        }

        private static byte[] FixtureBytes()
        {
            return System.IO.File.ReadAllBytes(
                System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "hello.wasm"));
        }
    }
}
