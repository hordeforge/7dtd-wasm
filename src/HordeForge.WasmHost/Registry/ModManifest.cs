using System;
using System.Collections.Generic;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Operator-authored per-mod manifest (wasm-mod.toml, or the deprecated
    /// wasm-mod.json) placed next to a guest module. The manifest is a
    /// trusted operator file: its limits never exceed the host caps
    /// (fuel_per_call overrides the effective default within the parser
    /// ceiling; max_memory_bytes only tightens it). Unknown fields are
    /// tolerated; malformed values reject the module with a specific reason.
    ///
    /// TOML shape (canonical, docs/CONFIG.md, following the zdtd-server
    /// conventions: snake_case keys, [section] groups, defaults identical
    /// to the code defaults):
    ///   name = "boss"                  (optional, informational)
    ///   description = "..."            (optional, informational)
    ///
    ///   [limits]                       (host-enforced caps)
    ///   fuel_per_call = 1000000        (optional, must be >= 1)
    ///   max_memory_bytes = 33554432    (optional, must be >= 1)
    ///
    ///   [settings]                     (operator policy served to the guest
    ///   boss_name = "maci"              through the get_setting host import)
    /// </summary>
    public sealed class ModManifest
    {
        private const long MaxFuelPerCall = 50_000_000L;

        private ModManifest()
        {
        }

        /// <summary>Per-mod fuel budget in instructions per call, or null to use the host default.</summary>
        public ulong? FuelPerCall { get; private set; }

        /// <summary>Per-mod memory ceiling in bytes, or null to use the host default.</summary>
        public ulong? MaxMemoryBytes { get; private set; }

        /// <summary>
        /// Per-mod settings from the [settings] table, served to the guest
        /// through the get_setting host import (resolved before shared
        /// settings). Empty when the manifest has no [settings] table.
        /// </summary>
        public IReadOnlyDictionary<string, string> Settings { get; private set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Parses a TOML manifest. Throws <see cref="WasmModLoadException"/>
        /// on malformed TOML or out-of-range values so the caller can reject
        /// the module with a clear reason.
        /// </summary>
        public static ModManifest ParseToml(string toml, string modId)
        {
            if (toml == null)
            {
                throw new ArgumentNullException(nameof(toml));
            }
            var manifest = new ModManifest();
            try
            {
                TomlTable root = MiniToml.Parse(toml).AsTable("wasm-mod.toml root");
                if (root.TryGet("limits", out TomlValue limitsValue))
                {
                    BindLimits(manifest, limitsValue.AsTable("limits"));
                }
                if (root.TryGet("settings", out TomlValue settingsValue))
                {
                    BindSettings(manifest, settingsValue.AsTable("settings"));
                }
                return manifest;
            }
            catch (FormatException ex)
            {
                throw new WasmModLoadException(modId, "invalid wasm-mod.toml manifest: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Parses a JSON manifest (deprecated; kept for compatibility with
        /// older modules). Prefer ParseToml.
        /// </summary>
        public static ModManifest Parse(string json, string modId)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }
            var manifest = new ModManifest();
            try
            {
                JsonValue root = MiniJson.Parse(json);
                JsonObject obj = root.AsObject();

                if (obj.TryGet("limits", out JsonValue limitsValue))
                {
                    JsonObject limits = limitsValue.AsObject();
                    if (limits.TryGet("fuelPerCall", out JsonValue fuel))
                    {
                        manifest.FuelPerCall = (ulong)CheckFuel(fuel.AsInteger("limits.fuelPerCall"));
                    }
                    if (limits.TryGet("maxMemoryBytes", out JsonValue memory))
                    {
                        manifest.MaxMemoryBytes = (ulong)CheckMemory(memory.AsInteger("limits.maxMemoryBytes"));
                    }
                }
                return manifest;
            }
            catch (FormatException ex)
            {
                throw new WasmModLoadException(modId, "invalid wasm-mod.json manifest: " + ex.Message, ex);
            }
        }

        private static void BindLimits(ModManifest manifest, TomlTable limits)
        {
            if (limits.TryGet("fuel_per_call", out TomlValue fuel))
            {
                manifest.FuelPerCall = (ulong)CheckFuel(fuel.AsInteger("limits.fuel_per_call"));
            }
            if (limits.TryGet("max_memory_bytes", out TomlValue memory))
            {
                manifest.MaxMemoryBytes = (ulong)CheckMemory(memory.AsInteger("limits.max_memory_bytes"));
            }
        }

        private static void BindSettings(ModManifest manifest, TomlTable settings)
        {
            var bound = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in settings.Keys)
            {
                string value;
                try
                {
                    value = settings.TryGet(key, out TomlValue v) ? v.AsString("settings." + key) : string.Empty;
                }
                catch (FormatException ex)
                {
                    throw new FormatException("settings." + key + " must be a scalar (string, number, or boolean): " + ex.Message);
                }
                bound[key] = value;
            }
            manifest.Settings = bound;
        }

        private static long CheckFuel(long value)
        {
            if (value < 1)
            {
                throw new FormatException("limits.fuel_per_call must be >= 1");
            }
            if (value > MaxFuelPerCall)
            {
                throw new FormatException("limits.fuel_per_call exceeds the host ceiling " + MaxFuelPerCall);
            }
            return value;
        }

        private static long CheckMemory(long value)
        {
            if (value < 1)
            {
                throw new FormatException("limits.max_memory_bytes must be >= 1");
            }
            return value;
        }
    }
}
