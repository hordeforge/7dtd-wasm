using System;
using System.IO;
using HordeForge.WasmHost.Registry;

namespace HordeForge.WasmHost.Config
{
    /// <summary>
    /// Applies the shared operator file (wasm.toml) over the code defaults
    /// on a <see cref="WasmHostConfig"/>, before a host is constructed. This
    /// is the middle layer of the documented load order (docs/CONFIG.md):
    /// code defaults, then the shared [limits], then a per-mod
    /// wasm-mod.toml (parsed by <see cref="ModManifest"/> and applied by
    /// <see cref="Core.WasmModHost.LoadModule"/>).
    ///
    /// A shared [limits] key replaces the code default rather than tightening
    /// it, because it is the host's own ceiling: a mod manifest can only
    /// narrow what the engine already allows. Keys outside [limits] are
    /// settings and are left here; serve them through
    /// <see cref="SettingsTable"/>.
    /// </summary>
    public static class SharedLimits
    {
        /// <summary>
        /// Mod id attributed to a shared-file parse failure, in place of a
        /// per-mod one, so a diagnostic about wasm.toml never names a module
        /// that does not exist.
        /// </summary>
        public const string SharedModId = "shared";

        /// <summary>
        /// Reads the shared file at <paramref name="path"/> and applies its
        /// [limits] to <paramref name="config"/>. Returns the parsed
        /// manifest, so a caller can report its
        /// <see cref="ModManifest.IgnoredKeys"/>. A null return with an empty
        /// <paramref name="failureReason"/> means the file is absent: there
        /// was nothing to apply and <paramref name="config"/> is unchanged.
        ///
        /// A null return with a non-empty <paramref name="failureReason"/>
        /// means the file exists but cannot be used: unreadable, oversize,
        /// not valid UTF-8, malformed TOML, or a limit outside the range the
        /// engine accepts. That is a refusal in every one of those cases,
        /// because the engine would then run under the code defaults rather
        /// than the configured ones, and an embedder that starts anyway is
        /// running limits the operator did not write. Nothing is applied
        /// from a file that failed, so <paramref name="config"/> is left
        /// untouched either way.
        ///
        /// <paramref name="failureReason"/> quotes the underlying exception
        /// message, which for a parse failure quotes raw text from the file.
        /// A caller that renders it into a log line should pass it through
        /// <see cref="TextSanitizer.Clean"/> first, as <paramref name="path"/>
        /// and every manifest under it are operator-authored content.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="config"/> or <paramref name="path"/> is null.
        /// </exception>
        public static ModManifest? TryApply(WasmHostConfig config, string path, out string failureReason)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            if (!File.Exists(path))
            {
                failureReason = string.Empty;
                return null;
            }
            ModManifest shared;
            try
            {
                shared = ModManifest.ParseToml(ManifestFiles.ReadRequired(path), SharedModId);
            }
            catch (WasmModLoadException ex)
            {
                failureReason = Reason(ex);
                return null;
            }
            catch (Exception ex)
            {
                // An unreadable or oversize file is the same verdict as a
                // malformed one: the engine would run under the code
                // defaults, not the configured ones.
                failureReason = Reason(ex);
                return null;
            }
            if (shared.FuelPerCall.HasValue)
            {
                config.FuelPerCall = shared.FuelPerCall.Value;
            }
            if (shared.MaxMemoryBytes.HasValue)
            {
                config.StaticMemoryMaximumBytes = shared.MaxMemoryBytes.Value;
            }
            failureReason = string.Empty;
            return shared;
        }

        /// <summary>
        /// A failure reason is never empty, so a caller can branch on an
        /// empty <c>failureReason</c> alone. An exception carrying no message
        /// still names its type, which is the one fact left to report.
        /// </summary>
        private static string Reason(Exception ex)
        {
            return string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
        }
    }
}
