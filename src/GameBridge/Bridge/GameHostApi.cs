using System;
using System.Collections.Generic;
using HordeForge.WasmHost.Abi;
using HordeForge.WasmHost.Core;
using HordeForge.WasmHost.Registry;

namespace HordeForge.GameBridge.Bridge
{
    /// <summary>
    /// Bridges the host ABI to the live game. Implements IGameHostApi so
    /// guest calls (log, get_world_time, get_setting, send_chat) reach real
    /// game services. All methods are defensive: on a dedicated server
    /// without a loaded world they degrade to defaults instead of throwing.
    ///
    /// Every method here runs on a guest import, so the raw config cache is
    /// the one piece of unsynchronized state this type owns: a register from
    /// a module load concurrent with a guest's config import would race on
    /// the dictionary. It has its own lock, never held across the servant or
    /// the game.
    /// </summary>
    public sealed class GameHostApi : IGameHostApi
    {
        private readonly WasmSettingsProvider _settings;
        private readonly BotServant _servant;
        // Prefix the host composes its per-module log source tag from, kept
        // here so a module's log window can be dropped on the same unload
        // that drops its config; see ForgetModule. The tag itself comes from
        // WasmModHost.LogSourceFor, so the key dropped here is the key the
        // host wrote to.
        private readonly string _logSourcePrefix;
        // Per-mod raw config (config.toml) cache, registered at module load
        // and invalidated on reload; a guest looping on the config import
        // must not stat the disk at call rate.
        private readonly Dictionary<string, string> _rawConfigs = new Dictionary<string, string>(StringComparer.Ordinal);

        // Guards the config cache only. The servant and the limiters guard
        // their own state, so this lock is never held while calling them and
        // the order BridgeHost.Gate -> this -> servant is one way.
        private readonly object _gate = new object();

