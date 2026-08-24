using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HordeForge.WasmHost.Config;
using HordeForge.WasmHost.Core;
using HordeForge.WasmHost.Limits;
using HordeForge.WasmHost.Registry;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Host behavior against real wasm32-wasip1 fixtures compiled from
    /// samples/guest-fixtures. Covers load validation, host API round trips,
    /// and the sandbox guarantees (fuel, traps, memory cap).
    /// </summary>
    public sealed class WasmModHostTests
    {
        private static byte[] Fixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".wasm");
            return File.ReadAllBytes(path);
        }

        private static (WasmModHost Host, TestGameHostApi Api) NewHost(Action<WasmHostConfig>? configure = null)
        {
            var api = new TestGameHostApi();
            var config = new WasmHostConfig();
            configure?.Invoke(config);
            var host = new WasmModHost(api, config);
            return (host, api);
        }

        [Fact]
        public void LoadAndInitRunsGuestAndReportsOk()
        {
            var (host, _) = NewHost();
            using (host)
            {
                WasmMod mod = host.LoadModule("strings", Fixture("strings"));
                ModRunResult init = host.DispatchInit().Single();
                Assert.True(init.Ok, init.Message + " " + init.Details);
                Assert.True(mod.TotalCalls == 1);
            }
        }

        [Fact]
        public void InitLogsUtf8Losslessly()
        {
            var (host, api) = NewHost();
            using (host)
            {
                host.LoadModule("strings", Fixture("strings"));
                host.DispatchInit();
                Assert.Contains(api.Logs, l => l.Message.Contains("héllo wörld 🧟"));
            }
        }

        [Fact]
        public void TickDispatchesHostApiRoundTrips()
        {
            var (host, api) = NewHost();
            using (host)
            {
                api.WorldTime = 12345;
                api.Settings["welcome"] = "hello from host";
                host.LoadModule("strings", Fixture("strings"));
                host.DispatchInit();

                ModRunResult tick = host.DispatchTick(100).Single();
                Assert.True(tick.Ok, tick.Message + " " + tick.Details);

                // get_tick and get_world_time round trip into the log line.
                Assert.Contains(api.Logs, l => l.Message.Contains("strings tick=100 world=12345"));
                // get_setting round trip: host wrote the value into guest memory.
                Assert.Contains(api.Logs, l => l.Message.Contains("setting='hello from host'"));
                // missing key reported.
                Assert.Contains(api.Logs, l => l.Message.Contains("missing-key correctly reported"));
                // send_chat round trip.
                Assert.Equal("strings fixture chat at tick 100", api.Chats.Single());
                Assert.Contains(api.Logs, l => l.Message.Contains("chat accepted"));
            }
        }

        [Fact]
        public void GuestTrapIsReportedAndHostSurvives()
        {
            var (host, api) = NewHost();
            using (host)
            {
                host.LoadModule("trap", Fixture("trap"));
                host.DispatchInit();

                ModRunResult first = host.DispatchTick(1).Single();
                Assert.Equal(ModRunStatus.Trap, first.Status);
                Assert.Contains("trap", first.Message, StringComparison.OrdinalIgnoreCase);

                // A second tick traps again; the host and instance are intact.
                ModRunResult second = host.DispatchTick(2).Single();
                Assert.Equal(ModRunStatus.Trap, second.Status);

                // And a healthy mod still runs after the trap.
                host.LoadModule("strings", Fixture("strings"));
                host.DispatchInit();
                ModRunResult healthy = host.DispatchTick(3).Last();
                Assert.True(healthy.Ok, healthy.Message);
                Assert.Single(api.Logs, l => l.Message.Contains("strings tick=3"));
            }
        }

        [Fact]
        public void FuelBudgetStopsGuestAndRecovers()
        {
            var (host, api) = NewHost(config => config.FuelPerCall = 1_000_000UL);
            using (host)
            {
                WasmMod fuel = host.LoadModule("fuel", Fixture("fuel"));
                host.DispatchInit();

                ModRunResult first = host.DispatchTick(1).Single();
                Assert.Equal(ModRunStatus.FuelExhausted, first.Status);
                Assert.True(fuel.FuelExhaustedCalls == 1);
                Assert.True(fuel.TotalFuelConsumed > 0);

                // The same guest stays loaded; next tick is budgeted again.
                ModRunResult second = host.DispatchTick(2).Single();
                Assert.Equal(ModRunStatus.FuelExhausted, second.Status);
                Assert.True(fuel.FuelExhaustedCalls == 2);

                // A healthy mod still runs alongside the burner.
                host.LoadModule("strings", Fixture("strings"));
                host.DispatchInit();
                ModRunResult healthy = host.DispatchTick(3).Last();
                Assert.True(healthy.Ok, healthy.Message);
            }
        }

        [Fact]
        public void ModuleSizeOverCapIsRejected()
        {
            var (host, _) = NewHost(config => config.MaxModuleSizeBytes = 1024);
            using (host)
            {
                byte[] big = new byte[2048];
                WasmModLoadException ex = Assert.Throws<WasmModLoadException>(() => host.LoadModule("huge", big));
                Assert.Contains("exceeds cap", ex.Message);
            }
        }

        [Fact]
        public void MemoryMaximumOverCapIsRejected()
        {
            var (host, _) = NewHost(); // default cap 32 MiB
            using (host)
            {
                // bigmem.wasm declares a 128 MiB memory maximum.
                WasmModLoadException ex = Assert.Throws<WasmModLoadException>(() => host.LoadModule("bigmem", Fixture("bigmem")));
                Assert.Contains("exceeds the effective cap", ex.Message);
            }
        }

        [Fact]
        public void MissingRequiredExportsAreRejected()
        {
            var (host, _) = NewHost();
            using (host)
            {
                WasmModLoadException ex = Assert.Throws<WasmModLoadException>(() => host.LoadModule("noexports", Fixture("noexports")));
                Assert.Contains("missing required export", ex.Message);
            }
        }

        [Fact]
        public void RegistryTracksLoadUnloadAndDuplicates()
        {
            var (host, api) = NewHost();
            using (host)
            {
                host.LoadModule("strings", Fixture("strings"));
                host.LoadModule("trap", Fixture("trap"));
                Assert.Equal(new[] { "strings", "trap" }, host.ModIds.OrderBy(x => x).ToArray());

                Assert.Throws<WasmModLoadException>(() => host.LoadModule("strings", Fixture("strings")));

                Assert.True(host.TryGetMod("trap", out WasmMod? trap));
                Assert.NotNull(trap);
                Assert.False(host.TryGetMod("nope", out _));

                Assert.True(host.Unload("trap"));
                Assert.False(host.Unload("trap"));
                Assert.Single(host.ModIds);
            }
        }

        [Fact]
        public void UnloadInvokesShutdownExport()
        {
            var (host, api) = NewHost();
            using (host)
            {
                host.LoadModule("strings", Fixture("strings"));
                host.DispatchInit();
                host.Unload("strings");
                Assert.Contains(api.Logs, l => l.Message.Contains("strings fixture shutdown"));
            }
        }

        [Fact]
        public void DisposeShutsDownAllMods()
        {
            var api = new TestGameHostApi();
            var config = new WasmHostConfig();
            var host = new WasmModHost(api, config);
            host.LoadModule("strings", Fixture("strings"));
            host.DispatchInit();
            host.Dispose();
            Assert.Contains(api.Logs, l => l.Message.Contains("strings fixture shutdown"));
        }

        [Fact]
        public void LoadOrderIsDispatchOrder()
        {
            var (host, _) = NewHost();
            using (host)
            {
                host.LoadModule("strings", Fixture("strings"));
                host.LoadModule("fuel", Fixture("fuel"));
                var results = host.DispatchTick(7);
                Assert.Equal(ModRunStatus.Ok, results[0].Status);
                Assert.Equal(ModRunStatus.FuelExhausted, results[1].Status);
            }
        }

        [Fact]
        public void ShutdownIsOptional()
        {
            // fuel and trap fixtures have no shutdown export; unloading and
            // disposing must still succeed.
            var (host, api) = NewHost();
            using (host)
            {
                host.LoadModule("fuel", Fixture("fuel"));
                host.LoadModule("trap", Fixture("trap"));
                Assert.True(host.Unload("fuel"));
                Assert.True(host.Unload("trap"));
                Assert.Empty(api.Logs);
            }
        }

        [Fact]
        public void SampleHelloRunsEndToEnd()
        {
            // The shipped demo mod (dist/Mods/Wasm/hello) must behave as
            // documented: log every 100 ticks, chat every 1000 with the
            // configured greeting.
            var (host, api) = NewHost();
            using (host)
            {
                api.Settings["wasm.greeting"] = "hello survivor";
                host.LoadModule("hello", Fixture("hello"));
                host.DispatchInit();

                Assert.True(host.DispatchTick(100).Single().Ok);
                Assert.Contains(api.Logs, l => l.Message.Contains("hello mod alive at tick 100"));

                Assert.True(host.DispatchTick(1000).Single().Ok);
                Assert.Equal("hello survivor from a wasm mod at tick 1000", api.Chats.Single());
            }
        }

        [Fact]
        public void ManifestFuelOverrideIsEnforced()
        {
            // A tiny per-mod fuel budget must stop a normally healthy guest.
            var (host, _) = NewHost();
            using (host)
            {
                var manifest = ModManifest.Parse("{\"limits\": {\"fuelPerCall\": 500}}", "strings");
                WasmMod mod = host.LoadModule("strings", Fixture("strings"), manifest);
                Assert.True(mod.Init(0).Ok);
                // The tick does real work (formatting, host API calls), far
                // beyond a 500-instruction budget.
                ModRunResult tick = mod.Tick(1);
                Assert.Equal(ModRunStatus.FuelExhausted, tick.Status);
                Assert.True(mod.FuelExhaustedCalls >= 1);
            }
        }

        [Fact]
        public void ManifestMemoryCeilingIsEnforced()
        {
            // The hello module declares a 32 MiB memory maximum; a manifest
            // ceiling of 1 MiB must reject it at load.
            var (host, _) = NewHost();
            using (host)
            {
                var manifest = ModManifest.Parse("{\"limits\": {\"maxMemoryBytes\": 1048576}}", "hello");
                WasmModLoadException ex = Assert.Throws<WasmModLoadException>(() => host.LoadModule("hello", Fixture("hello"), manifest));
                Assert.Contains("exceeds the effective cap", ex.Message);
            }
        }

        [Fact]
        public void ManifestFuelAboveCeilingIsRejected()
        {
            WasmModLoadException ex = Assert.Throws<WasmModLoadException>(
                () => ModManifest.Parse("{\"limits\": {\"fuelPerCall\": 99999999999}}", "x"));
            Assert.Contains("ceiling", ex.Message);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{\"limits\": {\"fuelPerCall\": 0}}")]
        [InlineData("{\"limits\": {\"fuelPerCall\": -5}}")]
        [InlineData("{\"limits\": {\"maxMemoryBytes\": \"big\"}}")]
        [InlineData("[1, 2, 3]")]
        public void MalformedManifestIsRejected(string json)
        {
            Assert.Throws<WasmModLoadException>(() => ModManifest.Parse(json, "bad"));
        }

        [Fact]
        public void ManifestDefaultsMatchNullManifest()
        {
            // Unknown fields are tolerated; an empty manifest behaves like
            // no manifest at all.
            var (host, _) = NewHost();
            using (host)
            {
                var manifest = ModManifest.Parse("{\"name\": \"x\", \"limits\": {}, \"future\": true}", "strings");
                WasmMod mod = host.LoadModule("strings", Fixture("strings"), manifest);
                Assert.True(mod.Init(0).Ok);
                Assert.True(mod.Tick(1).Ok);
            }
        }

        [Fact]
        public void PlayerJoinDispatchPrintsBossMessage()
        {
            // The C guest (samples/guest-boss, built with zig) prints
            // "THE BOSS IS HERE" when the joining player is named "maci".
            var (host, api) = NewHost();
            using (host)
            {
                WasmMod boss = host.LoadModule("boss", Fixture("boss"));
                Assert.True(boss.HasPlayerJoinHandler);
                Assert.True(boss.Init(0).Ok);

                IReadOnlyList<ModRunResult> joins = host.DispatchPlayerJoin("maci");
                ModRunResult result = Assert.Single(joins);
                Assert.True(result.Ok, result.Message + " " + result.Details);
                Assert.Contains(api.Logs, l => l.Message.Contains("THE BOSS IS HERE"));
            }
        }

        [Fact]
        public void PlayerJoinIgnoresOtherNames()
        {
            var (host, api) = NewHost();
            using (host)
            {
                host.LoadModule("boss", Fixture("boss"));
                host.DispatchInit();
                host.DispatchPlayerJoin("xela");
                Assert.DoesNotContain(api.Logs, l => l.Message.Contains("THE BOSS IS HERE"));
                // Case matters: "Maci" is not "maci".
                host.DispatchPlayerJoin("Maci");
                Assert.DoesNotContain(api.Logs, l => l.Message.Contains("THE BOSS IS HERE"));
            }
        }

        [Fact]
        public void PlayerJoinIsOptionalForGuests()
        {
            // Modules without the on_player_join export load and tick fine,
            // and the dispatch is a no-op for them.
            var (host, _) = NewHost();
            using (host)
            {
                WasmMod strings = host.LoadModule("strings", Fixture("strings"));
                Assert.False(strings.HasPlayerJoinHandler);
                Assert.Empty(host.DispatchPlayerJoin("maci"));
                Assert.True(strings.Tick(1).Ok);
            }
        }
    }
}
