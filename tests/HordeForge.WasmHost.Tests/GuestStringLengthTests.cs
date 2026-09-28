using HordeForge.WasmHost.Abi;
using HordeForge.WasmHost.Core;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The byte cap the host applies to a guest-declared string length. The
    /// length is guest-chosen, so an uncapped read is host-side work the fuel
    /// budget never sees.
    /// </summary>
    public sealed class GuestStringLengthTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void NonPositiveLengthsReadNothing(int declared)
        {
            Assert.Equal(0, GuestStringLength.Clamp(declared));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(256)]
        [InlineData(GuestStringLength.MaxBytes)]
        public void LengthsWithinTheCapPassThrough(int declared)
        {
            Assert.Equal(declared, GuestStringLength.Clamp(declared));
        }

        [Theory]
        [InlineData(GuestStringLength.MaxBytes + 1)]
        [InlineData(32 * 1024 * 1024)]
        [InlineData(int.MaxValue)]
        public void OversizedLengthsAreCutToTheCap(int declared)
        {
            int clamped = GuestStringLength.Clamp(declared);
            Assert.Equal(GuestStringLength.MaxBytes, clamped);
            Assert.True(clamped > 0);
        }

        [Fact]
        public void TheCapFitsInsideTheSmallestMemoryCeiling()
        {
            // One wasm page is the smallest memory ceiling the host accepts,
            // and a read longer than that is a read the engine cannot back
            // with memory at all.
            Assert.True(GuestStringLength.MaxBytes <= WasmModHost.WasmPageBytes);
        }
    }
}
