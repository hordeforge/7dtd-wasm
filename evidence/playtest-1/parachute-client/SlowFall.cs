using System;
using HarmonyLib;
using UnityEngine;

namespace ParachuteClient
{
    /// <summary>Shared glide buff name (applied by the bridge while armed).</summary>
    public static class GlideBuff
    {
        /// <summary>Buff the bridge applies while the glide flag is armed.</summary>
        public const string BUFF_NAME = "buffParachuteGlide";
    }

    /// <summary>
    /// Client half of the parachute glide (ADR 0037). While the glide buff is
    /// held, the fall is slowed and the landing is safe. vp_FPController
    /// integrates the fall speed in UpdateForces from Physics.gravity times
    /// PhysicsGravityModifier; this postfix clamps the integrated m_FallSpeed
    /// to the sink rate, the same -2.5 blocks/s the server-side [rules.glide]
    /// sink_vy_mps uses. Clamping the integrated speed instead of the gravity
    /// scale makes the fall visibly slow from deploy instead of building up
    /// slowly. The buff is applied by the bridge when the server-side
    /// parachute mod arms the glide, and must exist in the client's buffs.xml
    /// for the sync to be accepted.
    /// </summary>
    [HarmonyPatch(typeof(vp_FPController), "UpdateForces")]
    public static class Patch_GlideSlowFall
    {
        /// <summary>Sink rate while gliding (blocks/s, negative = down).</summary>
        public const float SinkRate = -2.5f;

        static void Postfix(vp_FPController __instance)
        {
            if (__instance == null || __instance.localPlayer == null)
            {
                return;
            }
            EntityPlayerLocal player = __instance.localPlayer;
            bool has = false;
            try
            {
                has = player.Buffs != null && player.Buffs.HasBuff(GlideBuff.BUFF_NAME);
            }
            catch (Exception ex)
            {
                Log.Warning("[parachute-client] buff check failed: " + ex.Message);
                return;
            }
            if (!has)
            {
                return;
            }
            if (__instance.m_FallSpeed < SinkRate)
            {
                __instance.m_FallSpeed = SinkRate;
            }
        }
    }

    /// <summary>
    /// Skips the vp fall impact while the glide buff is held, so a gliding
    /// landing causes no damage. The stock Buff path is not used for this:
    /// the client owns its own fall physics and the buff's effect_group
    /// patch point never fired for the local player in the live runs.
    /// </summary>
    [HarmonyPatch(typeof(vp_PlayerDamageHandler), "OnMessage_FallImpact")]
    public static class Patch_GlideNoFallDamage
    {
        static bool Prefix(vp_PlayerDamageHandler __instance)
        {
            if (__instance == null || __instance.m_Player == null)
            {
                return true;
            }
            try
            {
                var local = __instance.m_Player.GetComponentInParent<EntityPlayerLocal>();
                if (local == null)
                {
                    return true;
                }
                if (local.Buffs != null && local.Buffs.HasBuff(GlideBuff.BUFF_NAME))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[parachute-client] fall impact check failed: " + ex.Message);
            }
            return true;
        }
    }
}
