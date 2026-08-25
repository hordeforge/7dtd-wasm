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
