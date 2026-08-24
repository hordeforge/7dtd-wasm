# Security: running untrusted mods in a game server

## Threat model

The dedicated server operator installs third-party mods. Mods are treated as
**hostile**: they may try to read server memory, crash the server, burn CPU,
exhaust memory, or interfere with other mods. The host exists so a mod can
only do what the operator explicitly allows.

The game process itself is trusted. The bridge mod is trusted. Guest modules
are not.

## What the sandbox guarantees

| Attack | Defense | Enforced at |
|---|---|---|
| Infinite loop / CPU burn | Per-call fuel budget (`FuelPerCall`, default 1,000,000 instructions) | Every call (init, tick, shutdown) |
| Memory exhaustion | Declared memory maximum checked against `StaticMemoryMaximumBytes` (default 32 MiB); modules without a declared maximum are rejected | Module load |
| Giant module file | `MaxModuleSizeBytes` (default 1 MiB) | Module load |
| Reading host memory | Guests see only their own linear memory; no pointers into host space are ever exposed | Runtime (engine) |
| Reaching game objects | No game types, Reflection, or file APIs reachable from wasm; only the ABI imports | Design |
| Trap / crash | Traps return `ModRunResult` with a trap code; the host and other modules keep running | Every call |
| Host-API abuse (spam chat) | The game does NOT rate limit ChatMessageServer on its own (observed live); the bridge caps guest chat globally at 10 messages/second and counts drops | Bridge |
| Log flooding | Per-module rate cap (default 10 lines/second); excess lines are dropped and counted, visible in `wasm status` | Bridge |
| Stack exhaustion | Wasm caller stack bounded (`MaximumStackBytes`, default 1 MiB) | Engine |

## What is NOT sandboxed

- **The bridge itself**: a bug in `1_HordeForge_WasmHost` runs with game
  privileges. It is small, reviewed, and all its game API targets are
  validated by `tools/targetcheck`, but it is still game-process code.
- **The Wasmtime engine**: the host inherits Wasmtime's security model. Keep
  the `Wasmtime` NuGet package current; upstream treats security seriously
  and this host should track releases.
- **Settings file**: `wasm-settings.txt` is readable by any guest (all guests
  share it). Do not put secrets there; secrets belong in serverconfig via the
  normal env-only rule.

## Operational notes

- Any C# code mod forces the server to run with EAC off (`-noeac`). This
  project is no exception. Only XML-only mods keep EAC enabled.
- Guests are single-threaded and driven from the game main loop. Do not call
  the host from other threads; the engine store is not thread-safe.
- Load new modules only while the server is stopped, or use `wasm reload <id>`
  on a live server. A module that traps every tick cannot take the server
  down, but it will spam the log, so unload it via `wasm unload <id>`.

## Reporting

This is an experiment. If you believe a guest can escape the sandbox, report
it to the repository maintainers with a minimal reproducer before publishing
it. Do not test escape payloads on servers you do not administer.
