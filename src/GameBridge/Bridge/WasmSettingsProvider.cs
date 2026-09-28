using System;
using System.Collections.Generic;
using System.IO;
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
    /// Precedence (per-mod over shared) lives in SettingsTable, which this
    /// class feeds; this class owns only file watching and probe throttling.
    ///
    /// Thread safety: one provider is safe to share. Settings are registered
    /// from the module load path and read from a guest's get_setting import,
    /// and the probe fields below (mtime, failure latch, probe clock) are
    /// plain fields, so a lookup racing a register would corrupt the tables
    /// it walks. The gate is taken by every method here, and the disk read
    /// in <see cref="ReloadSharedIfChanged"/> runs under it: the probe is
    /// throttled to one stat per ProbeIntervalMs, so the stall is bounded
    /// and the alternative, checking the clock outside the gate, would let
    /// two threads both read the file.
    /// </summary>
    public sealed class WasmSettingsProvider
    {
        private readonly string _sharedPath;
        // Serializes the table and the probe state below; see the type
        // comment. Monitor, so the internal chains reenter.
        private readonly object _gate = new object();
        // Clock behind the probe throttle; see ProbeIntervalMs.
        private readonly Func<int> _clockMs;
        private readonly SettingsTable _table = new SettingsTable();
        // Identity of the shared file the table currently holds: its last
        // write time and its length. Both, because mtime alone is not a
        // version: a file replaced by a restore or a copy that carries the
        // original timestamp keeps the old mtime forever, and the change
        // would never reach a guest. A length that moved alongside an
        // unchanged mtime is a rewrite the mtime did not report.
        private DateTime _sharedMtime = DateTime.MinValue;
        private long _sharedLength = -1;
        // Identity of the file whose last reload failed. The previous
        // settings keep serving, but the file is not read and parsed again
        // until it changes: without this a broken wasm.toml is re-parsed
        // on every probe, forever, from the get_setting path.
        private DateTime _failedMtime = DateTime.MinValue;
        private long _failedLength = -1;
        private bool _failedValid;

        // Minimum interval between shared-file probes. A guest can loop on
        // get_setting misses within its fuel budget; without the throttle
        // every miss costs a stat syscall pair in the game main loop.
        private const int ProbeIntervalMs = 500;
        private int _lastProbeMs = int.MinValue;

        /// <summary>
        /// Creates the provider. <paramref name="clockMs"/> is the
        /// millisecond clock the shared-file probe throttle measures against
        /// (see <see cref="ProbeIntervalMs"/>); it defaults to the process
        /// clock, and a driver that steps its own time passes its own so
        /// the throttle is a function of that time rather than of wall time.
        /// </summary>
        public WasmSettingsProvider(string sharedPath, Func<int>? clockMs = null)
        {
            _sharedPath = sharedPath;
            _clockMs = clockMs ?? (() => Environment.TickCount);
        }

        /// <summary>Registers (or replaces) a module's settings from its manifest.</summary>
        public void UpdateMod(string modId, ModManifest? manifest)
        {
            IReadOnlyDictionary<string, string>? settings = manifest != null ? manifest.Settings : null;
            lock (_gate)
            {
                _table.UpdateMod(modId, settings);
            }
        }

        /// <summary>Drops a module's settings on unload.</summary>
        public void RemoveMod(string modId)
        {
            lock (_gate)
            {
                _table.RemoveMod(modId);
            }
        }

        /// <summary>Reads one setting for a module, per-mod first, then the shared file.</summary>
        /// <param name="modId">The module asking; its own settings win.</param>
        /// <param name="key">Setting name.</param>
        /// <param name="value">The value found, or empty when neither source has the key.</param>
        /// <returns>True when the key resolved to a value.</returns>
        public bool TryGetSetting(string modId, string key, out string value)
        {
            lock (_gate)
            {
                // Per-mod settings are current by registration; only the shared
                // file may have changed on disk, so reload before the lookup.
                ReloadSharedIfChanged();
                return _table.TryGetSetting(modId, key, out value);
            }
        }

        private void ReloadSharedIfChanged()
        {
            // Throttle disk probes (see ProbeIntervalMs); unchecked int
            // subtraction stays correct across TickCount wraparound.
            int nowMs = _clockMs();
            if (_lastProbeMs != int.MinValue && nowMs - _lastProbeMs < ProbeIntervalMs)
            {
                return;
            }
            _lastProbeMs = nowMs;
            DateTime attemptedMtime = DateTime.MinValue;
            long attemptedLength = -1;
            bool attempted = false;
            try
            {
                if (!File.Exists(_sharedPath))
                {
                    _table.ClearShared();
                    _sharedMtime = DateTime.MinValue;
                    _sharedLength = -1;
                    _failedValid = false;
                    return;
                }
                var info = new FileInfo(_sharedPath);
                attemptedMtime = info.LastWriteTimeUtc;
                attemptedLength = info.Length;
                attempted = true;
                if (attemptedMtime == _sharedMtime && attemptedLength == _sharedLength)
                {
                    return;
                }
                // Already read this exact file and it did not parse. Serving
                // the previous settings again is the documented behavior; a
                // fixed save moves the identity and is retried.
                if (_failedValid && attemptedMtime == _failedMtime && attemptedLength == _failedLength)
                {
                    return;
                }
                string text = ManifestFiles.ReadRequired(_sharedPath);
                ModManifest shared = ModManifest.ParseToml(text, "shared");
                _table.UpdateShared(shared.Settings);
                _sharedMtime = attemptedMtime;
                _sharedLength = attemptedLength;
                _failedValid = false;
            }
            catch (Exception ex)
            {
                // Keep the previous shared settings on any read error, but
                // say so once per file change: silently serving stale values
                // would hide operator mistakes from the log entirely. The
                // failed identity is remembered so an unchanged broken file
                // is not re-read and re-parsed on every probe, while a
                // later fixed save still re-reads.
                if (!attempted || !_failedValid || attemptedMtime != _failedMtime || attemptedLength != _failedLength)
                {
                    // Parser diagnostics quote raw file text; clean them like
                    // guest log output so control characters cannot forge log
                    // lines.
                    Log.Warning("[WasmHost] cannot reload " + _sharedPath + ": " + TextSanitizer.Clean(ex.Message) +
                                "; serving previous shared settings");
                }
                if (attempted)
                {
                    _failedMtime = attemptedMtime;
                    _failedLength = attemptedLength;
                    _failedValid = true;
                }
            }
        }
    }
}
