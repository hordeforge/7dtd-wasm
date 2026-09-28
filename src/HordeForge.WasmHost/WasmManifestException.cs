using System;

namespace HordeForge.WasmHost
{
    /// <summary>
    /// Raised when an operator-authored manifest (wasm-mod.toml, or the
    /// shared wasm.toml) is malformed or carries an out-of-range value.
    /// Separate from a module rejection so a consumer can tell "the mod's
    /// config is broken" from "the module was refused", which are fixed in
    /// different files.
    ///
    /// Derives from <see cref="WasmModLoadException"/>: the manifest gates
    /// the load of its mod, so existing catch sites keep working.
    /// </summary>
    public sealed class WasmManifestException : WasmModLoadException
    {
        /// <summary>Creates a manifest parse failure for the given mod id.</summary>
        public WasmManifestException(string modId, string message, Exception innerException)
            : base(modId, message, innerException)
        {
        }
    }
}
