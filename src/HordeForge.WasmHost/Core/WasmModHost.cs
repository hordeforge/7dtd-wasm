using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using HordeForge.WasmHost.Abi;
using HordeForge.WasmHost.Config;
using HordeForge.WasmHost.Registry;
using Wasmtime;

namespace HordeForge.WasmHost.Core
{
    /// <summary>
    /// Embeddable WebAssembly mod host for the 7 Days to Die dedicated server.
    ///
    /// Owns the Wasmtime engine and the linker with the "hordeforge" host
    /// API plus WASI preview1; each loaded module gets its own store. Modules
    /// are loaded per id, validated against the configured limits, and driven
    /// through the documented export surface (on_enable, on_tick,
    /// on_player_join, on_shutdown). on_admin_command is resolved and
    /// signature-checked at load but never dispatched (docs/ABI.md). Every
    /// entry point serializes on one
    /// internal gate, so an embedder that drives the host from more than one
    /// thread (a game main loop plus a console thread, say) gets the same
    /// state the single-caller case sees.
    ///
    /// Sandbox guarantees: fuel budget per call, hard memory maximum enforced
    /// at load time from the module's declared memory maximum, module size
    /// cap, and no access to anything outside the ABI. Guests never see game
    /// objects or .NET types.
    /// </summary>
    public sealed class WasmModHost : IDisposable
    {
        /// <summary>Size of one wasm page, the smallest memory a module can declare (64 KiB).</summary>
        public const long WasmPageBytes = 65536;

        /// <summary>
        /// wasm32 memory ceiling: 65536 pages of 64 KiB (4 GiB). The largest
        /// memory any wasm32 module can declare, so it bounds the host's
        /// static memory maximum as well: a ceiling above it describes an
        /// address space no guest can reach.
        /// </summary>
        internal const ulong Wasm32MemoryCeilingBytes = 65536UL * 65536;

        /// <summary>
        /// Largest wasm caller stack the engine accepts, in bytes. Wasmtime
        /// requires max_wasm_stack to fit inside its own async stack, and a
        /// larger value aborts the process from a panic inside the native
        /// engine rather than raising anything the host could turn into a
        /// configuration error. The bound is enforced here so an embedder
        /// gets the constructor's named rejection instead.
        /// </summary>
        private const int EngineMaxStackBytes = 2 * 1024 * 1024;

        private readonly WasmHostConfig _config;
        private readonly IGameHostApi _api;
        private readonly MonotonicTimer _timer;
        private readonly Engine _engine;
        private readonly Linker _linker;
        // Serializes every entry point that touches the registry, the load
        // order, the per-call mod id, or an engine handle. Without it a
        // second thread walks _modOrder while a load or unload rewrites it
        // (a skipped or duplicated mod, a list index past the end), reads
        // another mod's id out of _currentModId (one guest served the
        // other's settings), and enters a wasm store that is already
        // executing a call, which traps the engine itself. Monitor
        // reentrancy keeps an IGameHostApi callback that calls back into the
        // host working; it does not make a nested Dispatch safe, because the
        // nested dispatch would run guests inside the outer one's walk.
        private readonly object _gate = new object();
        private readonly Dictionary<string, WasmMod> _mods = new Dictionary<string, WasmMod>(StringComparer.Ordinal);
        // Dispatch happens in load order (documented); Dictionary enumeration
        // order is an implementation detail, so the ids are tracked here.
        private readonly List<string> _modOrder = new List<string>();
        // Shutdown outcomes that were not Ok, retained after Dispose so the
        // embedder can report a failed goodbye instead of losing it.
        private readonly List<ModRunResult> _shutdownFailures = new List<ModRunResult>();
        private string _currentJoinName = string.Empty;

        /// <summary>
        /// Mod id of the guest currently being called; lets the get_setting
        /// import resolve per-mod settings. Set before every guest call.
        /// </summary>
        private string _currentModId = string.Empty;

        // Last config served through the zdtd config import, as its UTF-8
        // bytes. The host api already hands back the same cached string
        // instance for an unchanged config, so keying the memo on that
        // instance turns a repeated import into a reference compare instead
        // of a full re-encode and allocation of the whole file. One entry is
        // enough: only one guest runs at a time, and a miss simply re-encodes.
        private string? _configBytesSource;
        private byte[]? _configBytes;

        /// <summary>
        /// Source tag for the guest currently being called, kept in step
        /// with <see cref="_currentModId"/> by <see cref="SetCurrentMod"/>
        /// and <see cref="ClearCurrentMod"/>. Built once per guest call
        /// instead of once per log line: the log import is the one host
        /// import a guest can loop on at fuel rate, and each call would
        /// otherwise concat and allocate a tag the rate limiter then
        /// throws away.
        /// </summary>
        private string _currentLogSource = string.Empty;

        private bool _disposed;

