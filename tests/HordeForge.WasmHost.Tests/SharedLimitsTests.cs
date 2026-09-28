using System;
using System.IO;
using HordeForge.WasmHost.Config;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The shared operator file (wasm.toml) is the middle layer of the
    /// documented limit load order: code defaults, then the shared [limits],
    /// then a per-mod manifest. These tests pin the layer an embedder of the
    /// package gets, in particular that a file it cannot use is a refusal
    /// rather than a silent fall back to the code defaults.
    /// </summary>
    public sealed class SharedLimitsTests : IDisposable
    {
        private readonly string _dir;

        public SharedLimitsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hf-shared-limits-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            Directory.Delete(_dir, true);
        }

        private string WriteShared(string toml)
        {
            string path = Path.Combine(_dir, "wasm.toml");
            File.WriteAllText(path, toml);
            return path;
        }

        [Fact]
        public void LimitsReplaceTheCodeDefaults()
        {
            var config = new WasmHostConfig();
            string path = WriteShared("[limits]\nfuel_per_call = 2000000\nmax_memory_bytes = 67108864\n");

            ModManifest? shared = SharedLimits.TryApply(config, path, out string reason);

            Assert.Equal(string.Empty, reason);
            Assert.NotNull(shared);
            Assert.Equal(2000000UL, config.FuelPerCall);
            Assert.Equal(67108864UL, config.StaticMemoryMaximumBytes);
        }

        [Fact]
        public void AbsentFileLeavesTheConfigAloneAndIsNotAFailure()
        {
            var config = new WasmHostConfig();
            string path = Path.Combine(_dir, "no-such-wasm.toml");

            ModManifest? shared = SharedLimits.TryApply(config, path, out string reason);

            Assert.Null(shared);
            Assert.Equal(string.Empty, reason);
            Assert.Equal(new WasmHostConfig().FuelPerCall, config.FuelPerCall);
        }

        [Fact]
        public void AFileWithoutLimitsIsAcceptedAndLeavesTheDefaults()
        {
            var config = new WasmHostConfig();
            string path = WriteShared("[settings]\ngreeting = \"hi\"\n");

            ModManifest? shared = SharedLimits.TryApply(config, path, out string reason);

            Assert.Equal(string.Empty, reason);
            Assert.NotNull(shared);
            Assert.Equal("hi", shared!.Settings["greeting"]);
            Assert.Equal(new WasmHostConfig().FuelPerCall, config.FuelPerCall);
            Assert.Equal(new WasmHostConfig().StaticMemoryMaximumBytes, config.StaticMemoryMaximumBytes);
        }

        [Fact]
        public void AMalformedFileIsRefusedAndChangesNothing()
        {
            var config = new WasmHostConfig();
            string path = WriteShared("[limits]\nfuel_per_call = \n");

            // The engine would run under the code defaults rather than the
            // configured ones, so the caller is told to refuse to start.
            SharedLimits.TryApply(config, path, out string reason);

            Assert.NotEqual(string.Empty, reason);
            Assert.Equal(new WasmHostConfig().FuelPerCall, config.FuelPerCall);
        }

        [Fact]
        public void AnOutOfRangeLimitIsRefusedAndChangesNothing()
        {
            var config = new WasmHostConfig();
            string path = WriteShared("[limits]\nmax_memory_bytes = 1\n");

            SharedLimits.TryApply(config, path, out string reason);

            Assert.NotEqual(string.Empty, reason);
            Assert.Equal(new WasmHostConfig().StaticMemoryMaximumBytes, config.StaticMemoryMaximumBytes);
        }

        [Fact]
        public void IgnoredKeysSurviveTheRoundTrip()
        {
            // A key the host does not read is tolerated, but the caller has
            // to be able to name it, so the parsed manifest is handed back
            // rather than discarded.
            var config = new WasmHostConfig();
            string path = WriteShared("fuel_per_call = 500\n[limits]\nfuel_per_call = 1000\n");

            ModManifest? shared = SharedLimits.TryApply(config, path, out string reason);

            Assert.Equal(string.Empty, reason);
            Assert.Equal(new[] { "fuel_per_call" }, shared!.IgnoredKeys);
            Assert.Equal(1000UL, config.FuelPerCall);
        }

        [Fact]
        public void NullArgumentsAreRejected()
        {
            var config = new WasmHostConfig();

            Assert.Throws<ArgumentNullException>(
                () => SharedLimits.TryApply(null!, "wasm.toml", out _));
            Assert.Throws<ArgumentNullException>(
                () => SharedLimits.TryApply(config, null!, out _));
        }
    }
}
