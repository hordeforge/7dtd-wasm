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
        /// <see cref="AbiConstants.LogDebug"/> constants.
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

        /// <summary>
        /// Sends a message to the global chat channel. Returns true when the
        /// game accepted the message.
        /// </summary>
        bool SendChat(string message);
    }
}
