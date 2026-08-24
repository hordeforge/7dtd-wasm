using System;
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

        /// <summary>True once the host is fully started.</summary>
        public static bool HostStarted { get; private set; }

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
                HostStarted = true;
            }
            catch (Exception ex)
            {
                Log.Error("[WasmHost] init failed: " + ex);
            }
        }

        /// <summary>
        /// Patches GameManager.Update (tick dispatch) and
        /// GameManager.PlayerSpawnedInWorld (player join dispatch). Fail
        /// soft: if a target is missing on this game version the rest of the
        /// mod still works (modules can be managed via "wasm" console
        /// commands, they just do not receive that event).
        /// </summary>
        private static void ApplyHarmonyPatches()
        {
            try
            {
                var harmony = new Harmony("hordeforge.7dtd.wasmhost");

                var tickTarget = AccessTools.Method(typeof(GameManager), "Update");
                if (tickTarget == null)
                {
                    Log.Warning("[WasmHost] GameManager.Update not found; tick hook disabled");
                }
                else
                {
                    harmony.Patch(tickTarget, postfix: new HarmonyMethod(typeof(GameTickHook).GetMethod(nameof(GameTickHook.Postfix))));
                    Log.Out("[WasmHost] patched GameManager.Update");
                }

                var spawnTarget = AccessTools.Method(typeof(GameManager), "RequestToSpawnPlayer");
                if (spawnTarget == null)
                {
                    Log.Warning("[WasmHost] GameManager.RequestToSpawnPlayer not found; player join hook disabled");
                }
                else
                {
                    harmony.Patch(spawnTarget, postfix: new HarmonyMethod(typeof(PlayerSpawnHook).GetMethod(nameof(PlayerSpawnHook.Postfix))));
                    Log.Out("[WasmHost] patched GameManager.RequestToSpawnPlayer");
                }
            }
            catch (Exception ex)
            {
                Log.Error("[WasmHost] failed to apply Harmony patches: " + ex);
            }
        }
    }
}
