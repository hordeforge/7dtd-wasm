using System;
using System.IO;
using System.Text;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Reads operator-authored manifest files (wasm-mod.toml, wasm-mod.json,
    /// wasm.toml) behind a hard size bound. These are tiny config files by
    /// nature; anything at or beyond the bound is rejected instead of being
    /// slurped into memory wholesale.
    ///
    /// Decoding is explicitly UTF-8 with an invalid-byte fallback that
    /// throws (TOML and JSON both mandate UTF-8): a file in any other
    /// encoding fails its load with a clear reason instead of silently
    /// corrupting setting values into U+FFFD before they are served to
    /// guests.
    /// </summary>
    internal static class ManifestFiles
    {
        /// <summary>Maximum accepted manifest file size (1 MiB).</summary>
        public const long MaxBytes = 1024 * 1024;

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Reads the whole file when it exists, is readable, and fits the
        /// size bound; returns false otherwise. <paramref name="failureReason"/>
        /// then says which bound failed (missing file, oversize, or the IO
        /// error) so callers can report the real cause instead of a generic
        /// "unreadable".
        /// </summary>
        public static bool TryRead(string path, out string content, out string? failureReason)
        {
            content = string.Empty;
            failureReason = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    failureReason = "the file does not exist";
                    return false;
                }
                if (info.Length > MaxBytes)
                {
                    failureReason = "the file is larger than " + MaxBytes + " bytes";
                    return false;
                }
                content = File.ReadAllText(path, StrictUtf8);
                return true;
            }
            catch (Exception ex)
            {
                content = string.Empty;
                failureReason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads a manifest file behind the shared size bound; throws so the
        /// caller's existing error paths (skip the module, keep defaults)
        /// handle it uniformly.
        /// </summary>
        public static string ReadRequired(string path)
        {
            if (!TryRead(path, out string content, out string? failureReason))
            {
                throw new InvalidOperationException(path + " is unreadable: " + failureReason);
            }
            return content;
        }
    }
}
