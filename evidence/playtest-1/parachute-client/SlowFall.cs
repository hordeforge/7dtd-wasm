using System;
using HarmonyLib;
using UnityEngine;

namespace ParachuteClient
{
    /// <summary>
    /// Caps a gliding player's fall speed at the sink rate (blocks/s,
    /// negative = down), matching the zdtd [rules.glide] sink_vy_mps clamp.
    /// The local player's fall gravity is driven by the Unity FPS controller
    /// (vp_FPController.PhysicsGravityModifier); while buffParachuteGlide is
    /// held this postfix forces the modifier very low, so the descent
    /// gravity (and therefore the fall speed) drops to a fraction of normal
    /// and the landing is gentle. The buff is applied by the bridge when
    /// the server-side parachute mod arms the glide, and must exist in the
    /// client's buffs.xml for the sync to be accepted.
    /// </summary>
    [HarmonyPatch(typeof(EntityPlayerLocal), "Update")]
    public static class Patch_GlideSlowFall
    {
        /// <summary>Gravity scale while gliding (0.05 = 5% of normal fall gravity).</summary>
        public const float GlideGravityScale = 0.05f;

        static void Postfix(EntityPlayerLocal __instance)
        {
            if (__instance == null)
            {
                return;
            }
            bool has = false;
            try
            {
                has = __instance.Buffs != null && __instance.Buffs.HasBuff("buffParachuteGlide");
            }
            catch (Exception ex)
            {
                Log.Warning("[parachute-client] buff check failed: " + ex.Message);
                return;
            }
            // TEMP diagnostic: first Update per entity reports the buff state.
            if (_reported.Add(__instance.entityId))
            {
                Log.Out("[parachute-client] update net=" + __instance.entityId + " buff=" + has);
            }
            if (!has)
            {
                return;
            }
            try
            {
                var controller = Traverse.Create(__instance).Field("m_vp_FPController").GetValue<vp_FPController>();
                if (controller != null)
                {
                    controller.PhysicsGravityModifier = GlideGravityScale;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[parachute-client] controller lookup failed: " + ex.Message);
            }
        }

        private static readonly System.Collections.Generic.HashSet<int> _reported =
            new System.Collections.Generic.HashSet<int>();
    }
}
