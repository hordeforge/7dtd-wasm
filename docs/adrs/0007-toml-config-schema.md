# ADR 0007: Mod config is TOML, following the zdtd-server conventions

## Status

Accepted (2026-08-24).

## Context

Mods need operator-facing config: host limits (fuel, memory) and behavior
settings (which player name to watch). The workspace has an established
config style in the sibling `zdtd-server` project (docs/RULES_CONFIG.md,
ADR 0021): TOML files with snake_case keys, `[section]` groups, keys
auto-bound to struct fields with defaults identical to the code, and a
documented load order (code -> shared config -> per-item overlay).

Candidates for our mods:

- **The existing wasm-mod.json + a line-format settings file**: two ad hoc
  formats, neither matching the workspace convention.
- **A TOML config mirroring zdtd**: `wasm-mod.toml` per mod and a shared
  `wasm.toml`, with `[limits]` and `[settings]` sections. This is the same
  schema family an operator already knows from the sibling project.

The guest side stays unchanged in spirit: guests cannot read files (they
are sandboxed), so `[settings]` values are served through the existing
`get_setting` host import, now resolved per calling mod. The TOML parsing
stays dependency-free (MiniToml): the trust boundary does not grow with a
TOML library dll. ADR 0005 recorded the same reasoning for the JSON
manifest and was superseded when that format was dropped.

## Decision

Mod config is TOML: per-mod `Mods/Wasm/<id>/wasm-mod.toml` with `[limits]`
(fuel_per_call, max_memory_bytes) and `[settings]` (served to the guest),
plus shared `Mods/Wasm/wasm.toml` (`[limits]` applied at host start,
`[settings]` re-read on change). Load order: host code defaults ->
wasm.toml -> wasm-mod.toml. The shared file replaces the code-default
limits at start, so an operator may raise them (the fps_bot path in the
ADR 0004 amendment needs that); a manifest's fuel_per_call overrides the
effective default within the parser ceiling, and its max_memory_bytes can
only tighten.
Hook export names are exactly the zdtd plugin hooks (on_enable,
on_tick, on_player_join, on_shutdown), exported bare like zdtd. The
deprecated JSON manifest is no longer accepted: only `wasm-mod.toml` is
read (see docs/ABI.md, per-mod manifests).

## Consequences

Easy: operators learn one schema across both projects; settings are
per-mod and retunable without rebuilding; the schema is documented in
docs/CONFIG.md. Foreclosed: TOML features outside the MiniToml subset
(multi-line strings, dotted keys). Honest downside: a hand-rolled TOML
parser must stay in sync with the documented subset, and the
calling-mod-aware get_setting adds a small amount of host plumbing.
Revisit if the manifest needs a real schema or a TOML library becomes
justified.

## Amendment (2026-09-28): a fifth hook, on_admin_command

`on_admin_command(cmd_ptr, cmd_len, out_ptr, out_cap) -> i32` joined the
hook set: the export name is the zdtd plugin name, and the host resolves
and signature-checks it at load exactly like the other four, reporting
`WasmMod.HasAdminCommandHandler`. The console wiring is not shipped, so
a guest that exports it is validated and then never called
(docs/ABI.md, stage 3). The decision is unchanged: hook export names are
the zdtd plugin hooks, exported bare.
