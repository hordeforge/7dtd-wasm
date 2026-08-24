namespace HordeForge.WasmHost.Limits
{
    /// <summary>
    /// Structured result of a guest call. The host never lets a misbehaving
    /// guest crash the process: every call outcome is reported here and the
    /// instance stays loaded for the next tick.
    /// </summary>
    public sealed class ModRunResult
    {
        /// <summary>Creates a structured call result.</summary>
        public ModRunResult(ModRunStatus status, string message, string details, ulong fuelConsumed)
        {
            Status = status;
            Message = message ?? string.Empty;
            Details = details ?? string.Empty;
            FuelConsumed = fuelConsumed;
        }

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
