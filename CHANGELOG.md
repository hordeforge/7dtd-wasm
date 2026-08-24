# Changelog

All notable changes to this project are recorded here. The format follows
the sibling projects: versioned sections with dated entries, newest first.

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
