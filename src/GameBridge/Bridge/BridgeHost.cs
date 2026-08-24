using System;
using System.Collections.Generic;
using System.IO;
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
        private static WasmModHost _host;
        private static WasmSettingsFile _settings;
        private static GameHostApi _gameApi;
        private static long _tick;

        /// <summary>Folder that holds guest modules: Mods/Wasm under the install.</summary>
        public static string WasmRoot { get; private set; } = string.Empty;

        /// <summary>True once Start completed (mods may still be empty).</summary>
        public static bool Started { get; private set; }

        public static void Start()
        {
            // ModApi.ModPath is the modlet folder itself (for example
            // Mods/1_HordeForge_WasmHost); Native/ lives inside it and
            // Mods/Wasm is its sibling.
            string modletDir = ModApi.ModPath;
            NativeBootstrap.Prepare(modletDir);

            WasmRoot = Path.Combine(Path.GetDirectoryName(modletDir) ?? string.Empty, "Wasm");
            _settings = new WasmSettingsFile(Path.Combine(WasmRoot, "wasm-settings.txt"));

            var config = new WasmHostConfig();
            _gameApi = new GameHostApi(_settings);
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
            foreach (var result in _host.DispatchTick(_tick))
            {
                if (!result.Ok)
                {
                    Log.Out("[WasmHost] tick: " + result.Message + (result.Details.Length > 0 ? " (" + result.Details + ")" : ""));
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
            Log.Out("[WasmHost] player spawned: " + name);
            foreach (var result in _host.DispatchPlayerJoin(name))
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
                _host.TryGetMod(id, out var mod);
                lines.Add("  " + id + " (init tick " + mod.InitTick + ", calls " + mod.TotalCalls + ", traps " + mod.TrapCalls + ", fuel exhausted " + mod.FuelExhaustedCalls + ")");
            }
            string dropped = _gameApi != null ? _gameApi.RateLimiter.DescribeDropped() : string.Empty;
            if (dropped.Length > 0)
            {
                lines.Add("  " + dropped);
            }
            string droppedChat = _gameApi != null ? _gameApi.ChatLimiter.DescribeDropped() : string.Empty;
            if (droppedChat.Length > 0)
            {
                lines.Add("  chat " + droppedChat);
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
                ModManifest manifest = null;
                string manifestPath = Path.Combine(dir, "wasm-mod.json");
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        manifest = ModManifest.Parse(File.ReadAllText(manifestPath), id);
                    }
                    catch (WasmModLoadException ex)
                    {
                        Log.Warning("[WasmHost] invalid wasm-mod.json for " + id + ": " + ex.Message + "; module skipped");
                        continue;
                    }
                }
                try
                {
                    _host.LoadModule(id, File.ReadAllBytes(modulePath), manifest);
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
            string modulePath = Path.Combine(WasmRoot, id, "module.wasm");
            if (!File.Exists(modulePath))
            {
                return false;
            }
            try
            {
                _host.LoadModule(id, File.ReadAllBytes(modulePath));
                _host.DispatchInit();
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
            return _host != null && _host.Unload(id);
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
