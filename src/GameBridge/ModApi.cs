using System;
using System.Reflection;
using HordeForge.WasmHost.Registry;
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
        // event to guests twice. Held under PatchLock, because a second
        // InitMod racing the first through the check would apply twice.
        private static bool _patched;

        private static readonly object PatchLock = new object();

        // The game raises WorldShuttingDown before it tears the world down and
        // GameShutdown later; both are registered because either can be the
        // last one a server reaches, and BridgeHost.Shutdown is idempotent.
        // A registered handler must not stack on a second InitMod.
        private static bool _shutdownRegistered;
        private static readonly object ShutdownLock = new object();

        /// <summary>
        /// Entry point the game calls once at load. Records the modlet path,
        /// stages the native engine, applies the Harmony patches, and scans
        /// Mods/Wasm for modules. A failure here is logged and swallowed:
        /// the game must load its Mods folder whether or not the host starts.
        /// </summary>
        /// <param name="_modInstance">The mod the game loaded, for its folder path.</param>
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
                RegisterShutdownHooks();
            }
            catch (Exception ex)
            {
                Log.Error("[WasmHost] init failed: " + TextSanitizer.Describe(ex));
            }
        }

        /// <summary>
        /// Stops the host when the game tears down the world or the process.
        /// Without this the Wasmtime engine and the bots it spawned are still
        /// alive after the game has cleaned up, and the V3.4.0 dedicated
        /// server then faults on exit (SIGSEGV after OnApplicationQuit). The
        /// game no longer needs either after these events, and BridgeHost
        /// shutdown despawns the bots while the world is still intact.
        /// </summary>
        private static void RegisterShutdownHooks()
        {
            lock (ShutdownLock)
            {
                if (_shutdownRegistered)
                {
                    return;
                }
                _shutdownRegistered = true;
            }
            try
            {
                ModEvents.WorldShuttingDown.RegisterHandler(OnWorldShuttingDown);
                ModEvents.GameShutdown.RegisterHandler(OnGameShutdown);
            }
            catch (Exception ex)
            {
                Log.Error("[WasmHost] could not register shutdown hooks: " + TextSanitizer.Describe(ex));
            }
        }

        private static void OnWorldShuttingDown(ref ModEvents.SWorldShuttingDownData _data)
        {
            ShutdownHost();
        }

        private static void OnGameShutdown(ref ModEvents.SGameShutdownData _data)
        {
            ShutdownHost();
        }

        /// <summary>
        /// BridgeHost.Shutdown is idempotent; a guest that traps on its
        /// shutdown export must not let one event escape the other, so both
        /// handlers go through here.
        /// </summary>
        private static void ShutdownHost()
        {
            try
            {
                BridgeHost.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("[WasmHost] shutdown failed: " + TextSanitizer.Describe(ex));
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
            lock (PatchLock)
            {
                if (_patched)
                {
                    return;
                }
                _patched = true;
            }
            try
            {
                var harmony = new Harmony("hordeforge.7dtd.wasmhost");
                Patch(harmony, "Update", "tick", typeof(GameTickHook).GetMethod(nameof(GameTickHook.Postfix)));
                Patch(harmony, "RequestToSpawnPlayer", "player join", typeof(PlayerSpawnHook).GetMethod(nameof(PlayerSpawnHook.Postfix)));
            }
            catch (Exception ex)
            {
                Log.Error("[WasmHost] Harmony could not be created; no game hook is active: " + TextSanitizer.Describe(ex));
            }
        }

        /// <summary>
        /// Posts one hook onto a GameManager method, reporting a target the
        /// game no longer ships instead of failing the whole patch pass. A
        /// patch that throws is named the same way and leaves the other hook
        /// to be attempted: the failure is per target, and a line saying only
        /// "failed to apply Harmony patches" leaves the reader unable to tell
        /// which event stopped reaching the guests or whether the other one
        /// landed.
        /// </summary>
        private static void Patch(Harmony harmony, string method, string label, MethodInfo postfix)
        {
            var target = AccessTools.Method(typeof(GameManager), method);
            if (target == null)
            {
                Log.Warning("[WasmHost] GameManager." + method + " not found; " + label + " hook disabled");
                return;
            }
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                Log.Error("[WasmHost] failed to patch GameManager." + method + " (" + label +
                          " hook disabled): " + TextSanitizer.Describe(ex));
                return;
            }
            Log.Out("[WasmHost] patched GameManager." + method);
        }
    }
}
