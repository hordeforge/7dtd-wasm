using HordeForge.GameBridge.Bridge;

namespace HordeForge.GameBridge.Hooks
{
    /// <summary>
    /// Harmony postfix on GameManager.RequestToSpawnPlayer, the server-side
    /// player join entry point (verified: void(ClientInfo, int,
    /// PlayerProfile, int)). PlayerSpawnedInWorld and OnClientSpawned do
    /// not fire on the dedicated server for remote joins (found live in the
    /// acceptance run). Also fires on respawns.
    /// </summary>
    public static class PlayerSpawnHook
    {
        public static void Postfix(ClientInfo _cInfo)
        {
            try
            {
                BridgeHost.PlayerSpawnedInWorld(_cInfo);
            }
            catch (System.Exception ex)
            {
                Log.Error("[WasmHost] player spawn hook failed: " + ex);
            }
        }
    }
}
