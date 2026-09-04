using System;
using System.Threading;
using HordeForge.GameBridge.Bridge;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Guest output caps: a talkative mod must not flood the server log or
    /// the global chat. The cap is per source per second; excess items are
    /// dropped and counted for "wasm status".
    /// </summary>
    public sealed class GuestRateLimiterTests
    {
        [Fact]
        public void AllowsUpToCapThenDrops()
        {
            var limiter = new GuestRateLimiter(3);
            Assert.True(limiter.TryWrite("mod", out long dropped));
            Assert.Equal(0, dropped);
            Assert.True(limiter.TryWrite("mod", out dropped));
            Assert.True(limiter.TryWrite("mod", out dropped));
            Assert.False(limiter.TryWrite("mod", out dropped));
            Assert.Equal(1, dropped);
            Assert.False(limiter.TryWrite("mod", out dropped));
            Assert.Equal(2, dropped);
        }

        [Fact]
        public void CapsArePerSource()
        {
            var limiter = new GuestRateLimiter(1);
            Assert.True(limiter.TryWrite("a", out _));
            Assert.True(limiter.TryWrite("b", out _));
            Assert.False(limiter.TryWrite("a", out _));
            Assert.False(limiter.TryWrite("b", out _));
        }

        [Fact]
        public void RejectsNonPositiveCap()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GuestRateLimiter(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GuestRateLimiter(-1));
        }

        [Fact]
        public void DescribeDroppedListsOnlyThrottledSources()
        {
            var limiter = new GuestRateLimiter(1);
            Assert.Equal(string.Empty, limiter.DescribeDropped("lines"));
            Assert.True(limiter.TryWrite("quiet", out _));
            Assert.True(limiter.TryWrite("loud", out _));
            Assert.False(limiter.TryWrite("loud", out _));
            Assert.Equal("lines dropped: loud=1", limiter.DescribeDropped("lines"));
        }

        [Fact]
        public void WindowResetsAfterOneSecond()
        {
            var limiter = new GuestRateLimiter(1);
            Assert.True(limiter.TryWrite("mod", out _));
            Assert.False(limiter.TryWrite("mod", out _));
            Thread.Sleep(1100);
            Assert.True(limiter.TryWrite("mod", out long dropped));
            Assert.Equal(1, dropped);
        }
    }
}
