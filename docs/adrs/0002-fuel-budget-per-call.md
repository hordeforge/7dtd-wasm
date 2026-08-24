# ADR 0002: Budget every guest call with fuel, not wall clock

## Status

Accepted (2026-08-24).

## Context

A guest mod may loop forever. The host must stop it and keep the server
tick (50 ms at 20 TPS) healthy. Two mechanisms were seriously considered:

- **Wall-clock deadlines**: run the call on a watchdog timer and abort when
  it exceeds N milliseconds. Simple to reason about, but aborts are
  cooperative or signal-based, interrupt at arbitrary instruction points,
  and the deadline can be fooled by host scheduling and preemption.
- **Fuel (instruction budget)**: the engine counts executed instructions
  and traps deterministically when the budget is consumed. Wasmtime exposes
  this as `Config.WithFuelConsumption(true)` plus a per-store fuel amount,
  and reports exactly how much fuel a call consumed.

The 50 ms tick budget is the hard constraint: a burning guest must cost
bounded milliseconds per tick, repeatedly, while healthy guests cost
nothing measurable. Fuel is deterministic (unaffected by machine load) and
stops the guest at a defined instruction boundary, which also makes the
budget testable in the unit suite (the fuel fixture exhausts identically on
every run).

## Decision

Every guest call (init, tick, player join, shutdown) runs under a fresh
fuel budget,
default 1,000,000 instructions, set per call before invocation. Exhaustion
returns `ModRunResult` with status `FuelExhausted`; the module stays loaded
for the next call. Per-mod manifests may lower the budget but not raise it
above the 50,000,000-instruction host ceiling (enforced by the manifest
parser; see docs/ABI.md, per-mod manifests).

## Consequences

Easy: deterministic, testable protection; per-call accounting is visible in
`wasm status`. Foreclosed: calls cannot be preempted mid-instruction by a
timer, so a pathological single instruction cannot be interrupted anyway
(the ceiling keeps the worst case bounded). Honest downside: fuel is
approximate cost, not wall time; the mapping shifts across engine versions,
so budgets may need retuning when Wasmtime upgrades. Revisit if a future
Wasmtime binding exposes a robust epoch-based interrupt and the game needs
sub-budget preemption.
