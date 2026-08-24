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
        /// Patches GameManager.Update with the tick postfix. Fail soft: if the
        /// target is missing on this game version the rest of the mod still
        /// works (modules can be managed via "wasm" console commands, they
        /// just do not receive ticks).
        /// </summary>
        private static void ApplyHarmonyPatches()
        {
            try
            {
                var harmony = new Harmony("hordeforge.7dtd.wasmhost");
                var target = AccessTools.Method(typeof(GameManager), "Update");
                if (target == null)
                {
                    Log.Warning("[WasmHost] GameManager.Update not found; tick hook disabled");
                    return;
                }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(GameTickHook).GetMethod(nameof(GameTickHook.Postfix))));
                Log.Out("[WasmHost] patched GameManager.Update");
            }
            catch (Exception ex)
            {
                Log.Error("[WasmHost] failed to apply Harmony patches: " + ex);
            }
        }
    }
}
