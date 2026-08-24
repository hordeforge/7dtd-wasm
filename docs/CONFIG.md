# Mod config (TOML schema)

Guest mods are configured with TOML files that follow the same conventions
as the sibling `zdtd-server` project (its `zdtd.toml` / mode packs, bound by
`src/util/toml_bind.zig`):

- **snake_case** keys everywhere.
- **[section] groups** carry related tunables (`[limits]`, `[settings]`).
- A key that is not in the file keeps its **code default**; moving a value
  onto the config surface is never a retune (same rule as zdtd's
  `RULES_CONFIG.md`).
- **Load order** (mirroring zdtd ADR 0010): host code defaults -> shared
  `Mods/Wasm/wasm.toml` -> per-mod `Mods/Wasm/<id>/wasm-mod.toml`. Each
  layer can only tighten what the layer below set.
- A **new tunable is a new field**, not a new parse arm: the host binds the
  file onto `ModManifest` struct fields, so adding a supported key means
  adding a field in one place.

## Files

| File | Owns | Re-read at runtime |
|---|---|---|
| `Mods/Wasm/wasm.toml` | Shared `[limits]` (host defaults) and `[settings]` (cross-mod) | settings yes, limits at host start |
| `Mods/Wasm/<id>/wasm-mod.toml` | That mod's `[limits]` and `[settings]` | on `wasm reload <id>` |

## wasm-mod.toml (per mod)

```toml
name = "boss-zig"            # informational; the folder name is the mod id
description = "Boss watcher"
version = "0.1.0"

# The mod id (the folder name under Mods/Wasm) must be a plain folder name:
# no path separators, no dot-only segments, no control characters. Invalid
# folders are skipped with a warning at load.

# Host-enforced caps. A manifest can only tighten the host defaults:
# fuel_per_call above the host ceiling (50,000,000) is rejected at load.
[limits]
fuel_per_call = 1000000
max_memory_bytes = 33554432

# Operator policy served to the guest through the get_setting host import.
# The guest's own [settings] win over shared settings with the same key.
[settings]
boss_name = "maci"
```

## wasm.toml (shared)

```toml
# Host defaults: the engine is created with these; per-mod [limits] can
# only tighten them further.
[limits]
fuel_per_call = 1000000
max_memory_bytes = 33554432

# Shared settings, served to every guest via get_setting.
[settings]
greeting = "hello survivor"
```

The example keeps the 32 MiB default. The `wasm.toml` staged by
`make dist` (from `samples/wasm.toml.example`) raises `max_memory_bytes`
to the wasm32 ceiling (4294967296) so plugins built without a declared
maximum load unmodified; see docs/ABI.md, "Modules without a declared
memory maximum".

## Supported TOML subset

Comments (`#`), top-level `key = value`, `[table]` and `[table.sub]`
headers, basic `"..."` strings with escapes, literal `'...'` strings,
integers, floats, booleans, and arrays of scalars. Multi-line strings and
dotted keys are not supported. The parser is dependency-free (`MiniToml`,
ADRs 0005 and 0007) and rejects anything outside this subset with a
specific error; the bridge skips the module and logs the reason.

## Settings resolution

`get_setting(key, out, cap)` (host import, docs/ABI.md) resolves in this
order, per calling mod:

1. the mod's own `[settings]` from its `wasm-mod.toml`
2. shared `[settings]` from `wasm.toml` (re-read when the file changes)
3. not found (-1), so the guest can fall back to its code default

The host tracks the calling mod per call, so two mods can use the same
setting key with different values.
