using System;
using System.Collections.Generic;
using HordeForge.WasmHost.Abi;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// In-memory IGameHostApi double used by the tests: records every log
    /// line and chat message, serves settings from a dictionary, and returns
    /// a fixed world time.
    /// </summary>
    internal sealed class TestGameHostApi : IGameHostApi
    {
        public List<(string Source, int Level, string Message)> Logs { get; } = new List<(string, int, string)>();

        public List<string> Chats { get; } = new List<string>();

        /// <summary>SimCommands queued by guests through the zdtd queue import.</summary>
        public List<string> QueuedCommands { get; } = new List<string>();

        /// <summary>Sense snapshot served through the zdtd sense import; null = no world data.</summary>
        public SenseSnapshotWriter.Snapshot? Sense { get; set; }

        /// <summary>Mod ids that reached WriteSenseSnapshot, in call order.</summary>
        public List<string> SenseSources { get; } = new List<string>();

        /// <summary>When true, WriteSenseSnapshot throws like a broken game-side service.</summary>
        public bool SenseThrows { get; set; }

        /// <summary>When true, SendChat refuses the message like a chat filter hit.</summary>
        public bool RejectChats { get; set; }

        /// <summary>When true, Log throws like a broken logging backend.</summary>
        public bool LogThrows { get; set; }

        /// <summary>Mod ids that reached TryQueueCommand, in call order.</summary>
        public List<string> QueueSources { get; } = new List<string>();

        public Dictionary<string, string> Settings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Per-mod settings keyed by mod id; resolved before the shared Settings.</summary>
        public Dictionary<string, Dictionary<string, string>> ModSettings { get; } = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        public long WorldTime { get; set; } = 600;

        public void Log(string source, int level, string message)
        {
            if (LogThrows)
            {
                throw new InvalidOperationException("log backend exploded");
            }
            Logs.Add((source, level, message));
        }

        public long GetWorldTime()
        {
            return WorldTime;
        }

        public bool TryGetSetting(string modId, string key, out string value)
        {
            if (modId.Length > 0 && ModSettings.TryGetValue(modId, out var modSettings) && modSettings.TryGetValue(key, out string? perMod))
            {
                value = perMod ?? string.Empty;
                return true;
            }
            bool found = Settings.TryGetValue(key, out string? shared);
            value = shared ?? string.Empty;
            return found;
        }

        public bool SendChat(string message)
        {
            if (RejectChats)
            {
                return false;
            }
            Chats.Add(message);
            return true;
        }

        public bool TryQueueCommand(string modId, string command)
        {
            QueueSources.Add(modId);
            QueuedCommands.Add(command);
            return true;
        }

        public int WriteSenseSnapshot(string modId, Span<byte> buffer)
        {
            SenseSources.Add(modId);
            if (SenseThrows)
            {
                throw new InvalidOperationException("sense backend exploded");
            }
            if (Sense == null)
            {
                return 0;
            }
            return SenseSnapshotWriter.Write(Sense, buffer);
        }

        public string? TryQuery(string request)
        {
            return null;
        }
    }
}
