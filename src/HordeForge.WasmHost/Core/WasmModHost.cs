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
    /// Owns the Wasmtime engine, a single store, and the linker with the
    /// "hordeforge" host API plus WASI preview1. Modules are loaded per id,
    /// validated against the configured limits, and driven through the
    /// documented export surface (on_enable, on_tick, on_player_join,
    /// on_shutdown). The host is single-threaded by design: call it from
    /// the game main loop only.
    ///
    /// Sandbox guarantees: fuel budget per call, hard memory maximum enforced
    /// at load time from the module's declared memory maximum, module size
    /// cap, and no access to anything outside the ABI. Guests never see game
    /// objects or .NET types.
    /// </summary>
    public sealed class WasmModHost : IDisposable
    {
        private const long WasmPageBytes = 65536;

        /// <summary>wasm32 memory ceiling: 65536 pages of 64 KiB.</summary>
        private const ulong Wasm32MemoryCeiling = 65536UL * 65536;

        private readonly WasmHostConfig _config;
        private readonly IGameHostApi _api;
        private readonly Engine _engine;
        private readonly Store _store;
        private readonly Linker _linker;
        private readonly Dictionary<string, WasmMod> _mods = new Dictionary<string, WasmMod>(StringComparer.Ordinal);
        // Dispatch happens in load order (documented); Dictionary enumeration
        // order is an implementation detail, so the ids are tracked here.
        private readonly List<string> _modOrder = new List<string>();
        // Live read-only view over _modOrder, built once: ModIds is read by
        // the bridge on every game tick, and a per-call array copy would
        // allocate at tick rate for no benefit. Callers get the same
        // mutation protection as a copy (the wrapper rejects writes) while
        // always seeing current load order.
        private readonly ReadOnlyCollection<string> _modIdsView;
        private string _currentJoinName = string.Empty;

        /// <summary>
        /// Mod id of the guest currently being called; lets the get_setting
        /// import resolve per-mod settings. Set before every guest call.
        /// </summary>
        private string _currentModId = string.Empty;
        private bool _disposed;

        /// <summary>
        /// Creates a host with its own Wasmtime engine, store, and linker.
        /// The api implementation is called on the caller thread for every
        /// guest host-API call; see <see cref="Abi.IGameHostApi"/>.
        /// </summary>
        public WasmModHost(IGameHostApi api, WasmHostConfig config)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            var engineConfig = new Wasmtime.Config()
                .WithFuelConsumption(true)
                .WithStaticMemoryMaximumSize(config.StaticMemoryMaximumBytes)
                .WithMaximumStackSize(config.MaximumStackBytes);
            _engine = new Engine(engineConfig);
            _store = new Store(_engine);
            if (config.InheritGuestStandardStreams)
            {
                _store.SetWasiConfiguration(new WasiConfiguration()
                    .WithInheritedStandardOutput()
                    .WithInheritedStandardError());
            }
            _linker = new Linker(_engine);
            _linker.DefineWasi();
            DefineHostApi();
            _modIdsView = new ReadOnlyCollection<string>(_modOrder);
        }

        /// <summary>Ids of the currently loaded mods, in load order.</summary>
        public IReadOnlyList<string> ModIds => _modIdsView;

        /// <summary>Game tick of the most recent DispatchTick call.</summary>
        public long Tick { get; private set; }

        /// <summary>
        /// Compiles, validates, and instantiates a guest module under the
        /// given id. Throws <see cref="WasmModLoadException"/> when the module
        /// is rejected; the host is unaffected. When a manifest is supplied,
        /// its limits are applied: fuel overrides the host default, and the
        /// memory ceiling can only tighten the host cap.
        /// </summary>
        public WasmMod LoadModule(string id, byte[] wasmBytes, ModManifest? manifest = null)
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
            ulong effectiveMax = declaredMax ?? Wasm32MemoryCeiling;
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

            ulong fuelPerCall = manifest != null && manifest.FuelPerCall.HasValue ? manifest.FuelPerCall.Value : _config.FuelPerCall;

            Instance instance;
            try
            {
                instance = _linker.Instantiate(_store, module);
            }
            catch (Exception ex)
            {
                throw new WasmModLoadException(id, "instantiation failed: " + ex.Message, ex);
            }

            var mod = new WasmMod(id, _store, fuelPerCall, instance, Tick);
            _mods.Add(id, mod);
            _modOrder.Add(id);
            return mod;
        }

        /// <summary>Looks up a loaded mod by id.</summary>
        public bool TryGetMod(string id, out WasmMod? mod)
        {
            return _mods.TryGetValue(id, out mod);
        }

        /// <summary>
        /// Removes a mod, invoking its shutdown export first (fail soft: a
        /// trapped shutdown still removes the mod). Returns null when the id
        /// was not loaded; otherwise the shutdown call result (Ok when the
        /// mod exports no shutdown handler) so callers can surface a failed
        /// goodbye instead of reporting a clean unload.
        /// </summary>
        public ModRunResult? Unload(string id)
        {
            ThrowIfDisposed();
            if (_mods.TryGetValue(id, out var mod))
            {
                _currentModId = mod.Id;
                ModRunResult shutdown = mod.Shutdown();
                _mods.Remove(id);
                _modOrder.Remove(id);
                return shutdown;
            }
            return null;
        }

        /// <summary>
        /// Drives one game tick into every loaded mod and returns the per-mod
        /// results in load order. A misbehaving mod never stops the loop:
        /// its failure is reported in its result.
        /// </summary>
        public IReadOnlyList<ModRunResult> DispatchTick(long tick)
        {
            ThrowIfDisposed();
            Tick = tick;
            var results = new List<ModRunResult>(_mods.Count);
            foreach (var mod in ModsInLoadOrder())
            {
                _currentModId = mod.Id;
                results.Add(mod.Tick(tick));
            }
            return results;
        }

        /// <summary>Invokes init on every loaded mod, in load order.</summary>
        public IReadOnlyList<ModRunResult> DispatchInit()
        {
            ThrowIfDisposed();
            var results = new List<ModRunResult>(_mods.Count);
            foreach (var mod in ModsInLoadOrder())
            {
                _currentModId = mod.Id;
                results.Add(mod.Init(Tick));
            }
            return results;
        }

        /// <summary>
        /// Notifies every loaded mod that a player spawned into the world.
        /// Only mods that export the optional on_player_join handler are
        /// called; the player name is available to them through the
        /// get_join_player_name host import. Fail soft like tick: one
        /// misbehaving handler never stops the others. The entity id is
        /// i32 on the wire (the on_player_join parameter), so it is taken
        /// as int and never narrowed silently.
        /// </summary>
        public IReadOnlyList<ModRunResult> DispatchPlayerJoin(int entityId, string playerName)
        {
            ThrowIfDisposed();
            _currentJoinName = playerName ?? string.Empty;
            try
            {
                var results = new List<ModRunResult>(_mods.Count);
                foreach (var mod in ModsInLoadOrder())
                {
                    _currentModId = mod.Id;
                    ModRunResult? result = mod.OnPlayerJoin(entityId);
                    if (result != null)
                    {
                        results.Add(result);
                    }
                }
                return results;
            }
            finally
            {
                // The join event is over: get_join_player_name must report
                // "no event" (-1) again, per docs/ABI.md, instead of serving
                // the stale name from this join to later calls.
                _currentJoinName = string.Empty;
            }
        }

        private IEnumerable<WasmMod> ModsInLoadOrder()
        {
            foreach (string id in _modOrder)
            {
                if (_mods.TryGetValue(id, out WasmMod? mod))
                {
                    yield return mod;
                }
            }
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
            _linker.DefineFunction<int, int, int>(AbiConstants.HostModule, "log", (Caller caller, int level, int ptr, int len) =>
            {
                string message = ReadGuestString(caller, ptr, len);
                _api.Log(LogSource(), level, message);
            });

            _linker.DefineFunction<long>(AbiConstants.HostModule, AbiConstants.ImportTick, caller =>
            {
                return Tick;
            });

            _linker.DefineFunction<long>(AbiConstants.HostModule, "get_world_time", caller =>
            {
                return _api.GetWorldTime();
            });

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
        /// binary world snapshot, and query answers text requests. See
        /// docs/ABI.md.
        /// </summary>
        private void DefineZdtdCompatibilityApi()
        {
            _linker.DefineFunction<int, int, int>(AbiConstants.ZdtdHostModule, "log", (caller, level, ptr, len) =>
            {
                string message = ReadGuestString(caller, ptr, len);
                _api.Log(LogSource(), level, message);
            });

            _linker.DefineFunction<long>(AbiConstants.ZdtdHostModule, AbiConstants.ImportTick, caller =>
            {
                return Tick;
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
                    _api.Log(LogSource(), AbiConstants.LogError, "sense failed: " + ex.Message);
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
        /// Source tag for guest log lines: the configured prefix plus the
        /// calling mod's id, so log attribution and the bridge's per-module
        /// rate cap (ADR 0006) key on the module, not on the shared prefix.
        /// </summary>
        private string LogSource()
        {
            return _currentModId.Length == 0
                ? _config.LogSourcePrefix
                : _config.LogSourcePrefix + "/" + _currentModId;
        }

        /// <summary>
        /// Writes a UTF-8 string into guest linear memory. Returns the byte
        /// count written, or <paramref name="tooSmallStatus"/> when the
        /// guest buffer cannot hold it or the guest exports no 'memory'.
        /// </summary>
        private static int WriteGuestString(Caller caller, int outPtr, int outCap, string value, int tooSmallStatus)
        {
            // Measure with a length pass only: encoding to a byte[] just to
            // count would allocate and encode twice (here and in WriteString).
            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > outCap)
            {
                return tooSmallStatus;
            }
            Memory? memory = caller.GetMemory("memory");
            if (memory == null)
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
        /// Shuts down every loaded mod (best effort) and releases the engine,
        /// store, and linker. Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            foreach (var mod in ModsInLoadOrder())
            {
                _currentModId = mod.Id;
                mod.Shutdown();
            }
            _mods.Clear();
            _modOrder.Clear();
            _linker.Dispose();
            _store.Dispose();
            _engine.Dispose();
            _disposed = true;
        }
    }
}
