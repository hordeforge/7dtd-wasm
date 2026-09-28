using System;
using System.IO;
using HordeForge.GameBridge.Bridge;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// get_setting resolution against the shared Mods/Wasm/wasm.toml: a mod's
    /// own [settings] first, the shared file second, not found third. The
    /// file read happens on the game main loop and a guest can loop on
    /// get_setting within its fuel budget, so the reload is both
    /// mtime-and-length gated and probe-throttled, a broken file keeps
    /// serving the last good values, and the failure is reported once per
    /// change instead of once per miss. The clock is injected so the
    /// throttle is a function of the driver's time, not of wall time.
    /// </summary>
    public sealed class WasmSettingsProviderTests : IDisposable
    {
        private const int ProbeIntervalMs = 500;
        private readonly string _base;
        private readonly string _shared;
        private int _nowMs;

        /// <summary>Base for the synthetic mtimes, far from the real clock.</summary>
        private static readonly DateTime SharedEpoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

        /// <summary>
        /// Writes the shared file with an explicit last write time, for the
        /// cases where the test needs the file's identity rather than the
        /// clock's: a rewrite under a pinned or older timestamp than the
        /// write before it.
        /// </summary>
        private void WriteSharedAt(string text, DateTime mtime)
        {
            File.WriteAllText(_shared, text);
            File.SetLastWriteTimeUtc(_shared, mtime);
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

        [Fact]
        public void AModWithoutItsOwnManifestFallsBackToShared()
        {
            WriteShared("[settings]\ngreeting = \"shared\"\n");
            var provider = NewProvider();
            // LoadModule registers every id, manifest or not: a null
            // manifest must fall through to shared rather than shadow it.
            provider.UpdateMod("mod", null);

            Assert.True(provider.TryGetSetting("mod", "greeting", out string value));
            Assert.Equal("shared", value);
        }

        [Fact]
        public void AKeyRemovedFromTheSharedFileStopsResolving()
        {
            // A key that disappears must become a miss, not keep serving the
            // value from the previous read: that is how an operator retires
            // a setting for a running server.
            WriteShared("[settings]\ngreeting = \"first\"\nfarewell = \"bye\"\n");
            var provider = NewProvider();
            Assert.True(provider.TryGetSetting("mod", "farewell", out _));

            WriteShared("[settings]\ngreeting = \"first\"\n");
            AdvancePastProbeThrottle();
            Assert.False(provider.TryGetSetting("mod", "farewell", out string value));
            Assert.Equal(string.Empty, value);
        }

        [Fact]
        public void ProbesAreThrottledSoAGuestMissLoopCostsNoDiskReads()
        {
            WriteShared("[settings]\ngreeting = \"first\"\n");
            var provider = NewProvider();
            for (int i = 0; i < 50; i++)
            {
                provider.TryGetSetting("mod", "missing", out _);
            }

            // An edit inside the interval is not yet visible: the throttle
            // is what keeps a fuel-budgeted miss loop off the filesystem.
            WriteShared("[settings]\ngreeting = \"second\"\n");
            Assert.True(provider.TryGetSetting("mod", "greeting", out string value));
            Assert.Equal("first", value);
        }

        [Fact]
        public void TheThrottleSurvivesAClockWraparound()
        {
            // Environment.TickCount is an int and wraps; the comparison is
            // unchecked subtraction, so a wrapped clock must not read as a
            // huge negative interval and suppress every later reload. The
            // step crosses the wrap and is longer than the probe interval.
            WriteShared("[settings]\ngreeting = \"first\"\n");
            _nowMs = int.MaxValue - 1000;
            var provider = NewProvider();
            Assert.True(provider.TryGetSetting("mod", "greeting", out string value));
            Assert.Equal("first", value);

            _nowMs += 2000;
            WriteShared("[settings]\ngreeting = \"second\"\n");
            Assert.True(provider.TryGetSetting("mod", "greeting", out value));
            Assert.Equal("second", value);
        }

        [Fact]
        public void ABrokenSharedFileIsReportedOncePerChange()
        {
            WriteShared("[settings]\ngreeting = \"good\"\n");
            var provider = NewProvider();
            provider.TryGetSetting("mod", "greeting", out _);

            WriteSharedAt("[limits\n", SharedEpoch.AddYears(1));
            AdvancePastProbeThrottle();
            for (int i = 0; i < 20; i++)
            {
                provider.TryGetSetting("mod", "missing", out _);
                AdvancePastProbeThrottle();
            }

            // A guest looping on get_setting must not turn one operator
            // mistake into a log flood.
            Assert.Single(Log.Captured);
            Assert.Contains("cannot reload", Log.Captured[0]);
            Assert.Contains("serving previous shared settings", Log.Captured[0]);

            // But a second, different broken save is a second operator
            // mistake: suppressing it would hide the fact that the file is
            // still not loading, so the operator would not know to recheck.
            WriteSharedAt("also broken = = =\n", SharedEpoch.AddYears(2));
            AdvancePastProbeThrottle();
            provider.TryGetSetting("mod", "missing", out _);
            Assert.Equal(2, Log.Captured.Count);
        }

        [Fact]
        public void ABrokenFileIsCleansedBeforeItReachesTheLog()
        {
            // Parser diagnostics quote raw file text, so a manifest holding
            // control characters could otherwise forge log lines here.
            WriteShared("[settings]\ngreeting = \"good\"\n");
            var provider = NewProvider();
            provider.TryGetSetting("mod", "greeting", out _);

            WriteSharedAt("k = \"a\nb\"\n", SharedEpoch.AddYears(1));
            AdvancePastProbeThrottle();
            provider.TryGetSetting("mod", "greeting", out _);

            Assert.Single(Log.Captured);
            Assert.DoesNotContain("\n", Log.Captured[0].Replace("\r", string.Empty));
        }

        [Fact]
        public void FixingTheFileRestoresItWithoutARestart()
        {
            WriteShared("[settings]\ngreeting = \"good\"\n");
            var provider = NewProvider();
            Assert.True(provider.TryGetSetting("mod", "greeting", out string value));
            Assert.Equal("good", value);

            WriteSharedAt("[limits\n", SharedEpoch.AddYears(1));
            AdvancePastProbeThrottle();
            provider.TryGetSetting("mod", "greeting", out _);

            // The failed mtime was never applied, so the fixed file is read
            // on the strength of its content. The repair keeps the broken
            // file's mtime deliberately: on a filesystem with coarse
            // timestamps an operator's quick edit can land in the same
            // second, and skipping it as "already seen" would leave the
            // server serving stale settings with no way back short of a
            // restart.
            WriteSharedAt("[settings]\ngreeting = \"fixed\"\n", SharedEpoch.AddYears(1));
            AdvancePastProbeThrottle();
            Assert.True(provider.TryGetSetting("mod", "greeting", out value));
            Assert.Equal("fixed", value);
        }

        [Fact]
        public void ASharedFileWrittenAfterStartupIsFound()
        {
            // The bridge reads wasm.toml before any mod loads; an operator
            // who creates the file afterwards must not need a restart.
            var provider = NewProvider();
            Assert.False(provider.TryGetSetting("mod", "greeting", out _));

            WriteShared("[settings]\ngreeting = \"late\"\n");
            AdvancePastProbeThrottle();
            Assert.True(provider.TryGetSetting("mod", "greeting", out string value));
            Assert.Equal("late", value);
        }

        [Fact]
        public void SharedLimitsDoNotBecomeGuestSettings()
        {
            // [limits] is a closed table the host binds; a guest reading
            // limits.fuel_per_call must not be handed a host cap as its own
            // configuration.
            WriteShared("[limits]\nfuel_per_call = 1234\n[settings]\ngreeting = \"hi\"\n");
            var provider = NewProvider();

            Assert.False(provider.TryGetSetting("mod", "fuel_per_call", out _));
            Assert.True(provider.TryGetSetting("mod", "greeting", out string value));
            Assert.Equal("hi", value);
        }
    }
}