        /// <summary>
        /// Creates a host with its own Wasmtime engine and linker; each
        /// loaded mod gets its own store. The api implementation is called
        /// on the calling thread for every guest host-API call, one call at
        /// a time across the whole host; see <see cref="Abi.IGameHostApi"/>.
        /// </summary>
        public WasmModHost(IGameHostApi api, WasmHostConfig config)
            : this(api, config, null)
        {
        }

        /// <summary>
        /// Creates a host whose guest calls are measured against
        /// <paramref name="timer"/>; null selects the process clock. Every
        /// cost the run reports (<see cref="WasmMod.LastCallMs"/>, which
        /// names the guest that spent a frame) comes from that one source, so
        /// an embedder that steps virtual time gets a run whose cost figures
        /// replay with the rest of it.
        /// </summary>
        public WasmModHost(IGameHostApi api, WasmHostConfig config, MonotonicTimer? timer)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _timer = timer ?? MonotonicTimer.Default;
            ValidateConfig(config);

            var engineConfig = new Wasmtime.Config()
                .WithFuelConsumption(true)
                .WithStaticMemoryMaximumSize(config.StaticMemoryMaximumBytes)
                .WithMaximumStackSize(config.MaximumStackBytes);
            _engine = new Engine(engineConfig);
            _linker = new Linker(_engine);
            _linker.DefineWasi();
            DefineHostApi();
            // No guest is current before the first call, so the log tag
            // starts at the bare prefix, matching ClearCurrentMod.
            ClearCurrentMod();
        }

