# Changelog

All notable changes to this project are recorded here. The format follows
the sibling projects: versioned sections with dated entries, newest first.
Codename: Quarantine (7dtd-wasm).

## Unreleased

### Changed

- Guest rate limiters bind their cap at construction
  (`new GuestRateLimiter(GuestRateLimiter.MaxCommandsPerSecond)`) and
  `TryWrite` lost its per-call override parameter, so a limiter's cap can
  no longer drift from its call sites. The generic `GameHostApi.RateLimiter`
  property is renamed `LogLimiter`, matching its siblings.
- `NativeAssets.StageNativeLibrary` stages from the newest installed
  Wasmtime NuGet package instead of a hard-coded version string, matching
  what `make dist` already does from the lock file.
- Named the zdtd queue/query result codes in `AbiConstants`
  (`QueueAccepted`, `QueueRejected`, `QueryNoAnswer`,
  `QueryBufferTooSmall`); no wire change (docs/ABI.md unchanged).
- Sense requests (`zdtd.sense`) are now rate capped per module
  (200/second, same reasoning as the SimCommand cap): building a snapshot
  scans the live world entity list on the host side, work the wasm fuel
  budget never sees, so an unbounded import loop could multiply that scan
  past the tick budget. Capped requests report "no world data" (0) and the
  drops surface in `wasm status`. `IGameHostApi.WriteSenseSnapshot` now
  receives the calling mod id so implementations can attribute and cap.
- `WasmModHost.DispatchPlayerJoin` takes the entity id as `int` (the wire
  type of `on_player_join`); the previous `long` parameter forced a silent
  narrowing cast on every caller.

