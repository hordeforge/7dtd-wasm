# Migration guide

Upgrade steps for consumers of this project, per release. Each entry names
the behavior before, the behavior after, and what a consumer has to do.

The changelog (`../CHANGELOG.md`) records every change with its reason. This
document records only the changes that need an action from a reader, grouped
by the kind of consumer that has to act.

## 0.3.1 to 0.4.0

0.4.0 is the next release, cut from the `## Unreleased` section of the
changelog. In this project's 0.x scheme the minor digit carries breaking
changes and the patch digit never does, so a consumer on any 0.1.x or 0.2.x
can take a 0.3.x without reading this file, and 0.4.0 is the first release
whose changes all three kinds of consumer below have to act on.

Nothing in this release changes the guest ABI surface: the import module
names, the export names, their signatures, the sense v4 wire format, and the
on-disk module layout are unchanged. Guests that only use documented imports
rebuild and run.

### Operator: files it reads

- **`[limits]` is a closed table.** A key the host does not know in
  `wasm-mod.toml` or in the shared `wasm.toml` is now a load failure instead
  of being ignored. Before, a typo such as `fuel_per_call` above `[limits]`
  left the code default in force while the operator believed their tighter
  cap was running. Fix the spelling of the key. A misspelled key that
  0.3.1 loaded now fails, and the load log names the key and the supported
  set. Keys outside `[limits]` (a root key, a `[settings]` key) stay
  tolerated, so a manifest written for a newer host still loads; those are
  named in the load log through `ModManifest.IgnoredKeys` instead of
  vanishing.
- **`limits.max_memory_bytes` has a floor of one wasm page (65,536 bytes).**
  A smaller value was accepted per mod, where it could only reject the
  module. In the shared `wasm.toml` the same value became the engine's
  memory ceiling, where the host constructor threw and took the bridge start
  down with it. The parser now rejects anything below one page, so an invalid
  file takes the documented "log it and keep the defaults" path. The upper
  bound is the wasm32 address space (4,294,967,296 bytes), which a
  `make dist` tree still declares. Raise a value that is too low to a real
  page count; a module that needs more memory needs a higher ceiling, not a
  lower one.
- **A shared `Mods/Wasm/wasm.toml` that exists but cannot be parsed now stops
  the host start.** Before, the host fell back to the code defaults, so every
  guest ran under a fuel budget and a memory ceiling the operator never
  wrote. The server keeps running and no guest loads; the log names the file
  and says to fix it and restart. A syntax error 0.3.1 ignored is now a stop.
- **Manifests and `config.toml` are read as UTF-8, strictly.** A file in any
  other encoding (a UTF-16 or UTF-32 BOM, which the .NET reader used to
  switch to silently) now fails its load with the reason instead of loading.
  A UTF-8 BOM is stripped and loads as before. Re-save any file your editor
  wrote in UTF-16.
- **Module ids that cannot name a portable directory are rejected.** An id
  containing the replacement character U+FFFD (a folder name that is not
  valid UTF-8, which is legal on Linux), a lone surrogate, a Windows device
  name (`con`, `nul`, `com1`, and so on, also with a suffix like `aux.wasm`),
  or a trailing space or period is refused at load. Before, such a folder
  either never loaded, with nothing in the log, or produced an id that no
  longer re-encoded to its own directory. Rename the module folder; the id is
  the folder name.
- **A manifest carrying a raw lone surrogate in a basic or literal string
  fails to load.** The `\uXXXX` escape form was already rejected. Such a
  value has no UTF-8 form and cannot round-trip the guest string ABI, so a
  guest that relied on it stops loading until the string is replaced.

### Operator: what the log says

- Guest log lines a guest drives (bot servant commands and their failures,
  the chat-rejection line) go through a per-source rate cap, and the dropped
  totals are reported in `wasm status`. Before, they were bounded only by the
  guest's fuel budget, so a guest could flood the server log. A guest whose
  output is being dropped is visible there.
- The player-join line records the entity id and states that the name went to
  the guests. It no longer writes the player's name into the server log,
  which outlives the session and travels with bug reports.
- Dispatch failures, slow dispatches, and the sense `wearing_glider` read
  report as warnings that name the tick, the module, and the fuel the call
  consumed. Before, several of those were info-level lines naming neither,
  which made a per-guest failure stream unattributable in a busy log.
- `wasm status` reports each module's error count, fuel used, and effective
  fuel per call, plus the shared limits in force (fuel per call, memory
  ceiling, module size cap, and whether guest stdio is inherited). Those
  values were tracked and printed nowhere.

### Embedder: the C# surface

- **Every `WasmModHost` entry point serializes on one internal gate.** The
  "call it from one thread" contract was a comment only; two threads entering
  it could skip or duplicate mods in a dispatch, serve one guest another
  guest's settings, or enter a wasm store that was already running a call
  (the engine aborts the process). The cost is that a second thread blocks
  until the dispatch in flight returns.
- **`ModIds`, the dispatch results, and `ShutdownFailures` are per-call
  copies.** Before, they were host-owned live views, so a list handed to one
  caller could be refilled by a dispatch running on another thread, and
  `ShutdownFailures` handed out the list `Dispose` fills under the gate. A
  consumer that read one and expected it to track later host state now holds
  the snapshot the call returned; re-read it after each dispatch.
