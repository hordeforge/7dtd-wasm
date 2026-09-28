using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Randomized fuzz harness for module tree resolution, the one place an
    /// id that arrived from outside the process (a console command, a
    /// staged modlet folder, a reload request) is turned into a filesystem
    /// path. <see cref="ModuleRoots.ResolveDir"/> and
    /// <see cref="ModuleRoots.ResolveFile"/> combine the id, and for the
    /// file the caller's file name, onto a root and then hand the result to
    /// a file read, so a resolution that escapes the root reads a module
    /// from outside Mods/Wasm, and a resolution that ignores the on-disk
    /// spelling registers one module twice under different ids.
    ///
    /// The tree is staged once, with the shapes that make those mistakes
    /// possible (a module that exists in two roots, a directory without its
    /// file, a case-variant sibling, a decoy file one level above the
    /// roots), and the fuzzer drives it with hostile ids and file names:
    /// separators, dot segments, drive-relative and absolute forms, control
    /// and C1 characters, NUL, trailing dots and spaces, device names,
    /// case flips, and the astral and bidi characters. Every resolution is
    /// then checked against the filesystem, so an escaping path is a failed
    /// assertion rather than a read that quietly happened.
    /// </summary>
    public sealed class ModuleRootsFuzzTests : IDisposable
    {
        private readonly string _base;
        private readonly string _primary;
        private readonly string _secondary;
        private readonly string _outside;
        private readonly IReadOnlyList<string> _roots;

        public ModuleRootsFuzzTests()
        {
            _base = Path.Combine(Path.GetTempPath(), "modroots-fuzz-" + Guid.NewGuid().ToString("N"));
            _primary = Path.Combine(_base, "primary");
            _secondary = Path.Combine(_base, "modlet", "Wasm");
            // A decoy one level above the roots: a traversal that lands
            // here is a traversal the assertions can name.
            _outside = Path.Combine(_base, "outside");
            Module("hello", "module.wasm");
            Module("parachute", "wasm-mod.toml");
            Module("Hello", "module.wasm");
            Module("con", "module.wasm");
            Directory.CreateDirectory(Path.Combine(_primary, "empty"));
            Directory.CreateDirectory(_outside);
            File.WriteAllBytes(Path.Combine(_outside, "module.wasm"), new byte[] { 0x00 });
            _roots = new[] { _primary, _secondary };
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

        private void Module(string name, string file)
        {
            string dir = Path.Combine(_primary, name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, file), new byte[] { 0x00, 0x61, 0x73, 0x6d });
        }

        /// <summary>
        /// Fragments spliced into ids and file names. Every one of them is
        /// either a way out of a root or a spelling a filesystem accepts and
        /// a string comparison would reject.
        /// </summary>
        private static readonly string[] HostileFragments =
        {
            "", ".", "..", "...", "/", "\\", "//", "..\\..", "a/b", "a\\b", ":", "::",
            "hello", "Hello", "HELLO", "con", "CON", "NUL", "aux", "com1", "lpt9",
            ".", " ", "  ", "\0", "\t", "\n", "\r", "\u0001", "\u007f", "\u0085",
            "\u202e", "\u200b", "\ufeff", "\ud800", "\udfff", "\ud83d\ude00", "\U0001F600",
            "*", "?", "[", "]", "|", "<", ">", "\"", "'", "%24", "%2e%2e", "~", "$HOME",
            "module.wasm", "wasm-mod.toml", "C:", "C:name",
        };

        private static readonly string[] SeedIds = { "hello", "parachute", "empty", "Hello", "con" };

        private static readonly string[] FileNames =
        {
            "module.wasm", "wasm-mod.toml", "wasm.toml", "", ".", "..", "../module.wasm",
            "..\\module.wasm", "/etc/passwd", "sub/module.wasm", "sub\\module.wasm", "\\module.wasm",
            "module.wasm\0", "\0", "MODULE.WASM", "hello", "con", "x/y/z",
        };

        [Fact]
        public void HostileIdsNeverResolveOutsideARoot()
        {
            int iterations = FuzzDriver.Iterations();
            long seed = FuzzDriver.Seed();
            var rng = new Random(unchecked((int)seed));
            for (int i = 0; i < iterations; i++)
            {
                string id = Generate(rng);
                AssertResolution(seed, i, id, FileNames[rng.Next(FileNames.Length)]);
                AssertResolution(seed, i, id, FileNames[rng.Next(FileNames.Length)]);
            }
        }

        [Fact]
        public void HostileFileNamesNeverEscapeTheModuleDirectory()
        {
            int iterations = FuzzDriver.Iterations();
            long seed = FuzzDriver.Seed() + 1;
            var rng = new Random(unchecked((int)seed));
            for (int i = 0; i < iterations; i++)
            {
                string id = SeedIds[rng.Next(SeedIds.Length)];
                AssertResolution(seed, i, id, Generate(rng));
            }
        }

        [Fact]
        public void ARejectedIdResolvesToNothing()
        {
            // The validator is the gate, so every id it refuses must leave
            // both resolvers empty rather than falling back to a scan.
            long seed = FuzzDriver.Seed() + 2;
            var refused = new List<string>
            {
                "", ".", "..", "a/b", "a\\b", "C:name", "con", "COM1", "lpt9", "NUL.txt",
                "aux", "hello ", "hello.", "id\0", "id\u0001", "id\u007f", "id\u0085",
                "id\u200b", "id\u202e", "wasm/hello", "\\hello",
            };
            for (int i = 0; i < refused.Count; i++)
            {
                string id = refused[i];
                Assert.False(ModId.IsValid(id), FuzzDriver.Report(seed, i, id, "this id was expected to be refused"));
                Assert.Equal(string.Empty, ModuleRoots.ResolveDir(_roots, id));
                Assert.Equal(string.Empty, ModuleRoots.ResolveFile(_roots, id, "module.wasm"));
            }
        }

        [Fact]
        public void ThePrimaryRootWinsPerId()
        {
            // Both roots carry "hello", so a root that is not consulted in
            // order hands back the modlet's copy.
            string chosen = Path.Combine(_secondary, "hello");
            Directory.CreateDirectory(chosen);
            File.WriteAllBytes(Path.Combine(chosen, "module.wasm"), new byte[] { 0x00 });
            var ordered = ModuleRoots.Order(_primary, new[] { _secondary });
            Assert.Equal(Path.Combine(_primary, "hello"), ModuleRoots.ResolveDir(ordered, "hello"));
            Assert.Equal(
                Path.Combine(_primary, "hello", "module.wasm"),
                ModuleRoots.ResolveFile(ordered, "hello", "module.wasm"));
        }

        [Fact]
        public void ARootedOrNestedFileNameResolvesToNothing()
        {
            // Path.Combine drops the root and the module directory when its
            // last part is rooted, so a file name that is not a plain leaf
            // name can name a file outside every module tree. Each of these
            // either exists on this machine or is one path join away from
            // existing, which is why the shape is checked rather than left
            // to the caller's literals.
            long seed = FuzzDriver.Seed() + 3;
            var escaping = new List<string>
            {
                "/etc/passwd", "/", "\\module.wasm", "..\\module.wasm", "../module.wasm",
                "sub/module.wasm", "sub\\module.wasm", ".", "..", "hello/../module.wasm",
                "module.wasm/", "a/b/c",
            };
            for (int i = 0; i < escaping.Count; i++)
            {
                string fileName = escaping[i];
                string resolved = ModuleRoots.ResolveFile(_roots, "hello", fileName);
                Assert.True(resolved.Length == 0, FuzzDriver.Report(seed, i, fileName,
                    "a non-leaf file name resolved to " + resolved));
            }
            // The plain name still resolves, so the check is not a blanket
            // refusal of the module's own files.
            Assert.Equal(
                Path.Combine(_primary, "hello", "module.wasm"),
                ModuleRoots.ResolveFile(_roots, "hello", "module.wasm"));
        }

        [Fact]
        public void ADirectoryWithoutTheFileDoesNotClaimTheModule()
        {
            // "parachute" is a staged directory holding only its manifest.
            Assert.Equal(Path.Combine(_primary, "parachute"), ModuleRoots.ResolveDir(_roots, "parachute"));
            Assert.Equal(string.Empty, ModuleRoots.ResolveFile(_roots, "parachute", "module.wasm"));
        }

        /// <summary>
        /// Resolves one pair and checks the answer against the filesystem
        /// rather than against the code that produced it.
        /// </summary>
        private void AssertResolution(long seed, int iteration, string id, string fileName)
        {
            // An id the validator refuses, and one the filesystem cannot
            // spell, must both leave the resolvers empty. Neither may
            // resolve anywhere, so the checks below run either way.
            bool spelled = id.Length > 0 && FuzzDriver.IsWellFormedUnicode(id);

            long ms = 0;
            string dir = string.Empty;
            string file = string.Empty;
            ms += FuzzDriver.Timed(() => dir = ModuleRoots.ResolveDir(_roots, id));
            ms += FuzzDriver.Timed(() => file = ModuleRoots.ResolveFile(_roots, id, fileName));
            Assert.True(ms < FuzzDriver.PerInputBudgetMs, FuzzDriver.Report(seed, iteration, id,
                "resolving took " + ms.ToString(CultureInfo.InvariantCulture) + " ms"));

            if (!spelled || !ModId.IsValid(id))
            {
                Assert.Equal(string.Empty, dir);
                Assert.Equal(string.Empty, file);
                return;
            }

            if (dir.Length > 0)
            {
                AssertInsideRoot(seed, iteration, id, fileName, dir, null);
                Assert.True(Directory.Exists(dir), FuzzDriver.Report(seed, iteration, id,
                    "resolved a directory that is not on disk: " + dir));
                Assert.Equal(id, Path.GetFileName(dir));
            }

            if (file.Length > 0)
            {
                AssertInsideRoot(seed, iteration, id, fileName, file, fileName);
                Assert.True(File.Exists(file), FuzzDriver.Report(seed, iteration, id,
                    "resolved a file that is not on disk: " + file));
                // The file has to sit directly in the module directory the
                // id named, spelled exactly, not one level up or sideways.
                string parent = Path.GetDirectoryName(file) ?? string.Empty;
                Assert.Equal(id, Path.GetFileName(parent));
                Assert.True(string.Equals(Path.GetFileName(file), fileName, StringComparison.Ordinal)
                    || fileName.Length == 0,
                    FuzzDriver.Report(seed, iteration, id,
                        "resolved a file the caller did not name: " + file));
            }
        }

        private void AssertInsideRoot(long seed, int iteration, string id, string fileName, string resolved, string? leaf)
        {
            string full = Path.GetFullPath(resolved);
            foreach (string root in _roots)
            {
                string fullRoot = Path.GetFullPath(root);
                if (string.Equals(full, fullRoot, StringComparison.Ordinal)
                    || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return;
                }
            }
            Assert.Fail(FuzzDriver.Report(seed, iteration, id,
                "id '" + id + "' with file name '" + FuzzDriver.Escape(fileName) + "' resolved to "
                + full + ", outside every module root"
                + (leaf == null ? string.Empty : " (leaf " + leaf + ")")));
        }

        private static string Generate(Random rng)
        {
            if (rng.Next(3) == 0)
            {
                return SeedIds[rng.Next(SeedIds.Length)];
            }
            int parts = 1 + rng.Next(3);
            var id = new System.Text.StringBuilder();
            for (int i = 0; i < parts; i++)
            {
                id.Append(HostileFragments[rng.Next(HostileFragments.Length)]);
            }
            return id.ToString();
        }
    }
}