        /// <summary>
        /// Fails fast on a configuration the host can never honor, at
        /// construction time rather than as per-call fuel exhaustion or
        /// blanket module rejection later.
        /// </summary>
        private static void ValidateConfig(WasmHostConfig config)
        {
            if (config.FuelPerCall == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(config), config.FuelPerCall,
                    "FuelPerCall must be at least 1 instruction; 0 would exhaust every call immediately.");
            }
            // The fuel budget is the host's only bound on how long a guest
            // call can hold the game main loop, so the ceiling the manifest
            // parser enforces on the file path is enforced on the embedder
            // path too: without it a host built from a hand-written config
            // can grant a call a budget of hours.
            if (config.FuelPerCall > (ulong)ModManifest.MaxFuelPerCall)
            {
                throw new ArgumentOutOfRangeException(nameof(config), config.FuelPerCall,
                    "FuelPerCall must be at most " + ModManifest.MaxFuelPerCall +
                    " instructions; a larger budget lets one guest call stall the loop it runs on.");
            }
            if (config.StaticMemoryMaximumBytes < (ulong)WasmPageBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(config), config.StaticMemoryMaximumBytes,
                    "StaticMemoryMaximumBytes must be at least one wasm page (" + (ulong)WasmPageBytes + " bytes); " +
                    "smaller ceilings reject every module.");
            }
            if (config.StaticMemoryMaximumBytes > Wasm32MemoryCeilingBytes)
            {
                // Without this bound a limits file naming a byte count past
                // the wasm32 address space (a copy-pasted size, a value in
                // the wrong unit) reaches the engine, which cannot honor it:
                // the engine build throws and takes the whole host start
                // down instead of the documented "invalid file, keep
                // defaults" path. A wasm32 linear memory is 65536 pages at
                // most, so a module declaring no maximum is already treated
                // as the full 4 GiB and nothing above that can load: the
                // value bounds nothing.
                throw new ArgumentOutOfRangeException(nameof(config), config.StaticMemoryMaximumBytes,
                    "StaticMemoryMaximumBytes must be at most the wasm32 memory ceiling (" +
                    Wasm32MemoryCeilingBytes + " bytes); no module can declare more, so a larger cap bounds nothing.");
            }
            if (config.MaxModuleSizeBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(config), config.MaxModuleSizeBytes,
                    "MaxModuleSizeBytes must be positive; zero or negative rejects every module.");
            }
            if (config.MaximumStackBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(config), config.MaximumStackBytes,
                    "MaximumStackBytes must be positive.");
            }
            if (config.MaximumStackBytes > EngineMaxStackBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(config), config.MaximumStackBytes,
                    "MaximumStackBytes must be at most " + EngineMaxStackBytes +
                    " bytes; the engine aborts the process on a larger stack rather than failing the call.");
            }
            if (string.IsNullOrEmpty(config.LogSourcePrefix))
            {
                throw new ArgumentException("LogSourcePrefix must be a non-empty source tag for guest log lines.", nameof(config));
            }
        }

        /// <summary>
        /// Ids of the currently loaded mods, in load order. A fresh read-only
        /// copy: an enumerator handed out here must stay valid even when
        /// another thread loads or unloads a module.
        /// </summary>
        public IReadOnlyList<string> ModIds
        {
            get
            {
                lock (_gate)
                {
                    return new ReadOnlyCollection<string>(new List<string>(_modOrder));
                }
            }
        }

        /// <summary>
        /// Largest module <see cref="LoadModule"/> accepts, in bytes. Exposed
        /// so a host that reads the module file itself can refuse an oversize
        /// file on its length instead of letting LoadModule reject it after
        /// the whole file has already been read into memory.
        /// </summary>
        public int MaxModuleSizeBytes => _config.MaxModuleSizeBytes;

        /// <summary>
        /// Fuel budget granted to a guest call that has no manifest of its
        /// own, read back from the configuration the engine was built with.
        /// </summary>
        public ulong FuelPerCall => _config.FuelPerCall;

        /// <summary>
        /// Effective engine-wide memory ceiling in bytes, after the shared
        /// limits file has been applied. Read back so an embedder can show
        /// the limits actually in force rather than the code defaults.
        /// </summary>
        public ulong StaticMemoryMaximumBytes => _config.StaticMemoryMaximumBytes;

        /// <summary>Whether guest WASI stdout/stderr reach the host console.</summary>
        public bool InheritGuestStandardStreams => _config.InheritGuestStandardStreams;

        /// <summary>Game tick of the most recent DispatchTick call.</summary>
        public long Tick
        {
            get
            {
                lock (_gate)
                {
                    return _tick;
                }
            }
        }

        private long _tick;

        /// <summary>
        /// Compiles, validates, and instantiates a guest module under the
        /// given id. Throws <see cref="WasmModLoadException"/> when the module
        /// is rejected; the host is unaffected. When a manifest is supplied,
        /// its limits are applied: fuel overrides the host default, and the
        /// memory ceiling can only tighten the host cap.
        /// </summary>
        public WasmMod LoadModule(string id, byte[] wasmBytes, ModManifest? manifest = null)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (wasmBytes == null)
                {
                    throw new ArgumentNullException(nameof(wasmBytes));
                }
                if (!ModId.IsValid(id))
                {
                    throw new WasmModLoadException(id ?? string.Empty, "mod id must be a plain folder name without path separators or control characters");
                }
                if (wasmBytes.Length > _config.MaxModuleSizeBytes)
                {
                    throw new WasmModLoadException(id, "module size " + wasmBytes.Length + " bytes exceeds cap " + _config.MaxModuleSizeBytes);
                }
                if (_mods.ContainsKey(id))
                {
                    throw new WasmModLoadException(id, "a mod with this id is already loaded");
                }

                Module module;
                try
                {
                    module = Module.FromBytes(_engine, id, wasmBytes);
                }
                catch (Exception ex)
                {
                    throw new WasmModLoadException(id, "failed to parse or compile module: " + ex.Message, ex);
                }

                // The compiled module holds native machine code behind its own
                // handle. Every rejection from here on must release it, or each
                // repeated failed load attempt (operator retrying "wasm reload")
                // accumulates engine memory until finalization.
                try
                {
                    return LoadValidated(id, module, manifest);
                }
                catch
                {
                    module.Dispose();
                    throw;
                }
            }
        }

        private WasmMod LoadValidated(string id, Module module, ModManifest? manifest)
        {
            ulong? declaredMax = DeclaredMemoryMaximumBytes(module);
            // A module with no declared maximum is treated as declaring the
            // wasm32 ceiling (4 GiB). Such modules load only when the
            // operator raised the effective cap accordingly (wasm.toml
            // limits.max_memory_bytes); the weaker bound is documented
            // (ADR 0004 amendment). This is how third-party plugins built
            // without --max-memory (for example the sibling zdtd fps_bot)
            // run unmodified.
            ulong effectiveMax = declaredMax ?? Wasm32MemoryCeilingBytes;
            ulong memoryCeiling = _config.StaticMemoryMaximumBytes;
            if (manifest != null && manifest.MaxMemoryBytes.HasValue && manifest.MaxMemoryBytes.Value < memoryCeiling)
            {
                memoryCeiling = manifest.MaxMemoryBytes.Value;
            }
            if (effectiveMax > memoryCeiling)
            {
                string detail = declaredMax.HasValue
                    ? "guest memory maximum " + effectiveMax + " bytes exceeds the effective cap " + memoryCeiling
                    : "guest memory has no declared maximum (treated as " + effectiveMax + " bytes); the effective cap is " + memoryCeiling +
                      "; raise wasm.toml limits.max_memory_bytes to run it";
                throw new WasmModLoadException(id, detail);
            }

            RequireExportSignature(module, id, AbiConstants.ExportInit, Array.Empty<ValueKind>(), new[] { ValueKind.Int32 }, allowVoidResult: true);
            RequireExportSignature(module, id, AbiConstants.ExportTick, Array.Empty<ValueKind>(), new[] { ValueKind.Int32 }, allowVoidResult: true);
            if (HasExport(module, AbiConstants.ExportPlayerJoin))
            {
                RequireExportSignature(module, id, AbiConstants.ExportPlayerJoin, new[] { ValueKind.Int32 }, new[] { ValueKind.Int32 });
            }
            // Optional exports must be validated too: resolution later is a
            // typed lookup that returns null on any mismatch, so a
            // wrong-signature handler would otherwise be dropped silently and
            // the guest would run without its shutdown or admin hook.
            if (HasExport(module, AbiConstants.ExportShutdown))
            {
                RequireExportSignature(module, id, AbiConstants.ExportShutdown, Array.Empty<ValueKind>(), new[] { ValueKind.Int32 }, allowVoidResult: true);
            }
            if (HasExport(module, AbiConstants.ExportAdminCommand))
            {
                RequireExportSignature(module, id, AbiConstants.ExportAdminCommand,
                    new[] { ValueKind.Int32, ValueKind.Int32, ValueKind.Int32, ValueKind.Int32 }, new[] { ValueKind.Int32 });
            }

            ulong fuelPerCall = manifest != null && manifest.FuelPerCall.HasValue ? manifest.FuelPerCall.Value : _config.FuelPerCall;

            // One store per mod: the binding has no per-instance release, so
            // owning the store is what lets Unload reclaim the instance and
            // its linear memory instead of retaining every reloaded
            // generation until the host is disposed.
            var store = new Store(_engine);
            try
            {
                if (_config.InheritGuestStandardStreams)
                {
                    store.SetWasiConfiguration(new WasiConfiguration()
                        .WithInheritedStandardOutput()
                        .WithInheritedStandardError());
                }
                Instance instance;
                try
                {
                    instance = _linker.Instantiate(store, module);
                }
                catch (Exception ex)
                {
                    throw new WasmModLoadException(id, "instantiation failed: " + ex.Message, ex);
                }

                var mod = new WasmMod(id, module, store, fuelPerCall, instance, _tick, _timer);
                _mods.Add(id, mod);
                _modOrder.Add(id);
                return mod;
            }
            catch
            {
                // Nothing may outlive a rejected load: the caller's catch
                // disposes the module; this disposes the freshly created
                // store so no failed attempt leaves state in the engine.
                store.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Runs on_enable on one loaded mod and returns its result, or null
        /// when the id is not loaded. Single-module init (the bridge start
        /// scan, "wasm reload") must go through here and never through
        /// <see cref="WasmMod.Init"/> directly: get_setting, config, and the
        /// log source tag all resolve against the mod currently being
        /// called, so a direct call would serve the previously dispatched
        /// mod's settings and config to this one.
        ///
        /// Under <see cref="_gate"/>, like every other entry point: it writes
        /// the per-call mod id and enters a store, so an enable racing a
        /// dispatch or an unload would otherwise hand this mod the setting
        /// the other guest is reading, or call into a store the unload is
        /// disposing. The mod's own enable latch makes a second call to an
        /// already-enabled mod a no-op rather than a second on_enable.
        /// </summary>
        public ModRunResult? InitModule(string id)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_mods.TryGetValue(id, out WasmMod? mod))
                {
                    return null;
                }
                SetCurrentMod(mod.Id);
                try
                {
                    return mod.Init();
                }
                finally
                {
                    // The call is over; no mod is current until the next one
                    // starts, so a later direct guest call cannot inherit this id.
                    ClearCurrentMod();
                }
            }
        }

        /// <summary>
        /// Looks up a loaded mod by id, for its counters, handler probes and
        /// id. The returned instance is the host's, not a copy: drive it
        /// through the methods on this class, not through
        /// <see cref="WasmMod"/> directly (see <see cref="WasmMod"/> for why).
        /// </summary>
        public bool TryGetMod(string id, out WasmMod? mod)
        {
            lock (_gate)
            {
                return _mods.TryGetValue(id, out mod);
            }
        }

        /// <summary>
        /// Removes a mod, invoking its shutdown export first (fail soft: a
        /// trapped shutdown still removes the mod), then releases its store
        /// and compiled module. Returns null when the id was not loaded;
        /// otherwise the shutdown call result (Ok when the mod exports no
        /// shutdown handler) so callers can surface a failed goodbye instead
        /// of reporting a clean unload.
        /// </summary>
        public ModRunResult? Unload(string id)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_mods.TryGetValue(id, out var mod))
                {
                    SetCurrentMod(mod.Id);
                    try
                    {
                        ModRunResult shutdown = mod.Shutdown();
                        _mods.Remove(id);
                        _modOrder.Remove(id);
                        return ReleaseStore(mod) ?? shutdown;
                    }
                    finally
                    {
                        // Same rule as InitModule: no mod is current once the call
                        // is over, so a later direct guest call cannot resolve
                        // settings or a log tag against the mod just unloaded.
                        ClearCurrentMod();
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Drives one game tick into every loaded mod and returns the per-mod
        /// results in load order. A misbehaving mod never stops the loop:
        /// its failure is reported in its result. The returned list belongs
        /// to the caller and rejects writes, so a dispatch on another thread
        /// can never rewrite results this call is still reading.
        /// </summary>
        public IReadOnlyList<ModRunResult> DispatchTick(long tick)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _tick = tick;
                return Dispatch(static mod => mod.Tick());
            }
        }

        /// <summary>Invokes init on every loaded mod, in load order.</summary>
        public IReadOnlyList<ModRunResult> DispatchInit()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return Dispatch(static mod => mod.Init());
            }
        }

        /// <summary>
        /// Notifies every loaded mod that a player spawned into the world.
        /// Only mods that export the optional on_player_join handler are
        /// called; the player name is available to them through the
        /// get_join_player_name host import. Fail soft like tick: one
        /// misbehaving handler never stops the others. The entity id is
        /// i32 on the wire (the on_player_join parameter), so it is taken
        /// as int and never narrowed silently. The returned list belongs to
        /// the caller and rejects writes, like the tick results.
        /// </summary>
        public IReadOnlyList<ModRunResult> DispatchPlayerJoin(int entityId, string playerName)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                _currentJoinName = playerName ?? string.Empty;
                try
                {
                    return Dispatch(mod => mod.OnPlayerJoin(entityId));
                }
                finally
                {
                    // The join event is over: get_join_player_name must report
                    // "no event" (-1) again, per docs/ABI.md, instead of serving
                    // the stale name from this join to later calls.
                    _currentJoinName = string.Empty;
                }
            }
        }

        /// <summary>
        /// Walks the loaded mods in load order, calls
        /// <paramref name="invoke"/> on each, and collects the results the
        /// mod reports (a null result means the mod does not handle the
        /// event). The tick and init delegates are static, so tick-rate
        /// dispatch allocates no delegate. Runs under <see cref="_gate"/>,
        /// which is what keeps a concurrent load or unload from rewriting
        /// the order this walk indexes. The result list is per call: the
        /// caller owns it, so a dispatch running on another thread cannot
        /// refill a list this one is still handing out. It is wrapped so it
        /// never escapes as a mutable list.
        /// </summary>
        private IReadOnlyList<ModRunResult> Dispatch(Func<WasmMod, ModRunResult?> invoke)
        {
            List<string> order = _modOrder;
            Dictionary<string, WasmMod> mods = _mods;
            // Pre-sized to the mod count: at tick rate the list otherwise
            // grows 4, 8, 16 ... reallocating and copying the whole backing
            // store a few times before it settles, once per tick per
            // dispatch. The capacity is an upper bound (a mod may report no
            // result), so this never under-allocates.
            var results = new List<ModRunResult>(order.Count);
            try
            {
                for (int i = 0; i < order.Count; i++)
                {
                    if (!mods.TryGetValue(order[i], out WasmMod? mod))
                    {
                        continue;
                    }
                    SetCurrentMod(mod.Id);
                    // Pattern-matched, not a ModRunResult? local narrowed by
                    // HasValue: the compiler drops the not-null state of a
                    // nullable value-type local at a loop back-edge. The
                    // pattern unwraps, so result is the struct itself.
                    if (invoke(mod) is ModRunResult result)
                    {
                        results.Add(result);
                    }
                }
            }
            finally
            {
                // The last mod walked stays current otherwise, and its id
                // would then answer get_setting and the log source tag for
                // any later single-module call that forgot to set one.
                ClearCurrentMod();
            }
            return new ReadOnlyCollection<ModRunResult>(results);
        }

        private ulong? DeclaredMemoryMaximumBytes(Module module)
        {
            foreach (var export in module.Exports)
            {
                if (export is MemoryExport memory)
                {
                    return MemoryBytes(memory.Maximum);
                }
            }
            foreach (var import in module.Imports)
            {
                if (import is MemoryImport memory)
                {
                    return MemoryBytes(memory.Maximum);
                }
            }
            return null;
        }

        private static ulong? MemoryBytes(long? pages)
        {
            if (pages == null)
            {
                return null;
            }
            return unchecked((ulong)pages.Value) * (ulong)WasmPageBytes;
        }

        private static void RequireExportSignature(Module module, string id, string name, ValueKind[] parameters, ValueKind[] results, bool allowVoidResult = false)
        {
            foreach (var export in module.Exports)
            {
                if (export is FunctionExport function && function.Name == name)
                {
                    bool resultOk = Matches(function.Results, results) ||
                                    (allowVoidResult && function.Results.Count == 0);
                    if (!Matches(function.Parameters, parameters) || !resultOk)
                    {
                        throw new WasmModLoadException(id, "export " + name + " has an unexpected signature; see docs/ABI.md");
                    }
                    return;
                }
            }
            throw new WasmModLoadException(id, "missing required export " + name);
        }

        private static bool HasExport(Module module, string name)
        {
            foreach (var export in module.Exports)
            {
                if (export.Name == name)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Matches(IReadOnlyList<ValueKind> actual, ValueKind[] expected)
        {
            if (actual.Count != expected.Length)
            {
                return false;
            }
            for (int i = 0; i < actual.Count; i++)
            {
                if (actual[i] != expected[i])
                {
                    return false;
                }
            }
            return true;
        }

        private void DefineHostApi()
        {
            DefineLogAndTick(AbiConstants.HostModule);

            _linker.DefineFunction<long>(AbiConstants.HostModule, "get_world_time", caller => _api.GetWorldTime());

            _linker.DefineFunction<int, int, int, int, int>(AbiConstants.HostModule, "get_setting", (caller, keyPtr, keyLen, outPtr, outCap) =>
            {
                string key = ReadGuestString(caller, keyPtr, keyLen);
                if (!_api.TryGetSetting(_currentModId, key, out string value))
                {
                    return AbiConstants.SettingNotFound;
                }
                return WriteGuestString(caller, outPtr, outCap, value, AbiConstants.SettingBufferTooSmall);
            });

            _linker.DefineFunction<int, int, int>(AbiConstants.HostModule, "send_chat", (caller, ptr, len) =>
            {
                string message = ReadGuestString(caller, ptr, len);
                return _api.SendChat(message) ? AbiConstants.ChatOk : AbiConstants.ChatRejected;
            });

            _linker.DefineFunction<int, int, int>(AbiConstants.HostModule, AbiConstants.ImportGetJoinPlayerName, (caller, outPtr, outCap) =>
            {
                // The name of the player that most recently spawned, made
                // available to guests during DispatchPlayerJoin.
                if (_currentJoinName.Length == 0)
                {
                    return AbiConstants.SettingNotFound;
                }
                return WriteGuestString(caller, outPtr, outCap, _currentJoinName, AbiConstants.SettingBufferTooSmall);
            });

            DefineZdtdCompatibilityApi();
        }

        /// <summary>
        /// Defines the zdtd-server import module so sibling plugins (the
        /// unmodified fps_bot and its kin) load as-is. The functions map onto
        /// the game host API: log and tick behave like the hordeforge ones,
        /// queue forwards SimCommands to the bot servant, sense fills the
        /// binary world snapshot, and query forwards a text request to the
        /// host API, which answers none today (stage 3, docs/ABI.md).
        /// </summary>
        private void DefineZdtdCompatibilityApi()
        {
            DefineLogAndTick(AbiConstants.ZdtdHostModule);

            _linker.DefineFunction<int, int, int>(AbiConstants.ZdtdHostModule, AbiConstants.ImportConfig, (caller, outPtr, outCap) =>
            {
                // zdtd contract: copy the calling module's config.toml
                // verbatim, min(out_cap, len) bytes; 0 = no config (module
                // has none, or the buffer is too small - the guest checks
                // the returned length). The host never parses it; each guest
                // owns its format. Mirrors zdtd's config import exactly so
                // the parachute mod's on_enable reads it unchanged. The cut
                // is made at a UTF-8 character boundary (Utf8Prefix), so a
                // guest that sized its buffer too small for the last
                // character of a value gets a short buffer, never a
                // truncated multi-byte sequence it would decode as U+FFFD.
                if (outCap <= 0)
                {
                    return 0;
                }
                if (!_api.TryGetRawConfig(_currentModId, out string content) || content.Length == 0)
                {
                    return 0;
                }
                byte[] bytes = EncodedConfig(content);
                int copy = Utf8Prefix.Length(bytes, outCap);
                if (copy == 0)
                {
                    // The buffer cannot even hold the first character, so
                    // there is no partial copy worth reporting: the guest
                    // reads 0 and grows its buffer, as with any other
                    // too-small config buffer.
                    return 0;
                }
                Memory? memory = caller.GetMemory("memory");
                if (memory == null)
                {
                    // 0 is also what "no config" returns, so without this the
                    // module runs on host defaults for the life of the server
                    // with an empty log. The adjacent catch reports the same
                    // class of host-side failure the same way.
                    _api.Log(_currentLogSource, AbiConstants.LogError, "config failed: guest has no exported memory named 'memory'");
                    return 0;
                }
                try
                {
                    bytes.AsSpan(0, copy).CopyTo(memory.GetSpan(outPtr, copy));
                }
                catch (Exception ex)
                {
                    _api.Log(_currentLogSource, AbiConstants.LogError, "config failed: " + ex);
                    return 0;
                }
                return copy;
            });

            _linker.DefineFunction<int, int, int>(AbiConstants.ZdtdHostModule, AbiConstants.ImportQueue, (caller, ptr, len) =>
            {
                string command = ReadGuestString(caller, ptr, len);
                return _api.TryQueueCommand(_currentModId, command) ? AbiConstants.QueueAccepted : AbiConstants.QueueRejected;
            });

            _linker.DefineFunction<int, int, int, int>(AbiConstants.ZdtdHostModule, AbiConstants.ImportSense, (caller, outPtr, outCap, token) =>
            {
                if (outCap <= 0)
                {
                    return 0;
                }
                Memory? memory = caller.GetMemory("memory");
                if (memory == null)
                {
                    // Same reason the catch below reports: 0 reads as "empty
                    // world" to the guest, so a miscompiled module would sense
                    // nothing forever with nothing to explain it.
                    _api.Log(_currentLogSource, AbiConstants.LogError, "sense failed: guest has no exported memory named 'memory'");
                    return 0;
                }
                try
                {
                    return _api.WriteSenseSnapshot(_currentModId, memory.GetSpan(outPtr, outCap));
                }
                catch (Exception ex)
                {
                    // The wire contract is "0 = no data", but a host-side
                    // failure must not leave the brain silently blind: report
                    // through the capped log path so it can be diagnosed.
                    _api.Log(_currentLogSource, AbiConstants.LogError, "sense failed: " + ex);
                    return 0;
                }
            });

            _linker.DefineFunction<int, int, int, int, int>(AbiConstants.ZdtdHostModule, AbiConstants.ImportQuery, (caller, reqPtr, reqLen, outPtr, outCap) =>
            {
                string request = ReadGuestString(caller, reqPtr, reqLen);
                string? answer = _api.TryQuery(request);
                if (answer == null)
                {
                    return AbiConstants.QueryNoAnswer;
                }
                return WriteGuestString(caller, outPtr, outCap, answer, AbiConstants.QueryBufferTooSmall);
            });
        }

        /// <summary>
        /// The log and tick imports, which the hordeforge and zdtd host
        /// modules define identically.
        /// </summary>
        private void DefineLogAndTick(string hostModule)
        {
            _linker.DefineFunction<int, int, int>(hostModule, "log", (Caller caller, int level, int ptr, int len) =>
            {
                string message = ReadGuestString(caller, ptr, len);
                _api.Log(_currentLogSource, level, message);
            });

            _linker.DefineFunction<long>(hostModule, AbiConstants.ImportTick, caller =>
            {
                return Tick;
            });
        }

        /// <summary>
        /// The UTF-8 encoding of <paramref name="content"/>, reusing the
        /// previous encoding when it was made from the very same string
        /// instance. The host api caches the config text per mod and hands
        /// back that same instance until the file is re-read, so a guest
        /// looping the config import stops re-encoding (and re-allocating)
        /// the whole file on every call. A different instance re-encodes,
        /// which covers a reloaded config and two mods alternating: the
        /// memo is a cache of a pure function, never a source of truth.
        /// </summary>
        private byte[] EncodedConfig(string content)
        {
            if (ReferenceEquals(_configBytesSource, content) && _configBytes != null)
            {
                return _configBytes;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            _configBytesSource = content;
            _configBytes = bytes;
            return bytes;
        }

        /// <summary>
        /// The tag a guest called under <paramref name="modId"/> logs under:
        /// <paramref name="prefix"/> alone for an empty id, otherwise the
        /// prefix, a slash, and the id. Public and static because the host
        /// and its embedders key per-module state on this exact string: the
        /// bridge's log rate limiter is keyed on it, and its ForgetModule
        /// drops that window on unload. An embedder that recomposed the tag
        /// itself would silently fail to drop the window of a module reloaded
        /// inside the second its previous generation saturated the cap, so
        /// the one place this host names a module is the one place to ask.
        /// Built once per guest call rather than per log line.
        /// </summary>
        public static string LogSourceFor(string prefix, string modId)
        {
            return modId == null || modId.Length == 0
                ? prefix
                : prefix + "/" + modId;
        }

        /// <summary>
        /// Marks <paramref name="modId"/> as the guest being called, and
        /// builds its log tag with it. Every site that begins a guest call
        /// goes through here, so the id and the tag can never disagree and
        /// get_setting and the log line name different modules.
        /// </summary>
        private void SetCurrentMod(string modId)
        {
            _currentModId = modId;
            _currentLogSource = LogSourceFor(_config.LogSourcePrefix, modId);
        }

        /// <summary>
        /// Forgets the current guest, so a later direct call cannot inherit
        /// its id or its log tag. Every site that ends a guest call runs
        /// this; the bare prefix is what an uncategorised line logs under.
        /// </summary>
        private void ClearCurrentMod()
        {
            _currentModId = string.Empty;
            _currentLogSource = _config.LogSourcePrefix;
        }

        /// <summary>
        /// Writes a UTF-8 string into guest linear memory. Returns the byte
        /// count written, or <paramref name="tooSmallStatus"/> when the
        /// guest buffer cannot hold it.
        ///
        /// A guest that exports no 'memory' throws, exactly as
        /// <see cref="ReadGuestString"/> does for the same condition. Reporting
        /// it as a too-small buffer instead was the wrong answer twice over:
        /// the guest, which cannot know its own module is malformed, obeys
        /// the ABI and grows its buffer on every call, so the module never
        /// works and nothing in the log says why; and the outcome then
        /// depends on the answer length, so the same defect looks like a
        /// too-small buffer or a success at random.
        /// </summary>
        private static int WriteGuestString(Caller caller, int outPtr, int outCap, string value, int tooSmallStatus)
        {
            // Checked before the size test so the diagnosis does not depend
            // on how much the guest happened to ask for.
            Memory? memory = caller.GetMemory("memory");
            if (memory == null)
            {
                throw new InvalidOperationException("guest has no exported memory named 'memory'");
            }
            // Measure with a length pass only: encoding to a byte[] just to
            // count would allocate and encode twice (here and in WriteString).
            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > outCap)
            {
                return tooSmallStatus;
            }
            memory.WriteString(outPtr, value, Encoding.UTF8);
            return byteCount;
        }

        private static string ReadGuestString(Caller caller, int ptr, int len)
        {
            if (len <= 0)
            {
                return string.Empty;
            }
            Memory? memory = caller.GetMemory("memory");
            if (memory == null)
            {
                throw new InvalidOperationException("guest has no exported memory named 'memory'");
            }
            return memory.ReadString(ptr, len, Encoding.UTF8) ?? string.Empty;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WasmModHost));
            }
        }

        /// <summary>
        /// Results of the shutdown calls made by <see cref="Dispose"/>, in
        /// load order, for every mod whose shutdown did not complete. A guest
        /// that traps on its way out would otherwise leave no trace at all,
        /// so the embedder can log these after disposing. Empty until
        /// Dispose has run, and unchanged by later calls.
        ///
        /// A fresh read-only copy, like <see cref="ModIds"/> and the
        /// dispatch results: Dispose fills the list while holding
        /// <see cref="_gate"/>, and an embedder that read the live list
        /// from another thread would be enumerating a List that is being
        /// appended to. The list behind the copy belongs to the host.
        /// </summary>
        public IReadOnlyList<ModRunResult> ShutdownFailures
        {
            get
            {
                lock (_gate)
                {
                    return new ReadOnlyCollection<ModRunResult>(new List<ModRunResult>(_shutdownFailures));
                }
            }
        }

        /// <summary>
        /// Releases a removed mod's store and compiled module, reporting a
        /// failure instead of throwing.
        ///
        /// Both handles are native engine resources. Wasmtime throws from
        /// Dispose when a store is still in use, and an exception here would
        /// escape Unload after the mod was already dropped from the registry:
        /// BridgeHost would never reach its own per-mod state release, leaving
        /// the module's settings, rate-limit budget, and bots registered
        /// against an id that is no longer loaded. The same applies inside
        /// Dispose, where one throw would strand every mod still in the loop.
        /// The mod is out of the registry either way, so the caller's cleanup
        /// chain must run; the caller gets the failure as a result instead.
        /// Returns null when the release succeeded, so a caller that already
        /// holds a failed shutdown result does not record it twice.
        /// </summary>
        private static ModRunResult? ReleaseStore(WasmMod mod)
        {
            try
            {
                mod.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                return new ModRunResult(
                    mod.Id,
                    ModRunStatus.Error,
                    "engine resources could not be released: " + ex.Message,
                    string.Empty,
                    0UL);
            }
        }

        /// <summary>
        /// Shuts down every loaded mod (best effort), releases each mod's
        /// store and compiled module, and releases the engine and linker.
        /// Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                for (int i = 0; i < _modOrder.Count; i++)
                {
                    if (!_mods.TryGetValue(_modOrder[i], out WasmMod? mod))
                    {
                        continue;
                    }
                    SetCurrentMod(mod.Id);
                    try
                    {
                        ModRunResult shutdown = mod.Shutdown();
                        if (!shutdown.Ok)
                        {
                            // Kept rather than dropped: the embedder reads them
                            // after Dispose to report a failed goodbye.
                            _shutdownFailures.Add(shutdown);
                        }
                        // ReleaseStore reports rather than throws, so a mod
                        // whose store or module would not release cannot
                        // strand every mod after it in this loop: those would
                        // never run their shutdown export, never free their
                        // engine memory, and would still be reported to the
                        // embedder as cleanly stopped. Null means the release
                        // succeeded, so a shutdown result already recorded
                        // above is not recorded twice.
                        ModRunResult? releaseFailure = ReleaseStore(mod);
                        if (releaseFailure.HasValue)
                        {
                            _shutdownFailures.Add(releaseFailure.Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        // The same blast radius seen from the shutdown call
                        // itself: one mod must not end the loop and leak the
                        // engine for the life of the process.
                        _shutdownFailures.Add(new ModRunResult(
                            mod.Id,
                            ModRunStatus.Error,
                            "dispose failed: " + ex.Message,
                            string.Empty,
                            0UL));
                    }
                    finally
                    {
                        // Same rule as Unload and Dispatch: no mod is current
                        // once the call is over, so nothing after the loop can
                        // resolve settings or a log tag against the last one.
                        ClearCurrentMod();
                    }
                }
                _mods.Clear();
                _modOrder.Clear();
                // The linker holds the engine's resolution tables and the
                // engine the compiled code of every module it compiled, so
                // neither release may be skipped because the one before it
                // threw. _disposed is set either way: the loop above has
                // already run, so a second Dispose would find nothing left
                // to release and would only rethrow the same failure.
                try
                {
                    _linker.Dispose();
                }
                finally
                {
                    try
                    {
                        _engine.Dispose();
                    }
                    finally
                    {
                        _disposed = true;
                    }
                }
            }
        }
    }
}
