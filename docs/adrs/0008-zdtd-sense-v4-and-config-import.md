# ADR 0008: Adopt the zdtd sense v4 snapshot and the self-contained config import

## Status

Accepted (2026-08-29).

## Context

The whole point of the `zdtd` compatibility module (docs/ABI.md) is that
sibling zdtd-server plugins load here unmodified. The sibling moved its
sense snapshot to v4 (ADR 0037 in that repo): magic `ZBS4`, 40-byte
records, with server-derived `vy` and the `wearing_glider` bit, plus a
`config` host import that serves a plugin's own config.toml verbatim.

The parachute mod (zdtd-server/mods/parachute) is built against exactly
that surface: it imports `zdtd.config`, parses the `ZBS4` layout, and
queues the `glide <net_id> <0|1>` verb. Our host still wrote the v3
snapshot (`ZBS3`, 32-byte records) and had no `config` import, so the
unmodified parachute module could not instantiate (unknown import
`zdtd.config`) and, even if it did, would read garbage out of the v3
records and never arm a glide.

We also shipped a stale fps_bot fixture: the committed binary predates
the sibling's v4 bump, so `make fixtures` refreshed it to a module whose
layout our v3 writer could not feed.

## Decision

1. **Sense v4, byte-identical to zdtd** (`SenseSnapshotWriter`): magic
   `ZBS4`, 40-byte records (`net_id, kind, self, alive, pad, x, y, z, hp,
   yaw, vy f32 @28, target_id @32, wearing @36, pad`), events unchanged.
   This is the ABI bump ADR 0037 already made in the sibling: guests built
   for v3 stop working loudly (magic mismatch) instead of misreading.
   `vy` is written as the f32 bit pattern the guests bitcast; the sibling
   server writes an i32 there today, which its own guests cannot parse, so
   the f32 bits are the value that works.
2. **`zdtd.config(out_ptr, out_cap) -> i32`**: serves the calling mod's
   `config.toml` verbatim (min(out_cap, len) bytes, 0 = none), the zdtd
   contract. The host never parses it; each guest owns its format. The
   bridge reads `Mods/Wasm/<id>/config.toml` at module load and caches it
   (invalidated on reload), so a guest looping on the import does not stat
   the disk at call rate.
3. **`queue` gains the `glide` verb and the chat announce**: the bridge
   tracks armed glider flags (ADR 0037) and surfaces them in `wasm status`;
   queue text that is not a servant verb is broadcast as chat, which is how
   the parachute deploy message reaches players ("announce via the stock
   chat broadcast"). The real game has no C2S movement envelope to exempt,
   so the glide flag is tracked authority state, not a physics clamp.
4. **`Entity.motion` and the equipment/item-tag surface are pinned in
   targetcheck** so a real `make bridge-check` validates the new game API
   the sense v4 fields read.

## Consequences

- The unmodified parachute mod loads, reads its config, watches the v4
  sense view, and arms/clears the glide exemption for falling worn players
  (covered by host tests against the real module).
- The fps_bot fixture is refreshed to the sibling's v4 build; `make
  fixtures` now stages both sibling modules.
- The sense layout change is a breaking ABI change for any guest built
  against v3; the only in-repo consumer is the fps_bot fixture, updated
  together (the discipline docs/ABI.md requires).
- A config.toml is optional: a mod without one keeps its built-in defaults
  (the `config` import returns 0).
