using System;

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
    /// working directory), no dot-only segments, and no control characters
    /// (C0, DEL, C1) that could forge log lines or drive terminals through
    /// the guest log and status output paths.
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
            }
            return true;
        }
    }
}