- Dependency audit pass: test stack moved to the newest serviced pins
  (`Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3; runner stays 2.8.2,
  the correct pairing for xunit 2.x). `Wasmtime` stays at 44.0.0: that is
  the newest binding ever published on NuGet; engine advisories patched in
  46/47/48 have no .NET binding yet, and the current host configuration
  does not reach the affected surfaces (no filesystem preopens, single
  Engine/Store). See the updated SECURITY.md note.

### Added

- `ModRunResult.ModId`: dispatch results now carry the registry id of the
  mod that produced them, plus a matching constructor overload. Attribution
  can no longer depend on list position, which was already impossible for
  `DispatchPlayerJoin` (it calls only the mods exporting the handler); the
  bridge's join failure logs name the module now.
- `WasmHostConfig` is validated fail-fast when the host is constructed:
  zero fuel per call, a sub-page memory ceiling, non-positive module size
  or stack caps, and an empty log source prefix are rejected with the
  offending value instead of surfacing later as instant fuel exhaustion or
  blanket module rejection.
- Guest SDK (`samples/guest-common`) covers the full hordeforge import
  surface: added the missing `get_join_player_name` binding plus safe
  wrappers `current_tick()`, `world_time()`, `join_player_name(&mut [u8])`,
  and `log_debug()` so guest code needs no `unsafe` for plain host reads;
  raw imports remain available. `guest-hello` and docs/GUEST_AUTHORS.md use
  the safe path, and the guide gained an `on_player_join` example for Rust
  guests.
- NuGet pack metadata on the host library (package id, version tracking
  CHANGELOG.md, license, repository): `dotnet pack` produces a complete
  package instead of an anonymous default.
- Committed NuGet lock files (`packages.lock.json` per project) with
  SHA512 content hashes for every direct and transitive package:
  restores now verify integrity and consumers get an exact inventory of
  what ships. Generated via `RestorePackagesWithLockFile` in
  Directory.Build.props.
- `make dist` derives the staged native Wasmtime version from the lock
  file instead of a hardcoded copy of the package version.

## [0.1.5] - 2026-08-24

### Added

- The bot servant (`BotServant`): spawns bot entities (zombieSoldier
  bodies) on the live world, applies the brain's SimCommands (bot
  spawn/remove/count/move/look/shoot/skill/cfg), and builds the 'ZBS3'
  world snapshot from the live entity list (players, zombies, our bots).
  `queue` and `sense` now dispatch through it; `query` still returns no
  answer (cover/path is stage 3).
- Game API targets for the servant added to `tools/targetcheck`
  (World.Entities/GetEntity/SpawnEntityInWorld, Entity position/SetPosition/
  SetRotation, EntityAlive Health/IsDead/SetDead/DamageEntity,
  EntityFactory, EntityClass), with overload-aware method checking.

### Verified live (container acceptance run)

The unmodified zdtd fps_bot brain now drives real spawned bots on a live
dedicated server: 4 bots spawned, and the brain (fed the live sense
snapshot) targeted each other and ordered shots that the servant applied,
including headshots (`bot 173 shot 171 dmg=24 head`). 1555 shots over the
run; evidence `evidence/acceptance-1/servant-join.log`.

Live-run fixes: spawn retries until the world is ready (the game's own
EAIManager NREs during world creation, and a failed spawn must not latch
the spawned flag), and the servant's own bots are reported as bot kind in
the snapshot (they are zombie-bodied, so classification must check the bot
roster first or the brain never drives them).

### Not yet implemented (stage 3)

- cover/path queries, on_admin_command console wiring, per-bot loadout
  records (weapon info events), and disabling the stock zombie AI on bot
  bodies so the game does not also move them.

## [0.1.4] - 2026-08-24

### Added

- zdtd-server compatibility surface so sibling plugins run unmodified: the
  host defines the `zdtd` import module (log, tick, queue, sense, query)
  and accepts the bare zdtd hooks, including `void`-returning
  on_enable/on_tick/on_shutdown and the optional `on_admin_command` export.
- `SenseSnapshotWriter`: byte-identical 'ZBS3' world snapshot format shared
  by the host, the bridge, and tests.
- ADR 0004 amendment: modules without a declared memory maximum are treated
  as declaring the wasm32 ceiling (4 GiB) and load only when the operator
  raises the cap via `wasm.toml [limits] max_memory_bytes`.
- Verified: the unmodified zdtd `fps_bot` wasm loads, and its brain, fed a
  synthetic sense snapshot, queues `bot look` and `bot shoot` SimCommands
  (four new host tests, 43 total).

### Not yet implemented (stage 2)

- The bot servant: `queue` commands are accepted and logged, `sense`
  returns no world data yet, `query` has no answers. The bridge wires the
  imports but the game-side spawn/move/shoot servant and the live entity
  snapshot are the next slice.

## [0.1.3] - 2026-08-24

### Added

- `samples/guest-boss-zig`: the boss watcher written in Zig
  (zig build-exe, wasm32-wasi), reading its target name from the
  `get_setting` import so `[settings] boss_name` in wasm-mod.toml retunes
  it without rebuilding. Built via `make boss-zig`, staged in
  `dist/Mods/Wasm/boss-zig`, covered by two host tests (39 total).
- TOML mod config following the zdtd-server conventions (docs/CONFIG.md):
  `wasm-mod.toml` (`[limits]`, `[settings]`) per mod, shared
  `Mods/Wasm/wasm.toml` (`[limits]` at host start, `[settings]` re-read on
  change), snake_case keys, load order code defaults -> wasm.toml ->
  wasm-mod.toml. Parsed by a dependency-free MiniToml (ADR 0007). The
  deprecated JSON manifest is still accepted.
- get_setting is now calling-mod aware: a mod's own `[settings]` win over
  shared keys, so two mods can use the same key with different values.

### Changed (breaking ABI, aligned with zdtd-server)

The guest ABI now follows the zdtd-server plugin contract exactly:
exports are the bare hook names `on_enable`, `on_tick`, `on_shutdown`,
`on_player_join` (the `hordeforge:mod/` prefix is gone), hooks are no-arg
(the tick number is read via the `tick` import, renamed from `get_tick`),
and `on_player_join` receives `(entity_id)` (zdtd passes slot and entity
id; we have no ECS slot, and the name comes via `get_join_player_name`).
All guests (Rust, C, Zig), fixtures, tests, and docs updated.

### Verified live (aligned ABI + TOML config)

Second container acceptance run after the alignment: the Zig guest printed
"THE BOSS IS HERE" for `boss_name = "maci1"` read from its wasm-mod.toml
(no rebuild), with the join dispatched as `player spawned: maci1 (entity
171)`. Evidence: `evidence/acceptance-1/aligned-abi-join.log`. The run
fixed a Harmony postfix naming bug: `RequestToSpawnPlayer`'s int
parameters are `_chunkViewDim` and `_nearEntityId` (not the player's id),
so the postfix must not declare `_entityId`; the entity id comes from
`ClientInfo.entityId`, now also verified by `tools/targetcheck`.

## [0.1.2] - 2026-08-24

### Added

- Player join events: optional guest export `hordeforge:mod/on_player_join`
  plus the `get_join_player_name` host import; the bridge patches
  `GameManager.RequestToSpawnPlayer` (verified via targetcheck) and
  forwards the joining player's name. Guests without the handler are
  unaffected.
- `samples/guest-boss`: a C guest built with the zig compiler that prints
  "THE BOSS IS HERE" to the console when the player "maci" spawns. Built
  via `make boss`, staged in `dist/Mods/Wasm/boss`, covered by three new
  host tests (26 total).

### Verified live (container acceptance run)

- A real player join (loadgen bot, named `maci1` by the harness) reached
  the bridge (`[WasmHost] player spawned: maci1`) and was dispatched to
  the guest handler. Evidence: `evidence/acceptance-1/boss-join-server.log`.
- Hook findings: `GameManager.OnClientSpawned` and
  `GameManager.PlayerSpawnedInWorld` never fire on the dedicated server
  for remote joins; `RequestToSpawnPlayer` is the working server-side
  entry point.

## [0.1.1] - 2026-08-24

In-game acceptance completed (docker container, fresh steamcmd install,
V 3.1.0 b14). Evidence: `evidence/acceptance-1/`, `docs/ACCEPTANCE.md`.

### Fixed (found by the acceptance run)

- `GameTimer.Instance.ticks` reads 0 on the dedicated server; the bridge
  now maintains its own monotonic tick counter (one increment per hook
  run, 20 TPS).
- The game does not rate limit `ChatMessageServer` on its own; the bridge
  now caps guest chat globally at 10 messages/second with a visible drop
  counter in `wasm status` (the log rate limiter was extended to chat).
- The bridge resolved the modlet directory wrong on live servers
  (Native/ and Mods/Wasm are siblings of the modlet, not children of
  Mods/); corrected in BridgeHost.Start.

### Docs conventions

- Added docs/INDEX.md hub, docs/adrs/ (6 ADRs + template), docs/rfcs/,
  docs/prds/ (0001 wasm-mod-hosting), CHANGELOG.md, TODO.md,
  CONTRIBUTING.md, matching the workspace pattern.

## [0.1.0] - 2026-08-24

Initial experiment release.

### Added

- Embeddable host library (`HordeForge.WasmHost`, netstandard2.0 + net8.0)
  on Wasmtime.Dotnet 44 with per-call fuel budgets, load-time memory cap
  from the declared maximum, module size cap, and structured trap
  reporting.
- Guest ABI v0 (`hordeforge` imports: log, get_tick, get_world_time,
  get_setting, send_chat; exports: init, tick, shutdown). Documented in
  docs/ABI.md.
- Per-mod manifests (`wasm-mod.json`) with fuel and memory ceilings, parsed
  by a dependency-free internal parser.
- net48 in-game bridge (`1_HordeForge_WasmHost`): dedicated gate, native
  bootstrap, Harmony tick hook on GameManager.Update, `wasm` console
  commands, settings file, guest log rate capping.
- Rust guest SDK (`samples/guest-common`) and example guests, built with an
  in-project rustup toolchain.
- Test suite: 23 tests covering ABI round trips, traps, fuel exhaustion,
  memory cap, manifest handling, registry semantics.
- `tools/targetcheck`: validates every game API target against a server
  install; all V3.1.0 targets verified.
- Docs gates (`make check`): em dashes, AI attribution, links, TODO format.
- Design records: docs/INDEX.md, docs/adrs/ (6 ADRs), docs/rfcs/,
  docs/prds/.

### Known gaps

- The dedicated server on this machine crashes at boot (environment issue);
  the in-game acceptance instead ran successfully in a docker container
  with a fresh steamcmd install (see evidence/acceptance-1/ and
  docs/ACCEPTANCE.md). The modlet compiles and all game targets are
  verified against both the host install and the container build.
