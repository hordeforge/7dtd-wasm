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
owns its concern and nowhere else. The gates and the guest side follow the
same one-folder-one-concern shape, so the whole tree is laid out here.

```text
src/HordeForge.WasmHost/     (netstandard2.0, net8.0) the embeddable host,
                              the only publishable artifact
  Abi/                       the guest contract: import and export names,
                              status codes, IGameHostApi, the sense snapshot
                              wire format, the UTF-8 cut helper
  Config/                    WasmHostConfig: the limits the host builds its
                              engine with and validates at construction
  Core/                      the host itself: WasmModHost, WasmMod, the
                              per-call result and status types, tick telemetry,
                              MonotonicTimer (the one monotonic source every
                              reported cost is measured on), and
                              BotOwnershipRegistry (which module owns each
                              tracked bot id).
                              The only area that references Wasmtime
  Registry/                  mod metadata off disk: the bounded manifest read
                              (ManifestFiles), the manifest parser (MiniToml
                              and the ModManifest it produces), module roots,
                              the settings table, mod id validation, and two
                              text helpers (UnicodeEscapes, TextSanitizer)
  WasmModLoadException.cs    the load failure Core raises, and the base of the
                             two failures Registry raises beneath it
  WasmManifestException.cs   its subtype, for a manifest the parser rejected
  ManifestReadException.cs   a manifest file that could not be read at all;
                              the three stay together in the root namespace,
                              in no folder, because a consumer catches the
                              family there and README.md shows those catch
                              sites

src/GameBridge/              (net48) the in-game mod
  ModApi.cs                  the game's entry point
  Bridge/                    host wiring and the game side of the host API:
                              BridgeHost, GameHostApi, NativeBootstrap,
                              BotServant, plus the four classes that carry
                              no game reference: GuestRateLimiter,
                              SenseRecordPicker, WasmSettingsProvider,
                              WorldTime (the fifth, MonotonicTimer, lives in
                              the host library, which measures guest calls
                              on it)
  Hooks/                     the Harmony patches
  Commands/                  the "wasm" console command

tools/                       the repository gates; each Python tool's tests
                             sit beside it as test_<tool>.py, and targetcheck/
                             is the one C# project among them
tests/HordeForge.WasmHost.Tests/
                             one net8 project, flat, one file per subject
                             named <Subject>Tests.cs. Four files are not a
                             subject: FuzzDriver.cs is the shared fuzz
                             driver, TestGameHostApi.cs the IGameHostApi
                             double the tests assert against, and LogShim.cs
                             and GameLogShim.cs the game-log stand-ins the
                             source-linked bridge files need in a suite with
                             no game assemblies

samples/                     the guest side, one directory per module, each
                             module holding its source and the wasm-mod.toml
                             that configures it (never its build output,
                             which goes to samples/target/). guest-common
                             and the guest-fixtures crates carry no
                             manifest
  Cargo.toml                 the Rust workspace: guest-common, guest-hello
                             and the five guest-fixtures crates
  rust-toolchain.toml        the pinned channel, read by "make toolchain"
  .cargo/config.toml         the link flags every guest carries, so a module
                             stays inside the host's memory cap
  guest-common/              helpers the Rust guests share
  guest-hello/               the reference Rust guest (docs/GUEST_AUTHORS.md)
  guest-fixtures/            one tiny Rust guest per host behavior under
                             test: trap, fuel, strings, bigmem, noexports.
                             A folder of crates, not a crate itself
  guest-boss/                the C guest (guest-boss.c, compiled by zig cc)
  guest-boss-zig/            the Zig guest (src/main.zig)
  parachute/                 wasm-mod.toml only
  zdtd-fps-bot/              wasm-mod.toml only: unmodified third-party
                             modules built in the sibling checkout the
                             Makefile points at
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
- A file at the root of a project is an entry point or a type consumers
  reach for from outside: `ModApi` is where the game starts the mod, and
  the three load and manifest failure types sit beside each other in the
  root so `catch (WasmModLoadException)` and its subtypes need no second
  using. They are the only three: a new type with one caller area moves
  into that area's folder.
- `UnicodeEscapes` and `TextSanitizer` are in `Registry/` because that is
  where they have always been, not because the registry owns them:
  `UnicodeEscapes` is private to `MiniToml`, and no `Registry` type calls
  `TextSanitizer`, which the bridge uses to keep guest text out of forged
  log and chat lines. `TextSanitizer` is public, so putting it in the
  folder that matches it is a surface move that needs a minor bump (see
  CONTRIBUTING); `UnicodeEscapes` is internal and free to move.
- `GameBridge` is net48 because it references game assemblies, so the net8
  test project cannot reference it. A bridge class that carries no game
  reference but needs suite coverage is source-linked into the test
  project, as `GuestRateLimiter`, `SenseRecordPicker`,
  `WasmSettingsProvider` and `WorldTime` are.
  That is the only accepted way to cover bridge code from the host suite,
  and the list is explicit: a new game-reference-free bridge class is
  listed in the test csproj beside those four, not discovered. A seam the
  host itself reads on belongs in the host library instead, where the host
  suite already reaches it without linking: `MonotonicTimer` moved to
  `HordeForge.WasmHost.Core` for exactly that reason.
- New host code goes in the folder that owns the concern, not in the
  nearest existing file. New guest-facing surface goes under `Abi/` alone,
  because [docs/ABI.md](ABI.md) is canonical for it. A new sample guest is
  a new directory under `samples/` with its manifest beside its source, and
  a Rust one joins the workspace members in `samples/Cargo.toml`.

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
  fuel budget, and the walk returns one `ModRunResult` per module as an
  `IReadOnlyList<ModRunResult>`, each with status Ok, Trap, FuelExhausted
  or Error. A bad module never stops the loop.
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
target, caching `.cargo/` and `.rustup/` against `samples/rust-toolchain.toml`
so an unchanged channel is not re-downloaded per run. `samples/rust-toolchain.toml`
pins the channel the guests build and
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
- Every tick dispatch is timed and folded into `TickTelemetry`, which the
  bridge reports three ways: a heartbeat every 1200 ticks (60 s at 20 TPS),
  a warning for a dispatch over `SlowDispatchMs` (half a frame) capped at
  one per second, and a per-mod failure line capped like guest log output.
  `wasm status` prints the same totals, and so does the shutdown summary,
  which runs only when an embedder calls `BridgeHost.Shutdown()` (nothing in
  the mod does, so a live server never prints it). The heartbeat also
  carries the guests that failed since the previous one (`FailureTally`),
  because the capped per-mod lines and the lifetime counters in
  `wasm status` cannot tell a guest failing every tick from one that trapped
  once and recovered.
- `GameHostApi` implements the ABI over live game services: log via the game
  logger (rate capped per module), world time via `GameManager.Instance.World.GetWorldTime()`,
  chat via `ChatMessageServer(..., EChatType.Global, ..., EMessageSender.Server,
  GeneratedTextManager.BbCodeSupportMode.NotSupported)` (rate capped globally),
  settings from `Mods/Wasm/wasm.toml` plus each mod's `wasm-mod.toml`
  ([docs/CONFIG.md](CONFIG.md); shared settings re-read on change).
- `BridgeHost.ClockMs` is the one millisecond clock behind every rate
  window, throttle, and probe in the bridge: the guest log, chat,
  SimCommand, and sense caps, the tick-failure and slow-dispatch log caps,
  the bot spawn top-up, and the shared-settings file probe. Each of those
  decides whether a guest's output is accepted or dropped, so they read one
  replaceable clock rather than the process clock at their own call sites.
  It defaults to `Environment.TickCount`; a driver that steps its own time
  (a test, or a simulation replaying a run) replaces it before `Start` and
  the whole bridge follows that time. The dispatch cost the telemetry prints
  is the sub-millisecond half of the same pair: `BridgeHost.Timer`
  (`HordeForge.WasmHost.Core.MonotonicTimer`) measures it, is handed to the
  `WasmModHost` at `Start` so every guest call is measured on it too, and
  defaults to the process `Stopwatch`, so a driver replaces both and the
  run's heartbeat, status, and shutdown lines report the driver's time
  rather than the host's. One
  input stays real by nature: the shared `wasm.toml`'s mtime decides when
  guests see new settings. It feeds a log and a file read, never a guest's
  tick.
- The module scan sorts each tree's directories ordinally, so the load order
  (which fixes the order every later tick dispatches mods in) does not follow
  the order the filesystem enumerates them in.
- `BotServant.WriteSense` picks the entities a snapshot reports on the net
  id, not on the game's entity list order (`SenseRecordPicker`): the list
  is in whatever order the game built it, so the first N of it would give
  the same world a different snapshot from run to run. Records come out in
  ascending net id order, and a world holding more alive entities than a
  snapshot carries reports the lowest ids. Above 2000 alive entities the
  picker selects the lowest ids with a bounded max-heap instead of sorting
  the whole set, since sense runs at tick rate and only 41 ids fit.
- `wasm status` prints its totals in a fixed order (limiter sources by
  ordinal key, armed glide net ids ascending), so two runs of the same
  workload print the same line and a replayed run can be diffed against the
  one that diverged.
- `CmdWasm` implements the V3 console command contract
  (`getCommands()`, `getDescription()`, `getHelp()`, `Execute(List<string>,
  CommandSenderInfo)`) with subcommands list, load, reload, unload, status
  (the default when none is given), and help. Load, reload, and unload also
  log their sender to the server log, so a change to what runs in the game
  process is attributable after the session ends.
- Threading: tick and player-join dispatch run on the game main loop, but
  console commands execute on the telnet/console thread. Every
  `BridgeHost` entry point therefore serializes on one internal gate, so a
  mid-dispatch unload cannot corrupt the load-order walk, no store is
  instantiated into while a guest call runs, and the bridge's own mutable
  state (settings tables, raw config cache, rate limiters, bot and glide
  records) has one writer at a time. The gate can pause a console command
  until the current dispatch returns; both sides are bounded by fuel and
  module size caps.
- The state a guest import reaches does not rely on that gate alone:
  `GuestRateLimiter`, the settings provider, the game host API's config
  cache, `TickTelemetry`, and the bot servant's pooled sense buffers each
  take one private lock, taken in the order gate -> host API -> servant ->
  limiter and never held across another. The per-mod counters on a `WasmMod`
  are read through `Interlocked`, because `TryGetMod` hands the instance to
  a reader that need not be the dispatching thread. The module tree list is
  published as an immutable snapshot rather than filled in place, so the
  resolvers a guest import reaches cannot walk a list `Start` or `Shutdown`
  is rewriting.

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