        /// <summary>
        /// Creates the API. <paramref name="clockMs"/> is the millisecond
        /// clock every cap below measures its one-second window against
        /// (BridgeHost.ClockMs is the process default), so a driver that
        /// steps its own time caps and releases output on that time instead
        /// of on wall time. <paramref name="logSourcePrefix"/> is the
        /// host's own prefix, the same value WasmModHost prepends to a mod
        /// id to form a log source tag.
        /// </summary>
        public GameHostApi(WasmSettingsProvider settings, BotServant servant, string logSourcePrefix, Func<int>? clockMs = null)
        {
            _settings = settings;
            _servant = servant;
            _logSourcePrefix = logSourcePrefix;
            Func<int> clock = clockMs ?? (() => Environment.TickCount);
            // Each limiter carries its own cap from construction; the
            // per-purpose constants cannot drift from their call sites.
            LogLimiter = new GuestRateLimiter(GuestRateLimiter.MaxLinesPerSecond, clock);
            ChatLimiter = new GuestRateLimiter(GuestRateLimiter.MaxLinesPerSecond, clock);
            CommandLimiter = new GuestRateLimiter(GuestRateLimiter.MaxCommandsPerSecond, clock);
            SenseLimiter = new GuestRateLimiter(GuestRateLimiter.MaxSensePerSecond, clock);
            WorldTimeErrorLimiter = new GuestRateLimiter(GuestRateLimiter.MaxLinesPerSecond, clock);
            ChatRejectLimiter = new GuestRateLimiter(GuestRateLimiter.MaxLinesPerSecond, clock);
            ConfigErrorLimiter = new GuestRateLimiter(GuestRateLimiter.MaxLinesPerSecond, clock);
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
        /// Per-module cap on the "chat rejected" log line. The rejection
        /// itself is bounded by the guest's fuel budget, not by the chat
        /// limiter (an oversized or capped message is refused before it
        /// reaches the chat check), so a guest looping on the import would
        /// otherwise write server log lines at fuel rate. Exposed for
        /// "wasm status".
        /// </summary>
        public GuestRateLimiter ChatRejectLimiter { get; }

        /// <summary>
        /// Per-module cap on the "config.toml unreadable" log line. A mod
        /// whose config file is present but unusable (permissions, oversize,
        /// not UTF-8) gets an empty config served, and a guest looping on the
        /// import would otherwise report it at fuel rate. Exposed for
        /// "wasm status".
        /// </summary>
        public GuestRateLimiter ConfigErrorLimiter { get; }

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
                if (dropped % GuestRateLimiter.SuppressedReportEvery == 1)
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
                return WorldTime.ToAbi(game.World.GetWorldTime());
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
                else if (dropped % GuestRateLimiter.SuppressedReportEvery == 1)
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
        ///
        /// An empty registration is ambiguous: a module that ships no
        /// config.toml and a module whose config.toml could not be read at
        /// load both arrive here as an empty string, and only the second one
        /// is worth retrying. So an empty registration is resolved against the
        /// same trees the loader read through (one lookup per load) and is
        /// dropped when the file is there: the module then falls through to
        /// <see cref="TryGetRawConfig"/>, which reports the read failure and
        /// retries on the next call. Caching it here would remember a locked
        /// or mid-write file as absent for the life of the server.
        /// </summary>
        public void RegisterConfig(string modId, string content)
        {
            lock (_gate)
            {
                if (content.Length == 0 && HasConfigFile(modId))
                {
                    return;
                }
                _rawConfigs[modId] = content;
            }
        }

        /// <summary>
        /// True when the module's config.toml resolves to a file, through the
        /// same multi-root trees the loader and the config fallback use.
        /// </summary>
        private static bool HasConfigFile(string modId)
        {
            return ModId.IsValid(modId) && BridgeHost.ResolveModuleFile(modId, "config.toml").Length > 0;
        }

        /// <summary>Drops a module's cached config; called on unload and before reload.</summary>
        public void UnregisterConfig(string modId)
        {
            lock (_gate)
            {
                _rawConfigs.Remove(modId);
            }
        }

        /// <summary>
        /// Drops every per-module cap window belonging to one module id:
        /// its log, SimCommand, sense, chat-rejection, and config-read-failure
        /// windows, and its dropped totals in "wasm status". Called on
        /// unload and before reload, so a fresh
        /// load generation starts with a full budget instead of inheriting
        /// the previous generation's window (a module reloaded inside the
        /// same second the old one saturated its cap would be throttled
        /// before it emitted a line). The shared tags ("chat",
        /// "world_time") are not module ids and are left alone.
        /// </summary>
        public void ForgetModule(string modId)
        {
            // The tag the host composes for this module's guest log lines,
            // named by the host itself: recomposing it here would drop a
            // window under a key the limiter never wrote to, and a module
            // reloaded inside the second its previous generation saturated
            // the cap would start inside that window.
            LogLimiter.ForgetSource(WasmModHost.LogSourceFor(_logSourcePrefix, modId));
            CommandLimiter.ForgetSource(modId);
            SenseLimiter.ForgetSource(modId);
            // The two caps keyed by the module id itself, so a reloaded
            // instance does not start inside the window its previous
            // generation saturated and have its first lines dropped.
            ChatRejectLimiter.ForgetSource(modId);
            ConfigErrorLimiter.ForgetSource(GuestRateLimiter.SourceKey("config/", modId));
        }

        /// <summary>
        /// Serves the calling mod's config.toml verbatim (the zdtd config
        /// import). The host never parses it: each guest owns its format.
        /// Returns false when the mod has no config file, so the guest keeps
        /// its built-in defaults (zdtd: 0 = none).
        /// </summary>
        public bool TryGetRawConfig(string modId, out string content)
        {
            lock (_gate)
            {
                if (_rawConfigs.TryGetValue(modId, out content))
                {
                    return content.Length > 0;
                }
                // Not registered: a module loaded outside the normal scan, or
                // one whose load-time read failed (RegisterConfig drops that
                // entry on purpose). Read the file once and remember the
                // outcome so a guest loop on the config import does not hit
                // the disk per call.
                // Resolved through the same multi-root trees as the loader, so
                // a modlet-carried module finds its config too. The read happens
                // under the lock because the alternative is two threads
                // missing the cache and both reading; it runs once per module
                // and the file is size capped, so the stall is bounded.
                content = string.Empty;
                if (!ModId.IsValid(modId))
                {
                    return false;
                }
                string path = BridgeHost.ResolveModuleFile(modId, "config.toml");
                if (path.Length == 0)
                {
                    _rawConfigs[modId] = content;
                    return false;
                }
                if (ManifestFiles.TryRead(path, out string raw, out string failureReason))
                {
                    content = raw;
                    _rawConfigs[modId] = content;
                    return true;
                }
                // The file exists but could not be served. The guest reads
                // 0 ("no config") either way, so dropping the reason here
                // would leave the mod running on defaults with nothing in
                // the log to explain why. Nothing is cached on this path:
                // a file that is locked, oversize, or mid-write is retried
                // on the next call instead of being remembered as absent
                // for the life of the server.
                ReportConfigReadFailure(modId, failureReason);
                return false;
            }
        }

        /// <summary>
        /// Reports a config.toml that is present but could not be read,
        /// bounded like the other guest output paths. Shared with
        /// BridgeHost's load-time read, which registers the same file.
        /// </summary>
        internal void ReportConfigReadFailure(string modId, string reason)
        {
            if (!ConfigErrorLimiter.TryWrite(GuestRateLimiter.SourceKey("config/", modId), out long dropped))
            {
                if (dropped % GuestRateLimiter.SuppressedReportEvery == 1)
                {
                    global::Log.Out("[WasmHost] suppressed " + dropped + " config read failure log(s) from guest " +
                                    TextSanitizer.Clean(modId));
                }
                return;
            }
            // The reason quotes the file path and the IO message, so it is
            // cleaned like every other mod-derived line.
            global::Log.Warning("[WasmHost] config.toml of " + TextSanitizer.Clean(modId) + " is unusable (" +
                                TextSanitizer.Clean(reason) + "); the guest gets no config and its defaults");
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
            if (_servant.TryQueue(modId, command, out bool handled))
            {
                return true;
            }
            if (handled)
            {
                // A servant command that failed mid-execution must reach the
                // guest as rejected (queue -> -1), not as accepted.
                return false;
            }
            if (!SendChat(modId, command))
            {
                if (ChatRejectLimiter.TryWrite(modId, out long dropped))
                {
                    global::Log.Out("[WasmHost] cmd (chat rejected): " + command);
                }
                else if (dropped % GuestRateLimiter.SuppressedReportEvery == 1)
                {
                    global::Log.Out("[WasmHost] suppressed " + dropped + " chat rejection log(s) from guest " +
                                    TextSanitizer.Clean(modId));
                }
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
            return _servant.WriteSense(modId, buffer);
        }

        public string? TryQuery(string request)
        {
            // Stage 3: cover/path queries are not wired yet; the brain falls
            // back to plain movement when the host has no answer.
            return null;
        }

        public bool SendChat(string message)
        {
            // The send_chat import carries no mod id (IGameHostApi.SendChat
            // takes the message alone), so this call cannot name the module
            // and the failure line below says so instead of guessing. The
            // paths that do have the id in hand pass it.
            return SendChat(null, message);
        }

        /// <summary>
        /// <see cref="SendChat(string)"/> with the calling module's id
        /// attached, for the paths that have one in hand.
        /// </summary>
        private bool SendChat(string? modId, string message)
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
                // failure must stay visible in the server log, and a log
                // line naming no module is undiagnosable when several guests
                // are loaded. Rate is bounded by the chat limiter check above.
                // The id is guest-derived, so it is cleaned like every other
                // mod-derived line.
                string from = modId != null && modId.Length > 0
                    ? " from guest " + TextSanitizer.Clean(modId)
                    : " (no mod id on this call)");
                global::Log.Warning("[WasmHost] send_chat failed" + from + ": " + ex.Message);
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
