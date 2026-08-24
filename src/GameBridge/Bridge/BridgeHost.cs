using System;
using System.Collections.Generic;
using System.IO;
using HordeForge.WasmHost;
using HordeForge.WasmHost.Config;
using HordeForge.WasmHost.Core;
using HordeForge.WasmHost.Registry;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Owns the WasmModHost for the game process: builds it, scans the
    /// &lt;dedicated&gt;/Mods/Wasm directory for guest modules, and drives
    /// the per-tick dispatch. Also hosts the console-command surface. All
    /// entry points are fail soft and safe to call before the world loads.
    /// </summary>
    public static class BridgeHost
    {
        // Nullable by design: null before Start() and after Shutdown();
        // every entry point re-checks per the fail-soft contract below.
        private static WasmModHost? _host;
        private static WasmSettingsProvider? _settings;
        private static GameHostApi? _gameApi;
        private static BotServant? _servant;
        private static long _tick;

        /// <summary>Folder that holds guest modules: Mods/Wasm under the install.</summary>
        public static string WasmRoot { get; private set; } = string.Empty;

        /// <summary>True once Start completed (mods may still be empty).</summary>
        public static bool Started { get; private set; }

        // Caps the per-tick dispatch-failure log lines per module so a
        // permanently trapping or fuel-burning guest cannot flood the server
        // log at tick rate; totals surface in "wasm status".
        private static readonly GuestRateLimiter DispatchFailureLimiter = new GuestRateLimiter();

        public static void Start()
        {
            // ModApi.ModPath is the modlet folder itself (for example
            // Mods/1_HordeForge_WasmHost); Native/ lives inside it and
            // Mods/Wasm is its sibling.
            string modletDir = ModApi.ModPath;
            NativeBootstrap.Prepare(modletDir);

            WasmRoot = Path.Combine(Path.GetDirectoryName(modletDir) ?? string.Empty, "Wasm");
            _settings = new WasmSettingsProvider(Path.Combine(WasmRoot, "wasm.toml"));

            var config = new WasmHostConfig();
            ApplySharedLimits(config);
            _servant = new BotServant(_settings);
            _gameApi = new GameHostApi(_settings, _servant);
            _host = new WasmModHost(_gameApi, config);

            LoadAllModules();
            _host.DispatchInit();
            Started = true;
            Log.Out("[WasmHost] started; loaded " + _host.ModIds.Count + " module(s) from " + WasmRoot);
        }

        /// <summary>Dispatches one game tick into every loaded guest mod.</summary>
        public static void Tick()
        {
            if (_host == null)
            {
                return;
            }
            // GameTimer.Instance.ticks reads 0 on the dedicated server, so
            // the bridge keeps its own monotonic counter: the hook runs once
            // per game tick (20 TPS), which is the same rhythm.
            _tick++;
            var ids = _host.ModIds;
            IReadOnlyList<ModRunResult> results = _host.DispatchTick(_tick);
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].Ok)
                {
                    continue;
                }
                string source = "tick/" + (i < ids.Count ? ids[i] : "?");
                if (DispatchFailureLimiter.TryWrite(source, out _))
                {
                    Log.Out("[WasmHost] tick: " + results[i].Message +
                            (results[i].Details.Length > 0 ? " (" + results[i].Details + ")" : ""));
                }
            }
        }

        /// <summary>
        /// Player-spawn handler invoked by the Harmony postfix on
        /// GameManager.PlayerSpawnedInWorld (the server-side spawn event;
        /// GameManager.OnClientSpawned does not fire on the dedicated
        /// server, found live in the acceptance run). Forwards the joining
        /// player's name to every guest that exports the optional
        /// on_player_join handler.
        /// </summary>
        public static void PlayerSpawnedInWorld(ClientInfo clientInfo)
        {
            if (_host == null)
            {
                return;
            }
            string name = clientInfo != null ? clientInfo.playerName : string.Empty;
            if (name.Length == 0)
            {
                return;
            }
            // The entity id comes from ClientInfo.entityId:
            // RequestToSpawnPlayer's int parameters are chunk view dim and
            // near-entity id, not the spawning player's id (found live in
            // the acceptance run: the Harmony postfix must not declare
            // parameters by names the target does not have).
            int entityId = clientInfo != null ? clientInfo.entityId : 0;
            Log.Out("[WasmHost] player spawned: " + name + " (entity " + entityId + ")");
            foreach (var result in _host.DispatchPlayerJoin(entityId, name))
            {
                if (!result.Ok)
                {
                    Log.Out("[WasmHost] on_player_join: " + result.Message + (result.Details.Length > 0 ? " (" + result.Details + ")" : ""));
                }
            }
        }

        public static List<string> StatusLines()
        {
            var lines = new List<string>();
            if (_host == null)
            {
                lines.Add("host not started");
                return lines;
            }
            lines.Add("host started, modules dir: " + WasmRoot);
            foreach (string id in _host.ModIds)
            {
                if (_host.TryGetMod(id, out var mod) && mod != null)
                {
                    lines.Add("  " + id + " (init tick " + mod.InitTick + ", calls " + mod.TotalCalls + ", traps " + mod.TrapCalls + ", fuel exhausted " + mod.FuelExhaustedCalls + ")");
                }
            }
            string dropped = _gameApi != null ? _gameApi.RateLimiter.DescribeDropped("guest log lines") : string.Empty;
            if (dropped.Length > 0)
            {
                lines.Add("  " + dropped);
            }
            string droppedChat = _gameApi != null ? _gameApi.ChatLimiter.DescribeDropped("chat messages") : string.Empty;
            if (droppedChat.Length > 0)
            {
                lines.Add("  " + droppedChat);
            }
            string droppedTick = DispatchFailureLimiter.DescribeDropped("tick failure logs");
            if (droppedTick.Length > 0)
            {
                lines.Add("  " + droppedTick);
            }
            return lines;
        }

        /// <summary>Loads every module found under Mods/Wasm/&lt;id&gt;/module.wasm.</summary>
        public static int LoadAllModules()
        {
            if (_host == null)
            {
                return 0;
            }
            int loaded = 0;
            if (!Directory.Exists(WasmRoot))
            {
                return 0;
            }
            foreach (string dir in Directory.GetDirectories(WasmRoot))
            {
                string id = Path.GetFileName(dir);
                string modulePath = Path.Combine(dir, "module.wasm");
                if (!File.Exists(modulePath))
                {
                    continue;
                }
                if (_host.TryGetMod(id, out _))
                {
                    continue;
                }
                if (!TryReadManifest(id, out ModManifest? manifest))
                {
                    // Invalid manifest: skip the module rather than run it
                    // with weaker-than-intended limits.
                    continue;
                }
                byte[] wasmBytes;
                try
                {
                    wasmBytes = File.ReadAllBytes(modulePath);
                }
                catch (Exception ex)
                {
                    // An unreadable module file must not abort the scan or
                    // the bridge start; skip it like any other bad module.
                    Log.Warning("[WasmHost] cannot read " + modulePath + ": " + ex.Message + "; module skipped");
                    continue;
                }
                try
                {
                    _host.LoadModule(id, wasmBytes, manifest);
                    _settings?.UpdateMod(id, manifest);
                    loaded++;
                }
                catch (WasmModLoadException ex)
                {
                    Log.Warning("[WasmHost] failed to load module " + id + ": " + ex.Message);
                }
            }
            return loaded;
        }

        public static bool Reload(string id)
        {
            if (_host == null)
            {
                return false;
            }
            _host.Unload(id);
            _settings?.RemoveMod(id);
            string modulePath = Path.Combine(WasmRoot, id, "module.wasm");
            if (!File.Exists(modulePath))
            {
                return false;
            }
            if (!TryReadManifest(id, out ModManifest? manifest))
            {
                // Invalid manifest: refuse to reload with weaker-than-
                // intended limits; the mod stays unloaded until fixed.
                return false;
            }
            byte[] wasmBytes;
            try
            {
                wasmBytes = File.ReadAllBytes(modulePath);
            }
            catch (Exception ex)
            {
                Log.Warning("[WasmHost] reload of " + id + " failed: cannot read " + modulePath + ": " + ex.Message);
                return false;
            }
            try
            {
                _host.LoadModule(id, wasmBytes, manifest);
                _settings?.UpdateMod(id, manifest);
                if (_host.TryGetMod(id, out var mod) && mod != null)
                {
                    // Init only the module that was reloaded; DispatchInit
                    // would re-run on_enable for every other guest too.
                    mod.Init(_host.Tick);
                }
                return true;
            }
            catch (WasmModLoadException ex)
            {
                Log.Warning("[WasmHost] reload of " + id + " failed: " + ex.Message);
                return false;
            }
        }

        public static bool Unload(string id)
        {
            if (_host != null && _host.Unload(id))
            {
                _settings?.RemoveMod(id);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Applies the shared Mods/Wasm/wasm.toml [limits] over the host code
        /// defaults before the engine is created (start time only; per-mod
        /// manifests can still tighten further at load). Load order follows
        /// the zdtd convention: code defaults -> wasm.toml -> wasm-mod.toml.
        /// </summary>
        private static void ApplySharedLimits(WasmHostConfig config)
        {
            try
            {
                string sharedPath = Path.Combine(WasmRoot, "wasm.toml");
                if (!File.Exists(sharedPath))
                {
                    return;
                }
                ModManifest shared = ModManifest.ParseToml(File.ReadAllText(sharedPath), "shared");
                if (shared.FuelPerCall.HasValue)
                {
                    config.FuelPerCall = shared.FuelPerCall.Value;
                }
                if (shared.MaxMemoryBytes.HasValue)
                {
                    config.StaticMemoryMaximumBytes = shared.MaxMemoryBytes.Value;
                }
            }
            catch (WasmModLoadException ex)
            {
                Log.Warning("[WasmHost] invalid shared wasm.toml limits: " + ex.Message + "; using code defaults");
            }
            catch (Exception ex)
            {
                // Same degradation as a malformed file: the bridge starts
                // with code defaults instead of failing to start.
                Log.Warning("[WasmHost] cannot read shared wasm.toml: " + ex.Message + "; using code defaults");
            }
        }

        /// <summary>
        /// Reads wasm-mod.toml (preferred) or the deprecated wasm-mod.json
        /// for a module id. Returns false when a manifest is present but
        /// invalid (logged); true with a null manifest when the module ships
        /// none, so host defaults apply.
        /// </summary>
        private static bool TryReadManifest(string id, out ModManifest? manifest)
        {
            string dir = Path.Combine(WasmRoot, id);
            string tomlPath = Path.Combine(dir, "wasm-mod.toml");
            string jsonPath = Path.Combine(dir, "wasm-mod.json");
            try
            {
                if (File.Exists(tomlPath))
                {
                    manifest = ModManifest.ParseToml(File.ReadAllText(tomlPath), id);
                    return true;
                }
                if (File.Exists(jsonPath))
                {
                    // Deprecated format, kept for older modules.
                    manifest = ModManifest.Parse(File.ReadAllText(jsonPath), id);
                    return true;
                }
                manifest = null;
                return true;
            }
            catch (WasmModLoadException ex)
            {
                Log.Warning("[WasmHost] invalid manifest for " + id + ": " + ex.Message + "; module skipped");
                manifest = null;
                return false;
            }
            catch (Exception ex)
            {
                // An unreadable manifest file is treated like a malformed
                // one: skip the module instead of running it with defaults.
                Log.Warning("[WasmHost] cannot read manifest for " + id + ": " + ex.Message + "; module skipped");
                manifest = null;
                return false;
            }
        }

        public static void Shutdown()
        {
            if (_host != null)
            {
                _host.Dispose();
                _host = null;
            }
            Started = false;
        }
    }
}
