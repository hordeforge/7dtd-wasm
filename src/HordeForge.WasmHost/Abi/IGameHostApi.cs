using System;

namespace HordeForge.WasmHost.Abi
{
    /// <summary>
    /// The game-facing API surface that the host exposes to guest mods.
    /// Implemented by the embedding application (the in-game bridge, a test
    /// double, or a standalone tool). All calls happen on the caller thread;
    /// guests can never reach game objects directly, only through this
    /// interface. See docs/ABI.md for the wire contract.
    /// </summary>
    public interface IGameHostApi
    {
        /// <summary>
        /// Writes a log line from a guest mod. Level is one of the
        /// <see cref="AbiConstants.LogDebug"/>..<see cref="AbiConstants.LogError"/> constants.
        /// </summary>
        void Log(string source, int level, string message);

        /// <summary>Returns the current world time in 7 Days to Die ticks (world time minutes), or 0 when not in a world.</summary>
        long GetWorldTime();

        /// <summary>
        /// Looks up a setting by key for the given mod. The mod id is the
        /// registry id (folder name); the implementation resolves per-mod
        /// settings before shared ones. Returns false when the key is
        /// unknown so the guest can fall back to a default.
        /// </summary>
        bool TryGetSetting(string modId, string key, out string value);

        // zdtd-server compatibility surface: the sibling fps_bot plugin and
        // its kin drive bots through these. Implemented by the bridge over
        // game services; a host that lacks a servant can still load the
        // plugins (the brain runs, commands are dropped or logged).

        /// <summary>
        /// Queues a text SimCommand from a guest (the bot servant command
        /// surface: "bot spawn", "bot move", "bot look", "bot shoot", ...).
        /// The mod id is the registry id of the calling module, so the
        /// implementation can attribute and rate limit per module. Returns
        /// true when the command was accepted.
        /// </summary>
        bool TryQueueCommand(string modId, string command);

        /// <summary>
        /// Builds the binary world snapshot ('ZBS3', see docs/ABI.md) into
        /// the given buffer (the guest's own linear memory). Returns the
        /// number of bytes written, or 0 when there is no world data to
        /// report.
        /// </summary>
        int WriteSenseSnapshot(Span<byte> buffer);

        /// <summary>
        /// Answers a text query ("cover x z tx tz", "path x z tx tz") with a
        /// text response, or null when the host has no answer.
        /// </summary>
        string? TryQuery(string request);

        /// <summary>
        /// Sends a message to the global chat channel. Returns true when the
        /// game accepted the message.
        /// </summary>
        bool SendChat(string message);
    }
}
