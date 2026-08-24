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
    /// The counters surface in "wasm status" and every 100th dropped item is
    /// logged so operators can see a mod is being throttled without the log
    /// itself being spammed.
    /// </summary>
    public sealed class GuestRateLimiter
    {
        public const int MaxLinesPerSecond = 10;

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

        private readonly int _maxPerSecond;

        private sealed class Window
        {
            public int StartTickMs;
            public int Count;
            public long Dropped;
        }

        private readonly Dictionary<string, Window> _windows = new Dictionary<string, Window>(StringComparer.Ordinal);

        /// <summary>
        /// Creates a limiter whose cap is fixed at construction, so the
        /// pairing of limiter instance and cap lives in one place instead of
        /// being re-declared at every call site.
        /// </summary>
        public GuestRateLimiter(int maxPerSecond = MaxLinesPerSecond)
        {
            if (maxPerSecond < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPerSecond));
            }
            _maxPerSecond = maxPerSecond;
        }

        /// <summary>
        /// Returns true when the line may be written, false when the source
        /// exceeded this limiter's cap for this second. Call once per
        /// candidate line. The source's total dropped-line count is reported
        /// in <paramref name="droppedTotal"/>.
        /// </summary>
        public bool TryWrite(string source, out long droppedTotal)
        {
            int nowMs = Environment.TickCount;
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

        /// <summary>One-line summary of dropped items per source, for "wasm status".</summary>
        public string DescribeDropped(string noun)
        {
            var parts = new List<string>();
            foreach (var pair in _windows)
            {
                if (pair.Value.Dropped > 0)
                {
                    parts.Add(pair.Key + "=" + pair.Value.Dropped);
                }
            }
            return parts.Count == 0 ? string.Empty : noun + " dropped: " + string.Join(", ", parts);
        }
    }
}
