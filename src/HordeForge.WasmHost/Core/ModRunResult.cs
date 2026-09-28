using HordeForge.WasmHost.Abi;

namespace HordeForge.WasmHost.Core
{
    /// <summary>
    /// Structured result of a guest call. The host never lets a misbehaving
    /// guest crash the process: every call outcome is reported here and the
    /// instance stays loaded for the next tick. A value type so dispatch at
    /// tick rate does not allocate one object per mod per tick.
    /// </summary>
    public readonly struct ModRunResult
    {
        /// <summary>Creates a structured call result attributed to a mod.</summary>
        public ModRunResult(string modId, ModRunStatus status, string message, string details, ulong fuelConsumed)
            : this(modId, status, message, details, fuelConsumed, AbiConstants.StatusOk)
        {
        }

        /// <summary>
        /// Creates a structured call result that also carries the status code
        /// the guest export returned, so a caller can tell a guest's "not
        /// implemented" (1) from its "internal error" (2) without parsing
        /// <see cref="Message"/>.
        /// </summary>
        public ModRunResult(string modId, ModRunStatus status, string message, string details, ulong fuelConsumed, int guestStatus)
        {
            ModId = modId ?? string.Empty;
            Status = status;
            Message = message ?? string.Empty;
            Details = details ?? string.Empty;
            FuelConsumed = fuelConsumed;
            GuestStatus = guestStatus;
        }

        /// <summary>
        /// Registry id of the mod that produced this result, or empty when
        /// the producer did not report one. Results from the Dispatch*
        /// methods are not positionally aligned with <see cref="Core.WasmModHost.ModIds"/>
        /// for event dispatches that call only a subset of mods
        /// (DispatchPlayerJoin), so consumers should attribute through this
        /// field instead of list index.
        /// </summary>
        public string ModId { get; }

        /// <summary>Outcome category of the call.</summary>
        public ModRunStatus Status { get; }

        /// <summary>Short human-readable outcome, empty when Ok.</summary>
        public string Message { get; }

        /// <summary>Extra context (trap code, wasm backtrace text) when available.</summary>
        public string Details { get; }

        /// <summary>Instructions consumed from the per-call fuel budget.</summary>
        public ulong FuelConsumed { get; }

        /// <summary>
        /// Status code the guest export returned (0 ok, 1 not implemented,
        /// 2 internal error, see <see cref="Abi.AbiConstants.StatusOk"/> and
        /// docs/ABI.md). <see cref="AbiConstants.StatusOk"/> for every outcome
        /// that did not complete a guest call (traps, fuel exhaustion, host
        /// errors) and for the no-handler results. Meaningful only when
        /// <see cref="Status"/> is <see cref="ModRunStatus.Error"/> with a
        /// non-ok guest return code; check it instead of matching
        /// <see cref="Message"/> text.
        /// </summary>
        public int GuestStatus { get; }

        /// <summary>True when the call completed successfully (Status == Ok).</summary>
        public bool Ok => Status == ModRunStatus.Ok;
    }
}
