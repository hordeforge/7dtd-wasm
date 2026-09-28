using System;
using System.IO;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Module tree resolution: the primary Mods/Wasm wins per id, modlet
    /// trees only supply what it lacks, and only existing directories count.
    /// </summary>
    public sealed class ModuleRootsTests : IDisposable
    {
        private readonly string _base;

        public ModuleRootsTests()
        {
            _base = Path.Combine(Path.GetTempPath(), "modroots-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_base);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_base, recursive: true);
            }
            catch (Exception)
            {
            }
        }

        [Fact]
        public void OrderSkipsMissingRoots()
        {
            string primary = Dir("primary");
            string missing = Path.Combine(_base, "nope");
            var ordered = ModuleRoots.Order(primary, new[] { missing });
            Assert.Equal(new[] { primary }, ordered);
        }

        [Fact]
        public void OrderCollapsesDuplicates()
        {
            string primary = Dir("primary");
            var ordered = ModuleRoots.Order(primary, new[] { primary });
            Assert.Equal(new[] { primary }, ordered);
        }

        [Fact]
        public void OrderHandlesEmptyExtras()
        {
            string primary = Dir("primary");
            var ordered = ModuleRoots.Order(primary, Array.Empty<string>());
            Assert.Equal(new[] { primary }, ordered);
        }

        [Fact]
        public void CollectExtraSkipsOwnModletAndSorts()
        {
            string mods = Dir("Mods");
            Directory.CreateDirectory(Path.Combine(mods, "b-mod", "Wasm"));
            Directory.CreateDirectory(Path.Combine(mods, "a-mod", "Wasm"));
            Directory.CreateDirectory(Path.Combine(mods, "bridge", "Wasm"));
            Directory.CreateDirectory(Path.Combine(mods, "plain"));
            var extras = ModuleRoots.CollectExtra(mods, Path.Combine(mods, "bridge"), out string failure);
            Assert.Empty(failure);
            Assert.Equal(
                new[]
                {
                    Path.Combine(mods, "a-mod", "Wasm"),
                    Path.Combine(mods, "b-mod", "Wasm"),
                },
                extras);
        }

        [Fact]
        public void CollectExtraHandlesMissingModsDir()
        {
            // No Mods/ folder is a server carrying no modlets, not a failure:
            // the caller must not be handed a warning for an empty result.
            var extras = ModuleRoots.CollectExtra(
                Path.Combine(_base, "nope"), Path.Combine(_base, "bridge"), out string failure);
            Assert.Empty(extras);
            Assert.Empty(failure);
        }

        [Fact]
        public void CollectExtraReportsAnUnreadableModsDir()
        {
            // A mods path the runtime rejects outright stands in for the
            // permission and IO failures a real server hits. An empty list
            // with no reason is indistinguishable from "no modlet carries a
            // Wasm tree", which is why the reason exists.
            string invalid = Path.Combine(_base, "bad\0name");
            var extras = ModuleRoots.CollectExtra(invalid, Path.Combine(_base, "bridge"), out string failure);
            Assert.Empty(extras);
            Assert.Contains("cannot list modlets", failure, StringComparison.Ordinal);
        }

        [Fact]
        public void PrimaryWinsOverModletTree()
        {
            string primary = Dir("primary");
            string modlet = Dir("modlet");
            Directory.CreateDirectory(Path.Combine(primary, "mod"));
            Directory.CreateDirectory(Path.Combine(modlet, "mod"));
            File.WriteAllText(Path.Combine(primary, "mod", "module.wasm"), "p");
            File.WriteAllText(Path.Combine(modlet, "mod", "module.wasm"), "m");
            var roots = ModuleRoots.Order(primary, new[] { modlet });
            Assert.Equal(Path.Combine(primary, "mod", "module.wasm"),
                ModuleRoots.ResolveFile(roots, "mod", "module.wasm"));
        }

        [Fact]
        public void ModletTreeSuppliesMissingIds()
        {
            string primary = Dir("primary");
            string modlet = Dir("modlet");
            Directory.CreateDirectory(Path.Combine(modlet, "mod"));
            File.WriteAllText(Path.Combine(modlet, "mod", "module.wasm"), "m");
            var roots = ModuleRoots.Order(primary, new[] { modlet });
            Assert.Equal(Path.Combine(modlet, "mod", "module.wasm"),
                ModuleRoots.ResolveFile(roots, "mod", "module.wasm"));
        }

        [Fact]
        public void DirWithoutFileDoesNotClaimModule()
        {
            string primary = Dir("primary");
            string modlet = Dir("modlet");
            Directory.CreateDirectory(Path.Combine(primary, "mod"));
            Directory.CreateDirectory(Path.Combine(modlet, "mod"));
            File.WriteAllText(Path.Combine(modlet, "mod", "module.wasm"), "m");
            var roots = ModuleRoots.Order(primary, new[] { modlet });
            Assert.Equal(Path.Combine(modlet, "mod", "module.wasm"),
                ModuleRoots.ResolveFile(roots, "mod", "module.wasm"));
        }

        [Fact]
        public void IdSpelledDifferentlyFromTheFolderDoesNotResolve()
        {
            // Windows and macOS match folder names case-insensitively, so
            // without the exact-spelling check "HELLO" would resolve the
            // "hello" module there and not on Linux. The outcome must be the
            // same on every platform: not found.
            string primary = Dir("primary");
            Directory.CreateDirectory(Path.Combine(primary, "hello"));
            File.WriteAllText(Path.Combine(primary, "hello", "module.wasm"), "m");
            var roots = ModuleRoots.Order(primary, Array.Empty<string>());
            Assert.Equal(string.Empty, ModuleRoots.ResolveDir(roots, "HELLO"));
            Assert.Equal(string.Empty, ModuleRoots.ResolveFile(roots, "HELLO", "module.wasm"));
            Assert.Equal(Path.Combine(primary, "hello", "module.wasm"),
                ModuleRoots.ResolveFile(roots, "hello", "module.wasm"));
        }

        [Fact]
        public void InvalidIdResolvesToEmpty()
        {
            string primary = Dir("primary");
            var roots = ModuleRoots.Order(primary, Array.Empty<string>());
            Assert.Equal(string.Empty, ModuleRoots.ResolveDir(roots, "../evil"));
            Assert.Equal(string.Empty, ModuleRoots.ResolveFile(roots, "../evil", "module.wasm"));
        }

        private string Dir(string name)
        {
            string path = Path.Combine(_base, name);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
