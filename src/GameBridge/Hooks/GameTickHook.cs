using HordeForge.GameBridge.Bridge;

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
                Log.Error("[WasmHost] tick hook failed: " + ex);
            }
        }
    }
}
