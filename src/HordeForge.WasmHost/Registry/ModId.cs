using System;
using System.Collections.Generic;
using System.Globalization;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Validation for mod ids: the registry key of a loaded module, derived
    /// from the folder name under Mods/Wasm or from console input. Ids end up
    /// in log source tags ("wasm/&lt;id&gt;"), trap messages, and module
    /// paths, so an id must be a plain folder name: no path separators (the
    /// module path must stay inside Mods/Wasm), no colons (on Windows a
    /// drive-relative "C:name" counts as rooted, so Path.Combine would drop
    /// the Mods/Wasm prefix and point the module path at another drive's
    /// working directory), no dot-only segments, no control characters
    /// (C0, DEL, C1) that could forge log lines or drive terminals through
    /// the guest log and status output paths, no Unicode line or paragraph
    /// separators (they break a line the same way a newline does), and no
    /// invisible format
    /// characters (zero-width space/joiners, word joiners, bidi controls,
    /// U+FEFF) or variation selectors: those render as nothing, so two ids
    /// that look identical could otherwise coexist as distinct registry
    /// entries and settings/log attribution would diverge silently. The
    /// replacement character is rejected for the same reason with a harder
    /// edge: a folder name that is not valid UTF-8 (legal on Linux) reaches
    /// .NET as U+FFFD, and the id built from it no longer names its own
    /// directory once re-encoded, so the module would silently never load.
    /// Two more rules come from the Windows filesystem: a name ending in a
    /// space or a period is stored without it, and CON, PRN, AUX, NUL and the
    /// COM/LPT series name a device rather than a directory (before any
    /// extension), and Win32 refuses a name carrying &lt; &gt; " | ? * at
    /// all. An id carrying one of those is a legal Linux folder name
    /// that names a different, or no, directory on Windows, so it would load
    /// on one platform and silently never load on the other.
    /// </summary>
    public static class ModId
    {
        /// <summary>
        /// Device names Windows reserves in every directory, before any
        /// extension. The superscript forms are reserved too, and read as
        /// their ASCII counterparts in a console listing.
        /// </summary>
        private static readonly HashSet<string> ReservedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "COM\u00b9", "COM\u00b2", "COM\u00b3",
            "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
        };

        /// <summary>
        /// Characters Win32 refuses in any file name, beyond the path
        /// separators, the colon, and the C0 controls rejected above. They
        /// are ordinary Linux folder names, so an id carrying one loads on
        /// Linux and names no directory at all on Windows.
        /// </summary>
        private const string WindowsForbiddenChars = "<>\"?*|";

        /// <summary>True when <paramref name="id"/> is a safe mod id.</summary>
        public static bool IsValid(string? id)
        {
            if (id == null || id.Length == 0)
            {
                return false;
            }
            if (id.IndexOf('/') >= 0 || id.IndexOf('\\') >= 0 || id.IndexOf(':') >= 0)
            {
                return false;
            }
            if (id.IndexOfAny(WindowsForbiddenChars.ToCharArray()) >= 0)
            {
                return false;
            }
            if (id == "." || id == "..")
            {
                return false;
            }
            if (id[id.Length - 1] == ' ' || id[id.Length - 1] == '.')
            {
                return false;
            }
            // The device name is what precedes the first period, so "con.toml"
            // is reserved exactly as "con" is.
            int dot = id.IndexOf('.');
            string stem = dot < 0 ? id : id.Substring(0, dot);
            if (ReservedDeviceNames.Contains(stem))
            {
                return false;
            }
            foreach (char c in id)
            {
                if (c < ' ' || c == '\x7f' || (c >= '\u0080' && c <= '\u009f'))
                {
                    return false;
                }
                if (char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                {
                    return false;
                }
                // Variation selectors are invisible too but Mn-categorized,
                // so not covered above: the BMP block U+FE00..U+FE0F, and
                // every plane-14 selector/tag character (they all encode
                // with the high surrogate 0xDB40).
                if ((c >= '\uFE00' && c <= '\uFE0F') || c == '\uDB40')
                {
                    return false;
                }
                // The Unicode line and paragraph separators are not C0
                // controls and not format characters, but they break a line
                // everywhere text is laid out, so an id carrying one splits
                // a log line the same way a newline would.
                if (c == '\u2028' || c == '\u2029')
                {
                    return false;
                }
                // U+FFFD: a folder name that is not valid UTF-8 (legal on
                // Linux) reaches .NET as replacement characters, and the id
                // built from it no longer names its own directory once it is
                // re-encoded, so the module would silently never load.
                if (c == '\uFFFD')
                {
                    return false;
                }
            }
            return true;
        }
    }
}