- **`on_enable` latches once per load generation.** `WasmMod.Init` records
  the enable, so a second `DispatchInit`, or an `InitModule` after the load
  scan, reports `Ok` without calling the guest again; `WasmMod.Enabled` reads
  the latch back. An embedder that relied on a repeated enable to re-run the
  guest's setup has to unload and reload the mod for that. A failed enable
  does not latch and stays retryable.
- **Configuration values the host cannot honor are rejected at construction.**
  `MaximumStackBytes` above the engine's 2 MiB caller-stack limit used to
  abort the process from a panic inside Wasmtime, which no caller could
  catch, and `FuelPerCall` above the 50,000,000 instruction ceiling the
  manifest parser enforces was accepted, so only the file path bounded how
  long one guest call could hold the game loop. Both now throw from the
  `WasmHostConfig` construction, naming the offending value.
- **A memory ceiling past the wasm32 address space is rejected where it is
  read**, in the manifest parser and in the host's own config validation.
  Before, it became the engine's static memory maximum and failed the host
  construction instead of the documented "invalid file, keep defaults" path.
- **A module whose directory resolves to nothing is refused rather than read
  from the server process's working directory.** `Path.Combine("", name)` is
  a relative path, so a missing tree root could have had its manifest read
  from wherever the process happened to be.
- **New surface, none of it removed:** `ModRunResult.GuestStatus` (the status
  code the guest export returned, 0 ok, 1 not implemented, 2 internal error)
  and a constructor overload that takes it, so a consumer can tell those
  apart without matching `ModRunResult.Message` text;
  `AbiConstants.StatusNotImplemented` and `AbiConstants.StatusInternalError`;
  `WasmMod.Enabled`; `WasmModHost.InitModule`, `MaxModuleSizeBytes`,
  `WasmPageBytes`, `InheritGuestStandardStreams`, `FuelPerCall`,
  `StaticMemoryMaximumBytes`, and `ShutdownFailures`;
  `TickTelemetry`; `ModManifest.IgnoredKeys`; `ManifestReadException` (an
  `InvalidOperationException` with `Path` and `Reason`, thrown where a
  concatenation-only message used to be) and `WasmManifestException` (a
  `WasmModLoadException` subtype, so a malformed manifest is distinguishable
  from a refused module; `WasmModLoadException` is no longer sealed). Both
  exception types derive from types a caller already catches, so the finer
  catches are optional.

### Guest author

- **`glide <net_id> <0|1>` only arms a live player.** An armed flag applies
  the glide buff and clamps the entity's descent, so accepting any world net
  id let a guest steer entities it does not own, other players included. A
  non-player net id is now refused with a `glide (not a player)` line. A
  guest that armed a non-player id has to arm its own player instead.
- **A glide flag belongs to the module that armed it.** The flag table was
  keyed by net id alone, so a second module could clear the first one's
  armed flag and its descent clamp. The first module to change a player's
  flag holds it, and another module's `glide <net_id> 0` is refused and
  logged as `glide (not owner of <net_id>)`. A guest that shared a player's
  flag with another module, or cleared a flag another module armed, has to
  keep to its own. Unloading or reloading a module drops the flags it armed,
  so a fresh instance starts from an empty set.
- **Invisible text in log, chat, and set-name output is replaced.** The
  Unicode line and paragraph separators (U+2028, U+2029), the bidi controls
  (U+202A to U+202E, U+2066 to U+2069), and the zero-width no-break space
  (U+FEFF) are replaced with '?' like a raw newline, so a guest can no longer
  split or reorder a log line, a console line, or a global chat message with
  a character the C0 filter never saw. A guest that relied on those code
  points passing through gets '?'. Zero-width space, joiner, word joiner,
  and variation selectors are left alone, so emoji sequences and non-Latin
  scripts still render.
- **Sense snapshots are a function of the entity set alone.** Records are
  selected on the net id and come out in ascending order (the lowest ids that
  fit in the 41-record window), where before they were the first 41 entities
  in the game's own list order, so the same world could hand a guest a
  different snapshot from run to run. The wire format (v4, 40-byte records)
  is unchanged; only which entities appear, and in what order, is now
  stable. Selection is also much faster above 2000 alive entities, with a
  differential test pinning the two paths to the same list.
- **The `config` import's cut lands on a character boundary.** It stopped at
  `min(out_cap, len)` bytes, which could land inside a multi-byte UTF-8
  character and hand the guest bytes it decodes as U+FFFD. A guest that
  sizes a buffer for a config slice can read the full text now when the
  buffer allows it.
- **Dispatch failures and slow dispatches are visible per guest.** Both are
  warnings naming the tick, the mod, and the fuel the call consumed.

### Removals, and what replaces them

Nothing was removed from the published library's public surface in this
release. Earlier removals and their replacements, for a consumer upgrading
from further back:

| Removed in | Symbol | Replacement |
|---|---|---|
| 0.3.1 | `WasmModHost.TryInit(id, out result)` | a dispatch walk, or `WasmModHost.InitModule(id)` for a single freshly loaded mod |
| 0.3.1 | `BotServant.ClearGlide()` (bridge, not the package) | `Glide` status, and `glide <net_id> 0` |
| 0.3.0 | the JSON manifest form `wasm-mod.json` and the MiniJson parser behind it | `wasm-mod.toml`, same fields with snake_case keys |
| 0.3.0 | `AbiConstants.SettingOk` | the settings precedence in `docs/CONFIG.md` |

`WasmModHost.TryInit` shipped in a patch slot, which under this project's
rule carries no breaking change, so its version number did not warn a
consumer. Its changelog entry is marked `(breaking)` for the audit trail.
