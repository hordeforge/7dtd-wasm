using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HordeForge.WasmHost.Abi;
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
                api.Settings["greeting"] = "hello survivor";
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
        public void TomlManifestBindsLimitsAndSettings()
        {
            const string toml = @"
name = ""boss""
description = ""watcher""

[limits]
fuel_per_call = 5000
max_memory_bytes = 1048576

[settings]
boss_name = ""maci""
greeting = ""hello""
";
            ModManifest m = ModManifest.ParseToml(toml, "boss");
            Assert.Equal(5000UL, m.FuelPerCall);
            Assert.Equal(1048576UL, m.MaxMemoryBytes);
            Assert.Equal("maci", m.Settings["boss_name"]);
            Assert.Equal("hello", m.Settings["greeting"]);
        }

        [Fact]
        public void TomlManifestDefaultsAndUnknownKeys()
        {
            ModManifest m = ModManifest.ParseToml("name = \"x\"\n[future]\nflag = true\n", "x");
            Assert.Null(m.FuelPerCall);
            Assert.Null(m.MaxMemoryBytes);
            Assert.Empty(m.Settings);
        }

        [Theory]
        [InlineData("not toml")]
        [InlineData("[limits]\nfuel_per_call = 0\n")]
        [InlineData("[limits]\nfuel_per_call = 99999999999\n")]
        [InlineData("[settings]\nbad = [1, 2]\n")]
        [InlineData("key without equals\n")]
        [InlineData("[limits\nfuel_per_call = 1\n")]
        public void MalformedTomlManifestIsRejected(string toml)
        {
            Assert.Throws<WasmModLoadException>(() => ModManifest.ParseToml(toml, "bad"));
        }

        [Fact]
        public void TomlManifestLimitsAreEnforcedAtLoad()
        {
            var (host, _) = NewHost();
            using (host)
            {
                var manifest = ModManifest.ParseToml("[limits]\nmax_memory_bytes = 1048576\n", "hello");
                WasmModLoadException ex = Assert.Throws<WasmModLoadException>(() => host.LoadModule("hello", Fixture("hello"), manifest));
                Assert.Contains("exceeds the effective cap", ex.Message);
            }
        }

        [Fact]
        public void PerModSettingsResolveBeforeShared()
        {
            // get_setting is calling-mod aware: a mod's own [settings] win
            // over shared settings with the same key.
            var (host, api) = NewHost();
            using (host)
            {
                api.Settings["welcome"] = "shared value";
                api.ModSettings["strings"] = new Dictionary<string, string> { ["welcome"] = "per-mod value" };

                host.LoadModule("strings", Fixture("strings"));
                host.DispatchInit();
                Assert.True(host.DispatchTick(1).Single().Ok);
                Assert.Contains(api.Logs, l => l.Message.Contains("setting='per-mod value'"));
            }
        }

        [Fact]
        public void SharedSettingsServeWhenNoPerModValue()
        {
            var (host, api) = NewHost();
            using (host)
            {
                api.Settings["welcome"] = "shared value";
                host.LoadModule("strings", Fixture("strings"));
                host.DispatchInit();
                Assert.True(host.DispatchTick(1).Single().Ok);
                Assert.Contains(api.Logs, l => l.Message.Contains("setting='shared value'"));
            }
        }

        private static WasmModHost NewHostForFpsBot(TestGameHostApi api)
        {
            // The sibling fps_bot declares no memory maximum; it is treated
            // as the wasm32 ceiling (4 GiB), so the cap must be raised to run
            // it unmodified (ADR 0004 amendment).
            var config = new WasmHostConfig { StaticMemoryMaximumBytes = 4294967296UL };
            return new WasmModHost(api, config);
        }


        [Fact]
        public void FpsBotLoadsUnmodified()
        {
            // The actual zdtd-server fps_bot plugin (mods/fps_bot/fps_bot.wasm)
            // loads as-is: its zdtd imports are satisfied and its exports are
            // recognized.
            var api = new TestGameHostApi();
            using (WasmModHost host = NewHostForFpsBot(api))
            {
                WasmMod bot = host.LoadModule("fps-bot", Fixture("fps-bot"));
                Assert.True(bot.HasAdminCommandHandler);
                Assert.False(bot.HasPlayerJoinHandler);
                Assert.True(bot.Init(0).Ok);
                Assert.True(bot.Tick(1).Ok);
            }
        }

        [Fact]
        public void FpsBotBrainTargetsHostileAndQueuesShoot()
        {
            // Feed the unmodified brain a sense snapshot with a zombie in
            // front of the self bot; it must acquire it and queue bot look
            // and bot shoot SimCommands.
            var api = new TestGameHostApi();
            api.Sense = new SenseSnapshotWriter.Snapshot
            {
                Tick = 100,
                SelfNetId = 900,
                WorldTime = 0,
                Records =
                {
                    new SenseSnapshotWriter.EntityRecord { NetId = 900, Kind = SenseSnapshotWriter.KindBot, IsSelf = true, Alive = true, X = 0, Y = 0, Z = 0, Hp = 100, Yaw = 0 },
                    new SenseSnapshotWriter.EntityRecord { NetId = 42, Kind = SenseSnapshotWriter.KindZombie, Alive = true, X = 0, Y = 0, Z = -10, Hp = 100, Yaw = 0 },
                },
            };
            using (WasmModHost host = NewHostForFpsBot(api))
            {
                WasmMod bot = host.LoadModule("fps-bot", Fixture("fps-bot"));
                Assert.True(bot.Init(0).Ok);
                // Reaction gate is ~0.38 s (~8 ticks) for skill 2; give it
                // enough ticks for the reaction to expire and the shot to fire.
                for (int t = 1; t <= 12; t++)
                {
                    Assert.True(host.DispatchTick(t).Single().Ok, "tick " + t);
                }
                Assert.Contains(api.QueuedCommands, c => c.StartsWith("bot look 900"));
                Assert.Contains(api.QueuedCommands, c => c.StartsWith("bot shoot 900 42"));
            }
        }

        [Fact]
        public void FpsBotBrainIdlesWithoutThreat()
        {
            // An empty snapshot (header only, no entities) drives no commands.
            var api = new TestGameHostApi();
            api.Sense = new SenseSnapshotWriter.Snapshot { Tick = 1, SelfNetId = 900, WorldTime = 0 };
            using (WasmModHost host = NewHostForFpsBot(api))
            {
                WasmMod bot = host.LoadModule("fps-bot", Fixture("fps-bot"));
                Assert.True(bot.Init(0).Ok);
                for (int t = 1; t <= 5; t++)
                {
                    Assert.True(host.DispatchTick(t).Single().Ok);
                }
                Assert.DoesNotContain(api.QueuedCommands, c => c.StartsWith("bot shoot"));
            }
        }

        [Fact]
        public void FpsBotWithoutRaisedCapIsRejectedWithClearReason()
        {
            // The default 32 MiB cap rejects the undeclared-max module with a
            // message pointing at wasm.toml.
            var (host, _) = NewHost();
            using (host)
            {
                WasmModLoadException ex = Assert.Throws<WasmModLoadException>(() => host.LoadModule("fps-bot", Fixture("fps-bot")));
                Assert.Contains("raise wasm.toml limits.max_memory_bytes", ex.Message);
            }
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
        public void LoadOrderSurvivesUnloadAndReload()
        {
            // Dispatch order must follow the load order of the currently
            // loaded mods, even after an unload + reload cycle (what
            // `wasm reload <id>` does): the reloaded mod goes last.
            var (host, _) = NewHost();
            using (host)
            {
                host.LoadModule("strings", Fixture("strings"));
                host.LoadModule("trap", Fixture("trap"));
                Assert.True(host.Unload("strings"));
                host.LoadModule("strings", Fixture("strings"));

                var results = host.DispatchTick(1);
                Assert.Equal(2, results.Count);
                Assert.Equal(ModRunStatus.Trap, results[0].Status); // trap reloaded first
                Assert.Equal(ModRunStatus.Ok, results[1].Status);   // strings reloaded last

                host.LoadModule("hello", Fixture("hello"));
                Assert.Equal(new[] { "trap", "strings", "hello" }, host.ModIds.ToArray());
            }
        }

        [Fact]
        public void TomlBadUnicodeEscapeIsRejectedCleanly()
        {
            // A truncated \uXXX escape must reject the manifest with the
            // normal load error, not crash with an unexpected exception.
            WasmModLoadException ex = Assert.Throws<WasmModLoadException>(
                () => ModManifest.ParseToml("name = \"\\u123\"", "bad"));
            Assert.Contains("unicode", ex.Message);
            Assert.Throws<WasmModLoadException>(
                () => ModManifest.ParseToml("name = \"\\u12\"", "bad"));
        }

        [Fact]
        public void TomlUnicodeEscapeDecodes()
        {
            ModManifest m = ModManifest.ParseToml("[settings]\nboss_name = \"h\\u00e9llo\"\n", "x");
            Assert.Equal("héllo", m.Settings["boss_name"]);
        }

        [Fact]
        public void TomlUnterminatedArrayIsRejected()
        {
            // "[abc" without the closing bracket must be rejected, not
            // silently parsed as the string "ab".
            Assert.Throws<WasmModLoadException>(
                () => ModManifest.ParseToml("future = [abc\n", "bad"));
        }

        [Fact]
        public void TomlArrayStringsMayContainCommas()
        {
            // Scalars containing commas stay single items inside arrays.
            ModManifest m = ModManifest.ParseToml("future = [\"a, b\", \"c\"]\n", "x");
            Assert.Null(m.FuelPerCall);
        }

        [Fact]
        public void TomlTableHeaderToleratesWhitespace()
        {
            // Whitespace inside a header must not change the table name;
            // before, [limits ] created a table named "limits " and the
            // limit was silently dropped.
            ModManifest m = ModManifest.ParseToml("[ limits ]\nfuel_per_call = 5000\n", "x");
            Assert.Equal(5000UL, m.FuelPerCall);
            Assert.Throws<WasmModLoadException>(
                () => ModManifest.ParseToml("[limits.]\nfuel_per_call = 1\n", "bad"));
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

                IReadOnlyList<ModRunResult> joins = host.DispatchPlayerJoin(171, "maci");
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
                host.DispatchPlayerJoin(172, "xela");
                Assert.DoesNotContain(api.Logs, l => l.Message.Contains("THE BOSS IS HERE"));
                // Case matters: "Maci" is not "maci".
                host.DispatchPlayerJoin(173, "Maci");
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
                Assert.Empty(host.DispatchPlayerJoin(171, "maci"));
                Assert.True(strings.Tick(1).Ok);
            }
        }

        [Fact]
        public void ZigBossPrintsForConfiguredName()
        {
            // The Zig guest (samples/guest-boss-zig) reads boss_name through
            // get_setting; the built-in default is "maci".
            var (host, api) = NewHost();
            using (host)
            {
                WasmMod boss = host.LoadModule("boss-zig", Fixture("boss-zig"));
                Assert.True(boss.HasPlayerJoinHandler);
                Assert.True(boss.Init(0).Ok);

                // No setting: the guest falls back to "maci".
                Assert.True(host.DispatchPlayerJoin(171, "maci").Single().Ok);
                Assert.Contains(api.Logs, l => l.Message.Contains("THE BOSS IS HERE"));
            }
        }

        [Fact]
        public void ZigBossUsesSettingWhenPresent()
        {
            var (host, api) = NewHost();
            using (host)
            {
                api.Settings["boss_name"] = "boss";
                host.LoadModule("boss-zig", Fixture("boss-zig"));
                host.DispatchInit();

                host.DispatchPlayerJoin(171, "maci");
                Assert.DoesNotContain(api.Logs, l => l.Message.Contains("THE BOSS IS HERE"));

                host.DispatchPlayerJoin(174, "boss");
                Assert.Contains(api.Logs, l => l.Message.Contains("THE BOSS IS HERE"));
            }
        }
    }
}
