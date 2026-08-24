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

        public Dictionary<string, string> Settings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Per-mod settings keyed by mod id; resolved before the shared Settings.</summary>
        public Dictionary<string, Dictionary<string, string>> ModSettings { get; } = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        public long WorldTime { get; set; } = 600;

        public void Log(string source, int level, string message)
        {
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
            Chats.Add(message);
            return true;
        }

        public bool TryQueueCommand(string modId, string command)
        {
            QueuedCommands.Add(command);
            return true;
        }

        public int WriteSenseSnapshot(Span<byte> buffer)
        {
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
