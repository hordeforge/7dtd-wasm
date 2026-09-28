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
        public void SweepRunsOnFreshSourcesThatNeverRollAWindow()
        {
            // A guest-chosen source key (the servant's failing verb) makes
            // every call a new source, and a window is only reset when the
            // same source writes again a second later. A sweep tied to that
            // reset therefore never runs, and the table grows by one entry
            // per guest command for the life of the server. Ten minutes of
            // one-new-source-per-call traffic must still leave only the last
            // idle window behind.
            int nowMs = 0;
            var limiter = new GuestRateLimiter(1, () => nowMs);
            for (int i = 0; i < 200; i++)
            {
                limiter.TryWrite("mod" + i, out _);
            }
            for (int i = 0; i < 600; i++)
            {
                nowMs += 1000;
                limiter.TryWrite("fresh" + i, out _);
            }
            // 600 fresh sources over 10 minutes: only the ones younger than
            // the 5 minute idle threshold are still tracked, and the 200
            // written before the clock moved are all idle by now.
            Assert.True(limiter.TrackedSourceCount <= 300,
                "tracked " + limiter.TrackedSourceCount + " sources; idle ones were not swept");
        }

        [Fact]
        public void SourceKeyBoundsGuestChosenDetail()
        {
            // The servant's failure key embeds the verb the guest wrote, so
            // one command must not pin its own string in a table that lives
            // as long as the process. Long details collapse onto the first
            // MaxSourceKeyChars characters.
            string verb = new string('v', 1 << 20);
            string key = GuestRateLimiter.SourceKey("bot/", verb);
            Assert.Equal("bot/", key.Substring(0, "bot/".Length));
            Assert.Equal(GuestRateLimiter.MaxSourceKeyChars, key.Length - "bot/".Length);
            Assert.Equal("bot/shoot", GuestRateLimiter.SourceKey("bot/", "shoot"));
        }

        [Fact]
        public void ForgetSourceGivesTheNextGenerationAFullBudget()
        {
            // A module reloaded inside the second the old one saturated its
            // cap must not start throttled, and its drop total must not
            // outlive it in "wasm status".
            var limiter = new GuestRateLimiter(1, () => 0);
            Assert.True(limiter.TryWrite("mod", out _));
            Assert.False(limiter.TryWrite("mod", out long dropped));
            Assert.Equal(1, dropped);
            limiter.ForgetSource("mod");
            Assert.True(limiter.TryWrite("mod", out dropped));
            Assert.Equal(0, dropped);
            Assert.Equal(string.Empty, limiter.DescribeDropped("lines"));
        }

        [Fact]
        public void ForgetSourceLeavesOtherSourcesAlone()
        {
            var limiter = new GuestRateLimiter(1, () => 0);
            Assert.True(limiter.TryWrite("stays", out _));
            Assert.False(limiter.TryWrite("stays", out _));
            limiter.ForgetSource("other");
            Assert.False(limiter.TryWrite("stays", out _));
            Assert.Equal("lines dropped: stays=2", limiter.DescribeDropped("lines"));
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
