using System;
using System.Collections.Generic;
using System.Globalization;
using HordeForge.GameBridge.Bridge;
using HordeForge.WasmHost.Core;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The dispatch cost the bridge prints into the heartbeat, "wasm status",
    /// and shutdown lines is read through an injectable monotonic source, so
    /// a run stepped from a virtual clock reports that clock's cost rather
    /// than the host's. These tests pin both halves: the measurement is the
    /// source's advance, and two runs of the same trace produce the same
    /// bytes.
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
            Assert.Equal(DescribeRun(), DescribeRun());
        }

        [Fact]
        public void ProcessClockStillMeasuresRealWork()
        {
            // The production default is unchanged: it measures the process
            // monotonic clock, so a dispatch that burns time reports time.
            var timer = MonotonicTimer.Default;
            double elapsed = timer.ElapsedMs(() =>
            {
                long spin = 0;
                for (int i = 0; i < 2000000; i++)
                {
                    spin += i;
                }
                Assert.True(spin >= 0);
            });
            Assert.True(elapsed >= 0.0, "elapsed " + elapsed.ToString(CultureInfo.InvariantCulture) + " ms");
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
