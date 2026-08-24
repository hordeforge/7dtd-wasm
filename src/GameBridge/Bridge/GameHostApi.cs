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
        }

        /// <summary>Per-module log rate limiter; exposed for "wasm status".</summary>
        public GuestRateLimiter RateLimiter { get; }

        /// <summary>Per-module chat rate limiter; exposed for "wasm status".</summary>
        public GuestRateLimiter ChatLimiter { get; }

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
            string line = "[" + source + "] " + message;
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
            catch (Exception)
            {
                return 0L;
            }
        }

        public bool TryGetSetting(string modId, string key, out string value)
        {
            return _settings.TryGetSetting(modId, key, out value);
        }

        public bool TryQueueCommand(string command)
        {
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
            // Stage 2: no cover/path queries; the brain falls back to plain
            // movement when the host has no answer.
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
                // The game does not rate limit ChatMessageServer on its own,
                // so the bridge does: a guest spamming chat must not flood
                // the global channel (observed live in the acceptance run).
                if (!ChatLimiter.TryWrite("chat", out _))
                {
                    return false;
                }
                // Verified signature (V3): ChatMessageServer(ClientInfo, EChatType, int, string, List<int>, EMessageSender, BbCodeSupportMode)
                game.ChatMessageServer(null, EChatType.Global, -1, message, null, EMessageSender.Server, GeneratedTextManager.BbCodeSupportMode.NotSupported);
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
