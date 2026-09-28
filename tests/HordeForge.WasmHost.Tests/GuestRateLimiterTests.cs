using System;
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
            Assert.Equal(0, dropped);
            Assert.True(limiter.TryWrite("mod", out dropped));
            Assert.Equal(0, dropped);
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
        public void DescribeDroppedOrdersSourcesByKey()
        {
            // The summary is a run's totals, so it must not depend on the
            // window table's hash order: two runs that drop the same items
            // in a different order print the same line and can be diffed.
            var limiter = new GuestRateLimiter(1);
            foreach (string source in new[] { "delta", "alpha", "charlie", "bravo" })
            {
                Assert.True(limiter.TryWrite(source, out _));
                Assert.False(limiter.TryWrite(source, out _));
            }
            Assert.Equal("lines dropped: alpha=1, bravo=1, charlie=1, delta=1",
                limiter.DescribeDropped("lines"));
        }

        [Fact]
        public void IdleSourcesAreSweptWhileActiveOnesSurvive()
        {
            // Sources only exist while a guest writes through them, so the
            // table must track live sources and not every module id an
            // operator ever loaded. Filling past the sweep threshold and
            // then keeping one source writing drops the quiet ones.
            int nowMs = 0;
            var limiter = new GuestRateLimiter(1, () => nowMs);
            for (int i = 0; i < 200; i++)
            {
                limiter.TryWrite("mod" + i, out _);
            }
            // Two writes inside the first second, so the live source has a
            // drop on record and DescribeDropped can report it either way.
            limiter.TryWrite("live", out _);
            for (nowMs = 0; nowMs <= 600000; nowMs += 1000)
            {
                limiter.TryWrite("live", out _);
            }
            string summary = limiter.DescribeDropped("lines");
            Assert.Contains("live=", summary);
            Assert.DoesNotContain("mod0=", summary);
            Assert.DoesNotContain("mod199=", summary);
        }

        [Fact]
        public void WindowResetsAfterOneSecond()
        {
            int nowMs = 1000;
            var limiter = new GuestRateLimiter(1, () => nowMs);
            Assert.True(limiter.TryWrite("mod", out _));
            Assert.False(limiter.TryWrite("mod", out _));
            nowMs += 999;
            Assert.False(limiter.TryWrite("mod", out _));
            nowMs += 1;
            Assert.True(limiter.TryWrite("mod", out long dropped));
            Assert.Equal(2, dropped);
        }

        [Fact]
        public void WindowResetSurvivesClockWraparound()
        {
            int nowMs = int.MaxValue - 500;
            var limiter = new GuestRateLimiter(1, () => nowMs);
            Assert.True(limiter.TryWrite("mod", out _));
            Assert.False(limiter.TryWrite("mod", out _));
            nowMs += 1000;
            Assert.True(limiter.TryWrite("mod", out _));
        }
    }
}
