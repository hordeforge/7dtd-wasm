using System;

namespace HordeForge.WasmHost.Core
{
    /// <summary>
    /// Raised when a module cannot be loaded: parse or compile failure,
    /// rejected memory maximum, missing or wrongly-signed exports, or
    /// instantiation failure. The host stays healthy; only the affected
    /// module is refused.
    /// </summary>
    public sealed class WasmModLoadException : Exception
    {
        /// <summary>Creates a load failure for the given mod id.</summary>
        public WasmModLoadException(string modId, string message)
            : base(message)
        {
            ModId = modId;
        }

        /// <summary>Creates a load failure with an inner cause.</summary>
        public WasmModLoadException(string modId, string message, Exception innerException)
            : base(message, innerException)
        {
            ModId = modId;
        }

        /// <summary>Mod id this load failure refers to.</summary>
        public string ModId { get; }
    }
}
