using System;
using System.Collections.Generic;
using System.Globalization;
using HordeForge.WasmHost.Core;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The cost the run prints (the bridge's heartbeat, "wasm status", and
    /// shutdown lines, and each guest call's own cost the slow-dispatch
    /// warning names) is read through an injectable monotonic source, so a
    /// run stepped from a virtual clock reports that clock's cost rather than
    /// the host's. These tests pin both halves: the measurement is the
    /// source's advance, and one traced run describes itself with the exact
    /// bytes a virtual clock makes it produce, so replaying the trace twice
    /// produces the same bytes.
    /// </summary>
    public sealed class MonotonicTimerTests
    {
        /// <summary>A monotonic source a test steps by hand.</summary>
        private sealed class VirtualClock
        {
            private double _nowMs;

            public double Read() => _nowMs;

            /// <summary>Advances virtual time, as simulated work would.</summary>
            public void Advance(double ms) => _nowMs += ms;
        }

        [Fact]
        public void ReportsTheSourceAdvance()
        {
            var clock = new VirtualClock();
            var timer = new MonotonicTimer(clock.Read);
            clock.Advance(1.5);
            double elapsed = timer.ElapsedMs(() => clock.Advance(4.25));
            Assert.Equal(4.25, elapsed, 6);
            Assert.Equal(5.75, timer.ReadMs(), 6);
        }

        [Fact]
        public void FrozenSourceReportsZero()
        {
            // A driver whose virtual time does not advance while the work
            // runs (a tick that dispatches nothing) measures a real zero, not
            // a stall: the difference must not leak into the run's totals.
            var timer = new MonotonicTimer(() => 42.0);
            Assert.Equal(0.0, timer.ElapsedMs(() => { }), 6);
        }

        [Fact]
        public void BackwardsSourceReportsZeroNotNegative()
        {
            // A virtual clock rewound mid-work, or a platform clock that
            // misreported: a negative cost would drag the average below zero
            // and read as a fast dispatch.
            double[] readings = { 10.0, 4.0 };
            int next = 0;
            var timer = new MonotonicTimer(() => readings[next++]);
            Assert.Equal(0.0, timer.ElapsedMs(() => { }), 6);
        }

        [Fact]
        public void StartMarkFormMeasuresTheSameInterval()
        {
            // The form the host's per-guest-call cost uses: the work is
            // already resolved and already finished (or already threw), so
            // the cost is computed from two marks rather than a closure.
            var clock = new VirtualClock();
            var timer = new MonotonicTimer(clock.Read);
            double startedAt = timer.ReadMs();
            clock.Advance(7.5);
            Assert.Equal(7.5, timer.ElapsedMs(startedAt), 6);
            Assert.Equal(7.5, timer.ElapsedMs(startedAt), 6);
        }

        [Fact]
        public void StartMarkFormClampsARewoundSource()
        {
            double[] readings = { 10.0, 4.0 };
            int next = 0;
            var timer = new MonotonicTimer(() => readings[next++]);
            Assert.Equal(0.0, timer.ElapsedMs(10.0), 6);
        }

        [Fact]
        public void NanSourceReportsZeroNotNaN()
        {
            // NaN fails every comparison, so a plain "elapsed < 0.0" test lets
            // it through; the caller then sums it into an average that reads
            // NaN for the rest of the window.
            double[] readings = { 10.0, double.NaN };
            int next = 0;
            var timer = new MonotonicTimer(() => readings[next++]);
            Assert.Equal(0.0, timer.ElapsedMs(() => { }), 6);
        }

        [Fact]
        public void RejectsNullWork()
        {
            var timer = new MonotonicTimer(() => 0.0);
            Assert.Throws<ArgumentNullException>(() => timer.ElapsedMs((Action)null!));
        }

        [Fact]
        public void ReplayingATraceProducesIdenticalTelemetry()
        {
            // The property the whole seam exists for: the same traced run,
            // measured twice against a virtual clock, must describe itself
            // with the same bytes, timings included. A source that leaked
            // real time here would put a different number in the same field.
            const string expected =
                "dispatch: 0.75 ms last, 6.00 ms avg, 30.00 ms max over 6 tick(s); " +
                "1 failure(s), 1 slow tick(s)|1|6";
            Assert.Equal(expected, DescribeRun());
            Assert.Equal(expected, DescribeRun());
        }

        [Fact]
        public void ProcessClockStillMeasuresRealWork()
        {
            // The production default is unchanged: it measures the process
            // monotonic clock, so a dispatch that burns time reports time.
            // The bound is exact in the only direction that is not flaky: a
            // source pinned at zero reports 0.0, which this rejects, while
            // real work can only make the figure larger.
            var timer = MonotonicTimer.Default;
            double before = timer.ReadMs();
            double elapsed = timer.ElapsedMs(Spin);
            Assert.True(elapsed > 0.0,
                "work took measurable time but the process clock reported " +
                elapsed.ToString(CultureInfo.InvariantCulture) + " ms");
            Assert.True(timer.ReadMs() >= before);
        }

        /// <summary>Real work: a JIT-compiled loop, far above clock resolution.</summary>
        private static void Spin()
        {
            long spin = 0;
            for (int i = 0; i < 2000000; i++)
            {
                spin += i;
            }

            GC.KeepAlive(spin);
        }

        /// <summary>
        /// One traced run: a per-tick dispatch whose cost is a fixed schedule
        /// of virtual advances, recorded into the telemetry the bridge prints
        /// and summarized the way "wasm status" does.
        /// </summary>
        private static string DescribeRun()
        {
            var clock = new VirtualClock();
            var timer = new MonotonicTimer(clock.Read);
            var telemetry = new TickTelemetry();
            int failures = 0;
            // Cost per tick, in virtual ms, and the failing ticks. Fixed, so
            // the run is a function of this table and nothing else.
            var costs = new List<double> { 1.0, 2.5, 0.5, 30.0, 1.25, 0.75 };
            for (int i = 0; i < costs.Count; i++)
            {
                double cost = costs[i];
                double elapsed = timer.ElapsedMs(() => clock.Advance(cost));
                failures = i == 3 ? 1 : 0;
                telemetry.Record(i + 1, elapsed, failures);
            }
            return telemetry.Describe() + "|" + telemetry.SlowTicks + "|" + telemetry.LastTick;
        }
    }
}
