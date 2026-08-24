# In-game integration (GameBridge)

## What the bridge does

- Loads once per server start via `ModApi.InitMod`.
- Only acts on dedicated servers (`GameManager.IsDedicatedServer`); on
  clients it logs a note and exits.
- Bootstraps the native Wasmtime library from `<modlet>/Native/`
  (LD_LIBRARY_PATH on Linux, PATH on Windows) before any Wasmtime type is
  touched.
- Starts the host, scans `<install>/Mods/Wasm/<id>/module.wasm` for guest
  modules, initializes them, and patches `GameManager.Update`.

## Tick dispatch

`GameTickHook.Postfix` runs after `GameManager.Update` on dedicated servers
(once per game tick, 20 TPS) and calls `BridgeHost.Tick()`, which dispatches
`tick` to every loaded guest with `GameTimer.Instance.ticks` as the tick
number. The postfix is try/caught so a host failure never breaks the game
loop.

## Verified game API surface (V3.1.0, via tools/targetcheck)

| Member | Verified signature |
|---|---|
| `GameManager.Update` | `void()` instance |
| `GameManager.IsDedicatedServer` | static property |
| `GameManager.Instance` | static field |
| `GameManager.World` | instance property |
| `GameManager.ChatMessageServer` | `void(ClientInfo, EChatType, int, string, List<int>, EMessageSender, BbCodeSupportMode)` |
| `GameTimer.Instance` | static property |
| `GameTimer.ticks` | instance field |
| `ConsoleCmdAbstract.getCommands/getDescription/getHelp/Execute` | `string[]()`, `string()`, `string()`, `void(List<string>, CommandSenderInfo)` |
| `SdtdConsole.Output` | `void(string)` instance |
| `SdtdConsole.ExecuteSync` | `List<string>(string, ClientInfo)` |
| `SingletonMonoBehaviour<T>.Instance` | static field |
| `World.GetWorldTime` | `ulong()` |
| `Log` (LogLibrary) | static `Out`, `Warning`, `Error` |
| `EChatType.Global`, `EMessageSender.Server`, `GeneratedTextManager.BbCodeSupportMode.NotSupported` | enum members |

Console output goes through `SingletonMonoBehaviour<SdtdConsole>.Instance.Output(...)`.
This differs from pre-V3 guides that used `SdtdConsole.Instance`, which no
longer exists on V3.

## Console commands

`wasm list`, `wasm load`, `wasm reload <id>`, `wasm unload <id>`,
`wasm status`.

## Settings

Guest settings live in `<install>/Mods/Wasm/wasm-settings.txt`, one
`key: value` per line, `#` comments. The bridge re-reads the file when its
mtime changes, so edits apply without a restart. All guests share this file;
do not put secrets in it.

## Known gaps

- No live-server acceptance run yet: the dedicated server crashes at boot
  on this machine before any mod loads (environment issue; see
  `evidence/acceptance-1/README.md` and `docs/ACCEPTANCE.md`). The modlet
  compiles against this install and all game API targets are verified.
- Guest log rate capping is bridge code exercised in the acceptance run,
  not by unit tests.
