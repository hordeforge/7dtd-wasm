# PRD 0001: Sandboxed hosting of untrusted wasm mods

## Status

Implemented (2026-08-24). Source of truth: `src/HordeForge.WasmHost`
(host library), `src/GameBridge` (in-game bridge), `tests/` (suite),
`tools/targetcheck` (game target gate), `docs/ABI.md` (guest contract).
The in-game acceptance box stays unchecked: a containerized live-server
run succeeded (see [ACCEPTANCE.md](../ACCEPTANCE.md)), but the native
install on this machine crashes at boot and Windows/long-soak remain
unproven.
Since implementation the surface grew beyond this PRD's original scope
(zdtd compatibility imports, the on_player_join hook, TOML config); those
additions are owned by [docs/ABI.md](../ABI.md), [docs/CONFIG.md](../CONFIG.md),
and [ADR 0007](../adrs/0007-toml-config-schema.md).

## Problem

7 Days to Die mods are .NET assemblies loaded into the game process; a buggy
or hostile mod can crash the server, burn the tick, or read anything the
process can read. Server operators want third-party mods without handing
them the process. A wasm sandbox with hard limits and a narrow game API
changes the trust question from "does this DLL behave" to "can it escape a
sandbox".

Real constraints: guests are hostile by default; limits are enforced by the
host, never by guest goodwill; the server tick is 50 ms at 20 TPS; the
in-game bridge is net48 against the installed Managed; tooling and tests
are net8; any C# mod forces EAC off.

## Goals

1. Run a guest module in-process with a hard instruction budget per call.
2. Bound guest memory by construction, at load time.
3. Expose game services through a tiny documented ABI only (log, tick,
   world time, settings, chat); never game objects, Reflection, or files.
4. Fail soft per module: a trapped or fuel-burning guest never stops the
   game loop or other modules.
5. Validate the bridge's game API targets against a server install after
   every game update.
6. Allow operators to tune per-module limits without touching code.

## Non-goals

- Not a general-purpose mod runtime. The v0 surface had no event hooks;
  the optional `on_player_join` hook has since shipped (ADR 0007), and the
  wider event surface, the init boot payload, and ABI versioning remain
  future work (see docs/ABI.md).
- Not a measurement, anti-cheat, or optimization product (workspace
  boundaries).
- No pure-managed runtime; the engine is the native Wasmtime binding
  (ADR 0001).

## Acceptance

- [x] Goal 1: per-call fuel budget; fuel fixture exhausts and the host
      recovers (tests `FuelBudgetStopsGuestAndRecovers`,
      `ManifestFuelOverrideIsEnforced`).
- [x] Goal 2: memory maximum validated at load; oversized and
      undeclared-maximum modules rejected (tests
      `MemoryMaximumOverCapIsRejected`, `ModuleSizeOverCapIsRejected`).
- [x] Goal 3: ABI round trips tested with multi-byte UTF-8, settings, and
      chat (tests `TickDispatchesHostApiRoundTrips`, `InitLogsUtf8Losslessly`,
      `SampleHelloRunsEndToEnd`); no game types reachable from guests.
- [x] Goal 4: trap and fuel-exhausted guests coexist with healthy modules
      (tests `GuestTrapIsReportedAndHostSurvives`, `LoadOrderIsDispatchOrder`).
- [x] Goal 5: `tools/targetcheck` validates every bridge target against
      the installed server; `make bridge-check` gates.
- [x] Goal 6: per-mod manifests (`wasm-mod.json` at the time; the canonical
      format is now `wasm-mod.toml`, JSON still accepted: ADR 0007) with
      fuel and memory ceilings (tests `ManifestMemoryCeilingIsEnforced`,
      `MalformedManifestIsRejected`).
- [ ] In-game acceptance on a live dedicated server: a containerized live
      server run succeeded (bot servant, on_player_join; see
      `evidence/acceptance-1/` and docs/ACCEPTANCE.md). Still unproven: the
      native install on this machine (crashes at boot, unrelated to the
      modlet), Windows native loading, and long-soak behavior.
