using System;
using System.IO;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Reads operator-authored manifest files (wasm-mod.toml, wasm-mod.json,
    /// wasm.toml) behind a hard size bound. These are tiny config files by
    /// nature; anything at or beyond the bound is rejected instead of being
    /// slurped into memory wholesale.
    /// </summary>
    internal static class ManifestFiles
    {
        /// <summary>Maximum accepted manifest file size (1 MiB).</summary>
        public const long MaxBytes = 1024 * 1024;

        /// <summary>
        /// Reads the whole file when it exists, is readable, and fits the
        /// size bound; returns false otherwise.
        /// </summary>
        public static bool TryRead(string path, out string content)
        {
            content = string.Empty;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxBytes)
                {
                    return false;
                }
                content = File.ReadAllText(path);
                return true;
            }
            catch (Exception)
            {
                content = string.Empty;
                return false;
            }
        }
    }
}
