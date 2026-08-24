namespace HordeForge.WasmHost.Limits
{
    /// <summary>
    /// Outcome of a single guest call (init, tick, player join, shutdown).
    /// </summary>
    public enum ModRunStatus
    {
        /// <summary>Call completed and the guest returned StatusOk.</summary>
        Ok = 0,

        /// <summary>Guest trapped (unreachable, out of bounds, stack overflow, ...).</summary>
        Trap = 1,

        /// <summary>Guest ran out of fuel (instruction budget) mid-call.</summary>
        FuelExhausted = 2,

        /// <summary>Guest call failed for another reason (host API threw, bad status code).</summary>
        Error = 3,

        /// <summary>The mod is not loaded.</summary>
        NotLoaded = 4,

        /// <summary>The required export is missing or has the wrong signature.</summary>
        MissingExport = 5,
    }
}
