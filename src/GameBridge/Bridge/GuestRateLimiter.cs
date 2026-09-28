using System;
using System.Collections.Generic;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Caps guest output per source key so a talkative mod cannot flood the
    /// server log or the global chat. A source is a mod id (per-module caps
    /// for log lines and SimCommands) or a shared tag such as "chat" (the
    /// global chat cap). Each source may emit at most the cap the limiter
    /// was constructed with per second, measured with the monotonic process
    /// clock so an operator clock step
    /// or NTP correction can neither freeze output nor open a burst; excess
    /// items are dropped and counted.
    /// The counters surface in "wasm status", and the callers that own a
    /// limit log the running total every 100th drop so an operator can see
    /// a mod is being throttled without the log itself being spammed.
    ///
    /// The window table tracks live sources in a long-running server: a
    /// source that has gone quiet is swept (see EvictIdleSources), and a key
    /// built from guest-written text goes in through
    /// <see cref="SourceKey"/>, which bounds its length. Neither the number
    /// of tracked sources nor the size of a key grows with total traffic.
    ///
    /// Thread safety: one limiter is safe to share between threads. The
    /// window table, the sweep scratch list and the sweep clock are
    /// otherwise unguarded collections, and every entry point here is
    /// reachable from a guest import, so two threads writing one source
    /// would race on the dictionary and a concurrent sweep could evict a
    /// window the other thread is still counting against.
    /// </summary>
    public sealed class GuestRateLimiter
    {
        public const int MaxLinesPerSecond = 10;

        /// <summary>
        /// Dropped items between "suppressed N" lines. A throttled source
        /// stays visible (a running total) without a log line per drop.
        /// </summary>
        public const int SuppressedReportEvery = 100;

        /// <summary>
        /// Cap for guest SimCommands (bot spawn/move/look/shoot) per module.
        /// Generous headroom above a busy brain (a few commands per tick at
        /// 20 TPS), while bounding the game-side work a hostile guest can
        /// trigger: host imports run outside the wasm fuel budget.
        /// </summary>
        public const int MaxCommandsPerSecond = 200;

        /// <summary>
        /// Cap for sense snapshot requests per module. Each request scans
        /// the live world entity list, game-side work the wasm fuel budget
        /// never sees; without the cap a guest could loop the sense import
        /// within one call budget and multiply that scan far past the tick
        /// budget. A brain polls sense once per tick (20/s), so 200/s leaves
        /// wide headroom. Excess requests report "no world data" (0) and are
        /// counted for "wasm status".
        /// </summary>
        public const int MaxSensePerSecond = 200;

        private const int WindowMs = 1000;

        /// <summary>
        /// A source that has not written for this long is dropped from the
        /// table. Windows exist to bound a live source, so keeping the ones
        /// for modules an operator already unloaded only grows the table:
        /// nothing ever read a window without first writing through it.
        /// </summary>
        private const int IdleSourceEvictionMs = 300000;

        /// <summary>
        /// Table size above which idle sources are swept. Below it the sweep
        /// is skipped, since a live server has a handful of sources and the
        /// walk would only cost time.
        /// </summary>
        private const int MaxTrackedSources = 128;

        /// <summary>
        /// How often the idle sweep is allowed to walk the table. The sweep
        /// has its own clock rather than riding on a source's window reset,
        /// because a source written exactly once never rolls its window: a
        /// workload that only ever creates fresh sources (guest-chosen keys
        /// such as the servant's failing verb) would otherwise never reach
        /// the sweep at all.
        /// </summary>
        private const int SweepIntervalMs = 1000;

        /// <summary>
        /// Longest detail a <see cref="SourceKey"/> embeds, in UTF-16 code
        /// units, cut so the prefix never ends in half a character. Source
        /// keys are held for as long as the limiter lives, and some of them
        /// carry guest-written text, so an unbounded detail would let one
        /// command pin an arbitrarily large string in a process-lifetime
        /// table.
        /// </summary>
        internal const int MaxSourceKeyChars = 64;

        private readonly int _maxPerSecond;

        // Serializes the window table, the sweep scratch list and the sweep
        // clock. Held for the length of one TryWrite or one DescribeDropped
        // and across no game call, so it never nests with another lock.
        private readonly object _gate = new object();

        // Monotonic millisecond clock, injectable for tests. Defaults to
        // Environment.TickCount (~24.9-day wraparound, handled by the
        // unchecked subtraction at the reset check).
        private readonly Func<int> _clockMs;

        private sealed class Window
        {
            public int StartTickMs;
            public int Count;
            public long Dropped;
        }

        private readonly Dictionary<string, Window> _windows = new Dictionary<string, Window>(StringComparer.Ordinal);

        /// <summary>Sources currently tracked; the table's own size, for tests.</summary>
        internal int TrackedSourceCount => _windows.Count;

        // Pooled removal list for the idle sweep, so bounding the table
        // never allocates on the guest's log path.
        private readonly List<string> _idle = new List<string>();

        // Clock behind the sweep cadence; see SweepIntervalMs. int.MinValue
        // means "never swept", so the first over-threshold write sweeps.
        private int _lastSweepMs = int.MinValue;

        /// <summary>
        /// Builds a source key whose detail is bounded to
        /// <see cref="MaxSourceKeyChars"/>. Callers whose key embeds
        /// guest-written text go through here so the retained key stays
        /// small however long that text is; a detail within the bound is
        /// passed through unchanged, so ordinary keys are unaffected. A
        /// detail past the bound is cut at a character boundary, one unit
        /// short when the cut would split a surrogate pair.
        /// </summary>
        internal static string SourceKey(string prefix, string detail)
        {
            if (detail.Length <= MaxSourceKeyChars)
            {
                return prefix + detail;
            }
            int cut = MaxSourceKeyChars;
            // A cut landing between the halves of a surrogate pair leaves a
            // lone high surrogate in a key the process then holds and prints
            // ("wasm status"), where it has no UTF-8 form. One unit short is
            // the largest prefix that is still whole characters.
            if (char.IsHighSurrogate(detail[cut - 1]))
            {
                cut--;
            }
            return prefix + detail.Substring(0, cut);
        }

        /// <summary>
        /// Creates a limiter whose cap is fixed at construction, so the
        /// pairing of limiter instance and cap lives in one place instead of
        /// being re-declared at every call site.
        /// </summary>
        public GuestRateLimiter(int maxPerSecond = MaxLinesPerSecond)
            : this(maxPerSecond, () => Environment.TickCount)
        {
        }

        /// <summary>
        /// Same cap with an injected millisecond clock, so window resets
        /// and wraparound are covered without sleeping.
        /// </summary>
        public GuestRateLimiter(int maxPerSecond, Func<int> clockMs)
        {
            if (maxPerSecond < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPerSecond));
            }
            _maxPerSecond = maxPerSecond;
            _clockMs = clockMs;
        }

        /// <summary>
        /// Returns true when the line may be written, false when the source
        /// exceeded this limiter's cap for this second. Call once per
        /// candidate line. The source's total dropped-line count is reported
        /// in <paramref name="droppedTotal"/>.
        /// </summary>
        public bool TryWrite(string source, out long droppedTotal)
        {
            lock (_gate)
            {
                int nowMs = _clockMs();
                if (!_windows.TryGetValue(source, out var window))
                {
                    window = new Window { StartTickMs = nowMs };
                    _windows[source] = window;
                }
                // Reset only after a full window of monotonic time. Wall-clock
                // seconds here would drop every line for as long as a backward
                // clock step (manual change, NTP correction) takes to catch up,
                // and hand every source a free burst on a forward step. Unchecked
                // int subtraction stays correct across TickCount wraparound
                // (~24.9 days) for any sane window length.
                if (nowMs - window.StartTickMs >= WindowMs)
                {
                    window.StartTickMs = nowMs;
                    window.Count = 0;
                }
                // The idle sweep runs on its own clock, not on the calling
                // source's reset: a source written once never rolls its window,
                // so tying the sweep to a reset meant a workload of nothing but
                // fresh sources never reached it and the table grew without
                // bound. At most once a second per limiter, and only above the
                // tracked-source threshold, so the log path stays O(1) and
                // allocation free in the common case. The int.MinValue sentinel
                // is checked explicitly; unchecked subtraction would wrap and
                // read as "not due" (same reasoning as the window reset).
                if (_windows.Count > MaxTrackedSources
                    && (_lastSweepMs == int.MinValue || nowMs - _lastSweepMs >= SweepIntervalMs))
                {
                    _lastSweepMs = nowMs;
                    EvictIdleSources(nowMs);
                }
                if (window.Count >= _maxPerSecond)
                {
                    window.Dropped++;
                    droppedTotal = window.Dropped;
                    return false;
                }
                window.Count++;
                droppedTotal = window.Dropped;
                return true;
            }
        }

        /// <summary>
        /// Drops the window for one source, so a module that is unloaded
        /// leaves no window behind and a module loaded again under the same
        /// id starts its first second with a full budget instead of
        /// resuming the previous generation's window and drop count. The
        /// shared tags ("chat", "world_time") are not module ids and are
        /// never passed here.
        /// </summary>
        public void ForgetSource(string source)
        {
            lock (_gate)
            {
                _windows.Remove(source);
            }
        }

        /// <summary>
        /// Drops windows for sources that have gone quiet, so the table
        /// tracks live sources rather than every id ever seen. The caller
        /// owns the threshold and the cadence. Unchecked int subtraction
        /// matches the window reset above. Dropping a window drops its
        /// dropped-item count too: an idle source is no longer being
        /// throttled, so reporting it as dropped would be wrong.
        /// </summary>
        private void EvictIdleSources(int nowMs)
        {
            List<string> idle = _idle;
            idle.Clear();
            foreach (var pair in _windows)
            {
                if (nowMs - pair.Value.StartTickMs >= IdleSourceEvictionMs)
                {
                    idle.Add(pair.Key);
                }
            }
            foreach (string source in idle)
            {
                _windows.Remove(source);
            }
        }

        /// <summary>
        /// One-line summary of dropped items per source, for "wasm status".
        /// Sources are listed in ordinal key order, not the window table's
        /// hash order: the status line is the run's totals, and two runs of
        /// the same workload must produce the same line so a replayed run
        /// can be diffed against the one that diverged.
        /// </summary>
        public string DescribeDropped(string noun)
        {
            lock (_gate)
            {
                var parts = new List<string>();
                foreach (var pair in _windows)
                {
                    if (pair.Value.Dropped > 0)
                    {
                        parts.Add(pair.Key);
                    }
                }
                if (parts.Count == 0)
                {
                    return string.Empty;
                }
                parts.Sort(StringComparer.Ordinal);
                for (int i = 0; i < parts.Count; i++)
                {
                    parts[i] = parts[i] + "=" + _windows[parts[i]].Dropped;
                }
                return noun + " dropped: " + string.Join(", ", parts);
            }
        }
    }
}
