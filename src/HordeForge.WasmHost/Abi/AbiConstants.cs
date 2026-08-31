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

        /// <summary>Guest export invoked once when the mod is loaded and started.</summary>
        public const string ExportInit = "on_enable";

        /// <summary>Guest export invoked once per game tick; the tick number is read via the tick import.</summary>
        public const string ExportTick = "on_tick";

        /// <summary>Guest export invoked when the mod is unloaded or the host shuts down.</summary>
        public const string ExportShutdown = "on_shutdown";

        /// <summary>
        /// Optional guest export invoked when a player spawns into the world.
        /// Signature (entity_id: i32) -> i32, mirroring zdtd's
        /// on_player_join(slot, entity_id): we pass the entity id; there is
        /// no ECS slot here. The player name is fetched through the
        /// <see cref="ImportGetJoinPlayerName"/> host import.
        /// </summary>
        public const string ExportPlayerJoin = "on_player_join";

        /// <summary>Host import: current game tick (zdtd: tick()).</summary>
        public const string ImportTick = "tick";

        /// <summary>Host import: writes the joining player's name into a guest buffer.</summary>
        public const string ImportGetJoinPlayerName = "get_join_player_name";

        // zdtd-server compatibility surface (docs/PLUGIN_API.md in the
        // zdtd-server repository): the sibling fps_bot plugin and its kin
        // import module "zdtd" with bare field names. Quarantine defines
        // the same module so those plugins load unmodified; the functions
        // are mapped onto the game host API.
        /// <summary>Import module name used by zdtd-server plugins.</summary>
        public const string ZdtdHostModule = "zdtd";

        /// <summary>zdtd import: queue a text SimCommand for the bot servant.</summary>
        public const string ImportQueue = "queue";

        /// <summary>
        /// zdtd import: fill the calling mod's config buffer with the raw
        /// text of its own config.toml (0 = none; the host never parses it).
        /// </summary>
        public const string ImportConfig = "config";

        /// <summary>zdtd import: fill a binary world snapshot ('ZBS4').</summary>
        public const string ImportSense = "sense";

        /// <summary>zdtd import: text request/response query (cover, path).</summary>
        public const string ImportQuery = "query";

        /// <summary>The zdtd queue import accepted the SimCommand.</summary>
        public const int QueueAccepted = 0;

        /// <summary>The queue target refused the command.</summary>
        public const int QueueRejected = -1;

        /// <summary>The query has no answer (cover/path not wired).</summary>
        public const int QueryNoAnswer = -1;

        /// <summary>The guest output buffer is smaller than the response.</summary>
        public const int QueryBufferTooSmall = -2;

        /// <summary>Optional guest export: console/admin command handler (zdtd plugin surface).</summary>
        public const string ExportAdminCommand = "on_admin_command";

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
