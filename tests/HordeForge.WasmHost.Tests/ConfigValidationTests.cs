using System;
using HordeForge.WasmHost.Config;
using HordeForge.WasmHost.Core;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Fail-fast configuration validation: a WasmHostConfig the host can
    /// never honor (zero or runaway fuel, a memory ceiling below one page or
    /// above wasm32, non-positive caps, a stack the engine aborts on, an
    /// empty log prefix) must be rejected at construction, not surface later
    /// as every call exhausting fuel, every module being rejected, or the
    /// process dying from a panic inside the native engine.
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

        [Fact]
        public void AboveWasm32MemoryCeilingIsRejected()
        {
            // wasm32 memory is 65536 pages; a larger ceiling rejects no
            // module, so accepting it reports a bound the engine never had.
            var config = new WasmHostConfig { StaticMemoryMaximumBytes = 8UL * 1024 * 1024 * 1024 };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new WasmModHost(new TestGameHostApi(), config));
            Assert.Contains("StaticMemoryMaximumBytes", ex.Message);
        }

        [Fact]
        public void UpperBoundsAreInclusive()
        {
            // The bounds are ceilings, not exclusive limits: the largest
            // value each engine can honor still has to build.
            var config = new WasmHostConfig
            {
                FuelPerCall = 50_000_000UL,
                MaximumStackBytes = 2 * 1024 * 1024,
                StaticMemoryMaximumBytes = 65536UL * 65536,
            };
            using var host = new WasmModHost(new TestGameHostApi(), config);
        }

        [Fact]
        public void FuelAboveTheMainLoopBudgetIsRejected()
        {
            // Fuel is the only bound on how long one guest call can hold the
            // game loop, so an embedder gets the same ceiling the manifest
            // parser reports by name. The engine itself accepts the value,
            // so without this check only the file path is bounded.
            var config = new WasmHostConfig { FuelPerCall = 50_000_001UL };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new WasmModHost(new TestGameHostApi(), config));
            Assert.Contains("FuelPerCall", ex.Message);
        }

        [Fact]
        public void StackCeilingAboveTheEngineLimitIsRejected()
        {
            // 2 MiB + 1 is the first value the engine rejects, and it does
            // so by aborting the process from a panic inside the native
            // engine, which no caller can catch. The host rejects it first,
            // with a message that names the field.
            var config = new WasmHostConfig { MaximumStackBytes = 2 * 1024 * 1024 + 1 };
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new WasmModHost(new TestGameHostApi(), config));
            Assert.Contains("MaximumStackBytes", ex.Message);
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
                MaximumStackBytes = 256 * 1024,
            };
            using var host = new WasmModHost(new TestGameHostApi(), config);
            Assert.Equal(250_000UL, host.FuelPerCall);
            Assert.Equal(16UL * 1024 * 1024, host.StaticMemoryMaximumBytes);
            Assert.Equal(2048, host.MaxModuleSizeBytes);
            Assert.True(host.InheritGuestStandardStreams);
            Assert.Equal(256 * 1024, host.MaximumStackBytes);
        }
    }
}
