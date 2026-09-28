using System;
using System.Collections.Generic;
using System.Globalization;

namespace HordeForge.WasmHost.Core
{
    /// <summary>
    /// The guest failure counts seen at the last observation, compared with
    /// the current ones, so a periodic line can name the guests failing
    /// right now. The lifetime counters on <see cref="WasmMod"/> (printed
    /// by "wasm status") cannot do this: a guest that trapped once an hour
    /// ago and has been healthy since carries the same counter as one
    /// failing on every tick, and the per-tick failure lines are rate
    /// capped, so a reader of the log is left with a per-second warning
    /// and no way to tell which guest the next one belongs to.
    ///
    /// One instance per host; the caller reports the loaded modules once
    /// per heartbeat. A module seen for the first time is baselined, not
    /// reported: a guest that loaded mid-window has no failures this
    /// window unless its counters move, and reporting every fresh module
    /// would put every load in the window in the failing list.
    /// Thread safety: one instance is safe to share; every method takes
    /// the same lock, so one report is one consistent comparison.
    /// </summary>
    public sealed class FailureTally
    {
        /// <summary>Reported when no guest gained a failure since the last observation.</summary>
        public const string NoFailures = "none";

        // Serializes the baseline below; see the type comment.
        private readonly object _gate = new object();
        private readonly Dictionary<string, long> _seen = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// Records the current failure counts of the loaded modules and
        /// returns the ones that gained failures since the previous call,
        /// each as "id (n failure(s))", in the order they were reported. A
        /// module absent from <paramref name="modules"/> is dropped from
        /// the baseline, so a module reloaded between heartbeats is
        /// baselined again instead of inheriting the previous instance's
        /// totals as a jump in failures.
        /// </summary>
        public string Record(IReadOnlyList<ModuleFailure> modules)
        {
            var gained = new List<string>();
            lock (_gate)
            {
                for (int i = 0; i < modules.Count; i++)
                {
                    ModuleFailure module = modules[i];
                    if (!_seen.TryGetValue(module.Id, out long previous))
                    {
                        _seen[module.Id] = module.Failures;
                        continue;
                    }
                    long delta = module.Failures - previous;
                    _seen[module.Id] = module.Failures;
                    if (delta > 0)
                    {
                        gained.Add(module.Id + " (" + delta.ToString(CultureInfo.InvariantCulture) + " failure(s))");
                    }
                }
                ForgetAbsent(modules);
            }
            return gained.Count == 0 ? NoFailures : string.Join(", ", gained.ToArray());
        }

        /// <summary>Resets the baseline; used on host start and shutdown.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _seen.Clear();
            }
        }

        /// <summary>
        /// Drops every baseline entry whose module was not reported this
        /// time, so the table tracks the loaded modules rather than every id
        /// ever seen. The walk runs whenever the table holds anything the
        /// report does not name: comparing the two counts is not a shortcut
        /// for it, because a stale entry can hide behind a count that does
        /// not exceed the report's (a two-module baseline where one id was
        /// unloaded and a different one loaded in its place), and an id
        /// loaded again under a stale baseline is reported as a jump in
        /// failures it did not have. One heartbeat builds the set, so the
        /// cost is nothing next to the hour between calls.
        /// </summary>
        private void ForgetAbsent(IReadOnlyList<ModuleFailure> modules)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < modules.Count; i++)
            {
                present.Add(modules[i].Id);
            }
            var gone = new List<string>();
            foreach (string id in _seen.Keys)
            {
                if (!present.Contains(id))
                {
                    gone.Add(id);
                }
            }
            for (int i = 0; i < gone.Count; i++)
            {
                _seen.Remove(gone[i]);
            }
        }
    }

    /// <summary>
    /// One module's failure counters at one observation: the id, and the
    /// sum of its trap, fuel-exhausted, and error calls (the same classes
    /// "wasm status" prints per module).
    /// </summary>
    public readonly struct ModuleFailure
    {
        /// <summary>Creates a sample for one module.</summary>
        public ModuleFailure(string id, long failures)
        {
            Id = id;
            Failures = failures;
        }

        /// <summary>Module id the counts belong to.</summary>
        public string Id { get; }

        /// <summary>Failure calls counted for the module so far.</summary>
        public long Failures { get; }

        /// <summary>
        /// The failure classes <see cref="WasmMod"/> counts, added up, so a
        /// caller does not repeat that a trap and a fuel exhaustion are the
        /// same signal to an operator reading a periodic line.
        /// </summary>
        public static ModuleFailure Of(WasmMod mod)
        {
            return new ModuleFailure(mod.Id, mod.TrapCalls + mod.FuelExhaustedCalls + mod.ErrorCalls);
        }
    }
}
