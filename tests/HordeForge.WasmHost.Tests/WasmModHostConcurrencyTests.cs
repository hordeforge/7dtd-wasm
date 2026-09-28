using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HordeForge.WasmHost.Config;
using HordeForge.WasmHost.Core;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// Concurrent entry-point coverage for the host: dispatch, load, and
    /// unload from several threads at once. The host owns mutable state the
    /// whole time (the mod registry, its load order, the per-call mod id the
    /// settings import resolves against, and the engine's per-mod stores),
    /// so every one of these is a corruption test rather than a coverage
    /// test: an unsynchronized entry point shows up as a skipped or
    /// duplicated mod in a tick, a setting served to the wrong mod, or a
    /// registry that disagrees with the load order.
    /// </summary>
    public sealed class WasmModHostConcurrencyTests
    {
        private const int Iterations = 150;

        private static byte[] Fixture(string name)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".wasm");
            return File.ReadAllBytes(path);
        }

        private static WasmModHost NewHost(out TestGameHostApi api)
        {
            api = new TestGameHostApi();
            return new WasmModHost(api, new WasmHostConfig());
        }

        /// <summary>
        /// Runs every worker and rethrows the first failure on the test
        /// thread, so an assertion inside a worker fails the test instead of
        /// being swallowed by the task.
        /// </summary>
        private static void RunWorkers(params Action[] workers)
        {
            Task[] tasks = workers.Select(worker => Task.Run(worker)).ToArray();
            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException ex)
            {
                throw ex.Flatten().InnerExceptions[0];
            }
        }

        [Fact]
        public void ConcurrentTicksReportEveryLoadedModExactlyOnce()
        {
            TestGameHostApi api;
            using (WasmModHost host = NewHost(out api))
            {
                host.LoadModule("alpha", Fixture("strings"));
                host.LoadModule("beta", Fixture("strings"));
                byte[] churn = Fixture("strings");

                // Two threads dispatch into the loaded mods while a third
                // loads and unloads a fourth. A tick must still report every
                // mod that was loaded for its whole duration, and the result
                // list each caller gets must not be rewritten by the other
                // dispatching thread.
                RunWorkers(
                    () => AssertEveryTickSeesBothMods(host),
                    () => AssertEveryTickSeesBothMods(host),
                    () =>
                    {
                        for (int i = 0; i < Iterations; i++)
                        {
                            host.LoadModule("churn", churn);
                            host.Unload("churn");
                        }
                        host.Unload("churn");
                    });

                // The registry and the load order agree after the churn: every
                // id in the load order resolves, and no stale entry survived
                // the last unload.
                List<string> ids = host.ModIds.ToList();
                Assert.Equal(new[] { "alpha", "beta" }, ids);
                Assert.All(ids, id => Assert.True(host.TryGetMod(id, out WasmMod? mod) && mod != null, id));
                Assert.False(host.TryGetMod("churn", out _));

                long calls = ids.Sum(id => host.TryGetMod(id, out WasmMod? mod) ? mod!.TotalCalls : 0L);
                Assert.Equal(2 * Iterations * 2, calls);
            }
        }

        [Fact]
        public void PerModSettingsAreNotServedToTheWrongModUnderLoad()
        {
            TestGameHostApi api;
            using (WasmModHost host = NewHost(out api))
            {
                host.LoadModule("alpha", Fixture("strings"));
                host.LoadModule("beta", Fixture("strings"));
                api.ModSettings["alpha"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["welcome"] = "alpha-only" };
                api.ModSettings["beta"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["welcome"] = "beta-only" };

                RunWorkers(
                    () => AssertOwnSettingOnly(host, Iterations),
                    () => AssertOwnSettingOnly(host, Iterations));

                // The settings import resolves against the mod currently
                // being called, so a crossed read would hand one guest the
                // other's value. Every tick line is attributed to the mod id
                // it was logged under, and the guest logs one such line per
                // tick, so the loop has to see a floor's worth of them.
                int checkedLines = 0;
                foreach (var entry in api.Logs)
                {
                    if (entry.Message.Contains("setting="))
                    {
                        Assert.Contains("setting='" + ExpectedSetting(entry.Source) + "'", entry.Message);
                        checkedLines++;
                    }
                }
                Assert.True(checkedLines >= 2 * Iterations,
                    "only " + checkedLines + " setting lines were checked; the guests did not run");
            }
        }

        [Fact]
        public void ConcurrentUnloadsLeaveOneRegistryState()
        {
            TestGameHostApi api;
            using (WasmModHost host = NewHost(out api))
            {
                byte[] bytes = Fixture("strings");
                host.LoadModule("keep", bytes);

                // Two threads race to load and unload the same id, and a third
                // keeps ticking. The end state is whatever the last operation
                // did; what must hold is that the registry never disagrees
                // with the load order and never hands out a disposed mod.
                RunWorkers(
                    () => Churn(host, bytes),
                    () => Churn(host, bytes),
                    () => AssertOwnSettingOnly(host, Iterations / 2));

                List<string> ids = host.ModIds.ToList();
                foreach (string id in ids)
                {
                    Assert.True(host.TryGetMod(id, out WasmMod? mod) && mod != null, id);
                }
                Assert.Contains("keep", ids);
            }
        }

        [Fact]
        public void ConcurrentEnableRacesNeverEnterAnUnusableStore()
        {
            // "wasm reload" runs InitModule while the tick hook is
            // dispatching and, on a double-typed command, while another
            // enable is in flight. InitModule writes the per-call mod id and
            // enters a store, so it has to serialize like the other entry
            // points: without the gate an enable can enter a store the
            // unload is disposing (reported as a failed enable) or hand this
            // guest the setting the other one is reading.
            TestGameHostApi api;
            using (WasmModHost host = NewHost(out api))
            {
                host.LoadModule("alpha", Fixture("strings"));
                host.LoadModule("beta", Fixture("strings"));
                api.ModSettings["alpha"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["welcome"] = "alpha-only" };
                api.ModSettings["beta"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["welcome"] = "beta-only" };
                byte[] churn = Fixture("strings");

                int churnEnabledA = 0;
                int churnEnabledB = 0;
                int churnEnabledByChurner = 0;
                RunWorkers(
                    () => EnableInALoop(host, new[] { "alpha", "beta", "churn" }, out churnEnabledA),
                    () => EnableInALoop(host, new[] { "alpha", "beta", "churn" }, out churnEnabledB),
                    () =>
                    {
                        for (int i = 0; i < Iterations; i++)
                        {
                            host.Unload("churn");
                            host.LoadModule("churn", churn);
                            // Enabling the mod this thread just loaded is
                            // what makes "an enable entered a store" a fact
                            // about the run rather than about the
                            // scheduler: the two enabling workers only ever
                            // enable, so nothing they do can take the mod
                            // away between these two calls. They keep
                            // racing this thread's unloads, which is the
                            // fault under test.
                            if (host.InitModule("churn") != null)
                            {
                                churnEnabledByChurner++;
                            }
                        }
                        host.Unload("churn");
                    },
                    () => AssertOwnSettingOnly(host, Iterations));

                // The race is only exercised if enables kept entering stores
                // while the churn worker disposed them. An enable of a mod
                // that latches on its first success never re-enters, so
                // without the churn id this loop would prove nothing.
                Assert.True(churnEnabledA + churnEnabledB + churnEnabledByChurner > 0,
                    "no enable ever entered a store, so the unload race did not happen");

                // A racing enable must not have served one guest the other's
                // setting, and the registry must still agree with the order.
                int checkedLines = 0;
                foreach (var entry in api.Logs)
                {
                    if (entry.Message.Contains("setting="))
                    {
                        Assert.Contains("setting='" + ExpectedSetting(entry.Source) + "'", entry.Message);
                        checkedLines++;
                    }
                }
                Assert.True(checkedLines >= 2 * Iterations,
                    "only " + checkedLines + " setting lines were checked; the guests did not run");
                List<string> ids = host.ModIds.ToList();
                Assert.Equal(new[] { "alpha", "beta" }, ids);
                Assert.All(ids, id => Assert.True(host.TryGetMod(id, out WasmMod? mod) && mod != null, id));
            }
        }

        /// <summary>
        /// Enables the given ids in a loop. The churn mod is unloaded under
        /// us, so a null result is its documented outcome; the two stable
        /// mods are always loaded and must return a real result. A
        /// non-Ok result is an enable that entered a store the unload was
        /// disposing, which is the fault under test.
        /// </summary>
        private static void EnableInALoop(WasmModHost host, string[] ids, out int churnEnabled)
        {
            churnEnabled = 0;
            for (int i = 0; i < Iterations; i++)
            {
                foreach (string id in ids)
                {
                    ModRunResult? result = host.InitModule(id);
                    if (id == "churn")
                    {
                        if (result != null)
                        {
                            churnEnabled++;
                        }
                        continue;
                    }
                    Assert.NotNull(result);
                    Assert.True(result!.Value.Ok, id + ": " + result.Value.Message);
                }
            }
        }

        private static void Churn(WasmModHost host, byte[] bytes)
        {            for (int i = 0; i < Iterations; i++)
            {
                if (host.TryGetMod("racer", out _))
                {
                    host.Unload("racer");
                }
                else
                {
                    try
                    {
                        host.LoadModule("racer", bytes);
                    }
                    catch (WasmModLoadException)
                    {
                        // The other churn thread loaded it between the check
                        // and the load; losing that race is the documented
                        // outcome, not a registry fault.
                    }
                }
            }
        }

        private static void AssertEveryTickSeesBothMods(WasmModHost host)
        {
            for (int i = 0; i < Iterations; i++)
            {
                IReadOnlyList<ModRunResult> results = host.DispatchTick(i);
                List<string> ids = results.Select(r => r.ModId).ToList();
                // The churn mod comes and goes at the end of the load order,
                // so what must hold every tick is that the two stable mods
                // lead it, in load order, once each, and nothing is skipped.
                Assert.True(ids.Count >= 2, string.Join(",", ids));
                Assert.Equal(new[] { "alpha", "beta" }, ids.Take(2));
                Assert.Equal(ids.Count, ids.Distinct().Count());
                Assert.All(results, r => Assert.True(r.Ok, r.ModId + ": " + r.Message));
            }
        }

        private static void AssertOwnSettingOnly(WasmModHost host, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                IReadOnlyList<ModRunResult> results = host.DispatchTick(i);
                Assert.All(results, r => Assert.True(r.Ok, r.ModId + ": " + r.Message));
            }
        }

        /// <summary>
        /// The value a guest under <paramref name="source"/> must see. The
        /// churn mod has no settings of its own, so its line carries the
        /// empty value: any other value there is a crossed read, which is
        /// the only kind this class can produce. An unrecognized source is
        /// its own failure rather than a default.
        /// </summary>
        private static string ExpectedSetting(string source)
        {
            if (source.EndsWith("/alpha", StringComparison.Ordinal))
            {
                return "alpha-only";
            }
            if (source.EndsWith("/beta", StringComparison.Ordinal))
            {
                return "beta-only";
            }
            if (source.EndsWith("/churn", StringComparison.Ordinal))
            {
                return string.Empty;
            }
            return "unexpected source " + source;
        }
    }
}
