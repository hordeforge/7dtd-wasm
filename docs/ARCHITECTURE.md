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

## Source layout

`src/` holds two projects that depend one way on each other, and one folder
per concern inside each. A folder is a namespace: the path under `src/` is
the namespace below the assembly root, so a type sits in the folder that
owns its concern and nowhere else.

```text
src/HordeForge.WasmHost/     (netstandard2.0, net8.0) the embeddable host,
                              the only publishable artifact
  Abi/                       the guest contract: import and export names,
                              status codes, IGameHostApi, the sense snapshot
                              wire format, the UTF-8 cut helper
  Config/                    WasmHostConfig: the limits the host builds its
                              engine with and validates at construction
  Core/                      the host itself: WasmModHost, WasmMod, the
                              per-call result and status types, tick telemetry.
                              The only area that references Wasmtime
  Registry/                  mod metadata off disk: the manifest parser
                              (MiniToml and the ModManifest it produces),
                              module roots, the settings table, mod id
                              validation, and two text helpers
                              (UnicodeEscapes, TextSanitizer)
  WasmModLoadException.cs    the one type Core and Registry both throw, so it
                              sits in the root namespace, in neither

src/GameBridge/              (net48) the in-game mod
  ModApi.cs                  the game's entry point
  Bridge/                    host wiring and the game side of the host API:
                              BridgeHost, GameHostApi, WasmSettingsProvider,
                              NativeBootstrap, BotServant, GuestRateLimiter
  Hooks/                     the Harmony patches
  Commands/                  the "wasm" console command

tools/                       the repository gates; each Python tool's tests
                             sit beside it as test_<tool>.py, and targetcheck/
                             is the one C# project among them
tests/HordeForge.WasmHost.Tests/
                             one net8 project, flat, one file per subject
                             named <Subject>Tests.cs
```

The placement rules the layout exists to enforce:

- The bridge references the host library. The host library references
  Wasmtime and never the game, the bridge, or a Unity type, which is why
  the host suite runs with no server install.
- Core consumes `Abi`, `Config`, and `Registry`, not the other way round.
  The one edge that runs up, `Registry.ModManifest` to
  `WasmModHost.WasmPageBytes`, is a published constant: the page size is a
  wasm32 protocol fact, so it belongs in `Abi/`, but moving it is a
  published-surface break and waits for the next minor (see CONTRIBUTING).
  Anything else in those three areas that needs a live module belongs in
  Core.
- A file at the root of a project is an entry point or a type several
  areas share: `ModApi` is where the game starts the mod,
  `WasmModLoadException` is what a rejected module raises everywhere.
- `UnicodeEscapes` and `TextSanitizer` are in `Registry/` because that is
  where they have always been, not because the registry owns them:
  `UnicodeEscapes` is private to `MiniToml`, and no `Registry` type calls
  `TextSanitizer`, which the bridge uses to keep guest text out of forged
  log and chat lines. Both are public members of the published package, so
  putting either in the folder that matches it is a surface move that needs
  a minor bump (see CONTRIBUTING).
- `GameBridge` is net48 because it references game assemblies, so the net8
  test project cannot reference it. A bridge class that carries no game
  reference but needs suite coverage is source-linked into the test
  project, as `GuestRateLimiter` is. That is the only accepted way to
  cover bridge code from the host suite.
- New host code goes in the folder that owns the concern, not in the
  nearest existing file. New guest-facing surface goes under `Abi/` alone,
  because [docs/ABI.md](ABI.md) is canonical for it.

## Host library (HordeForge.WasmHost)

Owns one Wasmtime engine and linker per host instance, and one store per
loaded module (unload disposes that module's store, so reload cycles do not
retain old instances). Every entry point serializes on one internal gate, so
the host is safe to drive from more than one thread: only one call runs at a
time, and the load order, the per-call mod id, and the engine handles are
never touched by two threads at once. The bridge still drives it from the
game main loop.

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
- `Dispose` runs every loaded guest's shutdown export and keeps the ones that
  did not complete in `ShutdownFailures`, which the embedder reads after
  disposing: a guest that traps on its way out must not disappear silently.

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
`on_tick`, and optionally `on_shutdown`, `on_player_join`, and
`on_admin_command`, and import
the `hordeforge` host API. String arguments are (pointer, length) pairs into
the guest's own memory; the host reads them only within the given range and
never holds a reference across calls.

The Rust toolchain lives inside the repo (`.cargo/`, `.rustup/`) so nothing
is installed system-wide; `make toolchain` populates it and CI calls the same
target. `samples/rust-toolchain.toml` pins the channel the guests build and
lint with, and `make toolchain` installs that same channel, so the pin and
the install cannot drift apart. The tracked `samples/.cargo/config.toml` pins
`--max-memory=33554432` (32 MiB) and a 1 MiB stack for every guest, which
keeps modules inside the host caps by construction in a fresh checkout and
in CI, not only on a host that already has a toolchain config.

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
  `BridgeHost` entry point therefore serializes on one internal gate, so a
  mid-dispatch unload cannot corrupt the load-order walk, no store is
  instantiated into while a guest call runs, and the bridge's own mutable
  state (settings tables, raw config cache, rate limiters, bot and glide
  records) has one writer at a time. The gate can pause a console command
  until the current dispatch returns; both sides are bounded by fuel and
  module size caps.

## Game API verification (tools/targetcheck)

The bridge only compiles against a real server install, and game targets
change silently on Steam patches. `targetcheck` reads `Assembly-CSharp.dll`
and `LogLibrary.dll` metadata (System.Reflection.Metadata, no code
execution) and verifies every member the bridge touches, including method
signatures and enum members. It also reports the detected game version.
An install that cannot be read at all (missing, unreadable, or a corrupt
image) exits 2 with the reason on stderr, never a stack trace.
`make bridge-check` must pass before the bridge is trusted.

## Evolution path

1. In-game acceptance ran in a docker container (docs/ACCEPTANCE.md);
   still unproven: Windows native loading and long-soak behavior.
   Per-mod manifests are already implemented (see docs/ABI.md).
2. Boot payload for init, richer host API (entities, players, world events)
   with WIT-style ABI versioning.
3. ABI stability pass: version the export names and document a compatibility
   policy before anything is called stable.
