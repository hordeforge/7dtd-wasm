using System;
using System.Diagnostics;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Cost of one unit of work in milliseconds, read through an injectable
    /// monotonic source. The per-tick guest dispatch is timed, and that
    /// measurement lands in the run's own log lines (the slow-dispatch
    /// warning, the minute heartbeat, "wasm status", the shutdown summary).
    /// Measuring it off a hardwired <see cref="Stopwatch"/> makes those lines
    /// the one part of the bridge a replay cannot reproduce: two runs over the
    /// same inputs then disagree on cost, and the diff names the clock rather
    /// than the guest that caused it. Every other decision the bridge makes on
    /// time already goes through <c>BridgeHost.ClockMs</c>; this is the
    /// sub-millisecond half of that pair, and a driver stepping virtual time
    /// replaces both.
    ///
    /// The default source is the process monotonic clock, so production timing
    /// is unchanged. A virtual source need only be monotonic and to advance
    /// when the simulated work does: a driver that returns the same reading
    /// twice measures zero, which is a legitimate dispatch cost, and one that
    /// steps forward by the work it simulated reports exactly that step.
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
        /// milliseconds. A source that moved backwards (a virtual clock
        /// rewound, a mismeasured platform clock) reports zero rather than a
        /// negative cost, which would otherwise drag an average below zero
        /// and read as a fast dispatch.
        /// </summary>
        public double ElapsedMs(Action work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }
            double startedAt = _readMs();
            work();
            double elapsed = _readMs() - startedAt;
            return elapsed < 0.0 ? 0.0 : elapsed;
        }
    }
}
