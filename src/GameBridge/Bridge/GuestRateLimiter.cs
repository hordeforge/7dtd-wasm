using System;
using System.Collections.Generic;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Caps guest output per module (log lines and chat messages) so a
    /// talkative mod cannot flood the server log or the global chat. Each
    /// source (mod id) may emit at most <see cref="MaxLinesPerSecond"/>
    /// items per wall-clock second; excess items are dropped and counted.
    /// The counters surface in "wasm status" and every 100th dropped item is
    /// logged so operators can see a mod is being throttled without the log
    /// itself being spammed.
    /// </summary>
    public sealed class GuestRateLimiter
    {
        public const int MaxLinesPerSecond = 10;

        private sealed class Window
        {
            public long StartSecond;
            public int Count;
            public long Dropped;
        }

        private readonly Dictionary<string, Window> _windows = new Dictionary<string, Window>(StringComparer.Ordinal);

        /// <summary>
        /// Returns true when the line may be written, false when the source
        /// exceeded its cap for this second. Call once per candidate line.
        /// The source's total dropped-line count is reported in
        /// <paramref name="droppedTotal"/>.
        /// </summary>
        public bool TryWrite(string source, out long droppedTotal)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!_windows.TryGetValue(source, out var window))
            {
                window = new Window { StartSecond = now };
                _windows[source] = window;
            }
            if (window.StartSecond != now)
            {
                window.StartSecond = now;
                window.Count = 0;
            }
            if (window.Count >= MaxLinesPerSecond)
            {
                window.Dropped++;
                droppedTotal = window.Dropped;
                return false;
            }
            window.Count++;
            droppedTotal = window.Dropped;
            return true;
        }

        /// <summary>One-line summary of dropped lines per source, for "wasm status".</summary>
        public string DescribeDropped()
        {
            var parts = new List<string>();
            foreach (var pair in _windows)
            {
                if (pair.Value.Dropped > 0)
                {
                    parts.Add(pair.Key + "=" + pair.Value.Dropped);
                }
            }
            return parts.Count == 0 ? string.Empty : "guest log lines dropped: " + string.Join(", ", parts);
        }
    }
}
