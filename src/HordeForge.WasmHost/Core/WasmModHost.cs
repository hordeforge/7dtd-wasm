using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HordeForge.WasmHost.Abi;
using HordeForge.WasmHost.Config;
using HordeForge.WasmHost.Limits;
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
    /// documented export surface (init, tick, shutdown). The host is
    /// single-threaded by design: call it from the game main loop only.
    ///
    /// Sandbox guarantees: fuel budget per call, hard memory maximum enforced
    /// at load time from the module's declared memory maximum, module size
    /// cap, and no access to anything outside the ABI. Guests never see game
    /// objects or .NET types.
    /// </summary>
    public sealed class WasmModHost : IDisposable
    {
        private const long WasmPageBytes = 65536;

        private readonly WasmHostConfig _config;
        private readonly IGameHostApi _api;
        private readonly Engine _engine;
        private readonly Store _store;
        private readonly Linker _linker;
        private readonly Dictionary<string, WasmMod> _mods = new Dictionary<string, WasmMod>(StringComparer.Ordinal);
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
        }

        /// <summary>Ids of the currently loaded mods, in load order.</summary>
        public IReadOnlyCollection<string> ModIds
        {
            get
            {
                var ids = new string[_mods.Count];
                _mods.Keys.CopyTo(ids, 0);
                return ids;
            }
        }

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
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("mod id must not be empty", nameof(id));
            }
            if (wasmBytes == null)
            {
                throw new ArgumentNullException(nameof(wasmBytes));
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

            ulong? maxBytes = DeclaredMemoryMaximumBytes(module);
            if (maxBytes == null)
            {
                throw new WasmModLoadException(id, "guest memory has no declared maximum; compile with a --max-memory link flag (see docs/ABI.md)");
            }
            ulong memoryCeiling = _config.StaticMemoryMaximumBytes;
            if (manifest != null && manifest.MaxMemoryBytes.HasValue && manifest.MaxMemoryBytes.Value < memoryCeiling)
            {
                memoryCeiling = manifest.MaxMemoryBytes.Value;
            }
            if (maxBytes.Value > memoryCeiling)
            {
                throw new WasmModLoadException(id, "guest memory maximum " + maxBytes.Value + " bytes exceeds the effective cap " + memoryCeiling);
            }

            RequireExportSignature(module, id, AbiConstants.ExportInit, Array.Empty<ValueKind>(), new[] { ValueKind.Int32 });
            RequireExportSignature(module, id, AbiConstants.ExportTick, Array.Empty<ValueKind>(), new[] { ValueKind.Int32 });
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
            if (instance == null)
            {
                throw new WasmModLoadException(id, "instantiation returned no instance");
            }

            var mod = new WasmMod(id, _store, fuelPerCall, instance, Tick);
            _mods.Add(id, mod);
            return mod;
        }

        /// <summary>Looks up a loaded mod by id.</summary>
        public bool TryGetMod(string id, out WasmMod? mod)
        {
            return _mods.TryGetValue(id, out mod);
        }

        /// <summary>
        /// Removes a mod, invoking its shutdown export first (fail soft: a
        /// trapped shutdown still removes the mod). Returns false when the id
        /// was not loaded.
        /// </summary>
        public bool Unload(string id)
        {
            ThrowIfDisposed();
            if (_mods.TryGetValue(id, out var mod))
            {
                _currentModId = mod.Id;
                mod.Shutdown();
                _mods.Remove(id);
                return true;
            }
            return false;
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
            foreach (var mod in _mods.Values)
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
            foreach (var mod in _mods.Values)
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
        /// misbehaving handler never stops the others.
        /// </summary>
        public IReadOnlyList<ModRunResult> DispatchPlayerJoin(long entityId, string playerName)
        {
            ThrowIfDisposed();
            _currentJoinName = playerName ?? string.Empty;
            var results = new List<ModRunResult>();
            foreach (var mod in _mods.Values)
            {
                _currentModId = mod.Id;
                ModRunResult? result = mod.OnPlayerJoin((int)entityId);
                if (result != null)
                {
                    results.Add(result);
                }
            }
            return results;
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

        private static void RequireExportSignature(Module module, string id, string name, ValueKind[] parameters, ValueKind[] results)
        {
            foreach (var export in module.Exports)
            {
                if (export is FunctionExport function && function.Name == name)
                {
                    if (!Matches(function.Parameters, parameters) || !Matches(function.Results, results))
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
                _api.Log(_config.LogSourcePrefix, level, message);
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
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                if (bytes.Length > outCap)
                {
                    return AbiConstants.SettingBufferTooSmall;
                }
                Memory? memory = caller.GetMemory("memory");
                if (memory == null)
                {
                    return AbiConstants.SettingBufferTooSmall;
                }
                memory.WriteString(outPtr, value, Encoding.UTF8);
                return bytes.Length;
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
                byte[] bytes = Encoding.UTF8.GetBytes(_currentJoinName);
                if (bytes.Length > outCap)
                {
                    return AbiConstants.SettingBufferTooSmall;
                }
                Memory? memory = caller.GetMemory("memory");
                if (memory == null)
                {
                    return AbiConstants.SettingBufferTooSmall;
                }
                memory.WriteString(outPtr, _currentJoinName, Encoding.UTF8);
                return bytes.Length;
            });
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
            foreach (var mod in _mods.Values)
            {
                _currentModId = mod.Id;
                mod.Shutdown();
            }
            _mods.Clear();
            _linker.Dispose();
            _store.Dispose();
            _engine.Dispose();
            _disposed = true;
        }
    }
}
