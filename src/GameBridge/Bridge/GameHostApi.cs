using System;
using System.Collections.Generic;
using System.IO;
using HordeForge.WasmHost.Abi;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Bridges the host ABI to the live game. Implements IGameHostApi so
    /// guest calls (log, get_world_time, get_setting, send_chat) reach real
    /// game services. All methods are defensive: on a dedicated server
    /// without a loaded world they degrade to defaults instead of throwing.
    /// </summary>
    public sealed class GameHostApi : IGameHostApi
    {
        private readonly WasmSettingsProvider _settings;
        private readonly BotServant _servant;
        // Folder that holds guest modules (Mods/Wasm): per-mod config.toml
        // files are served to guests through the zdtd config import.
        private readonly string _wasmRoot;
        // Per-mod raw config (config.toml) cache, registered at module load
        // and invalidated on reload; a guest looping on the config import
        // must not stat the disk at call rate.
        private readonly Dictionary<string, string> _rawConfigs = new Dictionary<string, string>(StringComparer.Ordinal);

        public GameHostApi(WasmSettingsProvider settings, BotServant servant, string wasmRoot)
        {
            _settings = settings;
            _servant = servant;
            _wasmRoot = wasmRoot;
            // Each limiter carries its own cap from construction; the
            // per-purpose constants cannot drift from their call sites.
            LogLimiter = new GuestRateLimiter();
            ChatLimiter = new GuestRateLimiter();
            CommandLimiter = new GuestRateLimiter(GuestRateLimiter.MaxCommandsPerSecond);
            SenseLimiter = new GuestRateLimiter(GuestRateLimiter.MaxSensePerSecond);
            WorldTimeErrorLimiter = new GuestRateLimiter();
        }

        /// <summary>Per-module log rate limiter; exposed for "wasm status".</summary>
        public GuestRateLimiter LogLimiter { get; }

        /// <summary>Global guest chat rate limiter (one shared "chat" source); exposed for "wasm status".</summary>
        public GuestRateLimiter ChatLimiter { get; }

        /// <summary>Per-module SimCommand rate limiter; exposed for "wasm status".</summary>
        public GuestRateLimiter CommandLimiter { get; }

        /// <summary>
        /// Per-module sense request rate limiter; exposed for "wasm status".
        /// Each snapshot scans the live entity list, game-side work outside
        /// the wasm fuel budget, so it is capped like the other imports that
        /// trigger game-side work (ADR 0006 reasoning).
        /// </summary>
        public GuestRateLimiter SenseLimiter { get; }

        /// <summary>Rate limiter for get_world_time failure logs; exposed for "wasm status".</summary>
        public GuestRateLimiter WorldTimeErrorLimiter { get; }

        /// <summary>
        /// Longest chat message accepted from a guest, counted in Unicode
        /// code points so an astral-plane character (emoji and friends,
        /// two UTF-16 units each) costs one like any other character.
        /// </summary>
        public const int MaxChatMessageLength = 256;

        public void Log(string source, int level, string message)
        {
            // The game logger must be named global::Log here: the simple
            // name binds to this class's own Log method (CS0119).
            if (!LogLimiter.TryWrite(source, out long dropped))
            {
                // Every 100th dropped line is logged so throttling is
                // visible without the log itself being flooded.
                if (dropped % 100 == 1)
                {
                    global::Log.Out("[WasmHost] dropped " + dropped + " log line(s) from guest " + source +
                                    " (rate cap " + GuestRateLimiter.MaxLinesPerSecond + "/s)");
                }
                return;
            }
            string line = "[" + source + "] " + TextSanitizer.Clean(message);
            switch (level)
            {
                case AbiConstants.LogWarn:
                    global::Log.Warning(line);
                    break;
                case AbiConstants.LogError:
                    global::Log.Error(line);
                    break;
                default:
                    global::Log.Out(line);
                    break;
            }
        }

        public long GetWorldTime()
        {
            try
            {
                var game = GameManager.Instance;
                if (game == null || game.World == null)
                {
                    return 0L;
                }
                return (long)game.World.GetWorldTime();
            }
            catch (Exception ex)
            {
                // Guests silently read 0 when this fails; without a log line
                // that degraded world view is undiagnosable. The limiter
                // bounds the log like the other guest output paths so a
                // persistently throwing game state cannot flood it.
                if (WorldTimeErrorLimiter.TryWrite("world_time", out long dropped))
                {
                    global::Log.Warning("[WasmHost] get_world_time failed (" + ex.Message + "); guests read 0 until it recovers");
                }
                else if (dropped % 100 == 1)
                {
                    global::Log.Out("[WasmHost] suppressed " + dropped + " get_world_time failure log(s)");
                }
                return 0L;
            }
        }

        public bool TryGetSetting(string modId, string key, out string value)
        {
            return _settings.TryGetSetting(modId, key, out value);
        }

        /// <summary>
        /// Registers a module's raw config (its config.toml, verbatim) so the
        /// zdtd config import can serve it. Called by BridgeHost as modules
        /// load; reload replaces the entry, unload removes it.
        /// </summary>
        public void RegisterConfig(string modId, string content)
        {
            _rawConfigs[modId] = content;
        }

        /// <summary>Drops a module's cached config; called on unload and before reload.</summary>
        public void UnregisterConfig(string modId)
        {
            _rawConfigs.Remove(modId);
        }

        /// <summary>
        /// Serves the calling mod's config.toml verbatim (the zdtd config
        /// import). The host never parses it: each guest owns its format.
        /// Returns false when the mod has no config file, so the guest keeps
        /// its built-in defaults (zdtd: 0 = none).
        /// </summary>
        public bool TryGetRawConfig(string modId, out string content)
        {
            if (_rawConfigs.TryGetValue(modId, out content))
            {
                return content.Length > 0;
            }
            // Not registered (for example a module loaded outside the normal
            // scan): read the file once and remember the outcome so a guest
            // loop on the config import does not hit the disk per call.
            content = string.Empty;
            if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(_wasmRoot))
            {
                return false;
            }
            string path = Path.Combine(_wasmRoot, modId, "config.toml");
            if (ManifestFiles.TryRead(path, out string raw, out _))
            {
                content = raw;
            }
            _rawConfigs[modId] = content;
            return content.Length > 0;
        }

        public bool TryQueueCommand(string modId, string command)
        {
            // SimCommands execute game-side work (entity spawn, damage) that
            // the wasm fuel budget never sees, so each module is rate capped
            // like its log output (ADR 0006 reasoning).
            if (!CommandLimiter.TryWrite(modId, out _))
            {
                return false;
            }
            command = TextSanitizer.Clean(command);
            // The bot servant dispatches the brain's SimCommands and the
            // parachute mod's glide verb; non-servant queue text is a chat
            // announce (the parachute deploy message reaches the stock chat
            // broadcast this way, matching the mod's config: "announce via
            // the stock chat broadcast"). A rejected chat falls back to a
            // log line and still counts as accepted (the bytes were read).
            if (_servant.TryQueue(command, out bool handled))
            {
                return true;
            }
            if (handled)
            {
                // A servant command that failed mid-execution must reach the
                // guest as rejected (queue -> -1), not as accepted.
                return false;
            }
            if (!SendChat(command))
            {
                global::Log.Out("[WasmHost] cmd (chat rejected): " + command);
            }
            return true;
        }

        public int WriteSenseSnapshot(string modId, Span<byte> buffer)
        {
            // Building a snapshot scans the live world entity list; that is
            // game-side work the wasm fuel budget never sees, so each module
            // is rate capped like its SimCommands (ADR 0006 reasoning). A
            // capped request reports "no world data" (0), the same verdict a
            // blind brain already handles, and the drop is counted.
            if (!SenseLimiter.TryWrite(modId, out _))
            {
                return 0;
            }
            return _servant.WriteSense(buffer);
        }

        public string? TryQuery(string request)
        {
            // Stage 3: cover/path queries are not wired yet; the brain falls
            // back to plain movement when the host has no answer.
            return null;
        }

        public bool SendChat(string message)
        {
            try
            {
                var game = GameManager.Instance;
                if (game == null)
                {
                    return false;
                }
                // A guest must not push arbitrarily large strings into the
                // chat broadcast; oversized messages are rejected outright
                // (visible to the guest author) instead of silently cut.
                // Counted in code points, not string.Length (UTF-16 units):
                // a 130-emoji message is 130 characters and 260 units.
                if (message == null || CountCodePoints(message) > MaxChatMessageLength)
                {
                    return false;
                }
                // The game does not rate limit ChatMessageServer on its own,
                // so the bridge does: a guest spamming chat must not flood
                // the global channel (observed live in the acceptance run).
                if (!ChatLimiter.TryWrite("chat", out _))
                {
                    return false;
                }
                // Verified signature (V3): ChatMessageServer(ClientInfo, EChatType, int, string, List<int>, EMessageSender, BbCodeSupportMode)
                game.ChatMessageServer(null, EChatType.Global, -1, TextSanitizer.Clean(message), null, EMessageSender.Server, GeneratedTextManager.BbCodeSupportMode.NotSupported);
                return true;
            }
            catch (Exception ex)
            {
                // The guest only sees ChatRejected; an unexpected game-side
                // failure must stay visible in the server log. Rate is
                // bounded by the chat limiter check above.
                global::Log.Warning("[WasmHost] send_chat failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Number of Unicode code points in <paramref name="text"/> (surrogate pairs count once).</summary>
        private static int CountCodePoints(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsLowSurrogate(text[i]))
                {
                    count++;
                }
            }
            return count;
        }
    }
}
