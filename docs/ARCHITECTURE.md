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
 Mods/Wasm/wasm-settings.txt                shared guest settings
```

## Host library (HordeForge.WasmHost)

Owns one Wasmtime engine, one store, and one linker per host instance.
Single-threaded by design: call it only from the game main loop.

- `WasmModHost` builds the engine with `WithFuelConsumption(true)`, a static
  memory ceiling, and a bounded wasm stack; wires WASI preview 1 (stdout and
  stderr inherited, no preopens, empty env); defines the `hordeforge` host
  API; and registers modules by id.
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

Guests are `wasm32-wasip1` cdylibs built from Rust (see
[docs/GUEST_AUTHORS.md](GUEST_AUTHORS.md)). They export `hordeforge:mod/init`,
`hordeforge:mod/tick`, and optionally `hordeforge:mod/shutdown`, and import
the `hordeforge` host API. String arguments are (pointer, length) pairs into
the guest's own memory; the host reads them only within the given range and
never holds a reference across calls.

The Rust toolchain lives inside the repo (`.cargo/`, `.rustup/`) so guest
builds are reproducible and nothing is installed system-wide. The shared
`.cargo/config.toml` pins `--max-memory=33554432` (32 MiB) and a 1 MiB stack
for every guest, which keeps modules inside the host caps by construction.

## Bridge (GameBridge, net48)

- `ModApi.InitMod` gates on `GameManager.IsDedicatedServer`, bootstraps the
  native library, starts the host, patches `GameManager.Update`, and logs.
  Every step is fail soft.
- `GameTickHook` is a Harmony postfix on `GameManager.Update` that calls
  `BridgeHost.Tick()`, which dispatches with `GameTimer.Instance.ticks` as
  the game tick.
- `GameHostApi` implements the ABI over live game services: log via the game
  logger, world time via `GameManager.Instance.World.GetWorldTime()`, chat
  via `ChatMessageServer(..., EChatType.Global, ..., EMessageSender.Server,
  GeneratedTextManager.BbCodeSupportMode.NotSupported)`, settings from
  `Mods/Wasm/wasm-settings.txt` (line format, re-read on change).
- `CmdWasm` implements the V3 console command contract
  (`getCommands()`, `getDescription()`, `getHelp()`, `Execute(List<string>,
  CommandSenderInfo)`) with subcommands list, load, reload, unload, status.

## Game API verification (tools/targetcheck)

The bridge only compiles against a real server install, and game targets
change silently on Steam patches. `targetcheck` reads `Assembly-CSharp.dll`
and `LogLibrary.dll` metadata (System.Reflection.Metadata, no code
execution) and verifies every member the bridge touches, including method
signatures and enum members. It also reports the detected game version.
`make bridge-check` must pass before the bridge is trusted.

## Evolution path

1. In-game acceptance on a live dedicated server (the current gap).
   Per-mod manifests are already implemented (see docs/ABI.md).
2. Boot payload for init, richer host API (entities, players, world events)
   with WIT-style ABI versioning.
3. ABI stability pass: version the export names and document a compatibility
   policy before anything is called stable.
