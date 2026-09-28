# Changelog

All notable changes to this project are recorded here. The format follows
the sibling projects: versioned sections with dated entries, newest first.
Codename: Quarantine (7dtd-wasm).

## Unreleased

### Added

- Per-tick dispatch telemetry: a once-a-minute heartbeat, a warning when a
  dispatch overruns half a frame, and the run's cost and failure totals in
  `wasm status` and at shutdown (`HordeForge.WasmHost.Core.TickTelemetry`).
  Guest dispatch failures are now warnings that name their tick instead of
  info-level lines without one.
- Guest-driven host log lines (bot servant commands and their failures, the
  chat-rejection line) go through a per-source rate cap, with the dropped
  totals in `wasm status`. They were bounded only by the guest's fuel
  budget and could flood the server log.
- `make toolchain`: populates the in-project rustup toolchain (`.cargo/`,
  `.rustup/`) from rustup on PATH, so a fresh clone has one documented way to
  get a working guest build. CI runs the same target instead of its own copy
  of the rustup commands.
- `make test TEST_FILTER=<expr>`: run a single test or class through the
  documented target rather than the whole suite.
- `global.json` declaring the .NET 8 SDK the tree builds against, matching
  what CI installs.
- `WasmModHostConcurrencyTests`: dispatch, load, and unload driven from
  several threads at once, asserting no mod is skipped or duplicated in a
  tick, that the settings import never answers with another mod's value, and
  that the registry and the load order agree afterwards.
- `ruff format` as the Python formatter, run in check mode by
  `make check-ci` next to `ruff check tools`.
- `THIRD-PARTY-NOTICES.md`: licenses and attribution for everything the
  modlet redistributes (Wasmtime, Apache-2.0 WITH LLVM-exception, plus the
  MIT-licensed .NET Foundation closure). `make dist` stages it next to the
  binaries it covers and the `HordeForge.WasmHost` NuGet package embeds it,
  so Apache-2.0 redistribution requirements travel with the artifacts.
- Public surface gate for the published library (`tools/apicheck.py`, in
  `make check`, with its baseline in `tools/api-surface.txt`): every
  public and protected member of `HordeForge.WasmHost` is recorded, and
  any difference, a removal included, fails until the minor digit is
  bumped, the changelog entry is written, and the baseline is regenerated
  with `--update`. A member's body is not surface, so an implementation
  change does not touch the baseline. This is the guard that was missing
  when `WasmModHost.TryInit` came off the surface in the 0.3.1 patch slot.
- Seeded fuzz harnesses for the manifest parser (`ModManifest.ParseToml`)
  and the guest-text entry points (`TextSanitizer.Clean`, `ModId.IsValid`),
  run by `make test`. They assert the load contract, a per-input time
  budget, manifest determinism, and sanitizer idempotence rather than only
  "did not crash". `HORDEFORGE_FUZZ_SEED` and `HORDEFORGE_FUZZ_ITERATIONS`
  reseed and lengthen a run; every failure prints its replay command.

### Fixed

- A raw lone surrogate in a manifest string (basic or literal) reached the
  settings table instead of being rejected, while the `\uXXXX` form already
  was. Such a value has no UTF-8 form and cannot round-trip the guest
  string ABI.
- Armed glide flags are dropped for net ids that no longer name a live
  player, in the same sense scan that prunes the position history. The
  servant kept one entry per player who ever armed a glider for the life
  of the server, and a net id the game later reused to another entity
  kept clamping that entity's descent. `wasm status` now lists only the
  armed ids, not every id the servant still remembers.

### Changed

