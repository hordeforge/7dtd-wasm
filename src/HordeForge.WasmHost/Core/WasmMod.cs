using System;
using System.Diagnostics;
using System.Threading;
using HordeForge.WasmHost.Abi;
using Wasmtime;

namespace HordeForge.WasmHost.Core
{
    /// <summary>
    /// A loaded and instantiated guest mod. One store, instance, and module
    /// per mod id (the binding cannot release an instance individually, so
    /// owning the store is what makes unload reclaim native memory).
    /// Calls are budgeted with fuel; every call returns a
    /// <see cref="ModRunResult"/> and never throws for a guest fault.
    ///
    /// The guest entry points are public for the counters and the handler
    /// probes, but an embedder must drive the module through
    /// <see cref="WasmModHost"/>: get_setting, the raw config import and the
    /// log source tag resolve against the mod the host is currently
    /// dispatching, so a direct call here is served the wrong settings (or
    /// none) and logs under the bare source prefix. See
    /// <see cref="WasmModHost.InitModule"/> and the Dispatch* methods.
    /// </summary>
    public sealed class WasmMod : IDisposable
    {
        private readonly Store _store;
        private readonly Module _module;
        private readonly ulong _fuelPerCall;
        private readonly Func<int> _init;
        private readonly Func<int> _tick;
        private readonly Func<int>? _shutdown;
        private readonly Func<int, int>? _onPlayerJoin;
        private readonly Func<int, int, int, int, int>? _onAdminCommand;
        private bool _disposed;
        private bool _enabled;

        /// <summary>Stopwatch ticks converted to milliseconds.</summary>
        private static readonly double MillisecondsPerTimestampTick = 1000.0 / Stopwatch.Frequency;

        internal WasmMod(string id, Module module, Store store, ulong fuelPerCall, Instance instance, long initTick)
        {
            Id = id;
            _module = module;
            _store = store;
            _fuelPerCall = fuelPerCall;
            InitTick = initTick;

            _shutdown = ResolveNoArg(instance, AbiConstants.ExportShutdown);
            _onPlayerJoin = instance.GetFunction<int, int>(AbiConstants.ExportPlayerJoin);
            _onAdminCommand = instance.GetFunction<int, int, int, int, int>(AbiConstants.ExportAdminCommand);
            var init = ResolveNoArg(instance, AbiConstants.ExportInit);
            var tick = ResolveNoArg(instance, AbiConstants.ExportTick);
            if (init == null)
            {
                throw new WasmModLoadException(id, "missing required export " + AbiConstants.ExportInit);
            }
            if (tick == null)
            {
                throw new WasmModLoadException(id, "missing required export " + AbiConstants.ExportTick);
            }
            _init = init;
            _tick = tick;
        }

        /// <summary>Unique mod id used as the registry key.</summary>
        public string Id { get; }

        /// <summary>Game tick at which the mod was loaded and initialized.</summary>
        public long InitTick { get; }

        /// <summary>
        /// Fuel budget this instance's calls are actually charged against:
        /// its manifest's fuel_per_call when it wrote one, the host default
        /// otherwise. The layering that produced it (docs/CONFIG.md) is only
        /// checkable from the value, so an embedder reports this rather than
        /// the host default.
        /// </summary>
        public ulong FuelPerCall => _fuelPerCall;

        // The counters below are written on whichever thread drives the guest
        // and read by any thread holding a WasmMod from
        // WasmModHost.TryGetMod, whose documented purpose is reading them.
        // They are updated with Interlocked so a reader on another thread
        // sees a whole value: a plain long++ is a read-modify-write, and on
        // a 32-bit runtime a concurrent reader can see a half-updated one.
        private long _totalFuelConsumed;
        private long _trapCalls;
        private long _fuelExhaustedCalls;
        private long _errorCalls;
        private long _totalCalls;

        // Wall-clock cost of the most recent call, as the bit pattern of a
        // double, so it can be read and written atomically from the calling
        // thread and the status thread at once. Fuel bounds the guest, but
        // only the clock says what a call cost the game frame, and the
        // aggregate dispatch cost reported by the embedder says nothing
        // about which guest produced it.
        private long _lastCallMs;

