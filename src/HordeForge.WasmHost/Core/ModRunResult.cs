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
        /// <summary>Creates a structured call result.</summary>
        public ModRunResult(ModRunStatus status, string message, string details, ulong fuelConsumed)
            : this(string.Empty, status, message, details, fuelConsumed)
        {
        }

        /// <summary>Creates a structured call result attributed to a mod.</summary>
        public ModRunResult(string modId, ModRunStatus status, string message, string details, ulong fuelConsumed)
        {
            ModId = modId ?? string.Empty;
            Status = status;
            Message = message ?? string.Empty;
            Details = details ?? string.Empty;
            FuelConsumed = fuelConsumed;
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

        /// <summary>True when the call completed successfully with StatusOk.</summary>
        public bool Ok => Status == ModRunStatus.Ok;
    }
}
