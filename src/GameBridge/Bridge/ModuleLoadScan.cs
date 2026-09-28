using System.Collections.Generic;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// What one module-tree scan did: the module ids it loaded, and one
    /// "what: why" line per module or tree it refused. "wasm load" reports
    /// both halves, so a module the operator just copied into Mods/Wasm is
    /// either named as loaded or explained as skipped on the console, where
    /// they typed the command, instead of only in the log file.
    /// </summary>
    public sealed class ModuleLoadScan
    {
        /// <summary>Scan that never ran: the host is not up, or no module
        /// tree resolved. Nothing loaded, nothing refused.</summary>
        public static readonly ModuleLoadScan None = new ModuleLoadScan(
            new List<string>(), new List<string>());

        public ModuleLoadScan(List<string> loadedIds, List<string> skipped)
        {
            LoadedIds = loadedIds;
            Skipped = skipped;
        }

        /// <summary>Ids loaded by this scan, in dispatch order.</summary>
        public List<string> LoadedIds { get; }

        /// <summary>One "what: why" line per refused module or tree.</summary>
        public List<string> Skipped { get; }
    }
}
