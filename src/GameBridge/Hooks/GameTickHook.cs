using HordeForge.GameBridge.Bridge;
using HordeForge.WasmHost.Registry;

namespace HordeForge.GameBridge.Hooks
{
    /// <summary>
    /// Harmony postfix on GameManager.Update. On a dedicated server this runs
    /// once per game tick (20 TPS) and drives the guest mods. Patched
    /// explicitly by ModApi so a failure here can never break the game loop:
    /// this hook wraps BridgeHost.Tick in try/catch, and per-mod guest
    /// faults are reported in their ModRunResult.
    /// </summary>
    public static class GameTickHook
    {
        /// <summary>Tick hook failure lines allowed per second.</summary>
        private const int FailureLogsPerSecond = 1;

        // A bridge fault that escapes Tick throws on every tick, so the hook
        // would otherwise write 20 full stack traces a second: the one signal
        // that says the guest dispatch itself is broken would drown the whole
        // logfile, and the operator would be reading the flood instead of the
        // first occurrence. Capped like every other per-tick failure path, and
        // the dropped total is reported so the cap never hides how long the
        // fault has been running.
        private static readonly GuestRateLimiter FailureLimiter =
            new GuestRateLimiter(FailureLogsPerSecond, () => BridgeHost.ClockMs);

        /// <summary>
        /// The patched method body. One call per game tick into the host
        /// dispatch; a guest that traps or exhausts its fuel is reported
        /// in its own ModRunResult and cannot reach the game loop.
        /// </summary>
        public static void Postfix()
        {
            if (!GameManager.IsDedicatedServer)
            {
                return;
            }
            try
            {
                BridgeHost.Tick();
            }
            catch (System.Exception ex)
            {
                // The tick number is what makes the line a pivot point: it is
                // the same counter the per-mod failure lines, the slow-dispatch
                // warning and the heartbeat print, so one bridge fault can be
                // lined up against the run's own cost and failure history.
                if (FailureLimiter.TryWrite("tick", out long dropped))
                {
                    Log.Error("[WasmHost] tick hook failed at tick " + BridgeHost.TickNumber + ": " + TextSanitizer.Describe(ex) +
                              (dropped > 0
                                  ? "; " + dropped + " earlier failure log(s) were suppressed"
                                  : string.Empty));
                }
                else if (dropped % GuestRateLimiter.SuppressedReportEvery == 1)
                {
                    Log.Out("[WasmHost] suppressed " + dropped + " tick hook failure log(s)");
                }
            }
        }
    }
}
