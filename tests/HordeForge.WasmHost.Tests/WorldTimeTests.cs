using HordeForge.GameBridge.Bridge;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The game world clock is unsigned and the guest ABI carries it as
    /// i64, so the conversion is the one place a wrapped clock could reach a
    /// guest: a negative world time reads as a moment before the world
    /// started, and a brain computing a day/night phase from it gets a
    /// plausible answer to the wrong question.
    /// </summary>
    public sealed class WorldTimeTests
    {
        [Theory]
        [InlineData(0UL, 0L)]
        [InlineData(1UL, 1L)]
        [InlineData(1440UL, 1440L)]
        public void OrdinaryWorldTimesPassThrough(ulong gameValue, long expected)
        {
            Assert.Equal(expected, WorldTime.ToAbi(gameValue));
        }

        [Fact]
        public void TheLargestSignedWorldTimeIsNotRewritten()
        {
            // The value the ABI can carry exactly; the cast must not turn it
            // into long.MinValue.
            Assert.Equal(long.MaxValue, WorldTime.ToAbi((ulong)long.MaxValue));
        }

        [Fact]
        public void WorldTimesPastTheSignedRangeSaturateInsteadOfWrapping()
        {
            // A (long) cast of ulong.MaxValue yields -1: a world time of minus
            // one, which every guest reads as a time before the world start.
            Assert.Equal(long.MaxValue, WorldTime.ToAbi(ulong.MaxValue));
            Assert.Equal(long.MaxValue, WorldTime.ToAbi((ulong)long.MaxValue + 1UL));
        }
    }
}
