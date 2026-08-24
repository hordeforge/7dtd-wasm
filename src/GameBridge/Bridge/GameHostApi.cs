using System;
using System.IO;
using System.Text;
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

        public GameHostApi(WasmSettingsProvider settings, BotServant servant)
        {
            _settings = settings;
            _servant = servant;
            RateLimiter = new GuestRateLimiter();
            ChatLimiter = new GuestRateLimiter();
            CommandLimiter = new GuestRateLimiter();
            WorldTimeErrorLimiter = new GuestRateLimiter();
        }

        /// <summary>Per-module log rate limiter; exposed for "wasm status".</summary>
        public GuestRateLimiter RateLimiter { get; }

        /// <summary>Per-module chat rate limiter; exposed for "wasm status".</summary>
        public GuestRateLimiter ChatLimiter { get; }

        /// <summary>Per-module SimCommand rate limiter; exposed for "wasm status".</summary>
        public GuestRateLimiter CommandLimiter { get; }

        /// <summary>Rate limiter for get_world_time failure logs; exposed for "wasm status".</summary>
        public GuestRateLimiter WorldTimeErrorLimiter { get; }

        /// <summary>Longest chat message accepted from a guest, in characters.</summary>
        public const int MaxChatMessageLength = 256;

        public void Log(string source, int level, string message)
        {
            if (!RateLimiter.TryWrite(source, out long dropped))
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

        public bool TryQueueCommand(string modId, string command)
        {
            // SimCommands execute game-side work (entity spawn, damage) that
            // the wasm fuel budget never sees, so each module is rate capped
            // like its log output (ADR 0006 reasoning).
            if (!CommandLimiter.TryWrite(modId, out _, GuestRateLimiter.MaxCommandsPerSecond))
            {
                return false;
            }
            command = TextSanitizer.Clean(command);
            // The bot servant dispatches the brain's SimCommands; non-bot
            // commands are logged and accepted.
            if (_servant.TryQueue(command, out bool handled))
            {
                return true;
            }
            if (handled)
            {
                // A bot command that failed mid-execution must reach the
                // guest as rejected (queue -> -1), not as accepted.
                return false;
            }
            global::Log.Out("[WasmHost] cmd: " + command);
            return true;
        }

        public int WriteSenseSnapshot(Span<byte> buffer)
        {
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
                if (message == null || message.Length > MaxChatMessageLength)
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
    }
}
