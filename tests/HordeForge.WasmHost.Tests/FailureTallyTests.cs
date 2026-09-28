using System.Collections.Generic;
using HordeForge.WasmHost.Core;
using Xunit;

namespace HordeForge.WasmHost.Tests
{
    /// <summary>
    /// The heartbeat names the guests that failed since the previous one.
    /// A log reader has capped per-tick warnings and lifetime counters, and
    /// neither separates a guest failing every tick from one that trapped
    /// once an hour ago, so these pin the comparison the line reports.
    /// </summary>
    public sealed class FailureTallyTests
    {
        [Fact]
        public void FirstObservationBaselinesInsteadOfReporting()
        {
            var tally = new FailureTally();
            // A module that loaded mid-window carries whatever it already
            // failed; reporting that here would put every load in the
            // window into the failing list.
            Assert.Equal(FailureTally.NoFailures, tally.Record(Counts(("brain", 7))));
        }

        [Fact]
        public void NamesGuestsThatGainedFailuresAndHowMany()
        {
            var tally = new FailureTally();
            tally.Record(Counts(("brain", 0), ("parachute", 3)));

            Assert.Equal("brain (2 failure(s)), parachute (1 failure(s))",
                tally.Record(Counts(("brain", 2), ("parachute", 4))));
        }

        [Fact]
        public void ReportsNothingWhenCountersStandStill()
        {
            var tally = new FailureTally();
            tally.Record(Counts(("brain", 2)));
            Assert.Equal(FailureTally.NoFailures, tally.Record(Counts(("brain", 2))));
        }

        [Fact]
        public void ReportsEachFailureOnceAcrossHeartbeats()
        {
            var tally = new FailureTally();
            tally.Record(Counts(("brain", 0)));
            Assert.Equal("brain (2 failure(s))", tally.Record(Counts(("brain", 2))));
            // The baseline moved with the last report, so the same two
            // failures are not repeated a minute later.
            Assert.Equal(FailureTally.NoFailures, tally.Record(Counts(("brain", 2))));
        }

        [Fact]
        public void UnloadedModuleDoesNotHauntTheNextReport()
        {
            var tally = new FailureTally();
            tally.Record(Counts(("brain", 0), ("parachute", 0)));
            // "parachute" is gone for one heartbeat and comes back with
            // fresh counters: it is a new generation and is baselined, not
            // reported as jumping by its previous instance's totals.
            Assert.Equal(FailureTally.NoFailures, tally.Record(Counts(("brain", 0))));
            Assert.Equal(FailureTally.NoFailures, tally.Record(Counts(("brain", 0), ("parachute", 0))));
            Assert.Equal("parachute (1 failure(s))", tally.Record(Counts(("brain", 0), ("parachute", 1))));
        }

        [Fact]
        public void ResetForgetsTheBaseline()
        {
            var tally = new FailureTally();
            tally.Record(Counts(("brain", 5)));
            tally.Reset();
            Assert.Equal(FailureTally.NoFailures, tally.Record(Counts(("brain", 5))));
        }

        private static IReadOnlyList<ModuleFailure> Counts(params (string Id, long Failures)[] modules)
        {
            var counts = new List<ModuleFailure>(modules.Length);
            for (int i = 0; i < modules.Length; i++)
            {
                counts.Add(new ModuleFailure(modules[i].Id, modules[i].Failures));
            }
            return counts;
        }
    }
}
