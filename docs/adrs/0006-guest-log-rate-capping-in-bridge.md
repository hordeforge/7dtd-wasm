# ADR 0006: Rate cap guest log output in the bridge, not the host

## Status

Accepted (2026-08-24).

## Context

A guest that logs once per tick produces 20 lines per second, forever. That
is not a sandbox escape (fuel bounds each call), but it is an availability
concern: unbounded log growth over a long-lived server. Where to cap:

- **In the host library**: would make the generic embeddable host know
  about time-based rate policy, which conflicts with its deterministic,
  per-call contract, and would force the cap policy into every embedding.
- **In the bridge**: the bridge already maps host API calls onto game
  services; the rate policy is a game-ops concern, and the bridge can
  expose the counters in `wasm status`. The host stays policy-free.

The cap itself: per module id, at most 10 lines per second, measured on a
monotonic clock so clock steps cannot freeze output or open bursts;
excess lines are dropped and counted; every 100th dropped line is logged so
throttling is visible without flooding the log, and the totals appear in
`wasm status`.

## Decision

`GameHostApi.Log` routes every guest log line through
`GuestRateLimiter` (in the net48 bridge), which drops lines past the
per-module per-second cap, counts them, and reports the totals.

## Consequences

Easy: the host stays generic; the policy is visible and inspectable at
runtime; SECURITY.md documents the guarantee. Foreclosed: a single shared
cap policy across all embeddings (each embedding decides). Honest downside:
the limiter is monotonic-clock based and bridge code, so it is exercised in
the acceptance run rather than by the host unit suite. Revisit if the host
API gains a per-module quota mechanism that makes rate policy a host concern.

## Amendment (2026-08-25)

The acceptance run showed the game does not rate limit `ChatMessageServer`
on its own, so the bridge now caps guest chat with the same limiter class:
a second `GuestRateLimiter` instance keyed on the shared source "chat"
(10 messages per second, monotonic clock, global, drops counted and
surfaced in `wasm status`). The decision itself is unchanged: the cap lives
in the bridge, not the host. Evidence: docs/ACCEPTANCE.md, finding 2;
SECURITY.md documents both caps.

## Amendment (2026-08-25): game-side work imports are capped too

The same limiter class now bounds every guest-triggered path whose cost
the fuel budget cannot see because it happens outside the wasm call:
SimCommands at 200 per second per module and sense snapshot fills at
200 per second per module (excess rejected or reported as no data,
counted, surfaced in `wasm status`; documented in docs/ABI.md, "Host-side
bounds on the servant"), plus the failure-log paths (tick dispatch
failures, get_world_time failures) so the diagnostics themselves cannot
flood the log. Each cap is fixed at limiter construction
(`GuestRateLimiter(maxPerSecond)`). The decision is unchanged: all rate
policy lives in the bridge, not the host.
