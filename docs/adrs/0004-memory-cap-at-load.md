# ADR 0004: Enforce the memory cap at load from the declared maximum

## Status

Accepted (2026-08-24).

## Context

A guest must not be able to grow its memory without bound. Candidate
mechanisms in the Wasmtime 44 binding:

- **Pooling allocator with a max memory size**: the principled engine-level
  cap, but `PoolingAllocationConfig` is not exposed by the .NET binding in
  this version, so it is not available to the host.
- **Runtime observation**: reject a guest only after it grows too far. Too
  late: the allocation already happened, and the host cannot distinguish
  "about to exhaust the machine" from "about to trap".
- **Declared-maximum validation**: WebAssembly semantics make memory growth
  past the module's declared maximum impossible. If the host requires a
  declared maximum and rejects any module whose declared maximum exceeds
  the cap, the cap is enforced by construction, at load time, with zero
  runtime cost.

The binding exposes the declared maximum through `Module.Exports` /
`Module.Imports` as `MemoryExport.Maximum` (in 64 KiB pages), so the check
is one metadata read. Guests built with the shared toolchain config declare
32 MiB via the `--max-memory` linker flag, matching the default host cap.

## Decision

`LoadModule` reads the guest's declared memory maximum and rejects the
module when it is absent or above the effective cap (the host
`StaticMemoryMaximumBytes` default of 32 MiB, lowered by any per-mod
manifest ceiling). Memory growth beyond the declared maximum is then
impossible by WebAssembly semantics.

## Consequences

Easy: a hard, load-time, zero-cost cap; oversized modules fail with a
specific message; tests cover the rejection path. Foreclosed: guests that
rely on dynamic growth beyond the cap (they must declare their real
maximum). Honest downside: the engine's own static memory size must also be
configured to match (the host sets `WithStaticMemoryMaximumSize`), and a
guest that declares a large but legal maximum is still trusted to stay
within it by construction, not by the host watching it. Revisit if the
binding exposes the pooling allocator with a max memory size, which would
back the same policy at the engine level.
