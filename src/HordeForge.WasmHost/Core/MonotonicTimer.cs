using System;
using System.Diagnostics;

namespace HordeForge.WasmHost.Core
{
    /// <summary>
    /// Cost of one unit of work in milliseconds, read through an injectable
    /// monotonic source. Every guest call is timed here, and that
    /// measurement lands in the run's own log lines (<c>WasmMod.LastCallMs</c>
    /// names the slowest guest in the bridge's slow-dispatch warning, and the
    /// per-tick dispatch is timed into the minute heartbeat, "wasm status",
    /// and the shutdown summary). Measuring it off a hardwired
    /// <see cref="Stopwatch"/> makes those lines the one part of a run a
    /// replay cannot reproduce: two runs over the same inputs then disagree
    /// on cost, and the diff names the clock rather than the guest that
    /// caused it. Every other decision made on time goes through an injected
    /// millisecond clock (<c>BridgeHost.ClockMs</c> in the bridge); this is
    /// the sub-millisecond half of that pair, and a driver stepping virtual
    /// time replaces both.
    ///
    /// The default source is the process monotonic clock, so production timing
    /// is unchanged. A virtual source need only be monotonic and to advance
    /// when the simulated work does: a driver that returns the same reading
    /// twice measures zero, which is a legitimate dispatch cost, and one that
    /// steps forward by the work it simulated reports exactly that step.
    ///
    /// It lives here rather than in the bridge because the host itself
    /// measures guest calls: a timer the bridge owned could not reach
    /// <c>WasmMod</c>, and the per-call cost would stay the one unguarded
    /// clock in the dispatch path.
    ///
    /// Thread safety: an instance is safe to share; the source it reads is the
    /// caller's to make thread safe.
    /// </summary>
    public sealed class MonotonicTimer
    {
        /// <summary>Timer over the process monotonic clock; the production default.</summary>
        public static MonotonicTimer Default { get; } = new MonotonicTimer();

        private static readonly double MillisecondsPerTimestampTick = 1000.0 / Stopwatch.Frequency;

        private readonly Func<double> _readMs;

        private static double ReadProcessMs()
        {
            return Stopwatch.GetTimestamp() * MillisecondsPerTimestampTick;
        }

        /// <summary>
        /// Creates a timer over <paramref name="readMs"/>, which returns a
        /// monotonic millisecond reading. Null selects the process clock.
        /// </summary>
        public MonotonicTimer(Func<double>? readMs = null)
        {
            _readMs = readMs ?? ReadProcessMs;
        }

        /// <summary>Current reading of the source, in milliseconds.</summary>
        public double ReadMs()
        {
            return _readMs();
        }

        /// <summary>
        /// Runs <paramref name="work"/> and returns how long it took, in
        /// milliseconds.
        /// </summary>
        public double ElapsedMs(Action work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }
            double startedAt = ReadMs();
            work();
            return ElapsedMs(startedAt);
        }

        /// <summary>
        /// The cost from a <see cref="ReadMs"/> mark taken before the work to
        /// the reading now, in milliseconds. This is the form a per-call hot
        /// path uses: <see cref="ElapsedMs(Action)"/> would wrap the call in a
        /// closure, which is an allocation per guest call at tick rate, and it
        /// reports no cost at all when the work threw, which is the cost an
        /// operator most wants from a trapping or fuel-burning guest.
        ///
        /// A source that moved backwards (a virtual clock rewound, a
        /// mismeasured platform clock) reports zero rather than a negative
        /// cost, which would otherwise drag an average below zero and read as
        /// a fast dispatch.
        /// </summary>
        public double ElapsedMs(double startedAtMs)
        {
            double elapsed = _readMs() - startedAtMs;
            return elapsed < 0.0 ? 0.0 : elapsed;
        }
    }
}
