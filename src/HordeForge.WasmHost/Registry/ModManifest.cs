using System;
using System.IO;
using HordeForge.WasmHost.Core;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Operator-authored per-mod manifest (wasm-mod.json) placed next to a
    /// guest module. The manifest is a trusted operator file: it can only
    /// tighten host defaults (fuel and memory ceilings), never weaken the
    /// host caps. Unknown fields are tolerated; malformed values reject the
    /// module with a specific reason.
    ///
    /// Supported shape:
    ///   {
    ///     "id": "hello",                    (optional, informational)
    ///     "name": "...",                    (optional, informational)
    ///     "limits": {
    ///       "fuelPerCall": 1000000,         (optional, must be >= 1)
    ///       "maxMemoryBytes": 33554432      (optional, must be >= 1)
    ///     }
    ///   }
    /// Any other key is ignored. See docs/ABI.md.
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
        /// Parses manifest text. Throws <see cref="WasmModLoadException"/> on
        /// malformed JSON or out-of-range values so the caller can reject the
        /// module with a clear reason.
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
                        long fuelValue = fuel.AsInteger("limits.fuelPerCall");
                        if (fuelValue < 1)
                        {
                            throw new FormatException("limits.fuelPerCall must be >= 1");
                        }
                        if (fuelValue > MaxFuelPerCall)
                        {
                            throw new FormatException("limits.fuelPerCall exceeds the host ceiling " + MaxFuelPerCall);
                        }
                        manifest.FuelPerCall = (ulong)fuelValue;
                    }
                    if (limits.TryGet("maxMemoryBytes", out JsonValue memory))
                    {
                        long memoryValue = memory.AsInteger("limits.maxMemoryBytes");
                        if (memoryValue < 1)
                        {
                            throw new FormatException("limits.maxMemoryBytes must be >= 1");
                        }
                        manifest.MaxMemoryBytes = (ulong)memoryValue;
                    }
                }
                return manifest;
            }
            catch (FormatException ex)
            {
                throw new WasmModLoadException(modId, "invalid wasm-mod.json manifest: " + ex.Message, ex);
            }
        }
    }
}
