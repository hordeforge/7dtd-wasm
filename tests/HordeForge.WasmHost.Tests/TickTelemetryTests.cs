using HordeForge.WasmHost.Core;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The tick dispatch is the only host work on the game main loop, so
    /// its cost, liveness, and failure totals are what an operator reads
    /// out of the server log. These pin the numbers the bridge logs.
    /// </summary>
    public sealed class TickTelemetryTests
    {
        [Fact]
        public void RecordsLastAverageMaxAndFailures()
        {
            var telemetry = new TickTelemetry();
            telemetry.Record(1, 2.0, 0);
            telemetry.Record(2, 4.0, 1);
            telemetry.Record(3, 6.0, 2);

            Assert.Equal(3, telemetry.Ticks);
            Assert.Equal(3, telemetry.LastTick);
            Assert.Equal(6.0, telemetry.LastMs);
            Assert.Equal(4.0, telemetry.AverageMs);
            Assert.Equal(6.0, telemetry.WindowMaxMs);
            Assert.Equal(3, telemetry.TotalFailures);
        }

        [Fact]
        public void NoSamplesReadAsZeroNotNaN()
        {
            var telemetry = new TickTelemetry();
            Assert.Equal(0.0, telemetry.AverageMs);
            Assert.Equal(0.0, telemetry.LastMs);
            Assert.Equal(0.0, telemetry.WindowMaxMs);
            Assert.False(telemetry.HeartbeatDue);
            Assert.False(telemetry.IsSlow);
        }

        [Fact]
        public void CountsSlowTicksAgainstTheBudget()
        {
            var telemetry = new TickTelemetry();
            telemetry.Record(1, TickTelemetry.SlowDispatchMs, 0);
            Assert.False(telemetry.IsSlow);
            telemetry.Record(2, TickTelemetry.SlowDispatchMs + 0.01, 0);
            Assert.True(telemetry.IsSlow);
            Assert.Equal(1, telemetry.SlowTicks);
        }

        [Fact]
        public void NegativeDurationCannotDragTheAverage()
        {
            var telemetry = new TickTelemetry();
            telemetry.Record(1, -5.0, 0);
            telemetry.Record(2, 2.0, 0);
            Assert.Equal(1.0, telemetry.AverageMs);
        }

        [Fact]
        public void NanDurationCannotPoisonTheWindow()
        {
            // One NaN sample would sum into _windowMs and make every later
            // average, the window max and the summary line read NaN until the
            // window rolls. It also fails the slow-budget comparison, so the
            // tick would be counted neither as a sample nor as a slow tick.
            var telemetry = new TickTelemetry();
            telemetry.Record(1, 4.0, 0);
            telemetry.Record(2, double.NaN, 0);
            telemetry.Record(3, 2.0, 0);

            Assert.Equal(2.0, telemetry.AverageMs, 6);
            Assert.Equal(4.0, telemetry.WindowMaxMs, 6);
            Assert.Equal(3, telemetry.Ticks);
            Assert.DoesNotContain("NaN", telemetry.Describe());
        }

        [Fact]
        public void HeartbeatFiresOncePerIntervalNotEveryTick()
        {
            var telemetry = new TickTelemetry();
            for (long tick = 1; tick < TickTelemetry.HeartbeatIntervalTicks; tick++)
            {
                telemetry.Record(tick, 1.0, 0);
                Assert.False(telemetry.HeartbeatDue);
            }
            telemetry.Record(TickTelemetry.HeartbeatIntervalTicks, 1.0, 0);
            Assert.True(telemetry.HeartbeatDue);
            telemetry.Record(TickTelemetry.HeartbeatIntervalTicks + 1, 1.0, 0);
            Assert.False(telemetry.HeartbeatDue);
        }

        [Fact]
        public void WindowRollsSoTheAverageCoversRecentTicksOnly()
        {
            var telemetry = new TickTelemetry();
            for (long tick = 1; tick <= TickTelemetry.WindowTicks; tick++)
            {
                telemetry.Record(tick, 30.0, 0);
            }
            Assert.Contains("30.00 ms max over " + TickTelemetry.WindowTicks + " tick(s)", telemetry.Describe());
            Assert.Equal(TickTelemetry.WindowTicks, telemetry.SlowTicks);

            telemetry.Record(TickTelemetry.WindowTicks + 1, 1.0, 0);
            Assert.Contains("1.00 ms max over 1 tick(s)", telemetry.Describe());
            Assert.Equal(1.0, telemetry.WindowMaxMs);
            // The slow ticks from the previous window stay counted for the
            // run, they just no longer skew the current average; the fresh
            // sample is under budget.
            Assert.Equal(TickTelemetry.WindowTicks, telemetry.SlowTicks);
        }

        [Fact]
        public void ResetClearsEveryCounter()
        {
            var telemetry = new TickTelemetry();
            telemetry.Record(1200, 30.0, 2);
            telemetry.Reset();

            Assert.Equal(0, telemetry.Ticks);
            Assert.Equal(0, telemetry.LastTick);
            Assert.Equal(0, telemetry.TotalFailures);
            Assert.Equal(0, telemetry.SlowTicks);
            Assert.Equal(0.0, telemetry.AverageMs);
            // The two cost counters the summary line prints: left set, the
            // first line after a restart would open with the previous run's
            // numbers.
            Assert.Equal(0.0, telemetry.LastMs);
            Assert.Equal(0.0, telemetry.WindowMaxMs);
            Assert.False(telemetry.IsSlow);
            Assert.Equal(
                "dispatch: 0.00 ms last, 0.00 ms avg, 0.00 ms max over 0 tick(s); 0 failure(s), 0 slow tick(s)",
                telemetry.Describe());
            Assert.False(telemetry.HeartbeatDue);
        }

        [Fact]
        public void DescribeNamesCostAndFailureCounts()
        {
            var telemetry = new TickTelemetry();
            telemetry.Record(1, 3.0, 0);
            telemetry.Record(2, 5.0, 1);
            string line = telemetry.Describe();

            Assert.Contains("dispatch:", line);
            Assert.Contains("5.00 ms last", line);
            Assert.Contains("4.00 ms avg", line);
            Assert.Contains("5.00 ms max", line);
            Assert.Contains("1 failure(s)", line);
        }
    }
}
