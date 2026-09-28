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
  `Mods/Wasm/wasm.toml` -> per-mod `Mods/Wasm/<id>/wasm-mod.toml`. Shared
  `[limits]` replace the code defaults at host start, so an operator may
  raise them; a manifest's `fuel_per_call` overrides the effective default
  within the host ceiling, and its `max_memory_bytes` can only tighten it.
  A mod that asks for more fuel than the shared value gets is named in the
  log at load, so the override is visible rather than inferred.
- **Unknown keys** are tolerated outside `[limits]` (a manifest written for a
  newer host still loads) and rejected inside it, where a typo such as
  `fuel_percall` would otherwise leave the operator believing a cap is in
  force that the engine never applies. An unknown key fails the same way a
  malformed one does: per mod, the module is skipped; for the shared file,
  see below.
- **A bad `wasm.toml` is not silently replaced by the defaults.** The shared
  file is the only place the host limits come from, so a file that exists
  but cannot be parsed aborts the bridge start: the server keeps running and
  no guest loads until the file is fixed. Falling back to the code defaults
  would hand every guest a fuel budget and memory ceiling nobody wrote.
- `wasm status` prints the limits the engine is actually running under (fuel
  per call, memory ceiling, module size cap, guest stdio), and the same line
  is logged at start. That is the check that the layering produced the limits
  the operator intended.
- A **new tunable is a new field**, not a new parse arm: the parser binds
  the file onto `ModManifest` struct fields, so adding a supported key means
  adding a field in one place, and adding one to `[limits]` also means adding
  it to the closed key list the manifest parser accepts.

## Files

| File | Owns | Re-read at runtime |
|---|---|---|
| `Mods/Wasm/wasm.toml` | Shared `[limits]` (host defaults) and `[settings]` (cross-mod) | settings yes, limits at host start |
| `Mods/Wasm/<id>/wasm-mod.toml` | That mod's `[limits]` and `[settings]` | on `wasm reload <id>` |
| `Mods/Wasm/<id>/config.toml` | That mod's own config, served to the guest verbatim through the `zdtd.config` import | on `wasm reload <id>` |

A staged modlet may carry its own `Wasm/` tree (for example
`Mods/wasm-bridge/Wasm/parachute`); the bridge scans those after the
top-level `Mods/Wasm`, first tree wins per id. A managed test instance can
only stage whole modlets, never loose files under `Mods/`, so the guest
tree ships as one modlet there.

The per-mod `config.toml` is the zdtd self-contained-config convention
(docs/PLUGIN_API.md in the sibling repo): the host never parses it, each
guest owns its format, and a missing file means the guest keeps its
built-in defaults. The unmodified zdtd parachute mod reads its deploy
tuning (`deploy_vy_threshold`, `deploy_delay_ticks`, ...) from this file
at `on_enable`.

## wasm-mod.toml (per mod)

```toml
name = "boss-zig"            # informational; the folder name is the mod id
description = "Boss watcher"
version = "0.1.0"

# The mod id (the folder name under Mods/Wasm) must be a plain folder name:
# no path separators, no colons, no dot-only segments, no control
# characters. Invalid folders are skipped with a warning at load. Ids are
# matched to folders by exact spelling on every platform, so a console
# command for "Hello" does not load the "hello" module on a
# case-insensitive filesystem.

# Host-enforced caps. fuel_per_call overrides the effective default
# (rejected above the 50,000,000 ceiling); max_memory_bytes can only
# tighten the effective cap and must sit between one wasm page (65536)
# and the wasm32 address space (4294967296).
# These two keys are the whole [limits] schema: any other key here (a
# misspelling included) fails the load with the supported names, because
# a limit that does not bind leaves the host cap in force instead.
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
# Host defaults: the engine is created with these. Per-mod [limits]
# override fuel_per_call within the host ceiling and tighten
# max_memory_bytes; see the load-order rule above.
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
ADR 0007) and rejects anything outside this subset with a specific error;
the bridge skips the module and logs the reason.

Strings must be well-formed Unicode: a lone surrogate, raw or written as a
`\uXXXX` escape, is rejected, because it has no UTF-8 form and could not
round-trip the guest string ABI.

## Settings resolution

`get_setting(key, out, cap)` (host import, docs/ABI.md) resolves in this
order, per calling mod:

1. the mod's own `[settings]` from its `wasm-mod.toml`
2. shared `[settings]` from `wasm.toml` (re-read when the file changes)
3. not found (-1), so the guest can fall back to its code default

The host tracks the calling mod per call, so two mods can use the same
setting key with different values.
