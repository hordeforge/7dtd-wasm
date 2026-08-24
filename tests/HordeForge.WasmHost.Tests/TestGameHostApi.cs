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

        public Dictionary<string, string> Settings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public long WorldTime { get; set; } = 600;

        public void Log(string source, int level, string message)
        {
            Logs.Add((source, level, message));
        }

        public long GetWorldTime()
        {
            return WorldTime;
        }

        public bool TryGetSetting(string key, out string value)
        {
            bool found = Settings.TryGetValue(key, out string? v);
            value = v ?? string.Empty;
            return found;
        }

        public bool SendChat(string message)
        {
            Chats.Add(message);
            return true;
        }
    }
}