- Decision and requirement documents that had drifted from the code:
  ADR 0007 no longer claims the JSON manifest is still accepted (ADR 0005
  superseded it), PRD 0001 no longer claims undeclared-maximum modules are
  rejected (ADR 0004's 4 GiB amendment) or that JSON manifests are
  accepted, ADR 0005 links the record that superseded it, `docs/CONFIG.md`
  points MiniToml at ADR 0007, and the ADR index carries a status column.
- `BotServant.Glide` returns a copy of the armed flags instead of the live
  dictionary, so the "wasm status" read cannot reach the servant's state.
  `ModApi` applies its Harmony patches under a lock, so a second `InitMod`
  racing the first cannot stack duplicate postfixes.
- The tools lint gate covers more rule groups (S, A, BLE, DTZ, FBT, FURB,
  G, ICN, ISC, LOG, N, PERF, PIE, SLF, TID), all of which the tree passes
  today. `tools/` is reformatted to the pinned ruff's style.
- A `vX.Y.Z` tag now runs the full CI gate (docs, lint, guest lint, build,
  test) before the version check, by calling `ci.yml` as a reusable
  workflow instead of tagging on version agreement alone.
- CI caches the NuGet restore, keyed on the committed `packages.lock.json`
  files. Locked mode still verifies every content hash, so a warm cache
  changes restore time, not what is trusted.
- The acceptance image build ignores its own directory (`.dockerignore`),
  so recorded run logs no longer enter the build context or bust the cache.
- `WasmModHost` runs `DispatchTick`, `DispatchInit`, and
  `DispatchPlayerJoin` through one private `Dispatch` walk instead of three
  copies of the same loop, and the `log` and `tick` imports are defined once
  for both the `hordeforge` and `zdtd` host modules.
- `BridgeHost` formats a failed call for the log through one `Describe`
  helper, and the `ModRunResult?` unwrap in `Reload` and `Unload` uses a
  pattern match. Log lines are unchanged.
- `ModApi.ApplyHarmonyPatches` posts each hook through one `Patch` helper
  (same targets, same messages).
- `CmdWasm` writes console output through one `Output` helper.
- The `glide` queue verb only arms a net id that names a live player
  (`EntityPlayer`) in the loaded world. An armed flag applies the glide buff
  and clamps the entity's descent, so accepting any world net id let a guest
  steer entities it does not own, other players included. A non-player net id
  is refused with a `glide (not a player)` line in the log.
  `docs/ABI.md` states the gate.
- `TextSanitizer` also strips the invisible bidi controls (U+202A to
  U+202E, U+2066 to U+2069) and the zero-width no-break space (U+FEFF) from
  guest- and client-supplied log, chat and set-name text, replacing them
  with '?' like the control characters it already stripped. They render as
  nothing while reordering the text around them, so a chat or log line
  could read as some other name or as text the guest never wrote. Zero-width
  space, joiner, word joiner and variation selectors are left alone, so
  emoji sequences and non-Latin scripts still render.
- The repository tools (`doccheck.py`, `versioncheck.py`, `sbom.py`,
  `targetcheck`) share one command-line contract: `--help` documents every
  flag, `--root` selects the repository to work on, machine-readable data
  goes to stdout and progress to stderr, and exit codes are 0 pass, 1 check
  failed, 2 usage error. `versioncheck.py` moved its messages to stderr and
  gained `--root`.

### Fixed

- The guest, fixture and dist targets failed on a clean clone with a bare
  `sh: .cargo/bin/cargo: No such file or directory` or a `cp: cannot stat`,
  because the in-project toolchain, zig and the sibling `zdtd-server`
  checkout were each an undocumented prerequisite. Each target now names the
  missing piece and the command that provides it, and the path to the
  sibling checkout is overridable with `ZDTD_SERVER=...`.
- `make help` listed neither `make clean` nor the guest toolchain step, and
  described `make check` without the bridge build it runs.
- `make check-ci` failed on a clean clone: `tools/` had never been formatted
  with the ruff the gate pins, so `ruff format --check` refused six files.
  They are formatted now, and `pyproject.toml` declares
  `required-version = "==0.16.4"` so a developer's ruff has to be the one CI
  installs rather than whatever happens to be on PATH.
- `WasmModHost` guarded its "call it from one thread" contract with a comment
  only: two threads entering it skipped or duplicated mods in a dispatch,
  served one guest another guest's settings, and could enter a wasm store
  that was already running a call (the engine aborts the process). Every
  entry point now serializes on one internal gate, and the dispatch results
  and `ModIds` are per-call copies instead of host-owned live views, so a
  list handed to one caller cannot be refilled by a dispatch running on
  another thread. `WasmModHostConcurrencyTests` drives dispatch, load, and
  unload from several threads against real guest fixtures.
- The `config` host import cut its copy at `min(out_cap, len)` bytes, which
  could land inside a multi-byte UTF-8 character and hand the guest bytes it
  decodes as U+FFFD. The cut now stops on a character boundary
  (`Utf8Prefix`), and `docs/ABI.md` states the boundary.
- Manifest and `config.toml` reads decoded the bytes in this class instead of
  through `File.ReadAllText`, whose reader silently switches encoding on a
  UTF-16 or UTF-32 BOM: a non-UTF-8 file now fails its load with a reason
  instead of loading, and a UTF-8 BOM is stripped explicitly.
- `ModId.IsValid` accepts U+FFFD, so a module folder whose name is not valid
  UTF-8 (legal on Linux) produced an id that no longer re-encodes to its own
  directory and the module silently never loaded. Replacement characters are
  rejected like the other invisible characters.
- The guest link flags (`--max-memory=33554432`, 1 MiB stack) move into the
  tracked `samples/.cargo/config.toml`. They lived only in the gitignored
  in-project toolchain config, so a fresh checkout and CI built guests that
  declare no memory maximum, which the host treats as the 4 GiB wasm32
  ceiling and refuses under the default cap.
- `wasm load` skips a module tree it cannot enumerate (permissions, a
  modlet being replaced) with a warning instead of aborting the whole
  scan and leaving the host unstarted.
- The server-side glide clamp measured the fall against a single tick while
  `vy` is averaged over every tick since the entity's stored position, so a
  player sampled several ticks after the last one (sense called below tick
  rate) was lifted back up even while sinking within the glide rate. The
  drop budget now covers the same interval `vy` was measured over.
- `limits.max_memory_bytes` was accepted down to 1 byte. As a per-mod value
  it could only reject the module, but the shared `wasm.toml` value becomes
  the engine's memory ceiling, where the host constructor threw and took the
  bridge start down with it. The manifest parser now rejects anything below
  one wasm page, the bound the host already enforces, so an invalid file
  keeps the documented "log it and use the defaults" behavior.
- The CycloneDX SBOM now carries an SPDX license per NuGet component.
  `tools/sbom.py` holds the table and fails the build when a package
  reaches a committed lock file without a recorded license, and the SBOM
  skips lock files under `evidence/` (a frozen playtest record, not a
  shipped artifact).
- `BotServant.PruneDeadBots` collects dead ids into the pooled scratch list
  instead of allocating one per call, matching the pooling the sense path
  already does.
- A module initialized on its own (the start scan, `wasm reload`) read its
  settings, its `config.toml`, and its log attribution from whichever mod
  the host happened to have called last, or from no mod at all on a fresh
  start. `WasmModHost.InitModule(id)` runs `on_enable` with the calling mod
  set, and the host clears that state after every dispatch.
- The sense position history is pruned by entity membership instead of a
  size comparison. A tick where entities left while others joined kept the
  departed ids, and a net id the game later reused was reported with a
  vertical velocity derived from the previous occupant.
- Guest rate limiter windows for sources that stopped writing are swept
  once the table grows past its threshold, so unloading and reloading
  modules no longer leaves one window per id ever seen.

### Removed

- `ModuleRoots.Contains` (an ordinal string search that `List.Contains`
  already does) and the null checks on `Order`, `ResolveDir`, and
  `ResolveFile` parameters, which the non-nullable signatures already
  require. One manifest test that duplicated another, and the redundant
  InlineData cases in the duplicate malformed-manifest theory, are gone
  (the surviving theory keeps every case).
- `ModApi.HostStarted` (the bridge mod's public flag, never read) and the
  `BotServant` weapon damage table. Stage 2 bots all carry the pistol
  (weapon id 0) and nothing reads the other five ids, so the table held
  values with no servant-side use; the pistol constant replaces it.
- `TomlArray` no longer stores the items it parsed. Items are still parsed,
  so a malformed one fails the load, but nothing reads array elements: no
  manifest field is an array, and `AsString` and its siblings reject an
  array value either way.

## [0.3.1] - 2026-09-21

### Removed (breaking)

- `WasmModHost.TryInit(id, out result)` and `BotServant.ClearGlide()`. Both
  had zero callers; dispatch walks and `Glide` status cover their uses.
  `WasmModHost` is part of the published `HordeForge.WasmHost` package, so a
  consumer that called `TryInit` (the documented way to init a single freshly
  loaded mod outside a dispatch walk) does not compile against 0.3.1 and must
  move to the dispatch walk. The version number does not warn of this: it
  shipped in a patch slot, which under this project's rule carries no
  breaking change. The entry is marked here so the audit trail is right.

## [0.3.0] - 2026-09-20

### Changed (breaking)

- Only `wasm-mod.toml` is read: the deprecated `wasm-mod.json` manifest
  format and the MiniJson parser behind it are deleted, so a module still
  shipping JSON must convert its limits to TOML (same fields, snake_case
  keys). MiniToml no longer unwraps quoted keys and headers either; a
  quoted key is a literal name. ADR 0005 is marked superseded and
  `docs/ABI.md` states the current surface.

### Removed

- `NativeAssets` (the platform runtime-id map duplicated by the `make
  dist` staging) and the unused `StatusNotImplemented` /
  `StatusInternalError` / `SettingOk` ABI constants.

## [0.2.0] - 2026-09-11

This cycle changes the public C# surface of the host library and the bridge
(marked below). In this project's 0.x scheme the minor digit carries breaking
changes and the patch digit never does, so any 0.1.x remains safe to take
without reading further.

### Changed (breaking)

- Guest rate limiters bind their cap at construction
  (`new GuestRateLimiter(GuestRateLimiter.MaxCommandsPerSecond)`) and
  `TryWrite` lost its per-call override parameter, so a limiter's cap can
  no longer drift from its call sites. The generic `GameHostApi.RateLimiter`
  property is renamed `LogLimiter`, matching its siblings.
- Sense requests (`zdtd.sense`) are now rate capped per module
  (200/second, same reasoning as the SimCommand cap): building a snapshot
  scans the live world entity list on the host side, work the wasm fuel
  budget never sees, so an unbounded import loop could multiply that scan
  past the tick budget. Capped requests report "no world data" (0) and the
  drops surface in `wasm status`. `IGameHostApi.WriteSenseSnapshot` now
  receives the calling mod id so implementations can attribute and cap;
  implementers of the interface must add the parameter.
- `WasmModHost.DispatchPlayerJoin` takes the entity id as `int` (the wire
  type of `on_player_join`); the previous `long` parameter forced a silent
  narrowing cast on every caller.
- The zdtd sense snapshot is bumped to v4 (magic `ZBS4`, 40-byte records
  with server `vy` and the `wearing_glider` bit, ADR 0037), byte-identical
  to the sibling zdtd wire format; v3 guests fail loudly on the magic check
  instead of misreading records. `IGameHostApi` gains
  `TryGetRawConfig(modId, out content)` for the new `zdtd.config` import;
  implementers of the interface must add it.

### Changed

- `NativeAssets.StageNativeLibrary` stages from the newest installed
  Wasmtime NuGet package instead of a hard-coded version string, matching
  what `make dist` already does from the lock file.
- Named the zdtd queue/query result codes in `AbiConstants`
  (`QueueAccepted`, `QueueRejected`, `QueryNoAnswer`,
  `QueryBufferTooSmall`); no wire change (docs/ABI.md unchanged).
- The `zdtd` compatibility module serves the calling mod's own `config.toml`
  verbatim through `zdtd.config` (0 = none; the host never parses it, each
  guest owns its format). The bridge registers `Mods/Wasm/<id>/config.toml`
  at module load (invalidated on reload) and the bridge queue surface now
  handles the parachute mod's `glide <net_id> <0|1>` verb (tracked per
  player, surfaced in `wasm status`) and forwards non-verb queue text to the
  chat broadcast (the parachute deploy announce). Sense records report `vy`
  (from `Entity.motion`) and the `wearing_glider` bit (a worn item whose
  ItemClass carries the parachute tag); the new game API surface is pinned
  in `tools/targetcheck`.
- Dependency audit pass: test stack moved to the newest serviced pins
  (`Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3; runner stays 2.8.2,
  the correct pairing for xunit 2.x). `Wasmtime` stays at 44.0.0: that is
  the newest binding ever published on NuGet; engine advisories patched in
  46/47/48 have no .NET binding yet, and the current host configuration
  does not reach the affected surfaces (no filesystem preopens, single
  Engine/Store). See the updated SECURITY.md note.

### Added

- Release consistency gate (`tools/versioncheck.py`, part of `make check`
  and the tools unit tests): the three shipped-version declarations
  (`src/GameBridge/ModInfo.xml`, `<Version>` in the host library csproj,
  and the newest released `## [X.Y.Z]` changelog section) must agree, and
  the release workflow now checks all three against the tag instead of
  only ModInfo.xml. Also fixed the first real drift this gate exists for:
  ModInfo.xml still said 0.1.0 while the repository ships 0.1.5.
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
- The unmodified zdtd parachute module (workspace sibling, sense v4 +
  `zdtd.config` + `glide`) is staged by `make dist` and `make fixtures`
  (module + its config.toml) and covered by host tests: it loads as-is,
  parses its own config.toml through `zdtd.config`, and arms/clears the
  glide exemption for a falling worn player. A live-server run through the
  `7dtd-playtest` orchestrator (parachute suite) is documented under
  `evidence/playtest-1/`.
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
- Guest output caps covered by unit tests (`GuestRateLimiterTests`): the
  limiter file links into the net8 test project and a second constructor
  takes the millisecond clock, so window resets and TickCount wraparound
  are covered deterministically (ADR 0006 amended).
- Pure bridge helpers moved into the host library with tests:
  `ModuleRoots` (multi-tree module resolution), `SettingsTable`
  (get_setting precedence), `TextSanitizer` (log-forgery guard),
  `ManifestFiles` (bounded manifest reads), plus unit tests for the
  `doccheck` gate rules.
- `bridge-check` pins the glide buff surface (`EntityAlive.Buffs`,
  `EntityBuffs.AddBuff/RemoveBuff/HasBuff`) so a game update renaming
  them fails the gate instead of breaking glides at runtime.

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
