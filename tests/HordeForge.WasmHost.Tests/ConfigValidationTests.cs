using System;
using HordeForge.WasmHost.Config;
using HordeForge.WasmHost.Core;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Fail-fast configuration validation: a WasmHostConfig the host can
    /// never honor (zero fuel, sub-page memory ceiling, non-positive caps,
    /// empty log prefix) must be rejected at construction, not surface later
    /// as every call exhausting fuel or every module being rejected.
    /// </summary>
    public sealed class ConfigValidationTests
    {
        [Fact]
        public void DefaultConfigIsAccepted()
        {
            using var host = new WasmModHost(new TestGameHostApi(), new WasmHostConfig());
        }

        [Fact]
        public void ZeroFuelPerCallIsRejected()
        {
            // Fuel 0 would exhaust the very first instruction of every call.
            var config = new WasmHostConfig { FuelPerCall = 0UL };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new WasmModHost(new TestGameHostApi(), config));
            Assert.Contains("FuelPerCall", ex.Message);
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(65535UL)]
        public void SubPageMemoryCeilingIsRejected(ulong bytes)
        {
            // Below one wasm page (64 KiB) no module can ever instantiate.
            var config = new WasmHostConfig { StaticMemoryMaximumBytes = bytes };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new WasmModHost(new TestGameHostApi(), config));
            Assert.Contains("StaticMemoryMaximumBytes", ex.Message);
        }

        [Theory]
        [InlineData(4294967297UL)]
        [InlineData(ulong.MaxValue)]
        public void MemoryCeilingPastTheWasm32AddressSpaceIsRejected(ulong bytes)
        {
            // A ceiling above the 4 GiB wasm32 address space names memory no
            // guest can reach, and the engine cannot honor it: without this
            // bound a limits file with such a value fails the host
            // construction instead of being reported as an invalid file.
            var config = new WasmHostConfig { StaticMemoryMaximumBytes = bytes };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new WasmModHost(new TestGameHostApi(), config));
            Assert.Contains("StaticMemoryMaximumBytes", ex.Message);
        }

        [Fact]
        public void TheWholeWasm32AddressSpaceIsAnAcceptedMemoryCeiling()
        {
            // 4 GiB exactly is the bound, not a rejection: a module built
            // without --max-memory declares no maximum and is treated as
            // declaring the full address space, so this is the only ceiling
            // that runs it.
            var config = new WasmHostConfig { StaticMemoryMaximumBytes = 4294967296UL };
            using var host = new WasmModHost(new TestGameHostApi(), config);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NonPositiveModuleSizeCapIsRejected(int bytes)
        {
            var config = new WasmHostConfig { MaxModuleSizeBytes = bytes };
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new WasmModHost(new TestGameHostApi(), config));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1024)]
        public void NonPositiveStackCeilingIsRejected(int bytes)
        {
            var config = new WasmHostConfig { MaximumStackBytes = bytes };
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new WasmModHost(new TestGameHostApi(), config));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EmptyLogSourcePrefixIsRejected(string? prefix)
        {
            var config = new WasmHostConfig { LogSourcePrefix = prefix! };
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new WasmModHost(new TestGameHostApi(), config));
            Assert.Contains("LogSourcePrefix", ex.Message);
        }

        [Fact]
        public void EffectiveLimitsAreReadableFromTheHost()
        {
            // "wasm status" reports these, so they must reflect the
            // configuration the engine was built with, not the code defaults
            // a later file could have changed underneath the embedder.
            var config = new WasmHostConfig
            {
                FuelPerCall = 250_000UL,
                StaticMemoryMaximumBytes = 16UL * 1024 * 1024,
                MaxModuleSizeBytes = 2048,
                InheritGuestStandardStreams = true,
            };
            using var host = new WasmModHost(new TestGameHostApi(), config);
            Assert.Equal(250_000UL, host.FuelPerCall);
            Assert.Equal(16UL * 1024 * 1024, host.StaticMemoryMaximumBytes);
            Assert.Equal(2048, host.MaxModuleSizeBytes);
            Assert.True(host.InheritGuestStandardStreams);
        }
    }
}
