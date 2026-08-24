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
        private readonly WasmSettingsFile _settings;

        public GameHostApi(WasmSettingsFile settings)
        {
            _settings = settings;
            RateLimiter = new GuestLogRateLimiter();
        }

        /// <summary>Per-module log rate limiter; exposed for "wasm status".</summary>
        public GuestLogRateLimiter RateLimiter { get; }

        public void Log(string source, int level, string message)
        {
            if (!RateLimiter.TryWrite(source, out long dropped))
            {
                // Every 100th dropped line is logged so throttling is
                // visible without the log itself being flooded.
                if (dropped % 100 == 1)
                {
                    global::Log.Out("[WasmHost] dropped " + dropped + " log line(s) from guest " + source +
                                    " (rate cap " + GuestLogRateLimiter.MaxLinesPerSecond + "/s)");
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

        public bool TryGetSetting(string key, out string value)
        {
            return _settings.TryRead(key, out value);
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
                // Verified signature (V3): ChatMessageServer(ClientInfo, EChatType, int, string, List<int>, EMessageSender, BbCodeSupportMode)
                game.ChatMessageServer(null, EChatType.Global, -1, message, null, EMessageSender.Server, GeneratedTextManager.BbCodeSupportMode.NotSupported);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
