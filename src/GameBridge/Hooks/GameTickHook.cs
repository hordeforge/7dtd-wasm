using HordeForge.GameBridge.Bridge;

namespace HordeForge.GameBridge.Hooks
{
    /// <summary>
    /// Harmony postfix on GameManager.Update. On a dedicated server this runs
    /// once per game tick (20 TPS) and drives the guest mods. Patched
    /// explicitly by ModApi so a failure here can never break the game loop
    /// (the postfix is wrapped in try/catch inside BridgeHost.Tick).
    /// </summary>
    public static class GameTickHook
    {
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
