using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Reads key-value settings for guest mods from a simple text file:
    /// &lt;dedicated&gt;/Mods/Wasm/wasm-settings.json-ish. Format is one
    /// "key: value" per line, "#" starts a comment, blank lines are ignored.
    /// Values are re-read when the file changes (mtime check), so editing
    /// the file at runtime takes effect without a server restart.
    /// </summary>
    public sealed class WasmSettingsFile
    {
        private readonly string _path;
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private DateTime _cacheMtime = DateTime.MinValue;

        public WasmSettingsFile(string path)
        {
            _path = path;
        }

        public bool TryRead(string key, out string value)
        {
            ReloadIfChanged();
            return _cache.TryGetValue(key, out value);
        }

        private void ReloadIfChanged()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _cache.Clear();
                    _cacheMtime = DateTime.MinValue;
                    return;
                }
                DateTime mtime = File.GetLastWriteTimeUtc(_path);
                if (mtime == _cacheMtime)
                {
                    return;
                }

                var fresh = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string raw in File.ReadAllLines(_path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }
                    int colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }
                    string k = line.Substring(0, colon).Trim();
                    string v = line.Substring(colon + 1).Trim();
                    fresh[k] = v;
                }
                _cache.Clear();
                foreach (var pair in fresh)
                {
                    _cache[pair.Key] = pair.Value;
                }
                _cacheMtime = mtime;
            }
            catch (Exception)
            {
                // Keep the previous cache on any read error.
            }
        }
    }
}
