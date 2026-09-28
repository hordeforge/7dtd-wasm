using System;
using System.Reflection;
using HordeForge.GameBridge.Bridge;
using HordeForge.GameBridge.Hooks;
using HarmonyLib;

namespace HordeForge.GameBridge
{
    /// <summary>
    /// Mod entry point. Initializes the WASM host on dedicated servers only.
    /// Everything here is fail soft: a missing target or a broken module
    /// never prevents the server from starting.
    /// </summary>
    public class ModApi : IModApi
    {
        /// <summary>Path of this modlet folder (the game sets it before InitMod).</summary>
        public static string ModPath { get; private set; } = string.Empty;

        // Harmony patches must be applied at most once per process: a second
        // InitMod would stack duplicate postfixes and dispatch every game
        // event to guests twice.
        private static bool _patched;

        public void InitMod(Mod _modInstance)
        {
            ModPath = _modInstance?.Path ?? string.Empty;
            try
            {
                if (!GameManager.IsDedicatedServer)
                {
                    Log.Out("[WasmHost] not a dedicated server, bridge disabled");
                    return;
                }

                BridgeHost.Start();
                ApplyHarmonyPatches();
            }
            catch (Exception ex)
            {
                Log.Error("[WasmHost] init failed: " + ex);
            }
        }

        /// <summary>
        /// Patches GameManager.Update (tick dispatch) and
        /// GameManager.RequestToSpawnPlayer (player join dispatch). Fail
        /// soft: if a target is missing on this game version the rest of the
        /// mod still works (modules can be managed via "wasm" console
        /// commands, they just do not receive that event).
        /// </summary>
        private static void ApplyHarmonyPatches()
        {
            if (_patched)
            {
                return;
            }
            _patched = true;
            try
            {
                var harmony = new Harmony("hordeforge.7dtd.wasmhost");
                Patch(harmony, "Update", "tick", typeof(GameTickHook).GetMethod(nameof(GameTickHook.Postfix)));
                Patch(harmony, "RequestToSpawnPlayer", "player join", typeof(PlayerSpawnHook).GetMethod(nameof(PlayerSpawnHook.Postfix)));
            }
            catch (Exception ex)
            {
                Log.Error("[WasmHost] failed to apply Harmony patches: " + ex);
            }
        }

        /// <summary>
        /// Posts one hook onto a GameManager method, reporting a target the
        /// game no longer ships instead of failing the whole patch pass.
        /// </summary>
        private static void Patch(Harmony harmony, string method, string label, MethodInfo postfix)
        {
            var target = AccessTools.Method(typeof(GameManager), method);
            if (target == null)
            {
                Log.Warning("[WasmHost] GameManager." + method + " not found; " + label + " hook disabled");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Log.Out("[WasmHost] patched GameManager." + method);
        }
    }
}
