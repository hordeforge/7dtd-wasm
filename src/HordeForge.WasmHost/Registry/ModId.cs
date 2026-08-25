using System;
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
    /// the guest log and status output paths, and no invisible format
    /// characters (zero-width space/joiners, word joiners, bidi controls,
    /// U+FEFF) or variation selectors: those render as nothing, so two ids
    /// that look identical could otherwise coexist as distinct registry
    /// entries and settings/log attribution would diverge silently.
    /// </summary>
    public static class ModId
    {
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
            if (id == "." || id == "..")
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
            }
            return true;
        }
    }
}
