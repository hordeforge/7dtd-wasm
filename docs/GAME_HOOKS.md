# In-game integration (GameBridge)

## What the bridge does

- Loads once per server start via `ModApi.InitMod`.
- Only acts on dedicated servers (`GameManager.IsDedicatedServer`); on
  clients it logs a note and exits.
- Bootstraps the native Wasmtime library from `<modlet>/Native/` before
  any Wasmtime type is touched. On Windows it prepends `Native/` to
  `PATH` (consulted on every library load). On Linux the loader captures
  `LD_LIBRARY_PATH` at process start, so the server must be started with
  `<modlet>/Native/` already on it; the bridge probes resolution at init
  and logs exact instructions when the engine is not resolvable (see
  docs/ACCEPTANCE.md for the working acceptance setup).
- Starts the host, scans `<install>/Mods/Wasm/<id>/module.wasm` for guest
  modules, initializes them, and patches `GameManager.Update`.

## Tick dispatch

`GameTickHook.Postfix` runs after `GameManager.Update` on dedicated servers
(once per game tick, 20 TPS) and calls `BridgeHost.Tick()`, which dispatches
`tick` to every loaded guest with the bridge's own monotonic counter as the
tick number. `GameTimer.Instance.ticks` reads 0 on the dedicated server
(observed in the acceptance run), so the bridge does not use it. The postfix
is try/caught so a host failure never breaks the game loop.

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

## Player join events

The bridge patches `GameManager.RequestToSpawnPlayer` (verified:
`void(ClientInfo, int, PlayerProfile, int)`) with a Harmony postfix
(`Hooks/PlayerSpawnHook`). When a player requests to spawn into the world,
the handler reads `ClientInfo.playerName` and dispatches it to every guest
that exports the optional `on_player_join` handler.

Hook history (found live in the acceptance run): `GameManager.OnClientSpawned`
does not fire on the dedicated server, and neither does the
`PlayerSpawnedInWorld` method for remote joins; `RequestToSpawnPlayer` is
the server-side entry point the game itself logs on every join. Note that
the hook also fires on respawns, not only on first join; guests that care
should track state across calls.

## Settings

Guest settings live in `<install>/Mods/Wasm/wasm-settings.txt`, one
`key: value` per line, `#` comments. The bridge re-reads the file when its
mtime changes, so edits apply without a restart. All guests share this file;
do not put secrets in it.

## Known gaps

- Live acceptance succeeded in a docker container (fresh steamcmd install);
  the native install on this machine crashes at boot and was not used. See
  `evidence/acceptance-1/` and `docs/ACCEPTANCE.md`.
- Guest log and chat rate capping are bridge code; the log cap was
  exercised in the acceptance run (drop counter in `wasm status`), not by
  host unit tests.
