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
        private readonly Func<int> _init;
        private readonly Func<int> _tick;
        private readonly Func<int>? _shutdown;
        private readonly Func<int, int>? _onPlayerJoin;

        internal WasmMod(string id, Store store, ulong fuelPerCall, Instance instance, long initTick)
        {
            Id = id;
            _store = store;
            _fuelPerCall = fuelPerCall;
            InitTick = initTick;

            _shutdown = instance.GetFunction<int>(AbiConstants.ExportShutdown);
            _onPlayerJoin = instance.GetFunction<int, int>(AbiConstants.ExportPlayerJoin);
            var init = instance.GetFunction<int>(AbiConstants.ExportInit);
            var tick = instance.GetFunction<int>(AbiConstants.ExportTick);
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
        /// Invokes the guest on_enable export. Guests read configuration
        /// through get_setting. See docs/ABI.md.
        /// </summary>
        public ModRunResult Init(long tick)
        {
            return Run("on_enable", () => _init());
        }

        /// <summary>Invokes the guest on_tick export; the tick number is read via the tick import.</summary>
        public ModRunResult Tick(long tick)
        {
            return Run("on_tick", () => _tick());
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

        /// <summary>True when the guest exports the optional player-join handler.</summary>
        public bool HasPlayerJoinHandler
        {
            get { return _onPlayerJoin != null; }
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
