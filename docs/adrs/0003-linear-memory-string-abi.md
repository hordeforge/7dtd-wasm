# ADR 0003: Strings cross the ABI as linear-memory pointers, never managed handles

## Status

Accepted (2026-08-24).

## Context

Guests and host must exchange strings (log lines, settings, chat). The
candidate mechanisms:

- **Managed handles**: pass an id the host resolves to a .NET object. This
  leaks .NET object identity into the guest ABI and invites handle
  confusion; it also requires a guest-side handle table with lifetimes.
- **WIT / component model**: the principled long-term answer, but it
  requires a component toolchain for guests and a newer linking path than
  the MVP's raw modules.
- **Linear-memory pointers**: the guest passes `(pointer, length)` pairs
  into its own memory; the host reads exactly that range and writes into
  guest-provided output buffers. WASI itself uses this model.

The host can already read and write guest memory through the binding's
`Caller.GetMemory` and `Memory.ReadString` / `WriteString`, and the guest
side is trivially expressible in Rust with a scratch buffer. The rule that
makes it safe: the host never holds a guest pointer across calls, and never
touches memory outside the given range.

## Decision

All string parameters and results in the `hordeforge` ABI are
`(i32 pointer, i32 length)` pairs into the guest's linear memory. Host
imports read within the given range; `get_setting` writes into a
guest-provided output buffer and returns the byte count. Guests share one
scratch buffer and must not keep pointers from one call into the next.

## Consequences

Easy: no handle tables, no object identity leaks, works with any guest
language, testable with multi-byte UTF-8 round trips. Foreclosed: the
ergonomics of a component-model ABI (deferred, see docs/ABI.md versioning
section). Honest downside: host-side code must bound-check every range, and
a guest bug can still corrupt its own memory (which is the guest's
business, not the host's). Revisit when the component model toolchain
matures and the ABI gains a versioned form.
