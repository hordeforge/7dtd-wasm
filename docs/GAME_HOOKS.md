# In-game integration (GameBridge)

## What the bridge does

- Loads once per process: `ModApi.InitMod` gates on
  `GameManager.IsDedicatedServer`, and both the `BridgeHost.Start` call and
  the Harmony patch pass are one-shot, so a second `InitMod` (a server-side
  mod reload) is ignored, in the patch case without a log line.
- Only acts on dedicated servers (`GameManager.IsDedicatedServer`); on
  clients it logs a note and exits.
- Bootstraps the native Wasmtime library from `<modlet>/Native/` before
  any Wasmtime type is touched. On Windows it prepends `Native/` to
  `PATH` (consulted on every library load). On Linux the loader captures
  `LD_LIBRARY_PATH` at process start, so the server must be started with
  `<modlet>/Native/` already on it; on macOS the same holds for
  `DYLD_LIBRARY_PATH`, and the probe and the logged instruction name
  `libwasmtime.dylib` there. The bridge probes resolution at init and logs
  exact instructions when the engine is not resolvable (see
  docs/ACCEPTANCE.md for the working acceptance setup).
- Starts the host, scans `<install>/Mods/Wasm/<id>/module.wasm` and then
  each staged modlet's own `Wasm/<id>/module.wasm` (the primary tree wins
  per id, see docs/CONFIG.md) for guest modules, initializes them, and
  patches `GameManager.Update`.

## Tick dispatch

`GameTickHook.Postfix` runs after `GameManager.Update` on dedicated servers
(once per game tick, 20 TPS) and calls `BridgeHost.Tick()`, which dispatches
`tick` to every loaded guest with the bridge's own monotonic counter as the
tick number. `GameTimer.Instance.ticks` reads 0 on the dedicated server
(observed in the acceptance run), so the bridge does not use it. The postfix
is try/caught so a host failure never breaks the game loop.

### What the operator sees

Every tick dispatch is timed (one sample per tick, through
`BridgeHost.Timer`, a `HordeForge.WasmHost.Core.MonotonicTimer` over the
process `Stopwatch` by default, and the same timer measures each guest call
inside the host) and rolled into a one-minute window by
`HordeForge.WasmHost.Core.TickTelemetry`:

| Signal | Level | When |
|---|---|---|
| heartbeat | `Out` | every 1200 ticks (60 s at 20 TPS): tick number, module count, last/avg/max dispatch cost, failure and slow-tick totals, and the guests that failed since the previous heartbeat |
| slow dispatch | `Warning` | a dispatch over 25 ms, half a 20 TPS frame, naming the guest whose last call cost the most; capped at one per second, `tick` failure logs are capped separately |
| tick failure | `Warning` | a guest that trapped, exhausted fuel, or errored on `on_tick`, naming the tick, the mod, and the fuel the call consumed |
| join failure | `Warning` | a guest whose `on_player_join` trapped, exhausted fuel, or errored |
| shutdown summary | `Out` | totals for the whole run, printed only when an embedder calls `BridgeHost.Shutdown()`; nothing in the mod calls it, so a live server never prints it |

Per-guest cost is measured on every call (`WasmMod.LastCallMs`), so the
slow-dispatch warning names the guest that spent the frame rather than only
the total. It is read on the warning path, which is capped at one per
second, never on the tick rate.

A failure caught on the host side (world time, chat, the sense snapshot,
bot spawn and despawn, the glide buff) is logged as the exception, so the
log carries its type and stack and not only its message. Each of those
lines is rate capped, so the extra lines cost one write per second at
most. Diagnostics that quote guest- or file-supplied text (manifest and
parser errors, trap details) keep the control-character filter and log the
message only, because a stack would carry that text unsanitized.

Silence from the bridge is therefore a fault, not the healthy state: a
missing heartbeat means the tick hook stopped firing. The same summary is
printed by `wasm status` on demand, alongside the per-module call, trap,
fuel-exhausted, error, and total-fuel counters and the limits in force (fuel
per call, memory ceiling, module size cap, guest stdio), read back from the
running host and logged at start. Guests that spam are rate capped; the
running drop totals surface in `wasm status` and every 100th dropped line is
logged.

## Verified game API surface (via tools/targetcheck; V3.1.0 b14 acceptance and V3.2.0 b9 playtest)

