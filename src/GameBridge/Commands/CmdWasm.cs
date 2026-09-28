using System.Collections.Generic;
using HordeForge.GameBridge.Bridge;
using HordeForge.WasmHost.Registry;

namespace HordeForge.GameBridge.Commands
{
    /// <summary>
    /// Console command "wasm" with subcommands:
    ///   wasm list      loaded module ids, one per line
    ///   wasm load      (re)scan Mods/Wasm and load new modules
    ///   wasm reload &lt;id&gt;  reload one module from disk
    ///   wasm unload &lt;id&gt;  unload one module (runs its shutdown export)
    ///   wasm status    host health and per-module counters (the default)
    ///   wasm help      the same list this class documents
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
            return "wasm list\nwasm load\nwasm reload <id>\nwasm unload <id>\nwasm status (default)\nwasm help";
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            string sub = _params.Count > 0 ? _params[0].ToLowerInvariant() : "status";
            // An argument past the subcommand's own is dropped by every
            // case below, so "wasm reload trap 3" and "wasm list now" would
            // both read as a call that did what was asked. Only reload and
            // unload take a second word.
            int maxArgs = sub == "reload" || sub == "unload" ? 2 : 1;
            if (_params.Count > maxArgs)
            {
                Output("wasm " + sub + " takes no more than " +
                       (maxArgs - 1) + " argument(s)");
                Output(Usage());
                return;
            }
            switch (sub)
            {
                case "list":
                    foreach (string line in BridgeHost.ModuleIds())
                    {
                        Output(line);
                    }
                    break;

                case "load":
                    int loaded = BridgeHost.LoadAllModules();
                    Output("loaded " + loaded + " new module(s)");
                    break;

                case "reload":
                    if (_params.Count < 2)
                    {
                        Output("usage: wasm reload <id>");
                        break;
                    }
                    // The id is echoed back to the console and telnet clients;
                    // clean it like log text so control characters typed at
                    // the console cannot drive terminals.
                    Output(BridgeHost.Reload(_params[1]) ? "reloaded " + TextSanitizer.Clean(_params[1]) : "reload failed or module not found: " + TextSanitizer.Clean(_params[1]));
                    break;

                case "unload":
                    if (_params.Count < 2)
                    {
                        Output("usage: wasm unload <id>");
                        break;
                    }
                    Output(BridgeHost.Unload(_params[1]) ? "unloaded " + TextSanitizer.Clean(_params[1]) : "not loaded: " + TextSanitizer.Clean(_params[1]));
                    break;

                case "status":
                    foreach (string line in StatusLines("status:"))
                    {
                        Output(line);
                    }
                    break;

                case "help":
                    Output(Usage());
                    break;

                default:
                    // Not the status dump: a mistyped subcommand that prints a
                    // full report reads like it did what was asked.
                    Output("unknown subcommand: " + TextSanitizer.Clean(sub));
                    Output(Usage());
                    break;
            }
        }

        private string Usage()
        {
            return "usage: " + getHelp().Replace("\n", "\n       ");
        }

        private static void Output(string line)
        {
            SingletonMonoBehaviour<SdtdConsole>.Instance.Output(line);
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
