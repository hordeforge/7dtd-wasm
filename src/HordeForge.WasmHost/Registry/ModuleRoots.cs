using System;
using System.Collections.Generic;
using System.IO;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Where guest modules live on disk. The primary tree is Mods/Wasm; each
    /// staged modlet may additionally carry its own Wasm/ folder (a managed
    /// test instance stages whole modlets, never loose files under Mods/).
    /// The primary tree wins per id; a modlet tree only supplies ids the
    /// primary tree lacks. Pure filesystem logic, no game references, so it
    /// is covered by unit tests rather than acceptance.
    /// </summary>
    public static class ModuleRoots
    {
        /// <summary>
        /// Every module tree in priority order: the primary root first, then
        /// each modlet-carried Wasm/ folder. Only existing directories are
        /// returned, so a missing primary root is fine when a modlet carries
        /// the whole tree. Duplicates collapse to the first occurrence.
        /// </summary>
        public static IReadOnlyList<string> Order(string primaryRoot, IReadOnlyList<string> extraRoots)
        {
            var ordered = new List<string>(1 + (extraRoots?.Count ?? 0));
            if (!string.IsNullOrEmpty(primaryRoot) && Directory.Exists(primaryRoot))
            {
                ordered.Add(primaryRoot);
            }
            if (extraRoots != null)
            {
                foreach (string extra in extraRoots)
                {
                    if (!string.IsNullOrEmpty(extra) && Directory.Exists(extra) && !Contains(ordered, extra))
                    {
                        ordered.Add(extra);
                    }
                }
            }
            return ordered;
        }

        /// <summary>
        /// Collects each staged modlet's own Wasm/ folder, sorted by modlet
        /// name, excluding the bridge's own modlet (its sibling Mods/Wasm is
        /// the primary root already).
        /// </summary>
        public static IReadOnlyList<string> CollectExtra(string modsDir, string ownModletDir)
        {
            var found = new List<string>();
            string[] modlets;
            try
            {
                modlets = Directory.GetDirectories(modsDir);
            }
            catch (Exception)
            {
                return found;
            }
            string ownFull;
            try
            {
                ownFull = Path.GetFullPath(ownModletDir);
            }
            catch (Exception)
            {
                return found;
            }
            Array.Sort(modlets, StringComparer.Ordinal);
            foreach (string modlet in modlets)
            {
                string candidate = Path.Combine(modlet, "Wasm");
                if (!Directory.Exists(candidate))
                {
                    continue;
                }
                try
                {
                    if (string.Equals(Path.GetFullPath(modlet), ownFull, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }
                found.Add(candidate);
            }
            return found;
        }

        /// <summary>
        /// First tree holding the module's directory, or empty when none
        /// does. Earlier roots win over later ones.
        /// </summary>
        public static string ResolveDir(IReadOnlyList<string> roots, string id)
        {
            if (roots == null || !ModId.IsValid(id))
            {
                return string.Empty;
            }
            foreach (string root in roots)
            {
                string dir = Path.Combine(root, id);
                if (Directory.Exists(dir))
                {
                    return dir;
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// First tree holding the module's named file, or empty when none
        /// does. A directory without the file does not claim the module.
        /// </summary>
        public static string ResolveFile(IReadOnlyList<string> roots, string id, string fileName)
        {
            if (roots == null || !ModId.IsValid(id) || string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }
            foreach (string root in roots)
            {
                string path = Path.Combine(root, id, fileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            return string.Empty;
        }

        private static bool Contains(List<string> ordered, string candidate)
        {
            foreach (string existing in ordered)
            {
                if (string.Equals(existing, candidate, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
