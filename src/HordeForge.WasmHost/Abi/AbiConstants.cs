namespace HordeForge.WasmHost.Abi
{
    /// <summary>
    /// Names of the host-to-guest import module, guest exports, and ABI
    /// constants. This is the contract between the host and any guest mod.
    /// Any change here is a breaking ABI change; see docs/ABI.md.
    /// </summary>
    public static class AbiConstants
    {
        /// <summary>Module name under which the host defines its game API functions.</summary>
        public const string HostModule = "hordeforge";

        /// <summary>Prefix for guest exports; full names are host-exports like "hordeforge:mod/init".</summary>
        public const string GuestExportPrefix = "hordeforge:mod/";

        /// <summary>Guest export invoked once when the mod is loaded and started.</summary>
        public const string ExportInit = GuestExportPrefix + "init";

        /// <summary>Guest export invoked once per game tick with the tick number.</summary>
        public const string ExportTick = GuestExportPrefix + "tick";

        /// <summary>Guest export invoked when the mod is unloaded or the host shuts down.</summary>
        public const string ExportShutdown = GuestExportPrefix + "shutdown";

        /// <summary>
        /// Optional guest export invoked when a player spawns into the world.
        /// The guest fetches the player name through the
        /// <see cref="ImportGetJoinPlayerName"/> host import.
        /// </summary>
        public const string ExportPlayerJoin = GuestExportPrefix + "on_player_join";

        /// <summary>Host import: writes the joining player's name into a guest buffer.</summary>
        public const string ImportGetJoinPlayerName = "get_join_player_name";

        /// <summary>Log levels understood by the host log import.</summary>
        public const int LogDebug = 0;

        /// <summary>Informational message from a guest.</summary>
        public const int LogInfo = 1;

        /// <summary>Warning message from a guest.</summary>
        public const int LogWarn = 2;

        /// <summary>Error message from a guest.</summary>
        public const int LogError = 3;

        /// <summary>Status codes returned by guest exports. Zero always means ok.</summary>
        public const int StatusOk = 0;

        /// <summary>The guest export is present but intentionally not implemented.</summary>
        public const int StatusNotImplemented = 1;

        /// <summary>The guest export failed internally.</summary>
        public const int StatusInternalError = 2;

        /// <summary>Status codes returned by the get_setting host import.</summary>
        public const int SettingOk = 0;

        /// <summary>The requested setting key does not exist.</summary>
        public const int SettingNotFound = -1;

        /// <summary>The guest output buffer is smaller than the setting value.</summary>
        public const int SettingBufferTooSmall = -2;

        /// <summary>Status codes returned by the send_chat host import.</summary>
        public const int ChatOk = 0;

        /// <summary>The host refused to send the chat message.</summary>
        public const int ChatRejected = -1;
    }
}
