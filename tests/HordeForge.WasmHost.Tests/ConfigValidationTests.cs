using System;
using HordeForge.WasmHost.Config;
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
    }
}
