# Changelog

All notable changes to this project are recorded here. The format follows
the sibling projects: versioned sections with dated entries, newest first.

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
