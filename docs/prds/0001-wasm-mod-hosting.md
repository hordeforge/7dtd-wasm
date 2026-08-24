# PRD 0001: Sandboxed hosting of untrusted wasm mods

## Status

Implemented (2026-08-24). Source of truth: `src/HordeForge.WasmHost`
(host library), `src/GameBridge` (in-game bridge), `tests/` (suite),
`tools/targetcheck` (game target gate), `docs/ABI.md` (guest contract).
The in-game acceptance box is not checked: the dedicated server on this
machine crashes at boot (see [ACCEPTANCE.md](../ACCEPTANCE.md)).

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

- Not a general-purpose mod runtime; no event surface yet (entity hooks
  are a future RFC), no boot payload, no ABI versioning (see docs/ABI.md).
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
- [x] Goal 6: per-mod manifests (`wasm-mod.json`) with fuel and memory
      ceilings (tests `ManifestMemoryCeilingIsEnforced`,
      `MalformedManifestIsRejected`).
- [ ] In-game acceptance on a live dedicated server: blocked by a
      machine-level server boot crash (see `evidence/acceptance-1/` and
      docs/ACCEPTANCE.md); the modlet compiles and every game target is
      verified against the installed server.
