using System;

namespace HordeForge.WasmHost
{
    /// <summary>
    /// Raised when an operator-authored manifest file (wasm.toml,
    /// wasm-mod.toml) cannot be read: missing, oversize, not valid UTF-8, or
    /// an IO error. The path and the reason are structured properties, so a
    /// consumer reports or classifies the failure without parsing the
    /// message text.
    ///
    /// Derives from <see cref="InvalidOperationException"/> so callers that
    /// already catch that around a config load keep working.
    /// </summary>
    public sealed class ManifestReadException : InvalidOperationException
    {
        /// <summary>Creates a read failure for the given path and reason.</summary>
        public ManifestReadException(string path, string reason)
            : base(path + " is unreadable: " + reason)
        {
            Path = path ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        /// <summary>Path of the file that could not be read.</summary>
        public string Path { get; }

        /// <summary>
        /// Why the read failed, as reported by the filesystem or the size
        /// bound (for example "the file does not exist").
        /// </summary>
        public string Reason { get; }
    }
}