| Member | Verified signature |
|---|---|
| `GameManager.Update` | `void()` instance |
| `GameManager.IsDedicatedServer` | static property |
| `GameManager.Instance` | static field |
| `GameManager.World` | instance property |
| `GameManager.ChatMessageServer` | `void(ClientInfo, EChatType, int, string, List<int>, EMessageSender, BbCodeSupportMode)` |
| `GameManager.RequestToSpawnPlayer` | `void(ClientInfo, int, PlayerProfile, int)` |
| `ClientInfo.playerName`, `ClientInfo.entityId` | instance fields |
| `ConsoleCmdAbstract.getCommands/getDescription/getHelp/Execute` | `string[]()`, `string()`, `string()`, `void(List<string>, CommandSenderInfo)` |
| `SdtdConsole.Output` | `void(string)` instance |
| `SingletonMonoBehaviour<T>.Instance` | static field |
| `World.Entities` | instance field |
| `World.GetEntity` | `Entity(int)` |
| `World.SpawnEntityInWorld` | `void(Entity)` |
| `World.GetWorldTime` | `ulong()` |
| `Entity.entityId`, `Entity.position` | instance fields |
| `Entity.SetPosition` | `void(Vector3, bool)` |
| `Entity.SetRotation` | `void(Vector3)` |
| `EntityAlive.Health` | instance property |
| `EntityAlive.IsDead` | `bool()` |
| `EntityAlive.SetDead` | `void()` |
| `EntityAlive.DamageEntity` | `int(DamageSource, int, bool, float)` |
| `EntityAlive.equipment`, `EntityAlive.Buffs` | instance fields |
| `EntityBuffs.AddBuff` | `BuffStatus(string, int, bool, bool, float)` |
| `EntityBuffs.RemoveBuff` | `void(string, int, bool)` |
| `EntityBuffs.HasBuff` | `bool(string)` |
| `Equipment.GetItems` | `ItemValue[]()` |
| `ItemValue.IsEmpty` | `bool()` |
| `ItemValue.ItemClass` | instance property |
| `ItemClass.HasAnyTags` | ``bool(FastTags`1<Global>)`` |
| `EntityFactory.CreateEntity` | static `Entity(int, Vector3, Vector3)` |
| `EntityClass.FromString` | static `int(string)` |
| `Log` (LogLibrary) | static `Out`, `Warning`, `Error` |
| `EChatType.Global`, `EMessageSender.Server`, `GeneratedTextManager.BbCodeSupportMode.NotSupported` | enum members |

The `World`, `Entity`, `EntityAlive`, `EntityFactory`, `EntityClass`,
`EntityBuffs`, `Equipment`, `ItemValue`, and `ItemClass` rows back the bot
servant (`Bridge/BotServant.cs`).

A row marked "field" is checked as a field or a property, whichever the game
exposes; a row marked "property" is checked as a property.

Console output goes through `SingletonMonoBehaviour<SdtdConsole>.Instance.Output(...)`.
This differs from pre-V3 guides that used `SdtdConsole.Instance`, which no
longer exists on V3.

## Console commands

`wasm list`, `wasm load`, `wasm reload <id>`, `wasm unload <id>`,
`wasm status` (the default when no subcommand is given; the full report:
limits, per-module counters, dropped line summaries), `wasm help`. `help
wasm` in the game prints the same list. An unknown subcommand, and any
argument past the ones a subcommand takes, print the same usage list rather
than falling through to the status report, so a typo does not read like a
successful command.

Each subcommand says what it did, at the console the operator typed it in:

- `wasm list` prints the loaded module ids, one per line, so the next
  command can copy one. When nothing is loaded it says how to load the
  first one.
- `wasm load` names the modules it loaded and prints a `skipped` line, with
  the reason, for every module or tree the scan refused. A module sitting
  in `Mods/Wasm` that never appears is explained there instead of only in
  the server log.
- `wasm reload <id>` and `wasm unload <id>` print the reason a load was
  refused (no `module.wasm`, a malformed `wasm-mod.toml`, a module over the
  size cap, an id that is not loaded) and then the ids that are loaded, so
  a mistyped id is corrected from the console. An unload whose shutdown
  export failed says so on the same command; the module is gone either way.

## Player join events

The bridge patches `GameManager.RequestToSpawnPlayer` (verified:
`void(ClientInfo, int, PlayerProfile, int)`) with a Harmony postfix
(`Hooks/PlayerSpawnHook`). When a player requests to spawn into the world,
the handler reads `ClientInfo.playerName` and dispatches it to every guest
that exports the optional `on_player_join` handler. A join whose
`ClientInfo.playerName` is empty is dropped before both the log line and the
dispatch, so an empty name reads as silence, not as a guest that saw the
join. The join log line records the entity id and that the dispatch
happened, not the name: a name in the server log outlives the session and
identifies a player.

Hook history (found live in the acceptance run): `GameManager.OnClientSpawned`
does not fire on the dedicated server, and neither does the
`PlayerSpawnedInWorld` method for remote joins; `RequestToSpawnPlayer` is
the server-side entry point the game itself logs on every join. Note that
the hook also fires on respawns, not only on first join; guests that care
should track state across calls.

## Settings

Guest settings are TOML: shared `<install>/Mods/Wasm/wasm.toml` (or, when
that file is absent, the first `wasm.toml` in a staged modlet's `Wasm/`
tree) plus each mod's `wasm-mod.toml`. The schema, defaults, and resolution
order are owned by [docs/CONFIG.md](CONFIG.md). Operational notes: the
bridge re-reads the shared file when its last write time and its length
both change (a copy or restore that preserves the timestamp is still
picked up), probed at most every 500 ms from `get_setting` traffic, so
edits apply without a restart; the settings files are read by the host and
served to guests, so do not put secrets in them.

## Known gaps

- Live acceptance succeeded in a docker container (fresh steamcmd install,
  V 3.1.0 b14); the native install on this machine crashes at boot and was
  not used. See `evidence/acceptance-1/` and `docs/ACCEPTANCE.md`. The
  parachute module was later driven end to end on a V3.2.0 b9 live server
  (`evidence/playtest-1/`).
- Guest log and chat rate capping are bridge code; the log cap was
  exercised in the acceptance run (drop counter in `wasm status`), not by
  host unit tests.
