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
    ///
    /// Thread model: tick dispatch and player joins arrive on the game main
    /// loop, while "wasm" console commands execute on the telnet/console
    /// thread of the dedicated server. Every entry point therefore takes
    /// <see cref="Gate"/>; the host library itself stays single-threaded per
    /// its contract (one store must never be touched from two threads, and
    /// a mid-dispatch unload would throw out of the load-order walk). The
    /// gate can stall a console command until the current dispatch ends;
    /// both sides are bounded (fuel per guest call, module size cap on
    /// compile), so this trades a bounded pause for the crash risk of
    /// concurrent store access.
    /// </summary>
    public static class BridgeHost
    {
        // Serializes main-loop entry points (Tick, PlayerSpawnedInWorld)
        // against console-thread ones (LoadAllModules, Reload, Unload,
        // StatusLines) and lifecycle (Start, Shutdown). Monitor reentrancy
        // makes the internal call chains (Start -> LoadAllModules,
        // Reload -> TryLoadFromDisk -> InitOne) safe without extra hops.
        private static readonly object Gate = new object();

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
            lock (Gate)
            {
                // ModApi.ModPath is the modlet folder itself (for example
                // Mods/1_HordeForge_WasmHost); Native/ lives inside it and
                // Mods/Wasm is its sibling.
                string modletDir = ModApi.ModPath;
                NativeBootstrap.Prepare(modletDir);

                WasmRoot = Path.Combine(Path.GetDirectoryName(modletDir) ?? string.Empty, "Wasm");
                string sharedTomlPath = Path.Combine(WasmRoot, "wasm.toml");
                _settings = new WasmSettingsProvider(sharedTomlPath);

                var config = new WasmHostConfig();
                ApplySharedLimits(config, sharedTomlPath);
                _servant = new BotServant();
                _gameApi = new GameHostApi(_settings, _servant);
                _host = new WasmModHost(_gameApi, config);

                // LoadAllModules runs each newly loaded module's on_enable (see
                // there), so start and "wasm load" initialize exactly once.
                LoadAllModules();
                Started = true;
                Log.Out("[WasmHost] started; loaded " + _host.ModIds.Count + " module(s) from " + WasmRoot);
            }
        }

        /// <summary>Dispatches one game tick into every loaded guest mod.</summary>
        public static void Tick()
        {
            lock (Gate)
            {
                WasmModHost? host = _host;
                if (host == null)
                {
                    return;
                }
                // GameTimer.Instance.ticks reads 0 on the dedicated server, so
                // the bridge keeps its own monotonic counter: the hook runs once
                // per game tick (20 TPS), which is the same rhythm.
                _tick++;
                var ids = host.ModIds;
                IReadOnlyList<ModRunResult> results = host.DispatchTick(_tick);
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
        }

        /// <summary>Current value of the bridge's monotonic tick counter.</summary>
        internal static long CurrentTick => _tick;

        /// <summary>
        /// Player-spawn handler invoked by the Harmony postfix on
        /// GameManager.RequestToSpawnPlayer (Hooks/PlayerSpawnHook; the
        /// server-side join entry point, since PlayerSpawnedInWorld and
        /// OnClientSpawned do not fire on the dedicated server, found live
        /// in the acceptance run). Forwards the joining player's name to
        /// every guest that exports the optional on_player_join handler.
        /// </summary>
        public static void PlayerSpawnedInWorld(ClientInfo clientInfo)
        {
            if (clientInfo == null)
            {
                return;
            }
            lock (Gate)
            {
                WasmModHost? host = _host;
                if (host == null)
                {
                    return;
                }
                string name = clientInfo.playerName ?? string.Empty;
                if (name.Length == 0)
                {
                    return;
                }
                // The entity id comes from ClientInfo.entityId:
                // RequestToSpawnPlayer's int parameters are chunk view dim and
                // near-entity id, not the spawning player's id (found live in
                // the acceptance run: the Harmony postfix must not declare
                // parameters by names the target does not have).
                int entityId = clientInfo.entityId;
                Log.Out("[WasmHost] player spawned: " + TextSanitizer.Clean(name) + " (entity " + entityId + ")");
                foreach (var result in host.DispatchPlayerJoin(entityId, name))
                {
                    if (!result.Ok)
                    {
                        Log.Out("[WasmHost] on_player_join: " + result.Message + (result.Details.Length > 0 ? " (" + result.Details + ")" : ""));
                    }
                }
            }
        }

        public static List<string> StatusLines()
        {
            lock (Gate)
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
                if (_gameApi != null)
                {
                    AddDropped(lines, _gameApi.RateLimiter, "guest log lines");
                    AddDropped(lines, _gameApi.ChatLimiter, "chat messages");
                    AddDropped(lines, _gameApi.CommandLimiter, "sim commands");
                    AddDropped(lines, _gameApi.WorldTimeErrorLimiter, "world time failures");
                }
                AddDropped(lines, DispatchFailureLimiter, "tick failure logs");
                return lines;
            }
        }

        /// <summary>Appends the limiter's dropped summary when it has one.</summary>
        private static void AddDropped(List<string> lines, GuestRateLimiter limiter, string noun)
        {
            string dropped = limiter.DescribeDropped(noun);
            if (dropped.Length > 0)
            {
                lines.Add("  " + dropped);
            }
        }

        /// <summary>
        /// Loads every module found under Mods/Wasm/&lt;id&gt;/module.wasm and
        /// runs its on_enable export (docs/ABI.md: called once when the mod
        /// is loaded and enabled), so "wasm load" leaves new modules in the
        /// same state as a server start. Returns the number of new modules.
        /// </summary>
        public static int LoadAllModules()
        {
            lock (Gate)
            {
                WasmModHost? host = _host;
                if (host == null)
                {
                    return 0;
                }
                int loaded = 0;
                if (!Directory.Exists(WasmRoot))
                {
                    return 0;
                }
                var loadedIds = new List<string>();
                foreach (string dir in Directory.GetDirectories(WasmRoot))
                {
                    string id = Path.GetFileName(dir);
                    if (host.TryGetMod(id, out _))
                    {
                        continue;
                    }
                    if (!TryLoadFromDisk(host, id))
                    {
                        continue;
                    }
                    loadedIds.Add(id);
                    loaded++;
                }
                foreach (string id in loadedIds)
                {
                    InitOne(id);
                }
                return loaded;
            }
        }

        /// <summary>
        /// Loads one module from its Mods/Wasm/&lt;id&gt;/module.wasm file and
        /// registers its manifest settings. Shared by start scanning and
        /// "wasm reload". Returns false (logged) when the manifest is
        /// invalid or the module is unreadable or rejected; on_enable is
        /// NOT run here, callers init explicitly.
        /// </summary>
        private static bool TryLoadFromDisk(WasmModHost host, string id)
        {
            string modulePath = Path.Combine(WasmRoot, id, "module.wasm");
            if (!File.Exists(modulePath))
            {
                return false;
            }
            if (!TryReadManifest(id, out ModManifest? manifest))
            {
                // Invalid manifest: refuse to run the module with
                // weaker-than-intended limits.
                return false;
            }
            byte[] wasmBytes;
            try
            {
                wasmBytes = File.ReadAllBytes(modulePath);
            }
            catch (Exception ex)
            {
                // An unreadable module file must not abort the scan or the
                // bridge start; skip it like any other bad module.
                Log.Warning("[WasmHost] cannot read " + modulePath + ": " + ex.Message + "; module skipped");
                return false;
            }
            try
            {
                host.LoadModule(id, wasmBytes, manifest);
            }
            catch (WasmModLoadException ex)
            {
                Log.Warning("[WasmHost] failed to load module " + id + ": " + ex.Message);
                return false;
            }
            _settings?.UpdateMod(id, manifest);
            return true;
        }

        /// <summary>
        /// Runs one module's on_enable, fail soft: a trapped enable is
        /// logged and the module stays loaded (its next tick is budgeted
        /// like any other).
        /// </summary>
        private static void InitOne(string id)
        {
            if (_host == null || !_host.TryGetMod(id, out WasmMod? mod) || mod == null)
            {
                return;
            }
            ModRunResult result = mod.Init(_host.Tick);
            if (!result.Ok)
            {
                Log.Warning("[WasmHost] on_enable of " + id + ": " + result.Message +
                            (result.Details.Length > 0 ? " (" + result.Details + ")" : ""));
            }
        }

        /// <summary>
        /// True when the id is a plain folder name under WasmRoot: non-empty
        /// and free of path separators. Ids arrive from console input
        /// ("wasm reload &lt;id&gt;"), so this keeps the module path inside
        /// Mods/Wasm.
        /// </summary>
        public static bool IsValidModId(string id)
        {
            return !string.IsNullOrEmpty(id) &&
                   id.IndexOf('/') < 0 &&
                   id.IndexOf('\\') < 0 &&
                   id != "." && id != "..";
        }

        public static bool Reload(string id)
        {
            lock (Gate)
            {
                WasmModHost? host = _host;
                if (host == null || !IsValidModId(id))
                {
                    return false;
                }
                ModRunResult? oldShutdown = host.Unload(id);
                if (oldShutdown != null && !oldShutdown.Ok)
                {
                    // The reload proceeds either way, but the failed goodbye of
                    // the old instance must reach the log like an unload's would.
                    Log.Warning("[WasmHost] reload of " + id + ": shutdown of previous instance failed: " +
                                oldShutdown.Message + (oldShutdown.Details.Length > 0 ? " (" + oldShutdown.Details + ")" : ""));
                }
                _settings?.RemoveMod(id);
                if (!TryLoadFromDisk(host, id))
                {
                    return false;
                }
                // Init only the module that was reloaded; dispatching init to
                // the host would re-run on_enable for every other guest.
                InitOne(id);
                return true;
            }
        }

        public static bool Unload(string id)
        {
            lock (Gate)
            {
                WasmModHost? host = _host;
                if (host == null)
                {
                    return false;
                }
                ModRunResult? shutdown = host.Unload(id);
                if (shutdown == null)
                {
                    return false;
                }
                _settings?.RemoveMod(id);
                if (!shutdown.Ok)
                {
                    // Fail soft: the mod is gone either way, but a trapped or
                    // failing shutdown must reach the operator instead of a
                    // bare "unloaded" from the console command.
                    Log.Warning("[WasmHost] unload of " + id + ": " + shutdown.Message +
                                (shutdown.Details.Length > 0 ? " (" + shutdown.Details + ")" : ""));
                }
                return true;
            }
        }

        /// <summary>
        /// Applies the shared Mods/Wasm/wasm.toml [limits] over the host code
        /// defaults before the engine is created (start time only; per-mod
        /// manifests can still tighten further at load). Load order follows
        /// the zdtd convention: code defaults -> wasm.toml -> wasm-mod.toml.
        /// </summary>
        private static void ApplySharedLimits(WasmHostConfig config, string sharedPath)
        {
            try
            {
                if (!File.Exists(sharedPath))
                {
                    return;
                }
                ModManifest shared = ModManifest.ParseToml(ReadBounded(sharedPath), "shared");
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
                    manifest = ModManifest.ParseToml(ReadBounded(tomlPath), id);
                    return true;
                }
                if (File.Exists(jsonPath))
                {
                    // Deprecated format, kept for older modules.
                    manifest = ModManifest.Parse(ReadBounded(jsonPath), id);
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
                // An unreadable or oversized manifest file is treated like a
                // malformed one: skip the module instead of running it with
                // defaults.
                Log.Warning("[WasmHost] cannot read manifest for " + id + ": " + ex.Message + "; module skipped");
                manifest = null;
                return false;
            }
        }

        /// <summary>
        /// Reads a manifest file behind the shared size bound; throws so the
        /// caller's existing error paths (skip the module, keep defaults)
        /// handle it uniformly.
        /// </summary>
        private static string ReadBounded(string path)
        {
            if (!ManifestFiles.TryRead(path, out string content, out string? failureReason))
            {
                throw new InvalidOperationException(path + " is unreadable: " + failureReason);
            }
            return content;
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                if (_host != null)
                {
                    _host.Dispose();
                    _host = null;
                }
                // Release what Start() built so a shutdown leaves no static
                // references behind; the next Start() recreates all of them.
                _gameApi = null;
                _servant = null;
                _settings = null;
                Started = false;
            }
        }
    }
}
