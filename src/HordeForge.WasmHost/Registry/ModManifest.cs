using System;
using System.Collections.Generic;
using HordeForge.WasmHost.Core;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Operator-authored per-mod manifest (wasm-mod.toml) placed next to a
    /// guest module. The manifest is a trusted operator file: its limits
    /// never exceed the host caps (fuel_per_call overrides the effective
    /// default within the parser ceiling; max_memory_bytes only tightens
    /// it). Unknown fields outside [limits] are tolerated, so a manifest
    /// written for a newer host still loads; malformed values and unknown
    /// [limits] keys reject the module with a specific reason. A misspelled
    /// limit is not a harmless extra field: it would silently leave the
    /// host ceiling in force where the operator wrote a tighter one, so
    /// [limits] is a closed table.
    ///
    /// TOML shape (canonical, docs/CONFIG.md, following the zdtd-server
    /// conventions: snake_case keys, [section] groups, defaults identical
    /// to the code defaults):
    ///   name = "boss"                  (optional, informational)
    ///   description = "..."            (optional, informational)
    ///
    ///   [limits]                       (host-enforced caps)
    ///   fuel_per_call = 1000000        (optional, must be >= 1)
    ///   max_memory_bytes = 33554432    (optional, one wasm page to the wasm32 ceiling)
    ///
    ///   [settings]                     (operator policy served to the guest
    ///   boss_name = "maci"              through the get_setting host import)
    /// </summary>
    public sealed class ModManifest
    {
        /// <summary>
        /// Largest per-call fuel budget any source may configure, in
        /// instructions. It is a runtime invariant, not a format rule: one
        /// call at the ceiling is roughly 50 ms of CPU, and a 20 TPS game
        /// loop cannot absorb more than a small multiple of that per
        /// module. The host enforces it on the configuration it is built
        /// from too (<see cref="Core.WasmModHost"/>), so an embedder setting
        /// <see cref="Config.WasmHostConfig.FuelPerCall"/> directly gets the
        /// same bound the file path reports by name.
        /// </summary>
        internal const long MaxFuelPerCall = 50_000_000L;

        /// <summary>
        /// The closed set of [limits] keys. Anything else is a typo or a
        /// limit this host does not enforce, and either way the operator
        /// believes a cap is in force that the engine never applies.
        /// </summary>
        private static readonly string[] KnownLimitKeys = { "fuel_per_call", "max_memory_bytes" };

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
        /// Parses a TOML manifest. Throws <see cref="WasmManifestException"/>
        /// (a <see cref="WasmModLoadException"/>) on malformed TOML or
        /// out-of-range values so the caller can reject the module with a
        /// clear reason.
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
                throw new WasmManifestException(modId, "invalid wasm-mod.toml manifest: " + ex.Message, ex);
            }
        }

        private static void BindLimits(ModManifest manifest, TomlTable limits)
        {
            foreach (string key in limits.Keys)
            {
                if (Array.IndexOf(KnownLimitKeys, key) < 0)
                {
                    throw new FormatException("unknown limits key '" + key + "'; supported: " +
                        string.Join(", ", KnownLimitKeys) + " (an unlisted key would leave the host cap in force instead)");
                }
            }
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
            // The floor is one wasm page, the same bound the host enforces on
            // its own ceiling. A per-mod value below it can only ever reject
            // the module, and the shared wasm.toml value becomes the engine's
            // memory ceiling, where the host constructor would throw and take
            // the bridge start down with it. Rejecting the file here keeps
            // both callers on the documented "invalid file, keep defaults" path.
            if (value < WasmModHost.WasmPageBytes)
            {
                throw new FormatException("limits.max_memory_bytes must be at least one wasm page (" +
                    WasmModHost.WasmPageBytes + " bytes); smaller ceilings reject every module");
            }
            // The ceiling is the wasm32 address space. A value past it is a
            // byte count no wasm32 module can reach, and as a shared limit it
            // reaches the engine, where it fails the host construction
            // instead of this parser. Rejecting it here names the bound
            // instead of leaving the operator with an engine error.
            if ((ulong)value > WasmModHost.Wasm32MemoryCeilingBytes)
            {
                throw new FormatException("limits.max_memory_bytes must be at most the wasm32 address space (" +
                    WasmModHost.Wasm32MemoryCeilingBytes + " bytes)");
            }
            return value;
        }
    }
}
