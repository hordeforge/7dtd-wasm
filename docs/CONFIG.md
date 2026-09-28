# Mod config (TOML schema)

Guest mods are configured with TOML files that follow the same conventions
as the sibling `zdtd-server` project (its `zdtd.toml` / mode packs, bound by
`src/util/toml_bind.zig`):

- **snake_case** keys everywhere.
- **[section] groups** carry related tunables (`[limits]`, `[settings]`).
- A key that is not in the file keeps its **code default**; moving a value
  onto the config surface is never a retune (same rule as zdtd's
  `RULES_CONFIG.md`).
- **Load order** (mirroring zdtd ADR 0010): host code defaults -> the shared
  `wasm.toml` (`Mods/Wasm/wasm.toml`, or the first staged modlet tree that
  carries one when the top-level file is absent) -> per-mod
  `Mods/Wasm/<id>/wasm-mod.toml`. Shared `[limits]` replace the code
  defaults at host start, so an operator may raise them; a manifest's
  `fuel_per_call` overrides the effective default (bounded by the
  50,000,000 instruction parser ceiling, not by the shared value, so a mod
  can raise it), and its `max_memory_bytes` can only tighten it.
  A mod that asks for more fuel than the shared value gets is named in the
  log at load, so the override is visible rather than inferred. A
  `max_memory_bytes` above the effective ceiling does not bind and is named
  in the load log as a warning: the shared cap stays in force, so keep the
  per-mod value at or below it.
- **Unknown keys** are tolerated outside `[limits]` (a manifest written for a
  newer host still loads) and rejected inside it, where a typo such as
  `fuel_percall` would otherwise leave the operator believing a cap is in
  force that the engine never applies. An unknown key fails the same way a
  malformed one does: per mod, the module is skipped; for the shared file,
  see below. A tolerated top-level key is named in the load log rather than
  vanishing, so `fuel_per_call` written above `[limits]`, or a section header
  the host does not know, reaches the operator instead of leaving the engine
  on a default nobody wrote.
- **A bad `wasm.toml` is not silently replaced by the defaults.** The shared
  file is the only place the host limits come from, so a file that exists
  but cannot be parsed aborts the bridge start: the server keeps running and
  no guest loads until the file is fixed. Falling back to the code defaults
  would hand every guest a fuel budget and memory ceiling nobody wrote.
- `wasm status` prints the limits the engine is actually running under (fuel
  per call, memory ceiling, module size cap, guest stdio), each module's
  effective fuel per call, and the same host line is logged at start. That is
  the check that the layering produced the limits the operator intended.
- **The shared file in force is named, not assumed.** `wasm status` and the
  start log both print the `wasm.toml` the limits and settings come from, or
  `none, code defaults in force` when no tree carries one. A staged modlet
  can supply the file when the top-level `Mods/Wasm/wasm.toml` is absent
  (T5 in `docs/THREAT_MODEL.md`), so the limits line on its own cannot say
  whose configuration the engine is running under. The answer is re-checked
  against the filesystem at print time, so a file created after start shows
  up without a restart.
- A **new tunable is a new field**, not a new parse arm: the parser binds
  the file onto `ModManifest` struct fields, so adding a supported key means
  adding a field in one place, and adding one to `[limits]` also means adding
  it to the closed key list the manifest parser accepts. A new top-level key
  joins the informational set the parser recognizes, or every load logs it as
  ignored.

## Files

| File | Owns | Re-read at runtime |
|---|---|---|
| `Mods/Wasm/wasm.toml` (or the first `<modlet>/Wasm/wasm.toml`) | Shared `[limits]` (host defaults) and `[settings]` (cross-mod) | settings yes, limits at host start |
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
at `on_enable`. The file's contents are cached per mod id from load, so
the host reads it once rather than per `zdtd.config` call, and
`wasm reload <id>` is what picks up an edited file. A module loaded
outside the normal scan resolves the same file on its first config
import; only a file that is present but unreadable (locked, oversize,
mid-write) is retried, so a transient IO error is not remembered as
"this mod has no config" for the life of the server.

## wasm-mod.toml (per mod)

```toml
name = "boss-zig"            # informational; the folder name is the mod id
description = "Boss watcher"
version = "0.1.0"

# The mod id (the folder name under Mods/Wasm) must be a plain folder name:
# no path separators, no colons, no dot-only segments, no control
# characters, and nothing a filesystem renames or reserves: a name ending in
# a space or a period, and the Windows device names (CON, PRN, AUX, NUL,
# COM1-9, LPT1-9, before any extension). Invalid folders are skipped with a
# warning at load. Ids are matched to folders by exact spelling on every
# platform, so a console command for "Hello" does not load the "hello" module
# on a case-insensitive filesystem.

# Host-enforced caps. fuel_per_call overrides the effective default
# (must be at least 1 and at most 50,000,000); max_memory_bytes can only
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
# override fuel_per_call (any value from 1 up to the 50,000,000 parser
# ceiling) and tighten max_memory_bytes; see the load-order rule above.
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

A `[settings]` value must be a scalar (string, integer, float, or boolean);
an array or a nested table there fails the load with
`settings.<key> must be a scalar`. A `[limits]` value must be an integer.

`wasm.toml` and `wasm-mod.toml` are read as strict UTF-8 with a leading BOM
stripped, and each is capped at 1 MiB. A file in another encoding, or over
the cap, is not read: the per-mod module is skipped, and a shared file that
cannot be read aborts the bridge start as described above.

Strings must be well-formed Unicode: a lone surrogate, raw or written as a
`\uXXXX` escape, is rejected, because it has no UTF-8 form and could not
round-trip the guest string ABI. A raw control character in a quoted string
is rejected for the same reason; the tab is the only one TOML allows
unescaped, so write the others as `\n`, `\r`, `\t`, or `\uXXXX`. A float
outside binary64 (`1e999`) is rejected the same way: it is not a number any
guest can hold, and whether it parses at all would otherwise depend on the
runtime the host is built for.

## Settings resolution

`get_setting(key_ptr, key_len, out_ptr, out_cap)` (host import, docs/ABI.md)
resolves in this order, per calling mod:

1. the mod's own `[settings]` from its `wasm-mod.toml`
2. shared `[settings]` from `wasm.toml` (re-read when the file changes)
3. not found (-1), so the guest can fall back to its code default

The shared file is polled on the 500 ms probe interval a guest's
`get_setting` traffic is throttled to, and a change is recognized by the
file's last write time *and* its length, so a rewrite that carries the
old timestamp (a restore, a copy that preserves times) still reaches
guests. A `wasm.toml` that does not parse is reported once and the
previous shared settings keep serving; it is not re-read and re-parsed
on every probe, and a later fixed save is picked up. Deleting
`wasm.toml` while the server runs is not an error path: the shared
settings are dropped and the probe identity reset, so guests fall back
to their own code defaults with nothing in the log. A re-created file is
picked up by a later probe.

The host tracks the calling mod per call, so two mods can use the same
setting key with different values.
