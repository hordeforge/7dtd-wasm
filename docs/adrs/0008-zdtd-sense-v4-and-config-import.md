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
   `config.toml` verbatim, copying min(out_cap, len) bytes cut at a UTF-8
   character boundary; 0 means no bytes were served, which covers a mod
   with no config, an empty `out_cap`, and a buffer too small to hold the
   first character. The host never parses it; each guest owns its format. The
   bridge reads `Mods/Wasm/<id>/config.toml` at module load and caches it
   (invalidated on reload), so a guest looping on the import does not stat
   the disk at call rate.
3. **`queue` gains the `glide` verb and the chat announce**: the bridge
   tracks armed glider flags (ADR 0037) and surfaces them in `wasm status`;
   queue text that is not a servant verb is broadcast as chat, which is how
   the parachute deploy message reaches players ("announce via the stock
   chat broadcast"). The real game has no C2S movement envelope to exempt,
   so the glide flag is tracked authority state, not a movement-packet
   exemption. The armed flag does drive authority state the guest cannot
   set itself: it applies the glide buff and clamps the descent (amendment
   below).
4. **The equipment/item-tag surface the `wearing_glider` bit reads is
   pinned in targetcheck** (`Equipment.GetItems`, `ItemValue.ItemClass`,
   `ItemClass.HasAnyTags`) so a real `make bridge-check` validates that
   game API. `Entity.motion` is deliberately not pinned: the stock server
   does not populate it for remote players, so `vy` is derived from the
   per-tick position history.

## Consequences

- The unmodified parachute mod loads, reads its config, watches the v4
  sense view, and arms/clears the glide exemption for falling worn players
  (covered by host tests against the real module). The armed flag applies
  the glide buff and pins the descent (amendment below).
- The fps_bot fixture is refreshed to the sibling's v4 build; `make
  fixtures` now stages both sibling modules.
- The sense layout change is a breaking ABI change for any guest built
  against v3; the only in-repo consumer is the fps_bot fixture, updated
  together (the discipline docs/ABI.md requires).
- A config.toml is optional: a mod without one keeps its built-in defaults
  (the `config` import returns 0, as it also does for an empty `out_cap` or
  a buffer too small for the first character).

## Amendment (2026-09-28): the armed flag also applies a buff and clamps descent

The decision text above originally ended "the glide flag is tracked
authority state, not a physics clamp", and the code contradicted it from the
commit that added this record: an armed flag applies the
`buffParachuteGlide` buff and pins the entity's descent to the sink rate
(`ClampGlideDescent` in `src/GameBridge/Bridge/BotServant.cs`, driven off
the sense scan). The buff is what the client slow-fall patch keys on, the
clamp is its authority-side half. The changelog, docs/ABI.md, and
`make bridge-check` (which pins `EntityAlive.Buffs`,
`EntityBuffs.AddBuff/RemoveBuff/HasBuff`) describe what the code does, so
the decision text was the wrong half. The sense v4 decision stands.

An armed flag reaches any live player, not only the calling module's, so
`glide <net_id> 1` on another player's net id steers that player. That is
recorded, not endorsed: docs/THREAT_MODEL.md, "Tampering", and
docs/ABI.md name it as a known capability of the queue import, gated to
live players only.
