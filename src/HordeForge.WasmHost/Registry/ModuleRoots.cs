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
            var ordered = new List<string>(1 + extraRoots.Count);
            if (!string.IsNullOrEmpty(primaryRoot) && Directory.Exists(primaryRoot))
            {
                ordered.Add(primaryRoot);
            }
            foreach (string extra in extraRoots)
            {
                if (!string.IsNullOrEmpty(extra) && Directory.Exists(extra) && !ordered.Contains(extra))
                {
                    ordered.Add(extra);
                }
            }
            return ordered;
        }

        /// <summary>
        /// Collects each staged modlet's own Wasm/ folder, sorted by modlet
        /// name, excluding the bridge's own modlet (its sibling Mods/Wasm is
        /// the primary root already).
        ///
        /// <paramref name="failureReason"/> is empty on success and names the
        /// operation that failed otherwise (the Mods/ listing, or the
        /// modlet path the caller passed). A silent empty list is not
        /// distinguishable from "no modlet carries a Wasm tree", which is
        /// the difference between a server that loads its mods and one that
        /// silently runs without them, so the reason is reported rather than
        /// dropped.
        /// </summary>
        public static IReadOnlyList<string> CollectExtra(string modsDir, string ownModletDir, out string failureReason)
        {
            var found = new List<string>();
            failureReason = string.Empty;
            string[] modlets;
            try
            {
                modlets = Directory.GetDirectories(modsDir);
            }
            catch (DirectoryNotFoundException)
            {
                // No Mods/ folder is a server carrying no modlets, not a
                // failure the operator has to see.
                return found;
            }
            catch (Exception ex)
            {
                failureReason = "cannot list modlets under " + modsDir + " (" + ex.Message + ")";
                return found;
            }
            string ownFull;
            try
            {
                ownFull = Path.GetFullPath(ownModletDir);
            }
            catch (Exception ex)
            {
                failureReason = "cannot resolve the bridge modlet path " + ownModletDir + " (" + ex.Message + ")";
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
            if (!ModId.IsValid(id))
            {
                return string.Empty;
            }
            foreach (string root in roots)
            {
                string dir = Path.Combine(root, id);
                if (Directory.Exists(dir) && ChildNameMatches(root, id))
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
            if (!ModId.IsValid(id) || string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }
            foreach (string root in roots)
            {
                string path = Path.Combine(root, id, fileName);
                if (File.Exists(path) && ChildNameMatches(root, id))
                {
                    return path;
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// True when <paramref name="root"/> holds a directory whose name is
        /// spelled exactly as <paramref name="id"/>. Windows and macOS match
        /// file names case-insensitively, so on those a bare Exists check
        /// resolves "wasm reload HELLO" to the "hello" module and registers it
        /// under a second id: two entries reading one module.wasm, with their
        /// own limits, settings, and log source. Confirming the on-disk
        /// spelling keeps the outcome the same everywhere, where a wrong-case
        /// id is the "not found" Linux already reports.
        ///
        /// Enumeration runs only after the direct path resolved, which is the
        /// load and reload path plus one config lookup per module, never a
        /// per-tick call.
        /// </summary>
        private static bool ChildNameMatches(string root, string id)
        {
            string[] children;
            try
            {
                children = Directory.GetDirectories(root);
            }
            catch (Exception)
            {
                return false;
            }
            foreach (string child in children)
            {
                if (string.Equals(Path.GetFileName(child), id, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
