using System;
using System.IO;
using System.Text;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Reads operator-authored manifest files (wasm-mod.toml, wasm.toml)
    /// behind a hard size bound. These are tiny config files by nature;
    /// anything at or beyond the bound is rejected instead of being slurped
    /// into memory wholesale.
    ///
    /// Decoding is explicitly UTF-8 with an invalid-byte fallback that
    /// throws (TOML mandates UTF-8): a file in any other encoding fails
    /// its load with a clear reason instead of silently corrupting setting
    /// values into U+FFFD before they are served to guests. A leading
    /// UTF-8 BOM is stripped, since the parser has no use for it; the
    /// bytes are decoded here rather than through a reader that would
    /// silently switch encodings on a UTF-16 or UTF-32 BOM.
    /// </summary>
    public static class ManifestFiles
    {
        /// <summary>Maximum accepted manifest file size (1 MiB).</summary>
        public const long MaxBytes = 1024 * 1024;

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Marker the strict decoder puts in its message before the index of
        /// the byte it could not translate; the exception itself carries no
        /// public position.
        /// </summary>
        private const string ByteIndexMarker = "index ";

        private const byte LineFeed = (byte)'\n';

        /// <summary>
        /// Reads the whole file when it exists, is readable, and fits the
        /// size bound; returns false otherwise. <paramref name="failureReason"/>
        /// then says which bound failed (missing file, oversize, or the IO
        /// error) so callers can report the real cause instead of a generic
        /// "unreadable".
        /// </summary>
        public static bool TryRead(string path, out string content, out string failureReason)
        {
            content = string.Empty;
            failureReason = string.Empty;
            string oversize = "the file is larger than " + MaxBytes + " bytes";
            // Kept in scope for the catch: a decode failure can only be
            // located by counting into the bytes that failed to decode.
            byte[] bytes = Array.Empty<byte>();
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
                    failureReason = oversize;
                    return false;
                }
                bytes = File.ReadAllBytes(path);
                if (bytes.Length > MaxBytes)
                {
                    // The file grew between the stat and the read.
                    failureReason = oversize;
                    return false;
                }
                content = StrictUtf8.GetString(bytes);
                if (content.Length > 0 && content[0] == '\uFEFF')
                {
                    content = content.Substring(1);
                }
                return true;
            }
            catch (Exception ex)
            {
                content = string.Empty;
                // The path is in the reason because a decoder or filesystem
                // message names neither the file nor a position in it, and a
                // failure without them is not actionable for the operator.
                failureReason = path + ": " + DescribeFailure(ex, bytes);
                return false;
            }
        }

        /// <summary>
        /// The exception text, with the 1-based line and byte column of the
        /// offending byte appended for a decode failure; without them a
        /// non-UTF-8 manifest only reports a byte index the operator cannot
        /// map to a line.
        /// </summary>
        private static string DescribeFailure(Exception ex, byte[] bytes)
        {
            if (!(ex is DecoderFallbackException) || !TryReadByteIndex(ex.Message, out int byteIndex))
            {
                return ex.Message;
            }
            int line = 1;
            int column = 1;
            for (int i = 0; i < byteIndex && i < bytes.Length; i++)
            {
                if (bytes[i] == LineFeed)
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }
            return ex.Message + " (line " + line + ", byte " + column + ")";
        }

        /// <summary>
        /// Reads the byte index out of a decoder message of the form
        /// "Unable to translate bytes from index N to Unicode."; false when
        /// the message carries none, or when the value cannot be a position
        /// in a file that passed the size bound.
        /// </summary>
        private static bool TryReadByteIndex(string message, out int index)
        {
            index = 0;
            int at = message.IndexOf(ByteIndexMarker, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }
            at += ByteIndexMarker.Length;
            int digits = 0;
            int value = 0;
            while (at < message.Length && message[at] >= '0' && message[at] <= '9')
            {
                if (value > MaxBytes)
                {
                    return false;
                }
                value = (value * 10) + (message[at] - '0');
                at++;
                digits++;
            }
            index = value;
            return digits > 0;
        }

        /// <summary>
        /// Reads a manifest file behind the shared size bound; throws
        /// <see cref="ManifestReadException"/> (carrying the path and the
        /// reason) so the caller's existing error paths (skip the module,
        /// keep defaults) handle it uniformly.
        /// </summary>
        public static string ReadRequired(string path)
        {
            if (!TryRead(path, out string content, out string failureReason))
            {
                throw new ManifestReadException(path, failureReason);
            }
            return content;
        }
    }
}
