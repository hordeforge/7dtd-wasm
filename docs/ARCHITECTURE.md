# Architecture

## Goals

Run third-party mods on a 7 Days to Die dedicated server without giving them
the game process. Mods become WebAssembly modules; the host is a small,
auditable embed that enforces hard limits and exposes a narrow game API.

## Components

```text
 7dtd dedicated server (Unity Mono, net48, 20 TPS main loop)
  └─ Mods/1_HordeForge_WasmHost/            (net48, in-game, trusted)
      ├─ HordeForge.GameBridge.dll          ModApi, tick hook, console cmds
      ├─ HordeForge.WasmHost.dll            embeddable host (netstandard2.0)
      ├─ Wasmtime.dll                       managed binding (official)
      └─ Native/libwasmtime.so              native engine (per platform)

 Mods/Wasm/<id>/module.wasm                 guest modules (untrusted)
 Mods/Wasm/<id>/wasm-mod.toml               that mod's limits and settings
 Mods/Wasm/<id>/config.toml                 that mod's own config, served to
                                             the guest verbatim (zdtd.config)
 Mods/Wasm/wasm.toml                        shared limits and settings
```

## Host library (HordeForge.WasmHost)

Owns one Wasmtime engine and linker per host instance, and one store per
loaded module (unload disposes that module's store, so reload cycles do not
retain old instances). Single-threaded by design: call it only from the game
main loop.

- `WasmModHost` builds the engine with `WithFuelConsumption(true)`, a static
  memory ceiling, and a bounded wasm stack; wires WASI preview 1 (stdout and
  stderr discarded by default, no preopens, empty env); defines the
  `hordeforge` host API plus the `zdtd` compatibility module; and registers
  modules by id.
- `LoadModule` validates the module size, the declared memory maximum, and
  the export signatures before instantiation. Any failure throws
  `WasmModLoadException` with a specific reason and leaves the host intact.
- `DispatchTick` walks loaded modules in load order; each call gets a fresh
  fuel budget and returns a `ModRunResult` (Ok, Trap, FuelExhausted, Error).
  A bad module never stops the loop.
- `WasmMod` wraps one instance and its exports and keeps per-module counters
  (total calls, traps, fuel exhausted, total fuel consumed).

Why fuel over wall-clock: fuel is deterministic and cannot be fooled by host
scheduling; a guest either finishes within its budget or is stopped at it.
The 50 ms tick budget of the dedicated server is the reason the default
budget is 1,000,000 instructions per call: a burning guest costs at most a
few milliseconds per tick, repeatedly, while healthy guests cost nothing
measurable.

## Guest modules

Guests are `wasm32-wasip1` cdylibs; the reference toolchain is Rust, with
C (via `zig cc`) and Zig guests covered alongside it in
[docs/GUEST_AUTHORS.md](GUEST_AUTHORS.md). They export `on_enable`,
`on_tick`, and optionally `on_shutdown` and `on_player_join`, and import
the `hordeforge` host API. String arguments are (pointer, length) pairs into
the guest's own memory; the host reads them only within the given range and
never holds a reference across calls.

The Rust toolchain lives inside the repo (`.cargo/`, `.rustup/`) so guest
builds are reproducible and nothing is installed system-wide. The shared
`.cargo/config.toml` pins `--max-memory=33554432` (32 MiB) and a 1 MiB stack
for every guest, which keeps modules inside the host caps by construction.

## Bridge (GameBridge, net48)

- `ModApi.InitMod` gates on `GameManager.IsDedicatedServer`, bootstraps the
  native library, starts the host, patches `GameManager.Update` and
  `GameManager.RequestToSpawnPlayer`, and logs. Every step is fail soft.
- `GameTickHook` is a Harmony postfix on `GameManager.Update` that calls
  `BridgeHost.Tick()`, which dispatches with the bridge's own monotonic
  counter (`GameTimer.Instance.ticks` reads 0 on the dedicated server, and
  the hook runs once per game tick at 20 TPS). A second Harmony postfix on
  `GameManager.RequestToSpawnPlayer` (see Hooks/PlayerSpawnHook) forwards
  player joins to guests that export `on_player_join`.
- `GameHostApi` implements the ABI over live game services: log via the game
  logger (rate capped per module), world time via `GameManager.Instance.World.GetWorldTime()`,
  chat via `ChatMessageServer(..., EChatType.Global, ..., EMessageSender.Server,
  GeneratedTextManager.BbCodeSupportMode.NotSupported)` (rate capped globally),
  settings from `Mods/Wasm/wasm.toml` plus each mod's `wasm-mod.toml`
  ([docs/CONFIG.md](CONFIG.md); shared settings re-read on change).
- `CmdWasm` implements the V3 console command contract
  (`getCommands()`, `getDescription()`, `getHelp()`, `Execute(List<string>,
  CommandSenderInfo)`) with subcommands list, load, reload, unload, status.
- Threading: tick and player-join dispatch run on the game main loop, but
  console commands execute on the telnet/console thread. Every
  `BridgeHost` entry point therefore serializes on one internal gate so
  the single-threaded host library is never touched from two threads at
  once (a mid-dispatch unload would corrupt the load-order walk, and no
  store may be instantiated into while a guest call runs). The
  gate can pause a console command until the current dispatch returns;
  both sides are bounded by fuel and module size caps.

## Game API verification (tools/targetcheck)

The bridge only compiles against a real server install, and game targets
change silently on Steam patches. `targetcheck` reads `Assembly-CSharp.dll`
and `LogLibrary.dll` metadata (System.Reflection.Metadata, no code
execution) and verifies every member the bridge touches, including method
signatures and enum members. It also reports the detected game version.
`make bridge-check` must pass before the bridge is trusted.

## Evolution path

1. In-game acceptance ran in a docker container (docs/ACCEPTANCE.md);
   still unproven: Windows native loading and long-soak behavior.
   Per-mod manifests are already implemented (see docs/ABI.md).
2. Boot payload for init, richer host API (entities, players, world events)
   with WIT-style ABI versioning.
3. ABI stability pass: version the export names and document a compatibility
   policy before anything is called stable.
