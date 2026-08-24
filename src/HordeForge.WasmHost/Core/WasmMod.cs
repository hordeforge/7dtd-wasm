using System;
using System.Text;
using HordeForge.WasmHost.Abi;
using HordeForge.WasmHost.Limits;
using Wasmtime;

namespace HordeForge.WasmHost.Core
{
    /// <summary>
    /// A loaded and instantiated guest mod. One instance per mod id, bound to
    /// the host's single store. Calls are budgeted with fuel; every call
    /// returns a <see cref="ModRunResult"/> and never throws for a guest
    /// fault.
    /// </summary>
    public sealed class WasmMod
    {
        private readonly Store _store;
        private readonly ulong _fuelPerCall;
        private readonly Func<int, int, int> _init;
        private readonly Func<long, int> _tick;
        private readonly Func<int>? _shutdown;

        internal WasmMod(string id, Store store, ulong fuelPerCall, Instance instance, long initTick)
        {
            Id = id;
            _store = store;
            _fuelPerCall = fuelPerCall;
            InitTick = initTick;

            _shutdown = instance.GetFunction<int>(AbiConstants.ExportShutdown);
            var init = instance.GetFunction<int, int, int>(AbiConstants.ExportInit);
            var tick = instance.GetFunction<long, int>(AbiConstants.ExportTick);
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

        /// <summary>Total fuel consumed across all calls so far.</summary>
        public ulong TotalFuelConsumed { get; private set; }

        /// <summary>Number of calls that ended in a guest trap.</summary>
        public long TrapCalls { get; private set; }

        /// <summary>Number of calls that exhausted the fuel budget.</summary>
        public long FuelExhaustedCalls { get; private set; }

        /// <summary>Number of calls that ended in a host or guest error.</summary>
        public long ErrorCalls { get; private set; }

        /// <summary>Total number of calls (init, tick, shutdown) made so far.</summary>
        public long TotalCalls { get; private set; }

        /// <summary>
        /// Invokes the guest init export. The boot payload pointer is passed
        /// as zero for now; guests read boot configuration through
        /// get_setting instead. See docs/ABI.md.
        /// </summary>
        public ModRunResult Init(long tick)
        {
            return Run("init", () => _init(0, 0));
        }

        /// <summary>Invokes the guest tick export with the current game tick.</summary>
        public ModRunResult Tick(long tick)
        {
            return Run("tick", () => _tick(tick));
        }

        /// <summary>Invokes the guest shutdown export when present.</summary>
        public ModRunResult Shutdown()
        {
            if (_shutdown == null)
            {
                return new ModRunResult(ModRunStatus.Ok, string.Empty, string.Empty, 0UL);
            }
            return Run("shutdown", () => _shutdown());
        }

        private ModRunResult Run(string callName, Func<int> invoke)
        {
            TotalCalls++;
            _store.Fuel = _fuelPerCall;
            try
            {
                int status = invoke();
                ulong consumed = ConsumedFuel();
                if (status != AbiConstants.StatusOk)
                {
                    ErrorCalls++;
                    return new ModRunResult(
                        ModRunStatus.Error,
                        "export " + callName + " returned status " + status,
                        string.Empty,
                        consumed);
                }
                return new ModRunResult(ModRunStatus.Ok, string.Empty, string.Empty, consumed);
            }
            catch (Exception ex)
            {
                ulong consumed = ConsumedFuelSafely();
                return ClassifyFailure(callName, ex, consumed);
            }
        }

        private ulong ConsumedFuel()
        {
            ulong remaining = _store.Fuel;
            ulong consumed = remaining >= _fuelPerCall ? 0UL : _fuelPerCall - remaining;
            TotalFuelConsumed += consumed;
            return consumed;
        }

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
            if (MentionsFuel(message))
            {
                FuelExhaustedCalls++;
                return new ModRunResult(
                    ModRunStatus.FuelExhausted,
                    "fuel exhausted during " + callName,
                    message,
                    consumed);
            }
            if (ex is TrapException trap)
            {
                TrapCalls++;
                return new ModRunResult(
                    ModRunStatus.Trap,
                    "guest trap during " + callName,
                    message + " [" + trap.Type + "]",
                    consumed);
            }
            ErrorCalls++;
            return new ModRunResult(ModRunStatus.Error, "error during " + callName, message, consumed);
        }

        private static bool MentionsFuel(string message)
        {
            return message.IndexOf("fuel", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