        /// <summary>
        /// Milliseconds of wall clock the most recent call to this guest took,
        /// successful or not, 0 before the first call. Read at the moment a
        /// dispatch runs long, it names the guest that spent the frame.
        /// </summary>
        public double LastCallMs => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastCallMs));

        /// <summary>
        /// Total fuel consumed across all calls so far. Held as a long so it
        /// can be updated atomically; the cast back is exact because only
        /// non-negative amounts are ever added (a fuel total cannot reach
        /// long.MaxValue in a process lifetime).
        /// </summary>
        public ulong TotalFuelConsumed => (ulong)Interlocked.Read(ref _totalFuelConsumed);

        /// <summary>Number of calls that ended in a guest trap.</summary>
        public long TrapCalls => Interlocked.Read(ref _trapCalls);

        /// <summary>Number of calls that exhausted the fuel budget.</summary>
        public long FuelExhaustedCalls => Interlocked.Read(ref _fuelExhaustedCalls);

        /// <summary>Number of calls that ended in a host or guest error.</summary>
        public long ErrorCalls => Interlocked.Read(ref _errorCalls);

        /// <summary>Total number of calls (init, tick, player join, shutdown) made so far.</summary>
        public long TotalCalls => Interlocked.Read(ref _totalCalls);

        /// <summary>
        /// Invokes the guest on_enable export. Guests read configuration
        /// through get_setting. See docs/ABI.md.
        ///
        /// Runs at most once per load generation, which is what docs/ABI.md
        /// promises ("called once when the mod is loaded and enabled"). A
        /// repeated call is a no-op reporting Ok: an embedder that enables a
        /// mod twice (a second DispatchInit, an InitModule after the load
        /// scan already enabled it) must not run the guest's enable side
        /// effects twice. The latch is per generation, so an unload and
        /// reload enables the fresh instance as its first act.
        ///
        /// A failed enable does not latch, so an embedder may retry it.
        /// </summary>
        public ModRunResult Init()
        {
            if (Volatile.Read(ref _enabled))
            {
                return Ok(0UL);
            }
            ModRunResult result = Run("on_enable", _init);
            if (result.Ok)
            {
                Volatile.Write(ref _enabled, true);
            }
            return result;
        }

        /// <summary>True once on_enable has completed for this generation.</summary>
        public bool Enabled => Volatile.Read(ref _enabled);

        /// <summary>Invokes the guest on_tick export; the tick number is read via the tick import.</summary>
        public ModRunResult Tick()
        {
            return Run("on_tick", _tick);
        }

        /// <summary>Invokes the guest shutdown export when present.</summary>
        public ModRunResult Shutdown()
        {
            if (_shutdown == null)
            {
                return Ok(0UL);
            }
            return Run("shutdown", _shutdown);
        }

        /// <summary>True when the guest exports the optional player-join handler.</summary>
        public bool HasPlayerJoinHandler
        {
            get { return _onPlayerJoin != null; }
        }

        /// <summary>True when the guest exports the optional on_admin_command handler.</summary>
        public bool HasAdminCommandHandler
        {
            get { return _onAdminCommand != null; }
        }

        /// <summary>
        /// Releases the mod's store (its instance and linear memory) and the
        /// compiled module's native handle. Safe to call more than once;
        /// callers must have removed the mod from dispatch first.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _store.Dispose();
            _module.Dispose();
        }

        /// <summary>
        /// Invokes the guest's optional on_player_join export with the
        /// spawning player's entity id (zdtd passes slot and entity id; we
        /// have no ECS slot). The player name is fetched inside the guest
        /// through the get_join_player_name host import. Returns null when
        /// the guest does not handle the event.
        /// </summary>
        public ModRunResult? OnPlayerJoin(int entityId)
        {
            if (_onPlayerJoin == null)
            {
                return null;
            }
            return Run("on_player_join", () => _onPlayerJoin(entityId));
        }

        /// <summary>
        /// Resolves a no-argument hook that may return either an i32 status
        /// (our ABI) or void (the zdtd plugin contract). A void hook is
        /// wrapped and reported as StatusOk.
        /// </summary>
        private static Func<int>? ResolveNoArg(Instance instance, string name)
        {
            var withResult = instance.GetFunction<int>(name);
            if (withResult != null)
            {
                return () => withResult();
            }
            var withoutResult = instance.GetAction(name);
            if (withoutResult != null)
            {
                return () =>
                {
                    withoutResult();
                    return AbiConstants.StatusOk;
                };
            }
            return null;
        }

        /// <summary>
        /// Arms the fuel budget, invokes the guest export, and turns the
        /// outcome into a result, never throwing for a guest fault.
        /// <paramref name="invoke"/> is the already-resolved export delegate
        /// rather than a lambda wrapping it: on_tick runs once per module per
        /// game tick, so a closure per call would be an allocation per module
        /// per tick on the main loop, forever.
        /// </summary>
        private ModRunResult Run(string callName, Func<int> invoke)
        {
            Interlocked.Increment(ref _totalCalls);
            long startedAt = Stopwatch.GetTimestamp();
            try
            {
                // Inside the try: arming the budget touches the store, and a
                // failure there must stay this mod's problem instead of
                // escaping Run and skipping the tick for every other module.
                _store.Fuel = _fuelPerCall;
                int status = invoke();
                RecordCallCost(startedAt);
                ulong consumed = ConsumedFuel();
                if (status != AbiConstants.StatusOk)
                {
                    Interlocked.Increment(ref _errorCalls);
                    return new ModRunResult(
                        Id,
                        ModRunStatus.Error,
                        "export " + callName + " returned status " + status,
                        string.Empty,
                        consumed,
                        status);
                }
                return Ok(consumed);
            }
            catch (Exception ex)
            {
                RecordCallCost(startedAt);
                ulong consumed = ConsumedFuelSafely();
                return ClassifyFailure(callName, ex, consumed);
            }
        }

        /// <summary>
        /// Publishes the cost of the call that just ended, on the failure
        /// path as well as the success one: a guest that traps or runs out of
        /// fuel is the one whose cost an operator needs, and the clock is
        /// already read, so the figure is free. The measurement is a plain
        /// Stopwatch pair, two reads per guest call, on the same path that
        /// already arms a fuel budget and re-reads the store.
        /// </summary>
        private void RecordCallCost(long startedAt)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - startedAt) * MillisecondsPerTimestampTick;
            if (elapsedMs < 0.0)
            {
                elapsedMs = 0.0;
            }
            Interlocked.Exchange(ref _lastCallMs, BitConverter.DoubleToInt64Bits(elapsedMs));
        }

        /// <summary>Ok result with no message, for a call that ran clean.</summary>
        private ModRunResult Ok(ulong fuelConsumed)
        {
            return new ModRunResult(Id, ModRunStatus.Ok, string.Empty, string.Empty, fuelConsumed);
        }

        private ulong ConsumedFuel()
        {
            ulong remaining = _store.Fuel;
            ulong consumed = remaining >= _fuelPerCall ? 0UL : _fuelPerCall - remaining;
            // The accumulator is signed so it can be updated atomically. A
            // guest would have to burn more than long.MaxValue instructions
            // in one call to reach the clamp, which no engine runs that long;
            // the clamp is there so an enormous configured budget cannot
            // wrap the total negative.
            long delta = consumed > (ulong)long.MaxValue ? long.MaxValue : (long)consumed;
            Interlocked.Add(ref _totalFuelConsumed, delta);
            return consumed;
        }

        /// <summary>
        /// <see cref="ConsumedFuel"/> for the failure path, where the store
        /// that just trapped may refuse the read too. The counter is
        /// best effort: the call already failed, and losing its fuel figure
        /// must not replace one reported failure with an unhandled one.
        /// </summary>
        private ulong ConsumedFuelSafely()
        {
            try
            {
                return ConsumedFuel();
            }
            catch (Exception)
            {
                return 0UL;
            }
        }

        private ModRunResult ClassifyFailure(string callName, Exception ex, ulong consumed)
        {
            string message = ex.Message ?? ex.GetType().Name;
            if (ex is TrapException trap)
            {
                if (trap.Type == TrapCode.OutOfFuel)
                {
                    Interlocked.Increment(ref _fuelExhaustedCalls);
                    return new ModRunResult(
                        Id,
                        ModRunStatus.FuelExhausted,
                        "fuel exhausted during " + callName,
                        message,
                        consumed);
                }
                Interlocked.Increment(ref _trapCalls);
                return new ModRunResult(
                    Id,
                    ModRunStatus.Trap,
                    "guest trap during " + callName,
                    message + " [" + trap.Type + "]",
                    consumed);
            }
            Interlocked.Increment(ref _errorCalls);
            return new ModRunResult(Id, ModRunStatus.Error, "error during " + callName, message, consumed);
        }
    }
}
