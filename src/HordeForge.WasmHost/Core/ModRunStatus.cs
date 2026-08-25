namespace HordeForge.WasmHost.Core
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
    }
}
