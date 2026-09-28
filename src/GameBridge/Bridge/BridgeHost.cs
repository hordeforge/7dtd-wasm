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
    /// <see cref="Gate"/>: it keeps the host's guest calls off two threads
    /// at once and gives the bridge's own mutable state (settings tables,
    /// raw config cache, rate limiters, bot and glide records) a single
    /// writer. The gate can stall a console command until the current
    /// dispatch ends; both sides are bounded (fuel per guest call, module
    /// size cap on compile), so this trades a bounded pause for the crash
    /// risk of concurrent store access.
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

        // Monotonic millisecond clock behind every rate window, throttle,
        // and probe in the bridge (the guest log, chat, SimCommand, sense,
        // and servant caps, the spawn top-up, the shared-file probe). Each
        // of those decides whether a guest's output is accepted or dropped
        // on this clock, so a run replayed from the same inputs makes the
        // same decisions only if the clock reads the same too. Defaults to
        // the process clock; a driver that steps its own time (a simulation
        // or a test) replaces it before Start and the whole bridge follows
        // that time. Nothing in the bridge reads a millisecond clock any
        // other way. Volatile because a driver that swaps it from another
        // thread must be seen by every reader here, none of which holds
        // Gate when it asks for the clock.
        private static volatile Func<int> _clockMs = () => Environment.TickCount;

        // Monotonic source behind the per-tick dispatch measurement. Same
        // rule as _clockMs and for the same reason: the measured cost is
        // printed into the run's own heartbeat, status, and shutdown lines,
        // so a replayed run must read its cost from the same clock the
        // original did. Defaults to the process clock; a driver that steps
        // virtual time replaces this and the whole bridge follows.
        private static MonotonicTimer _timer = MonotonicTimer.Default;

        /// <summary>
        /// Timer the per-tick dispatch cost is measured with, the sub-
        /// millisecond half of the pair <see cref="ClockMs"/> and this make
        /// up. Set before <see cref="Start"/> to drive the bridge from a
        /// virtual clock. Never null.
        /// </summary>
        public static MonotonicTimer Timer
        {
            get { return _timer; }
            set { _timer = value ?? throw new ArgumentNullException(nameof(value)); }
        }

        /// <summary>
        /// The millisecond clock every bridge rate window measures against.
        /// Set before <see cref="Start"/> to drive the bridge from a virtual
        /// clock. Never null.
        /// </summary>
        public static Func<int> ClockMs
        {
            get { return _clockMs; }
            set { _clockMs = value ?? throw new ArgumentNullException(nameof(value)); }
        }

        /// <summary>Folder that holds guest modules: Mods/Wasm under the install.</summary>
        public static string WasmRoot { get; private set; } = string.Empty;

        /// <summary>
        /// Ordered module trees: Mods/Wasm first, then each staged modlet's
        /// own Wasm/ folder (a managed instance stages whole modlets, never
        /// loose files under Mods/, so a guest tree that must survive
        /// `sb wipe` ships as one, for example Mods/wasm-bridge/Wasm).
        /// Only existing directories count; two trees shipping the same id
        /// resolve to the first.
        ///
        /// Published as an immutable snapshot and replaced wholesale under
        /// <see cref="Gate"/>, never filled in place: the resolvers below
        /// are reachable from a guest import, which does not take Gate, and
        /// enumerating a list that Start was rebuilding or Shutdown was
        /// clearing throws mid-walk or resolves against half the trees. A
        /// reference read is atomic; volatile hands the whole published list
        /// to a reader that never takes the gate.
        /// </summary>
        private static volatile IReadOnlyList<string> _moduleTreeRoots = Array.Empty<string>();

        /// <summary>True once Start completed (mods may still be empty).</summary>
        public static bool Started { get; private set; }

        // Caps the per-tick dispatch-failure log lines per module so a
        // permanently trapping or fuel-burning guest cannot flood the server
        // log at tick rate; totals surface in "wasm status".
        private static readonly GuestRateLimiter DispatchFailureLimiter =
            new GuestRateLimiter(GuestRateLimiter.MaxLinesPerSecond, () => ClockMs());

        // A dispatch that overruns the frame budget is a real fault, not a
        // per-mod flood, so it gets its own one-per-second budget: a guest
        // burning fuel every tick must not suppress the warning, but it must
        // not produce 20 lines a second either.
        private static readonly GuestRateLimiter DispatchSlowLimiter =
            new GuestRateLimiter(DispatchSlowLogsPerSecond, () => ClockMs());

        /// <summary>Slow-dispatch warnings allowed per second.</summary>
        private const int DispatchSlowLogsPerSecond = 1;

        // Wall-clock cost of the per-tick dispatch: guest counters say how
        // often a mod failed, never how much of the game frame it ate.
        private static readonly TickTelemetry _telemetry = new TickTelemetry();

        public static void Start()
        {
            lock (Gate)
            {
                if (Started)
                {
                    Log.Warning("[WasmHost] start ignored: host is already running");
                    return;
                }
                // A previous Start that failed partway leaves a live engine
                // behind; rebuilding over it without disposal would leak
                // every module loaded in that attempt.
                if (_host != null)
                {
                    _host.Dispose();
                    _host = null;
                }

                // ModApi.ModPath is the modlet folder itself (for example
                // Mods/1_HordeForge_WasmHost); Native/ lives inside it and
                // Mods/Wasm is its sibling.
                string modletDir = ModApi.ModPath;
                NativeBootstrap.Prepare(modletDir);

                WasmRoot = Path.Combine(Path.GetDirectoryName(modletDir) ?? string.Empty, "Wasm");
                string extraFailure;
                IReadOnlyList<string> extraRoots = ModuleRoots.CollectExtra(
                    Path.GetDirectoryName(modletDir) ?? string.Empty, modletDir, out extraFailure);
                if (extraFailure.Length > 0)
                {
                    // Without this the modlet-carried Wasm trees are simply
                    // absent, and the operator sees a server that loaded
                    // fewer modules than they staged with nothing in the log
                    // to say why.
                    Log.Warning("[WasmHost] modlet-carried module trees unavailable: " +
                                TextSanitizer.Clean(extraFailure) + "; only " + WasmRoot + " is scanned");
                }
                var treeRoots = new List<string>(ModuleRoots.Order(WasmRoot, extraRoots));
                _moduleTreeRoots = treeRoots;
                // A modlet-carried tree can ship its own shared limits; the
                // top-level Mods/Wasm/wasm.toml still wins when both exist.
                string sharedTomlPath = Path.Combine(WasmRoot, "wasm.toml");
                foreach (string extra in treeRoots)
                {
                    string extraShared = Path.Combine(extra, "wasm.toml");
                    if (!File.Exists(sharedTomlPath) && File.Exists(extraShared))
                    {
                        sharedTomlPath = extraShared;
                        break;
                    }
                }
                _settings = new WasmSettingsProvider(sharedTomlPath, () => ClockMs());

                var config = new WasmHostConfig();
                if (!TryApplySharedLimits(config, sharedTomlPath))
                {
                    // The host's own limits file is the one piece of config
                    // the host cannot run without an answer for: falling back
                    // to the code defaults would quietly hand every guest a
                    // fuel budget and memory ceiling the operator never
                    // asked for. Refuse to start the host instead (the game
                    // keeps running, no guest loads) and say what to fix.
                    Log.Warning("[WasmHost] start aborted: fix " + TextSanitizer.Clean(sharedTomlPath) +
                                " and restart the server; no guest modules are loaded");
                    return;
                }
                _servant = new BotServant(() => _tick, () => ClockMs());
                _gameApi = new GameHostApi(_settings, _servant, config.LogSourcePrefix, () => ClockMs());
                try
                {
                    _host = new WasmModHost(_gameApi, config);
                }
                catch (Exception ex)
                {
                    // The limits file parsed, but the engine refuses the
                    // configuration it describes (a ceiling outside the range
                    // the engine or the wasm32 model can express). Letting
                    // that escape would take the tick and player-join hooks
                    // down with it, since they are applied after this call;
                    // the bridge's own contract is that no guest loads and
                    // the rest of the mod keeps working.
                    Log.Warning("[WasmHost] start aborted: " + TextSanitizer.Clean(ex.Message) +
                                "; fix " + TextSanitizer.Clean(sharedTomlPath) + " and restart the server");
                    _gameApi = null;
                    _servant = null;
                    _settings = null;
                    _moduleTreeRoots = Array.Empty<string>();
                    return;
                }
                _telemetry.Reset();

                // LoadAllModules runs each newly loaded module's on_enable (see
                // there), so start and "wasm load" initialize exactly once.
                LoadAllModules();
                if (treeRoots.Count > 1)
                {
                    Log.Out("[WasmHost] extra module tree(s): " +
                        string.Join(", ", treeRoots.GetRange(1, treeRoots.Count - 1).ToArray()));
                }
                Started = true;
                Log.Out("[WasmHost] started; loaded " + _host.ModIds.Count + " module(s) from " + WasmRoot);
                // The limits actually in force, not the code defaults: they
                // are the difference between the engine the operator meant
                // and the one the layering produced.
                Log.Out("[WasmHost] limits: " + EffectiveLimits());
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
                // Read on demand: ModIds hands out a fresh copy of the load
                // order, and the tick hook runs 20 times a second for a value
                // only a failing dispatch, a slow-dispatch warning, or the
                // once-a-minute heartbeat ever prints. Taking it here instead
                // would allocate two objects per tick for nothing.
                IReadOnlyList<string>? ids = null;
                IReadOnlyList<ModRunResult> results = Array.Empty<ModRunResult>();
                double elapsedMs = Timer.ElapsedMs(() => results = host.DispatchTick(_tick));
                int failures = 0;
                for (int i = 0; i < results.Count; i++)
                {
                    ModRunResult result = results[i];
                    if (result.Ok)
                    {
                        continue;
                    }
                    failures++;
                    ids ??= host.ModIds;
                    string modName = FailureName(result, ids, i);
                    if (DispatchFailureLimiter.TryWrite("tick/" + modName, out long dropped))
                    {
                        // The mod id and the tick number are what turn a line
                        // into a pivot point: without them "fuel exhausted
                        // during on_tick" is unattributable in a log that
                        // carries ten such lines per guest per second, and the
                        // tick number is the only handle that lines this up
                        // with the heartbeat and the per-mod counters in
                        // "wasm status".
                        Log.Warning("[WasmHost] tick " + _tick + " mod " + TextSanitizer.Clean(modName) + ": " +
                                   Describe(result));
                    }
                    else if (dropped % GuestRateLimiter.SuppressedReportEvery == 1)
                    {
                        // A mod failing every tick would otherwise be silent
                        // after the cap, leaving only a per-tick count in
                        // "wasm status"; the running total says how far behind
                        // the log is.
                        Log.Out("[WasmHost] suppressed " + dropped + " tick failure log(s) from guest " +
                                TextSanitizer.Clean(modName));
                    }
                }
                _telemetry.Record(_tick, elapsedMs, failures);
                if (_telemetry.IsSlow && DispatchSlowLimiter.TryWrite("tick", out _))
                {
                    ids ??= host.ModIds;
                    // The aggregate cost says the frame was lost but not who
                    // spent it, and the answer is a per-guest counter away.
                    // The walk runs on the warning path only, which is capped
                    // at one per second, so it never lands on the tick rate.
                    Log.Warning("[WasmHost] tick " + _tick + " dispatch took " +
                                TickTelemetry.FormatMilliseconds(elapsedMs) + " for " + ids.Count +
                                " module(s), over the " + TickTelemetry.FormatMilliseconds(TickTelemetry.SlowDispatchMs) +
                                " budget" + SlowestGuest(ids, host) +
                                "; the game loop is losing time to guests");
                }
                if (_telemetry.HeartbeatDue)
                {
                    ids ??= host.ModIds;
                    // Liveness plus cost once a minute: silence from this
                    // mod is otherwise ambiguous between a healthy host and
                    // a tick hook that stopped firing.
                    Log.Out("[WasmHost] heartbeat tick " + _tick + ", " + ids.Count + " module(s); " +
                            _telemetry.Describe());
                }
            }
        }

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
                // The name identifies a player, and the server log outlives
                // the session (it is quoted into bug reports and kept with
                // the server-data folder), so it is not written here. The
                // entity id is what the dispatch is keyed on and cannot name
                // a player on its own; the guests still receive the name
                // through the join handler, which is the documented ABI.
                Log.Out("[WasmHost] player spawned (entity " + entityId + "); name dispatched to guests");
                foreach (var result in host.DispatchPlayerJoin(entityId, name))
                {
                    if (!result.Ok)
                    {
                        // Results are attributed by ModId (join dispatch calls
                        // only the mods that export the handler, so list index
                        // does not identify the module).
                        // Same level as a failed tick: a trapped or
                        // fuel-starved join handler is a guest fault, not an
                        // event the operator should have to go looking for.
                        // Joins are not rate capped by tick rate, so this line
                        // cannot flood.
                        Log.Warning("[WasmHost] on_player_join " + TextSanitizer.Clean(result.ModId) + ": " +
                                    Describe(result));
                    }
                }
            }
        }

        /// <summary>
        /// The full host report: configured limits, one line per loaded
        /// module, the dropped totals of every limiter, the armed glide ids,
        /// and the tick telemetry line. Used by "wasm status".
        /// </summary>
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
                lines.Add("limits: " + EffectiveLimits());
                foreach (string id in _host.ModIds)
                {
                    if (_host.TryGetMod(id, out var mod) && mod != null)
                    {
                        // Every failure class the host counts, and the fuel
                        // the guest actually spent: "errors" and the fuel
                        // total were tracked per mod but never printed, so a
                        // guest that burns its whole budget and reports
                        // errors looked identical to one that traps.
                        lines.Add("  " + id + " (fuel/call " + mod.FuelPerCall +
                                  ", init tick " + mod.InitTick + ", calls " + mod.TotalCalls +
                                  ", fuel used " + mod.TotalFuelConsumed +
                                  ", traps " + mod.TrapCalls +
                                  ", fuel exhausted " + mod.FuelExhaustedCalls +
                                  ", errors " + mod.ErrorCalls + ")");
                    }
                }
                if (_gameApi != null)
                {
                    AddDropped(lines, _gameApi.LogLimiter, "guest log lines");
                    AddDropped(lines, _gameApi.ChatLimiter, "chat messages");
                    AddDropped(lines, _gameApi.CommandLimiter, "sim commands");
                    AddDropped(lines, _gameApi.SenseLimiter, "sense snapshots");
                    AddDropped(lines, _gameApi.WorldTimeErrorLimiter, "world time failures");
                    AddDropped(lines, _gameApi.ChatRejectLimiter, "chat rejection logs");
                    AddDropped(lines, _gameApi.ConfigErrorLimiter, "config read failures");
                }
                if (_servant != null)
                {
                    AddDropped(lines, _servant.CommandLogLimiter, "bot servant log lines");
                    var armed = new List<int>();
                    foreach (var pair in _servant.Glide)
                    {
                        if (pair.Value)
                        {
                            armed.Add(pair.Key);
                        }
                    }
                    if (armed.Count > 0)
                    {
                        // Ascending net id, not the glide table's hash order:
                        // the status line is a run's totals and two runs of
                        // the same workload must print it identically.
                        armed.Sort();
                        lines.Add("  glide armed (net ids): " + string.Join(", ", armed));
                    }
                }
                AddDropped(lines, DispatchFailureLimiter, "tick failure logs");
                AddDropped(lines, DispatchSlowLimiter, "slow dispatch warnings");
                lines.Add("  " + _telemetry.Describe());
                return lines;
            }
        }

        /// <summary>
        /// The guest a failed tick result came from. The result names its
        /// mod; a result that does not falls back to the position in the
        /// load order, which tick results do follow, so the rate-limiter
        /// key, the failure line, and the suppressed line all name the
        /// same guest.
        /// </summary>
        private static string FailureName(ModRunResult result, IReadOnlyList<string> ids, int index)
        {
            if (result.ModId.Length > 0)
            {
                return result.ModId;
            }
            return index < ids.Count ? ids[index] : "?";
        }

        /// <summary>
        /// Names the guest whose last call cost the most, for the slow
        /// dispatch warning. Empty when nothing is loaded or no guest has
        /// been called yet, in which case the warning stands on the
        /// aggregate alone rather than blaming an arbitrary module.
        /// </summary>
        private static string SlowestGuest(IReadOnlyList<string> ids, WasmModHost host)
        {
            string worstId = string.Empty;
            double worstMs = 0.0;
            for (int i = 0; i < ids.Count; i++)
            {
                if (!host.TryGetMod(ids[i], out WasmMod? mod) || mod == null)
                {
                    continue;
                }
                double cost = mod.LastCallMs;
                if (cost > worstMs)
                {
                    worstMs = cost;
                    worstId = mod.Id;
                }
            }
            return worstId.Length == 0
                ? string.Empty
                : "; slowest: " + TextSanitizer.Clean(worstId) + " at " + TickTelemetry.FormatMilliseconds(worstMs);
        }

        /// <summary>
        /// A failed call's message, details, and fuel, ready to log. Trap
        /// messages and backtraces can embed guest-chosen strings (module
        /// and function name sections); they pass through the same
        /// control-character filter as guest log text so a hostile module
        /// cannot forge server log lines. The fuel figure is what tells a
        /// guest that hit its budget apart from one that trapped early,
        /// and it is on every failure path because the store still knows
        /// the count after a trap.
        /// </summary>
        private static string Describe(ModRunResult result)
        {
            string details = result.Details;
            return TextSanitizer.Clean(result.Message) +
                   (details.Length > 0 ? " (" + TextSanitizer.Clean(details) + ")" : "") +
                   ", fuel " + result.FuelConsumed;
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
        /// Drops every piece of per-module bridge state for an id that is no
        /// longer loaded: its settings, its cached raw config, its host-side
        /// rate cap windows, and the bots it owned. Reload and unload both go
        /// through here, so a module can never be left with settings but
        /// without config, can never resume a previous generation's throttle,
        /// and cannot keep a share of the bot budget it no longer owns. The
        /// servant's own log windows ("glide/&lt;net id&gt;", "bot/&lt;verb&gt;",
        /// "sense/worn") are not module-keyed and are left to the limiter's
        /// idle sweep.
        /// </summary>
        private static void ReleaseModuleState(string id)
        {
            _settings?.RemoveMod(id);
            _gameApi?.UnregisterConfig(id);
            _gameApi?.ForgetModule(id);
            _servant?.ReleaseModule(id);
        }

        /// <summary>
        /// Loads every module found under Mods/Wasm/&lt;id&gt;/module.wasm and
        /// runs its on_enable export (docs/ABI.md: called once when the mod
        /// is loaded and enabled), so "wasm load" leaves new modules in the
        /// same state as a server start. Returns which modules were loaded
        /// and, for every module the scan refused, the reason. The reason
        /// matters at the console: a module the operator just copied into
        /// Mods/Wasm is either named as loaded or explained as skipped
        /// there, not only in the log file.
        /// </summary>
        public static ModuleLoadScan LoadAllModules()
        {
            lock (Gate)
            {
                WasmModHost? host = _host;
                if (host == null)
                {
                    return ModuleLoadScan.None;
                }
                // One read of the published snapshot: a scan must resolve
                // every id against the same set of trees even if a restart
                // publishes a new one while it runs.
                IReadOnlyList<string> treeRoots = _moduleTreeRoots;
                if (treeRoots.Count == 0)
                {
                    return ModuleLoadScan.None;
                }
                var loadedIds = new List<string>();
                var skipped = new List<string>();
                foreach (string root in treeRoots)
                {
                    string[] dirs;
                    try
                    {
                        dirs = Directory.GetDirectories(root);
                    }
                    catch (Exception ex)
                    {
                        // An unreadable tree (permissions, a network share, a
                        // modlet being replaced while the server runs, a
                        // folder removed mid-scan) must not abort the scan:
                        // the remaining trees still load, and at server start
                        // the whole bridge would otherwise come up dead. The
                        // IO message may embed the raw path, so clean both
                        // before logging.
                        skipped.Add(TextSanitizer.Clean(root) + ": cannot scan module tree: " +
                                    TextSanitizer.Clean(ex.Message));
                        Log.Warning("[WasmHost] cannot scan module tree " + TextSanitizer.Clean(root) + ": " +
                                    TextSanitizer.Clean(ex.Message) + "; tree skipped");
                        continue;
                    }
                    // Ordinal, so the load order (which fixes the order every
                    // later tick dispatches in) is the same on every run and
                    // on every filesystem. The directory order the OS
                    // enumerates is not a property of the tree.
                    Array.Sort(dirs, StringComparer.Ordinal);
                    foreach (string dir in dirs)
                    {
                        string id = Path.GetFileName(dir);
                        if (!ModId.IsValid(id))
                        {
                            // A folder name with path separators or control
                            // characters (both legal on some filesystems) must
                            // never reach the log source tags or module paths.
                            skipped.Add(TextSanitizer.Clean(id) + ": not a valid module folder name");
                            Log.Warning("[WasmHost] skipping " + TextSanitizer.Clean(id) +
                                        ": not a valid module folder name");
                            continue;
                        }
                        if (host.TryGetMod(id, out _))
                        {
                            continue;
                        }
                        if (!TryLoadFromDisk(host, id, out string reason))
                        {
                            skipped.Add(id + ": " + reason);
                            continue;
                        }
                        loadedIds.Add(id);
                    }
                }
                foreach (string id in loadedIds)
                {
                    InitOne(id);
                }
                return new ModuleLoadScan(loadedIds, skipped);
            }
        }

        /// <summary>
        /// Loads one module from its Mods/Wasm/&lt;id&gt;/module.wasm file and
        /// registers its manifest settings. Shared by start scanning and
        /// "wasm reload". Returns false (logged) when the manifest is
        /// invalid or the module is unreadable or rejected; on_enable is
        /// NOT run here, callers init explicitly. On false,
        /// <paramref name="reason"/> is the one-clause cause, cleaned like
        /// the log line, so the console command can report the same refusal
        /// the log records.
        /// </summary>
        private static bool TryLoadFromDisk(WasmModHost host, string id, out string reason)
        {
            string modulePath = ResolveModuleFile(id, "module.wasm");
            if (modulePath.Length == 0)
            {
                reason = "no module.wasm under Mods/Wasm/" + id;
                return false;
            }
            if (!TryReadManifest(id, out ModManifest? manifest, out reason))
            {
                // Invalid manifest: refuse to run the module with
                // weaker-than-intended limits.
                return false;
            }
            byte[] wasmBytes;
            try
            {
                // The size cap is enforced on the file length, not on the
                // array LoadModule receives: a module file is operator
                // content, and reading one of unknown size only to reject it
                // afterwards would slurp it into the server's heap first.
                long length = new FileInfo(modulePath).Length;
                if (length > host.MaxModuleSizeBytes)
                {
                    reason = "module.wasm is " + length +
                             " bytes, over the cap of " + host.MaxModuleSizeBytes;
                    Log.Warning("[WasmHost] module " + id + " is " + length +
                                " bytes, over the cap of " + host.MaxModuleSizeBytes + "; module skipped");
                    return false;
                }
                wasmBytes = File.ReadAllBytes(modulePath);
            }
            catch (Exception ex)
            {
                // An unreadable module file must not abort the scan or the
                // bridge start; skip it like any other bad module. The path
                // and the IO message may both carry text the host does not
                // control, so both are cleaned before they reach the log.
                reason = "cannot read module.wasm: " + TextSanitizer.Clean(ex.Message);
                Log.Warning("[WasmHost] cannot read " + TextSanitizer.Clean(modulePath) + ": " + TextSanitizer.Clean(ex.Message) + "; module skipped");
                return false;
            }
            try
            {
                host.LoadModule(id, wasmBytes, manifest);
            }
            catch (WasmModLoadException ex)
            {
                // Load rejections quote manifest and module diagnostics that
                // come from mod files (third-party content); clean them so
                // control characters cannot forge log lines.
                reason = TextSanitizer.Clean(ex.Message);
                Log.Warning("[WasmHost] failed to load module " + id + ": " + TextSanitizer.Clean(ex.Message));
                return false;
            }
            _settings?.UpdateMod(id, manifest);
            _gameApi?.RegisterConfig(id, ReadRawConfig(id));
            LogIgnoredKeys("manifest for " + id, manifest);
            LogFuelOverride(id, manifest, host.FuelPerCall);
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Names the top-level keys the manifest parser did not read, once
        /// per load. They are tolerated (a manifest written for a newer host
        /// still loads), but a limit key written above [limits], or a
        /// section header the host does not know, is a cap the operator
        /// believes is in force while the engine runs on the default. A
        /// limit key gets the placement named, because that is the mistake
        /// the message is there to catch.
        /// </summary>
        private static void LogIgnoredKeys(string what, ModManifest? manifest)
        {
            if (manifest == null)
            {
                return;
            }
            foreach (string key in manifest.IgnoredKeys)
            {
                string hint = ModManifest.IsLimitKey(key) ? "; limits belong in [limits]" : string.Empty;
                Log.Out("[WasmHost] " + what + ": ignoring unknown key '" + TextSanitizer.Clean(key) + "'" + hint);
            }
        }

        /// <summary>
        /// Logs a manifest that raises its fuel budget above the host default.
        /// A per-mod fuel_per_call overrides the shared value by design, so
        /// the operator sees which modules asked for more than wasm.toml
        /// grants instead of finding out from a slow dispatch.
        /// </summary>
        private static void LogFuelOverride(string id, ModManifest? manifest, ulong hostFuel)
        {
            if (manifest != null && manifest.FuelPerCall.HasValue && manifest.FuelPerCall.Value > hostFuel)
            {
                Log.Out("[WasmHost] " + id + " raises fuel/call to " + manifest.FuelPerCall.Value +
                        " over the host default " + hostFuel);
            }
        }

        /// <summary>
        /// Reads a module's raw config.toml (served to the guest verbatim via
        /// the zdtd config import; the host never parses it). A module that
        /// ships no config registers as empty so the guest keeps its
        /// defaults; a file that exists but cannot be read registers empty
        /// too, and says why: the guest sees the same 0 either way, so the
        /// reason has to reach the log here or not at all.
        /// </summary>
        private static string ReadRawConfig(string id)
        {
            string path = ResolveModuleFile(id, "config.toml");
            if (path.Length == 0)
            {
                return string.Empty;
            }
            if (ManifestFiles.TryRead(path, out string content, out string failureReason))
            {
                return content;
            }
            _gameApi?.ReportConfigReadFailure(id, failureReason);
            return string.Empty;
        }

        /// <summary>
        /// Runs one module's on_enable, fail soft: a trapped enable is
        /// logged and the module stays loaded (its next tick is budgeted
        /// like any other).
        /// </summary>
        private static void InitOne(string id)
        {
            // Through the host, not mod.Init(): the guest reads its settings,
            // its config.toml, and its log attribution from the mod the host
            // is currently calling, and only the host knows which that is.
            ModRunResult? maybeResult = _host?.InitModule(id);
            if (maybeResult is not { } result)
            {
                return;
            }
            if (!result.Ok)
            {
                Log.Warning("[WasmHost] on_enable of " + id + ": " + Describe(result));
            }
        }

        /// <summary>
        /// Unloads and reloads one module from disk, dropping its per-module
        /// state and running the new instance's on_enable. On false,
        /// <paramref name="reason"/> says why in one clause: the host is not
        /// started, the id is not usable, or the load from disk was refused. The
        /// command prints it, so an operator whose reload did not take effect is
        /// told why at the console instead of only in the log. A failing shutdown of
        /// the outgoing instance is logged but does not fail the reload.
        /// </summary>
        public static bool Reload(string id, out string reason)
        {
            lock (Gate)
            {
                WasmModHost? host = _host;
                if (host == null)
                {
                    reason = "the host is not started";
                    return false;
                }
                if (!ModId.IsValid(id))
                {
                    reason = "not a valid module id";
                    return false;
                }
                ModRunResult? oldShutdown = host.Unload(id);
                // The reload proceeds either way, but the failed goodbye of
                // the old instance must reach the log like an unload's would.
                if (oldShutdown is { Ok: false } shutdown)
                {
                    Log.Warning("[WasmHost] reload of " + id + ": shutdown of previous instance failed: " + Describe(shutdown));
                }
                // The outgoing instance's state leaves with it, so the reloaded
                // module starts from an empty share of the bot budget and the
                // cap budget, and cannot inherit the old one's bodies.
                ReleaseModuleState(id);
                if (!TryLoadFromDisk(host, id, out reason))
                {
                    return false;
                }
                // Init only the module that was reloaded; dispatching init to
                // the host would re-run on_enable for every other guest.
                InitOne(id);
                return true;
            }
        }

        /// <summary>
        /// Unloads one module, running its shutdown export and dropping its
        /// per-module state. On false, <paramref name="detail"/> says why, and the
        /// caller can name the ids that are loaded instead. On true it is empty
        /// unless the shutdown export itself failed: the module is gone either
        /// way, and that outcome belongs at the console that unloaded it, not only
        /// in the log. That is the opposite convention from <see cref="Reload"/>,
        /// where a false return means the load did not happen.
        /// </summary>
        public static bool Unload(string id, out string detail)
        {
            lock (Gate)
            {
                WasmModHost? host = _host;
                if (host == null)
                {
                    detail = "the host is not started";
                    return false;
                }
                ModRunResult? maybeShutdown = host.Unload(id);
                if (maybeShutdown is not { } shutdown)
                {
                    detail = "no module with that id is loaded";
                    return false;
                }
                ReleaseModuleState(id);
                if (!shutdown.Ok)
                {
                    // Fail soft: the mod is gone either way, but a trapped or
                    // failing shutdown must reach the operator instead of a
                    // bare "unloaded" from the console command.
                    detail = Describe(shutdown);
                    Log.Warning("[WasmHost] unload of " + id + ": " + detail);
                }
                else
                {
                    detail = string.Empty;
                }
                return true;
            }
        }

        /// <summary>
        /// Ids of the loaded modules, in dispatch order. The console command
        /// names them after a mistyped or unloaded id, so an operator who
        /// guessed wrong is shown the ids that do exist instead of having to
        /// remember "wasm list".
        /// </summary>
        public static List<string> LoadedModuleIds()
        {
            lock (Gate)
            {
                WasmModHost? host = _host;
                return host == null ? new List<string>() : new List<string>(host.ModIds);
            }
        }

        /// <summary>
        /// Applies the shared Mods/Wasm/wasm.toml [limits] over the host code
        /// defaults before the engine is created (start time only; per-mod
        /// manifests can still tighten further at load). Load order follows
        /// the zdtd convention: code defaults -> wasm.toml -> wasm-mod.toml.
        /// Returns false when the file exists but cannot be used, so the
        /// caller can refuse to start rather than run the engine under limits
        /// the operator did not write.
        /// </summary>
        private static bool TryApplySharedLimits(WasmHostConfig config, string sharedPath)
        {
            try
            {
                if (!File.Exists(sharedPath))
                {
                    return true;
                }
                ModManifest shared = ModManifest.ParseToml(ManifestFiles.ReadRequired(sharedPath), "shared");
                LogIgnoredKeys("shared wasm.toml", shared);
                if (shared.FuelPerCall.HasValue)
                {
                    config.FuelPerCall = shared.FuelPerCall.Value;
                }
                if (shared.MaxMemoryBytes.HasValue)
                {
                    config.StaticMemoryMaximumBytes = shared.MaxMemoryBytes.Value;
                }
                return true;
            }
            catch (WasmModLoadException ex)
            {
                Log.Warning("[WasmHost] invalid shared wasm.toml limits: " + TextSanitizer.Clean(ex.Message));
            }
            catch (Exception ex)
            {
                // Same verdict as a malformed file: the engine would run
                // under the code defaults, not the configured ones. The
                // parser message quotes raw file text, so clean it like guest
                // log output.
                Log.Warning("[WasmHost] cannot read shared wasm.toml: " + TextSanitizer.Clean(ex.Message));
            }
            return false;
        }

        /// <summary>
        /// The limits the engine is actually running under, for "wasm
        /// status". Values come from the live host, so a shared file that
        /// moved them and a per-mod manifest that tightened them are both
        /// visible from one place.
        /// </summary>
        private static string EffectiveLimits()
        {
            WasmModHost? host = _host;
            if (host == null)
            {
                return "none (host not started)";
            }
            return "fuel/call " + host.FuelPerCall +
                   ", memory " + host.StaticMemoryMaximumBytes + " bytes" +
                   ", module cap " + host.MaxModuleSizeBytes + " bytes" +
                   ", guest stdio inherited " + (host.InheritGuestStandardStreams ? "yes" : "no");
        }

        /// <summary>
        /// Reads wasm-mod.toml for a module id. Returns false when a manifest
        /// is present but invalid (logged); true with a null manifest when
        /// the module ships none, so host defaults apply. On false,
        /// <paramref name="reason"/> says why in one clause, for the console
        /// side of the same refusal.
        /// </summary>
        private static bool TryReadManifest(string id, out ModManifest? manifest, out string reason)
        {
            string dir = ResolveModuleDir(id);
            if (dir.Length == 0)
            {
                // No module directory resolved, so there is no manifest to
                // read. Combining onto the empty path would instead resolve
                // "wasm-mod.toml" against the server process's working
                // directory, which is the install root, and parse whatever
                // sits there as this module's limits.
                manifest = null;
                reason = "no module folder for " + id;
                return false;
            }
            string tomlPath = Path.Combine(dir, "wasm-mod.toml");
            try
            {
                if (File.Exists(tomlPath))
                {
                    manifest = ModManifest.ParseToml(ManifestFiles.ReadRequired(tomlPath), id);
                    reason = string.Empty;
                    return true;
                }
                manifest = null;
                reason = string.Empty;
                return true;
            }
            catch (WasmModLoadException ex)
            {
                // Parser diagnostics quote raw manifest text (third-party
                // mod content); clean them like guest log output.
                Log.Warning("[WasmHost] invalid manifest for " + id + ": " + TextSanitizer.Clean(ex.Message) + "; module skipped");
                manifest = null;
                reason = "invalid wasm-mod.toml: " + TextSanitizer.Clean(ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                // An unreadable or oversized manifest file is treated like a
                // malformed one: skip the module instead of running it with
                // defaults.
                Log.Warning("[WasmHost] cannot read manifest for " + id + ": " + TextSanitizer.Clean(ex.Message) + "; module skipped");
                manifest = null;
                reason = "cannot read wasm-mod.toml: " + TextSanitizer.Clean(ex.Message);
                return false;
            }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                if (_host != null)
                {
                    // The last heartbeat is an hour old on a long-running
                    // server, so the run's totals are logged here: without
                    // them a shutdown leaves no summary of what the guests
                    // cost or how often they failed.
                    Log.Out("[WasmHost] shutting down after " + _tick + " tick(s); " + _telemetry.Describe());
                    _host.Dispose();
                    // A guest that traps or runs out of fuel on its way out
                    // must still reach the log; Dispose keeps those results
                    // for exactly this.
                    foreach (ModRunResult failure in _host.ShutdownFailures)
                    {
                        Log.Warning("[WasmHost] shutdown of " + TextSanitizer.Clean(failure.ModId) + ": " +
                                    TextSanitizer.Clean(failure.Message) +
                                    (failure.Details.Length > 0 ? " (" + TextSanitizer.Clean(failure.Details) + ")" : ""));
                    }
                    _host = null;
                }
                // Release what Start() built so a shutdown leaves no static
                // references behind; the next Start() recreates all of them.
                _gameApi = null;
                _servant = null;
                _settings = null;
                _moduleTreeRoots = Array.Empty<string>();
                Started = false;
            }
        }

        /// <summary>
        /// First tree holding the module's directory, or empty when none
        /// does. Mods/Wasm wins over modlet-carried trees.
        /// </summary>
        internal static string ResolveModuleDir(string id)
        {
            return ModuleRoots.ResolveDir(_moduleTreeRoots, id);
        }

        /// <summary>
        /// First tree holding the module's named file, or empty when none
        /// does. A directory without the file does not claim the module.
        /// Exposed for GameHostApi's config fallback, which must resolve
        /// the same trees as the loader (not just the primary root).
        /// </summary>
        internal static string ResolveModuleFile(string id, string fileName)
        {
            return ModuleRoots.ResolveFile(_moduleTreeRoots, id, fileName);
        }
    }
}
