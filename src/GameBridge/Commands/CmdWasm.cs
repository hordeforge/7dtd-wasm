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
    /// Every subcommand says what it did: which ids loaded, which were
    /// skipped and why, and which ids exist after a mistyped or unloaded id,
    /// so an operator reading the console does not have to open the server
    /// log to find out whether the command took effect.
    /// </summary>
    public class CmdWasm : ConsoleCmdAbstract
    {
        private const string NotStartedLine =
            "the host is not started; see the server log for why the start was refused";

        /// <summary>
        /// One line per subcommand, description included: the game's
        /// "help wasm" and "wasm help" print the same block, so an operator
        /// who learned the syntax from one has it in the other.
        /// </summary>
        private static readonly string[] SubcommandHelp =
        {
            "wasm list        list the loaded module ids",
            "wasm load        (re)scan Mods/Wasm and load new modules",
            "wasm reload <id> reload one module from disk",
            "wasm unload <id> unload one module (runs its shutdown export)",
            "wasm status      host health and per-module counters (the default)",
            "wasm help        this list",
        };

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
            return string.Join("\n", SubcommandHelp);
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
                    ListModules();
                    break;

                case "load":
                    LoadModules();
                    break;

                case "reload":
                {
                    if (!RequireId(_params, "reload"))
                    {
                        break;
                    }
                    // The id is echoed back to the console and telnet clients;
                    // clean it like log text so control characters typed at
                    // the console cannot drive terminals.
                    string id = TextSanitizer.Clean(_params[1]);
                    if (BridgeHost.Reload(_params[1], out string reason))
                    {
                        Output("reloaded " + id);
                    }
                    else
                    {
                        Output("reload failed for " + id + ": " + reason);
                        OutputLoadedIds();
                    }
                    break;
                }

                case "unload":
                {
                    if (!RequireId(_params, "unload"))
                    {
                        break;
                    }
                    string target = TextSanitizer.Clean(_params[1]);
                    if (BridgeHost.Unload(_params[1], out string detail))
                    {
                        Output("unloaded " + target);
                        // The module is gone either way, so a failing
                        // shutdown export is a note on the same command
                        // rather than a log line the operator has to go read.
                        if (detail.Length > 0)
                        {
                            Output("note: the shutdown export of " + target + " failed: " + detail);
                        }
                    }
                    else
                    {
                        Output("unload failed for " + target + ": " + detail);
                        OutputLoadedIds();
                    }
                    break;
                }

                case "status":
                    if (!BridgeHost.Started)
                    {
                        Output("status:");
                        Output("  " + NotStartedLine);
                        break;
                    }
                    foreach (string line in StatusLines("status:"))
                    {
                        Output(line);
                    }
                    break;

                case "help":
                    OutputUsage();
                    break;

                default:
                    // Not the status dump: a mistyped subcommand that prints a
                    // full report reads like it did what was asked.
                    Output("unknown subcommand: " + TextSanitizer.Clean(sub));
                    OutputUsage();
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

        /// <summary>
        /// "wasm list" answers the one question it is asked: which ids are
        /// loaded, one per line, so the next command can copy one. The
        /// counters live in "wasm status", which is a different question.
        /// </summary>
        private static void ListModules()
        {
            if (!BridgeHost.Started)
            {
                Output(NotStartedLine);
                return;
            }
            List<string> ids = BridgeHost.LoadedModuleIds();
            if (ids.Count == 0)
            {
                Output("no modules loaded");
                Output("  copy a module into Mods/Wasm/<id>/ and run 'wasm load'");
                return;
            }
            foreach (string id in ids)
            {
                Output("  " + id);
            }
        }

        /// <summary>
        /// "wasm load" names what loaded and what did not. The refusals are
        /// on the console because that is where the operator is looking:
        /// a module that sits in Mods/Wasm and never appears is otherwise
        /// only explained in a log file they have to go and open.
        /// </summary>
        private static void LoadModules()
        {
            if (!BridgeHost.Started)
            {
                Output(NotStartedLine);
                return;
            }
            ModuleLoadScan scan = BridgeHost.LoadAllModules();
            if (scan.LoadedIds.Count == 0)
            {
                Output("no new modules found in " + TextSanitizer.Clean(BridgeHost.WasmRoot));
            }
            else
            {
                Output("loaded " + scan.LoadedIds.Count + " new module(s): " +
                       string.Join(", ", scan.LoadedIds));
            }
            foreach (string skipped in scan.Skipped)
            {
                Output("  skipped " + skipped);
            }
        }

        /// <summary>
        /// The loaded ids, as the next step after a command was refused for
        /// want of one. Empty list says how to get the first one.
        /// </summary>
        private static void OutputLoadedIds()
        {
            List<string> ids = BridgeHost.LoadedModuleIds();
            if (ids.Count == 0)
            {
                Output("  no modules are loaded; copy one into Mods/Wasm/<id>/ and run 'wasm load'");
                return;
            }
            Output("  loaded: " + string.Join(", ", ids));
        }

        /// <summary>
        /// A subcommand that takes an id, with the usage line the operator
        /// gets when they leave it out. False means the line was printed and
        /// the command is done.
        /// </summary>
        private static bool RequireId(List<string> _params, string sub)
        {
            if (_params.Count >= 2)
            {
                return true;
            }
            Output("usage: wasm " + sub + " <id>, where <id> is the module folder name under Mods/Wasm");
            return false;
        }

        private static void OutputUsage()
        {
            foreach (string line in SubcommandHelp)
            {
                Output(line);
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
