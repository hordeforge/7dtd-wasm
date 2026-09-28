using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HordeForge.WasmHost.Config;
using HordeForge.WasmHost.Core;
using HordeForge.GameBridge.Bridge;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Concurrency coverage for the shared state that is reachable from a
    /// guest import without a lock on the caller's side: the rate limiter's
    /// window table, the tick telemetry counters, and the per-mod counters
    /// on a WasmMod handed out by TryGetMod. Each of these is a plain
    /// field or an ordinary collection, so an unsynchronized reader either
    /// loses a count or walks a structure another thread is rewriting.
    /// </summary>
    public sealed class SharedStateConcurrencyTests
    {
        private const int Iterations = 2000;
        private const int Sources = 16;

        private static byte[] Fixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".wasm");
            return File.ReadAllBytes(path);
        }

        /// <summary>
        /// Runs every worker and rethrows the first failure on the test
        /// thread, so an assertion or a crash inside a worker fails the
        /// test instead of being swallowed by the task.
        /// </summary>
        private static void RunWorkers(params Action[] workers)
        {
            Task[] tasks = new Task[workers.Length];
            for (int i = 0; i < workers.Length; i++)
            {
                tasks[i] = Task.Run(workers[i]);
            }
            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException ex)
            {
                throw ex.Flatten().InnerExceptions[0];
            }
        }

        [Fact]
        public void RateLimiterWindowsSurviveConcurrentWritersAndReaders()
        {
            int nowMs = 0;
            var limiter = new GuestRateLimiter(100, () => nowMs);

            // Two threads write the same sources and a third describes the
            // dropped totals while they do. The limiter's table is a plain
            // Dictionary, so without its own lock the describe walk can run
            // into a resize and the writers lose a window to a sweep.
            RunWorkers(
                () => WriteSources(limiter),
                () => WriteSources(limiter),
                () =>
                {
                    for (int i = 0; i < Iterations; i++)
                    {
                        string described = limiter.DescribeDropped("lines");
                        Assert.True(described.Length == 0 || described.StartsWith("lines dropped: ", StringComparison.Ordinal), described);
                    }
                });

            // Every source was written past the cap, so each one is tracked
            // with a dropped count, and the summary names all of them exactly
            // once: a window lost to a racing sweep would be missing here.
            string summary = limiter.DescribeDropped("lines");
            List<string> named = new List<string>(summary.Substring("lines dropped: ".Length).Split(", "));
            named.Sort(StringComparer.Ordinal);
            var expected = new List<string>();
            for (int s = 0; s < Sources; s++)
            {
                expected.Add("source" + s + "=" + (2 * Iterations - 100));
            }
            expected.Sort(StringComparer.Ordinal);
            Assert.Equal(expected, named);
        }

        private static void WriteSources(GuestRateLimiter limiter)
        {
            // A source's dropped count is a running total, so on a clock
            // that never rolls a window it can only grow. A read that went
            // backwards would be a lost update under the racing describe.
            var lastDropped = new long[Sources];
            for (int i = 0; i < Iterations; i++)
            {
                for (int s = 0; s < Sources; s++)
                {
                    limiter.TryWrite("source" + s, out long dropped);
                    Assert.True(dropped >= lastDropped[s],
                        "source" + s + " dropped count went backwards: " + dropped + " < " + lastDropped[s]);
                    lastDropped[s] = dropped;
                }
            }
        }

        [Fact]
        public void TrackedSourceCountIsReadableWhileSourcesAreWritten()
        {
            int nowMs = 0;
            var limiter = new GuestRateLimiter(10, () => nowMs);

            // The table size is read by tests and by any caller sizing the
            // table, and it is written by every TryWrite. Reading
            // Dictionary.Count against a writer that is inserting (and
            // resizing) is a walk of the bucket array, so the read belongs
            // under the same gate as the write.
            RunWorkers(
                () =>
                {
                    for (int i = 0; i < Iterations; i++)
                    {
                        for (int s = 0; s < Sources; s++)
                        {
                            limiter.TryWrite("source" + (i * Sources + s), out _);
                        }
                    }
                },
                () =>
                {
                    for (int i = 0; i < Iterations; i++)
                    {
                        int tracked = limiter.TrackedSourceCount;
                        Assert.InRange(tracked, 0, Iterations * Sources);
                    }
                });

            // Every fresh source the writer named is tracked: the table is
            // only swept above the idle threshold, which this clock never
            // reaches, so nothing is lost.
            Assert.Equal(Iterations * Sources, limiter.TrackedSourceCount);
        }

        [Fact]
        public void TelemetryRecordsEverySampleWhileItIsRead()
        {
            var telemetry = new TickTelemetry();

            // One thread records samples and the other reads every counter
            // and the summary line. The window roll in Record rewrites four
            // fields at once, so an unsynchronized reader could report a
            // total that does not match the per-tick figures, or a negative
            // average out of a half-updated window.
            RunWorkers(
                () =>
                {
                    for (int i = 1; i <= Iterations; i++)
                    {
                        telemetry.Record(i, 1.0, 0);
                    }
                },
                () =>
                {
                    for (int i = 0; i < Iterations; i++)
                    {
                        // Every recorded sample costs 1 ms, so an average or
                        // a window max above that is a reader that mixed two
                        // samples, and one below zero is a half-updated
                        // window read as a numerator with a stale count.
                        Assert.InRange(telemetry.AverageMs, 0.0, 1.0);
                        Assert.InRange(telemetry.WindowMaxMs, 0.0, 1.0);
                        Assert.True(telemetry.LastMs <= 1.0);
                        Assert.True(telemetry.Ticks <= telemetry.LastTick);
                        Assert.Equal(0, telemetry.TotalFailures);
                        Assert.Equal(0, telemetry.SlowTicks);
                        Assert.True(telemetry.Describe().Length > 0);
                    }
                });

            // Every sample landed: a lost increment would show here.
            Assert.Equal(Iterations, telemetry.Ticks);
            Assert.Equal(Iterations, telemetry.LastTick);
            Assert.Equal(1.0, telemetry.AverageMs, 6);
        }

        [Fact]
        public void ModCountersAreReadableWhileTheHostDispatches()
        {
            const int ticks = 500;
            var api = new TestGameHostApi();
            using (var host = new WasmModHost(api, new WasmHostConfig()))
            {
                host.LoadModule("alpha", Fixture("strings"));

                // The counters on a WasmMod are written on the dispatching
                // thread and read by whoever holds the instance, which is
                // what TryGetMod exists for. A reader running alongside the
                // ticks must see a count that only ever grows.
                RunWorkers(
                    () =>
                    {
                        for (int i = 0; i < ticks; i++)
                        {
                            host.DispatchTick(i);
                        }
                    },
                    () =>
                    {
                        long previous = 0;
                        for (int i = 0; i < ticks; i++)
                        {
                            Assert.True(host.TryGetMod("alpha", out WasmMod? mod) && mod != null);
                            long calls = mod!.TotalCalls;
                            Assert.True(calls >= previous, "counter went backwards: " + calls + " < " + previous);
                            previous = calls;
                        }
                    });

                Assert.True(host.TryGetMod("alpha", out WasmMod? loaded) && loaded != null);
                // One tick per dispatch, and a counter that lost an
                // increment under the reader would come up short.
                Assert.Equal(ticks, loaded!.TotalCalls);
                Assert.True(loaded.TotalFuelConsumed > 0UL);
            }
        }

        [Fact]
        public void ShutdownFailuresIsASnapshotNotTheLiveList()
        {
            // Dispose fills the list under the host gate; an embedder reads
            // it afterwards, and on a second thread. Handing out the live
            // list would let that reader enumerate one being appended to.
            // The guest shuts down by trapping, so the list really holds a
            // failure: an empty one would make the copy indistinguishable
            // from the original.
            var api = new TestGameHostApi();
            var host = new WasmModHost(api, new WasmHostConfig());
            host.LoadModule("trapshutdown", Wasmtime.Module.ConvertText(
                "(module (memory (export \"memory\") 1 1)" +
                "(func (export \"on_enable\") (result i32) i32.const 0)" +
                "(func (export \"on_tick\") (result i32) i32.const 0)" +
                "(func (export \"on_shutdown\") (result i32) unreachable)"));
            host.Dispose();

            IReadOnlyList<ModRunResult> first = host.ShutdownFailures;
            IReadOnlyList<ModRunResult> second = host.ShutdownFailures;
            ModRunResult failure = Assert.Single(first);
            Assert.Equal("trapshutdown", failure.ModId);
            Assert.Equal(1, second.Count);
            Assert.Equal(failure.ModId, second[0].ModId);
            // Each read publishes its own list, and it is read-only, so the
            // embedder cannot reach back into the host's state through it.
            Assert.NotSame(first, second);
            Assert.Throws<NotSupportedException>(
                () => ((IList<ModRunResult>)second).Add(default));
        }
    }
}
