using HarmonyLib;

namespace ParachuteClient
{
    /// <summary>
    /// Client-side half of the parachute glide (the zdtd parachute mod's
    /// README assigns deceleration to the paired client mod). On the stock
    /// dedicated server the client owns its physics - the server cannot
    /// slow a remote player's fall (SetPosition / teleportplayer are
    /// no-ops for remote entities) - so the sink clamp runs here: while the
    /// glide buff is held, the downward velocity is capped at the sink
    /// rate, making the fall visibly slow. The buff is applied by the
    /// bridge when the server-side parachute mod arms the glide.
    /// </summary>
    public class ModApi : IModApi
    {
        public void InitMod(Mod _modInstance)
        {
            try
            {
                var harmony = new Harmony("hordeforge.7dtd.parachuteclient");
                harmony.PatchAll();
                Log.Out("[parachute-client] PatchAll ok");
            }
            catch (System.Exception ex)
            {
                Log.Warning("[parachute-client] PatchAll failed: " + ex.Message);
            }
        }
    }
}
