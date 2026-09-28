using System.Globalization;

namespace HordeForge.WasmHost.Core
{
    /// <summary>
    /// Rolling wall-clock cost of the per-tick guest dispatch. The guest
    /// counters on <see cref="WasmMod"/> say how often a mod failed, never
    /// how much of the game frame the dispatch ate, so without this a
    /// fuel-burning mod looks identical to a cheap one in the log. One
    /// instance per host; the caller records exactly one sample per tick.
    /// No allocation, no clock read of its own (the caller passes the
    /// measured duration, keeping this testable without sleeping).
    /// </summary>
    public sealed class TickTelemetry
    {
        /// <summary>
        /// Ticks between heartbeat lines: 1200 ticks is 60 s at the
        /// dedicated server's 20 TPS, frequent enough to show a stopped
        /// dispatch and rare enough not to be log noise.
        /// </summary>
        public const long HeartbeatIntervalTicks = 1200;

        /// <summary>
        /// Dispatch cost worth a warning: half of a 20 TPS frame. Past this
        /// the guests are measurably eating the tick budget of the game
        /// loop they run on.
        /// </summary>
        public const double SlowDispatchMs = 25.0;

        /// <summary>Samples in the rolling average window.</summary>
        public const int WindowTicks = 1200;

        private long _lastTick;
        private long _totalCalls;
        private long _totalFailures;
        private long _slowTicks;
        private long _windowCalls;
        private double _windowMs;
        private double _windowMaxMs;
        private double _lastMs;

        /// <summary>Ticks recorded; equals the tick counter of the host.</summary>
        public long Ticks => _totalCalls;

        /// <summary>Tick number of the most recent sample (0 before any).</summary>
        public long LastTick => _lastTick;

        /// <summary>Mods whose dispatch reported a failure, summed over all ticks.</summary>
        public long TotalFailures => _totalFailures;

        /// <summary>Ticks whose dispatch exceeded <see cref="SlowDispatchMs"/>.</summary>
        public long SlowTicks => _slowTicks;

        /// <summary>Duration of the most recent dispatch, in milliseconds.</summary>
        public double LastMs => _lastMs;

        /// <summary>Slowest dispatch in the current window, in milliseconds.</summary>
        public double WindowMaxMs => _windowMaxMs;

        /// <summary>Mean dispatch cost over the current window, 0 with no samples.</summary>
        public double AverageMs => _windowCalls == 0 ? 0.0 : _windowMs / _windowCalls;

        /// <summary>True when the most recent sample exceeded the slow-dispatch budget.</summary>
        public bool IsSlow => _lastMs > SlowDispatchMs;

        /// <summary>
        /// True on the tick that closes a heartbeat window, so the bridge
        /// can log liveness plus cost once a minute instead of every tick.
        /// </summary>
        public bool HeartbeatDue => _lastTick > 0 && _lastTick % HeartbeatIntervalTicks == 0;

        /// <summary>
        /// Records one tick: its dispatch duration and how many mods failed
        /// on it. A negative duration (a mismeasured clock) counts as zero
        /// so it cannot drag the average below zero.
        /// </summary>
        public void Record(long tick, double elapsedMs, int failures)
        {
            if (elapsedMs < 0.0)
            {
                elapsedMs = 0.0;
            }
            // The window rolls when a sample arrives into a full one, so a
            // full window always holds WindowTicks samples: resetting on
            // the way out would report an empty average on the heartbeat
            // tick that closes the window.
            if (_windowCalls >= WindowTicks)
            {
                _windowCalls = 0;
                _windowMs = 0.0;
                _windowMaxMs = 0.0;
            }
            _lastTick = tick;
            _lastMs = elapsedMs;
            _totalCalls++;
            if (failures > 0)
            {
                _totalFailures += failures;
            }
            if (elapsedMs > SlowDispatchMs)
            {
                _slowTicks++;
            }
            _windowCalls++;
            _windowMs += elapsedMs;
            if (elapsedMs > _windowMaxMs)
            {
                _windowMaxMs = elapsedMs;
            }
        }

        /// <summary>Resets every counter; used on host start and shutdown.</summary>
        public void Reset()
        {
            _lastTick = 0;
            _totalCalls = 0;
            _totalFailures = 0;
            _slowTicks = 0;
            _windowCalls = 0;
            _windowMs = 0.0;
            _windowMaxMs = 0.0;
            _lastMs = 0.0;
        }

        /// <summary>
        /// One-line summary for "wasm status" and the heartbeat: cost of
        /// the dispatch, not just the guest counters. Invariant formatting
        /// so the line is greppable.
        /// </summary>
        public string Describe()
        {
            return "dispatch: " + FormatMilliseconds(_lastMs) + " last, " + FormatMilliseconds(AverageMs) +
                   " avg, " + FormatMilliseconds(_windowMaxMs) + " max over " + _windowCalls + " tick(s); " +
                   _totalFailures + " failure(s), " + _slowTicks + " slow tick(s)";
        }

        /// <summary>Invariant millisecond rendering, so log lines are greppable.</summary>
        public static string FormatMilliseconds(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture) + " ms";
        }
    }
}
