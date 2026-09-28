using System;
using System.IO;
using HordeForge.GameBridge.Bridge;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The shared Mods/Wasm/wasm.toml settings cache: a guest reads the
    /// values, the cache re-reads when the file changes, and a file it
    /// cannot parse does not get re-parsed on every probe.
    /// </summary>
    public sealed class WasmSettingsProviderTests : IDisposable
    {
        private const int ProbeIntervalMs = 500;
        private readonly string _base;
        private readonly string _shared;
        private int _nowMs;

        public WasmSettingsProviderTests()
        {
            _base = Path.Combine(Path.GetTempPath(), "settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_base);
            _shared = Path.Combine(_base, "wasm.toml");
            Log.Clear();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_base, recursive: true);
            }
            catch (Exception)
            {
            }
        }

        private WasmSettingsProvider NewProvider()
        {
            return new WasmSettingsProvider(_shared, () => _nowMs);
        }

        private void WriteShared(string text)
        {
            File.WriteAllText(_shared, text);
        }

        private void AdvancePastProbeThrottle()
        {
            _nowMs += ProbeIntervalMs + 1;
        }

        [Fact]
        public void ServesSharedSettingsToEveryMod()
        {
            WriteShared("[settings]\nkey = \"shared-value\"\n");
            var provider = NewProvider();
            Assert.True(provider.TryGetSetting("mod-a", "key", out string a));
            Assert.Equal("shared-value", a);
            Assert.True(provider.TryGetSetting("mod-b", "key", out string b));
            Assert.Equal("shared-value", b);
        }

        [Fact]
        public void PerModSettingsWinOverShared()
        {
            WriteShared("[settings]\nkey = \"shared-value\"\n");
            var provider = NewProvider();
            provider.UpdateMod("mod-a", ModManifest.ParseToml("[settings]\nkey = \"own-value\"\n", "mod-a"));
            Assert.True(provider.TryGetSetting("mod-a", "key", out string own));
            Assert.Equal("own-value", own);
            // The shared value is untouched for every other mod, so one
            // mod's settings never leak into another's lookup.
            Assert.True(provider.TryGetSetting("mod-b", "key", out string shared));
            Assert.Equal("shared-value", shared);
        }

        [Fact]
        public void RemovedModFallsBackToSharedRatherThanItsOwnSettings()
        {
            WriteShared("[settings]\nkey = \"shared-value\"\n");
            var provider = NewProvider();
            provider.UpdateMod("mod-a", ModManifest.ParseToml("[settings]\nkey = \"own-value\"\n", "mod-a"));
            Assert.True(provider.TryGetSetting("mod-a", "key", out _));
            provider.RemoveMod("mod-a");
            Assert.True(provider.TryGetSetting("mod-a", "key", out string after));
            Assert.Equal("shared-value", after);
        }

        [Fact]
        public void ChangedSharedFileIsPickedUp()
        {
            WriteShared("[settings]\nkey = \"first\"\n");
            var provider = NewProvider();
            Assert.True(provider.TryGetSetting("mod", "key", out string first));
            Assert.Equal("first", first);

            WriteShared("[settings]\nkey = \"second\"\n");
            AdvancePastProbeThrottle();
            Assert.True(provider.TryGetSetting("mod", "key", out string second));
            Assert.Equal("second", second);
        }

        [Fact]
        public void RewriteWithTheSameMtimeButADifferentLengthIsPickedUp()
        {
            // A restore or a copy that carries the original timestamp leaves
            // the mtime where it was, so a cache keyed on the mtime alone
            // would serve the replaced file's predecessor forever.
            WriteShared("[settings]\nkey = \"first\"\n");
            var provider = NewProvider();
            Assert.True(provider.TryGetSetting("mod", "key", out _));
            DateTime pinned = File.GetLastWriteTimeUtc(_shared);

            WriteShared("[settings]\nkey = \"a-much-longer-second-value\"\n");
            File.SetLastWriteTimeUtc(_shared, pinned);
            Assert.Equal(pinned, File.GetLastWriteTimeUtc(_shared));

            AdvancePastProbeThrottle();
            Assert.True(provider.TryGetSetting("mod", "key", out string after));
            Assert.Equal("a-much-longer-second-value", after);
        }

        [Fact]
        public void DeletedSharedFileClearsTheSharedSettings()
        {
            // The operator removing wasm.toml must stop its settings reaching
            // guests, not leave them served from the cache.
            WriteShared("[settings]\nkey = \"value\"\n");
            var provider = NewProvider();
            Assert.True(provider.TryGetSetting("mod", "key", out _));

            File.Delete(_shared);
            AdvancePastProbeThrottle();
            Assert.False(provider.TryGetSetting("mod", "key", out string cleared));
            Assert.Equal(string.Empty, cleared);
        }

        [Fact]
        public void ProbeThrottleHoldsAChangeUntilTheNextProbe()
        {
            // A guest looping on get_setting within its fuel budget would
            // otherwise cost a stat pair per call in the game main loop, so
            // a change lands at the next probe rather than immediately.
            WriteShared("[settings]\nkey = \"first\"\n");
            var provider = NewProvider();
            Assert.True(provider.TryGetSetting("mod", "key", out _));

            WriteShared("[settings]\nkey = \"second\"\n");
            Assert.True(provider.TryGetSetting("mod", "key", out string throttled));
            Assert.Equal("first", throttled);

            AdvancePastProbeThrottle();
            Assert.True(provider.TryGetSetting("mod", "key", out string probed));
            Assert.Equal("second", probed);
        }

        [Fact]
        public void BrokenFileKeepsServingThePreviousSettingsAndIsReportedOnce()
        {
            WriteShared("[settings]\nkey = \"good\"\n");
            var provider = NewProvider();
            Assert.True(provider.TryGetSetting("mod", "key", out _));

            WriteShared("[settings\nkey = \"broken\"\n");
            AdvancePastProbeThrottle();
            Assert.True(provider.TryGetSetting("mod", "key", out string kept));
            Assert.Equal("good", kept);
            Assert.Single(Log.Captured);

            // The file is unchanged and still broken: the previous settings
            // keep serving and the operator is not told again on every probe.
            for (int i = 0; i < 5; i++)
            {
                AdvancePastProbeThrottle();
                Assert.True(provider.TryGetSetting("mod", "key", out string stillKept));
                Assert.Equal("good", stillKept);
            }
            Assert.Single(Log.Captured);
        }

        [Fact]
        public void AChangedBrokenFileIsRereadAndReportedAgain()
        {
            // The backoff must key on the file, not on "something failed
            // once": a second broken save is a second operator mistake and
            // has to reach the log, or the operator sees one warning for an
            // edit they made twice.
            WriteShared("[settings\nkey = \"broken\"\n");
            var provider = NewProvider();
            Assert.False(provider.TryGetSetting("mod", "key", out _));
            Assert.Single(Log.Captured);

            WriteShared("[settings\nkey = \"broken-again\"\n");
            AdvancePastProbeThrottle();
            Assert.False(provider.TryGetSetting("mod", "key", out _));
            Assert.Equal(2, Log.Captured.Count);
        }

        [Fact]
        public void FixedFileIsPickedUpAfterAFailure()
        {
            WriteShared("[settings\nkey = \"broken\"\n");
            var provider = NewProvider();
            Assert.False(provider.TryGetSetting("mod", "key", out _));

            WriteShared("[settings]\nkey = \"fixed\"\n");
            AdvancePastProbeThrottle();
            Assert.True(provider.TryGetSetting("mod", "key", out string fixedValue));
            Assert.Equal("fixed", fixedValue);
        }

        [Fact]
        public void MissingSharedFileReportsNotFound()
        {
            var provider = NewProvider();
            Assert.False(provider.TryGetSetting("mod", "key", out string value));
            Assert.Equal(string.Empty, value);
        }
    }
}
