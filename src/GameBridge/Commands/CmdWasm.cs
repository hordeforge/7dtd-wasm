using System.Collections.Generic;
using HordeForge.GameBridge.Bridge;

namespace HordeForge.GameBridge.Commands
{
    /// <summary>
    /// Console command "wasm" with subcommands:
    ///   wasm list      list loaded modules
    ///   wasm load      (re)scan Mods/Wasm and load new modules
    ///   wasm reload &lt;id&gt;  reload one module from disk
    ///   wasm unload &lt;id&gt;  unload one module (runs its shutdown export)
    ///   wasm status    host health and per-module counters
    /// </summary>
    public class CmdWasm : ConsoleCmdAbstract
    {
        public override string[] getCommands()
        {
            return new[] { "wasm" };
        }

        public override string getDescription()
        {
            return "Manage the WebAssembly mod host";
        }

        public override string getHelp()
        {
            return "wasm list\nwasm load\nwasm reload <id>\nwasm unload <id>\nwasm status";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string sub = _params.Count > 0 ? _params[0].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "list":
                    foreach (string line in StatusLines("modules:"))
                    {
                        SingletonMonoBehaviour<SdtdConsole>.Instance.Output(line);
                    }
                    break;

                case "load":
                    int loaded = BridgeHost.LoadAllModules();
                    SingletonMonoBehaviour<SdtdConsole>.Instance.Output("loaded " + loaded + " new module(s)");
                    break;

                case "reload":
                    if (_params.Count < 2)
                    {
                        SingletonMonoBehaviour<SdtdConsole>.Instance.Output("usage: wasm reload <id>");
                        break;
                    }
                    // The id is echoed back to the console and telnet clients;
                    // clean it like log text so control characters typed at
                    // the console cannot drive terminals.
                    SingletonMonoBehaviour<SdtdConsole>.Instance.Output(BridgeHost.Reload(_params[1]) ? "reloaded " + TextSanitizer.Clean(_params[1]) : "reload failed or module not found: " + TextSanitizer.Clean(_params[1]));
                    break;

                case "unload":
                    if (_params.Count < 2)
                    {
                        SingletonMonoBehaviour<SdtdConsole>.Instance.Output("usage: wasm unload <id>");
                        break;
                    }
                    SingletonMonoBehaviour<SdtdConsole>.Instance.Output(BridgeHost.Unload(_params[1]) ? "unloaded " + TextSanitizer.Clean(_params[1]) : "not loaded: " + TextSanitizer.Clean(_params[1]));
                    break;

                default:
                    foreach (string line in StatusLines("status:"))
                    {
                        SingletonMonoBehaviour<SdtdConsole>.Instance.Output(line);
                    }
                    break;
            }
        }

        private static IEnumerable<string> StatusLines(string header)
        {
            yield return header;
            foreach (string line in BridgeHost.StatusLines())
            {
                yield return "  " + line;
            }
        }
    }
}
