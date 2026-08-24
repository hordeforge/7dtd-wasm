using System;
using System.Collections.Generic;
using System.IO;
using HordeForge.WasmHost.Core;
using HordeForge.WasmHost.Registry;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Settings resolution for the get_setting host import, following the
    /// zdtd-server config conventions (docs/CONFIG.md):
    ///
    ///   1. the calling mod's own [settings] from its wasm-mod.toml
    ///   2. shared [settings] from Mods/Wasm/wasm.toml (re-read on change)
    ///   3. not found
    ///
    /// Per-mod settings are registered by BridgeHost as modules load, unload,
    /// and reload; the shared file is re-read when its mtime changes.
    /// </summary>
    public sealed class WasmSettingsProvider
    {
        private readonly string _sharedPath;
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _perMod =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _shared = new Dictionary<string, string>(StringComparer.Ordinal);
        private DateTime _sharedMtime = DateTime.MinValue;
        // mtime of the last reload attempt that failed and was logged, so a
        // broken wasm.toml is reported once per change instead of on every
        // get_setting miss.
        private DateTime _loggedFailureMtime = DateTime.MinValue;
        private bool _loggedFailureValid;

        public WasmSettingsProvider(string sharedPath)
        {
            _sharedPath = sharedPath;
        }

        /// <summary>Registers (or replaces) a module's settings from its manifest.</summary>
        public void UpdateMod(string modId, ModManifest? manifest)
        {
            _perMod[modId] = manifest?.Settings ?? EmptySettings;
        }

        /// <summary>Drops a module's settings on unload.</summary>
        public void RemoveMod(string modId)
        {
            _perMod.Remove(modId);
        }

        public bool TryGetSetting(string modId, string key, out string value)
        {
            if (modId.Length > 0 && _perMod.TryGetValue(modId, out var modSettings) && modSettings.TryGetValue(key, out value))
            {
                return true;
            }
            ReloadSharedIfChanged();
            return _shared.TryGetValue(key, out value);
        }

        private static readonly IReadOnlyDictionary<string, string> EmptySettings =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private void ReloadSharedIfChanged()
        {
            DateTime attemptedMtime = DateTime.MinValue;
            bool attempted = false;
            try
            {
                if (!File.Exists(_sharedPath))
                {
                    _shared.Clear();
                    _sharedMtime = DateTime.MinValue;
                    _loggedFailureValid = false;
                    return;
                }
                attemptedMtime = File.GetLastWriteTimeUtc(_sharedPath);
                attempted = true;
                if (attemptedMtime == _sharedMtime)
                {
                    return;
                }
                ModManifest shared = ModManifest.ParseToml(File.ReadAllText(_sharedPath), "shared");
                _shared.Clear();
                foreach (var pair in shared.Settings)
                {
                    _shared[pair.Key] = pair.Value;
                }
                _sharedMtime = attemptedMtime;
                _loggedFailureValid = false;
            }
            catch (Exception ex)
            {
                // Keep the previous shared settings on any read error, but
                // say so once per file change: silently serving stale values
                // would hide operator mistakes from the log entirely. The
                // mtime stays unapplied, so a later fixed save re-reads.
                if (!attempted || !_loggedFailureValid || attemptedMtime != _loggedFailureMtime)
                {
                    global::Log.Warning("[WasmHost] cannot reload " + _sharedPath + ": " + ex.Message +
                                        "; serving previous shared settings");
                    _loggedFailureMtime = attemptedMtime;
                    _loggedFailureValid = true;
                }
            }
        }
    }
}
