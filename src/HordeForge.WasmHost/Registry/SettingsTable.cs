using System.Collections.Generic;

namespace HordeForge.WasmHost.Registry
{
    /// <summary>
    /// Settings resolution for the get_setting host import (docs/CONFIG.md):
    /// the calling mod's own [settings] first, shared [settings] second,
    /// not found last. Pure precedence logic with no filesystem or clock,
    /// so it is covered by unit tests; the bridge provider owns file
    /// watching and probe throttling around it.
    /// </summary>
    public sealed class SettingsTable
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _perMod =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(System.StringComparer.Ordinal);
        private readonly Dictionary<string, string> _shared =
            new Dictionary<string, string>(System.StringComparer.Ordinal);

        private static readonly IReadOnlyDictionary<string, string> EmptySettings =
            new Dictionary<string, string>(System.StringComparer.Ordinal);

        /// <summary>Registers (or replaces) a module's settings from its manifest.</summary>
        public void UpdateMod(string modId, IReadOnlyDictionary<string, string>? settings)
        {
            _perMod[modId] = settings ?? EmptySettings;
        }

        /// <summary>Drops a module's settings on unload.</summary>
        public void RemoveMod(string modId)
        {
            _perMod.Remove(modId);
        }

        /// <summary>Replaces the shared settings (parsed by the caller).</summary>
        public void UpdateShared(IReadOnlyDictionary<string, string> settings)
        {
            _shared.Clear();
            if (settings != null)
            {
                foreach (var pair in settings)
                {
                    _shared[pair.Key] = pair.Value;
                }
            }
        }

        /// <summary>Clears the shared settings (shared file removed).</summary>
        public void ClearShared()
        {
            _shared.Clear();
        }

        /// <summary>
        /// Resolves one key: the mod's own settings win over shared ones.
        /// A miss leaves <paramref name="value"/> empty and returns false.
        /// </summary>
        public bool TryGetSetting(string modId, string key, out string value)
        {
            value = string.Empty;
            if (modId.Length > 0
                && _perMod.TryGetValue(modId, out IReadOnlyDictionary<string, string>? modSettings)
                && modSettings != null
                && modSettings.TryGetValue(key, out string? found)
                && found != null)
            {
                value = found;
                return true;
            }
            if (_shared.TryGetValue(key, out string? shared) && shared != null)
            {
                value = shared;
                return true;
            }
            return false;
        }
    }
}
