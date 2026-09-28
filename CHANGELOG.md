# Changelog

All notable changes to this project are recorded here. The format follows
the sibling projects: versioned sections with dated entries, newest first.
Codename: Quarantine (7dtd-wasm).

## Unreleased

This cycle carries breaking changes, so it ships as 0.4.0: in this project's
0.x scheme the minor digit carries breaking changes and the patch digit never
does (CONTRIBUTING, "Versioning and releases"). Cutting it as 0.3.2 would put
a third set of breaking changes in a patch slot, after 0.1.3 and 0.3.1. What
a consumer has to do about each one is in `docs/MIGRATION.md`, grouped by
operator, embedder, and guest author.

### Added

- The heartbeat names the guests that failed since the previous one
  (`HordeForge.WasmHost.Core.FailureTally`). The per-tick failure lines are
  rate capped and the per-mod counters in `wasm status` are lifetime
  totals, so neither separates a guest failing on every tick from one that
  trapped once an hour ago: a reader of the log had no way to tell which
  guest the next warning belongs to.
- `WasmModHost.LogSourceFor(prefix, modId)` names the log source tag the
  host attributes a module's guest lines under. The bridge keyed its per
  module log rate limiter on the same string and recomposed it, so the two
  could drift apart: the limiter would drop a window under a key it never
  wrote to, and a module reloaded inside the second its previous
  generation saturated the cap would start inside that window.
- Per-guest dispatch cost: `WasmMod.LastCallMs` records what the most recent
  call to a guest cost in wall clock, and the slow-dispatch warning names the
  guest that spent the frame. The aggregate cost said the frame was lost, not
  who spent it, so the warning could not be acted on without a profiler. The
  cost is read through `HordeForge.WasmHost.Core.MonotonicTimer`, and
  `WasmModHost(api, config, timer)` takes one, so a run driven from a virtual
  clock reports that clock's cost instead of the process stopwatch's. The
  timer moved from the bridge into the host library for this: the per-call
  cost lives in the host, so a bridge-owned timer could not reach it.
  `BridgeHost.Timer` keeps the type and now governs both the dispatch
  measurement and every guest call the host measures.
- `wasm status` reports each module's `errors` count and total `fuel used`.
  Both were tracked per guest and printed nowhere, so a guest that burns its
  budget and reports errors read like one that traps.
- A top-level key or section the manifest parser does not read is now named
  in the load log, per mod and for the shared `wasm.toml`
  (`ModManifest.IgnoredKeys`). Unknown keys stay tolerated so a manifest
  written for a newer host still loads, but a `fuel_per_call` written above
  `[limits]`, or a section header the host does not know, no longer vanishes:
  the engine would run on the default while the operator believed a cap was
  in force.
- `wasm status` reports each module's effective fuel per call
  (`WasmMod.FuelPerCall`), the value the engine actually charges, so a
  per-mod override is checkable after the load line has scrolled away.
- `make pack` builds the publishable library as a NuGet package under
  `artifacts/packages/`, and `tools/packcheck.py` gates the package metadata
  it depends on (identity fields, the license expression against LICENSE,
  the readme, and the third-party notices). Both run on `make check-ci`, so a
  package that stops packing or stops declaring its terms fails the gate
  instead of the publish step. The README now ships inside the package, which
  is what the nuget.org listing renders.
- `docs/THREAT_MODEL.md`: the repository threat model, risk-ranked, with
  every entry point, trust boundary, per-boundary threat, and mitigation
  mapped to the code that implements it, and the gaps named separately from
  the controls.
- Per-tick dispatch telemetry: a once-a-minute heartbeat, a warning when a
  dispatch overruns half a frame, and the run's cost and failure totals in
  `wasm status` and at shutdown (`HordeForge.WasmHost.Core.TickTelemetry`).
  Guest dispatch failures are now warnings that name their tick instead of
  info-level lines without one.
- The limits in force are visible: `wasm status` prints fuel per call, the
  memory ceiling, the module size cap, and whether guest stdio is
  inherited, and the same line is logged at host start. `WasmModHost`
  reads them back from the configuration the engine was built with.
- Guest-driven host log lines (bot servant commands and their failures, the
  chat-rejection line) go through a per-source rate cap, with the dropped
  totals in `wasm status`. They were bounded only by the guest's fuel
  budget and could flood the server log.
- `make toolchain`: populates the in-project rustup toolchain (`.cargo/`,
  `.rustup/`) from rustup on PATH, so a fresh clone has one documented way to
  get a working guest build. CI runs the same target instead of its own copy
  of the rustup commands.
- `make test TEST_FILTER=<expr>`: run a single test or class through the
  documented target rather than the whole suite.
- `global.json` declaring the .NET 8 SDK the tree builds against, matching
  what CI installs.
- `WasmModHostConcurrencyTests`: dispatch, load, and unload driven from
  several threads at once, asserting no mod is skipped or duplicated in a
  tick, that the settings import never answers with another mod's value, and
  that the registry and the load order agree afterwards.
- `ModRunResult.GuestStatus`: the status code the guest export returned
  (0 ok, 1 not implemented, 2 internal error), so a consumer tells those
  apart without matching `ModRunResult.Message` text. A new constructor
  overload takes the code; the existing one reports `StatusOk`.
- `AbiConstants.StatusNotImplemented` and `AbiConstants.StatusInternalError`,
  the guest return codes `docs/ABI.md` and the guest SDK already define.
- `ManifestReadException` (an `InvalidOperationException`) with `Path` and
  `Reason`, thrown by `ManifestFiles.ReadRequired` instead of a
  concatenation-only message.
- `WasmManifestException`, a `WasmModLoadException` subtype thrown by
  `ModManifest.ParseToml`, so a malformed manifest is distinguishable from a
  refused module. `WasmModLoadException` is no longer sealed.
- `guest-common`: the `zdtd` import surface (`queue`, `sense`, `query`,
  `config`) with safe wrappers `queue_command`, `sense_snapshot`,
  `query_text`, and `config_text`, plus the `on_player_join` and
  `on_admin_command` export-name constants and the queue and query status
  codes. A Rust guest no longer hand-declares the externs documented in
  docs/ABI.md.
- `ruff format` as the Python formatter, run in check mode by
  `make check-ci` next to `ruff check tools`.
- `THIRD-PARTY-NOTICES.md`: licenses and attribution for everything the
  modlet redistributes (Wasmtime, Apache-2.0 WITH LLVM-exception, plus the
  MIT-licensed .NET Foundation closure). `make dist` stages it next to the
  binaries it covers and the `HordeForge.WasmHost` NuGet package embeds it,
  so Apache-2.0 redistribution requirements travel with the artifacts.
- Public surface gate for the published library (`tools/apicheck.py`, in
  `make check`, with its baseline in `tools/api-surface.txt`): every
  public and protected member of `HordeForge.WasmHost` is recorded, and
  any difference, a removal included, fails until the minor digit is
  bumped, the changelog entry is written, and the baseline is regenerated
  with `--update`. A member's body is not surface, so an implementation
  change does not touch the baseline. This is the guard that was missing
  when `WasmModHost.TryInit` came off the surface in the 0.3.1 patch slot.
- Seeded fuzz harnesses for the sense snapshot writer
  (`SenseSnapshotWriter.Write`) and the guest string cut (`Utf8Prefix.Length`),
  the two guest-facing boundaries that had no harness. The snapshot writer is
  driven with random entity, damage, and bot-info counts, integer extremes,
  and every float class from NaN to the denormals, against a buffer the guest
  sized plus a guard region, so a miscounted record becomes a failed assertion
  rather than a write past `out_cap` in the server process; the string cut is
  driven with hostile bytes (overlong forms, truncated sequences, lone
  continuations) and a guest-chosen capacity, and asserts the cut stays inside
  the capacity, is the longest one that fits, and never splits a character.
- Seeded fuzz harnesses for the manifest parser (`ModManifest.ParseToml`)
  and the guest-text entry points (`TextSanitizer.Clean`, `ModId.IsValid`),
  run by `make test`. They assert the load contract, a per-input time
  budget, manifest determinism, and sanitizer idempotence rather than only
  "did not crash". `HORDEFORGE_FUZZ_SEED` and `HORDEFORGE_FUZZ_ITERATIONS`
  reseed and lengthen a run; every failure prints its replay command.
- Seeded fuzz harnesses for the two untrusted-input entry points that had
  none: the manifest file decoder (`ManifestFiles.TryRead`, driven with real
  byte payloads: overlong forms, bare continuations, CESU-8 surrogate halves,
  sequences past U+10FFFF, UTF-16 and UTF-32 BOMs, truncation, byte flips
  into the shipped manifests) and module tree resolution
  (`ModuleRoots.ResolveDir` / `ResolveFile`, driven with hostile ids and file
  names against a staged tree with a decoy above the roots). Each asserts the
  contract rather than survival: strict UTF-8 and the size bound, a reason on
  every refusal, the typed load exception for a payload that decodes but does
  not parse, and a resolved path that is inside a root, on disk, and spelled
  exactly as the id named it.
- CI builds the host library and runs its test suite on Windows as well as
  Linux. The net48 bridge is loaded inside a Windows game process, so every
  path, case comparison and encoding rule in `HordeForge.WasmHost` decides
  what a Windows install does; a Linux-only matrix asserted those rules
  without ever running them. The job calls `dotnet` directly because the
  Windows runner image has no GNU make; the Makefile targets are unchanged
  and README still records them as unproven there.
- `HordeForge.WasmHost.Core.GlideOwnershipRegistry` maps each armed glide
  net id to the module that armed it, the glide counterpart of
  `BotOwnershipRegistry`. The rule lives in the host so it is testable
  without a game.
- The start log and `wasm status` name the shared `wasm.toml` the engine's
  limits and cross-mod settings come from, or that no tree carries one. A
  staged modlet supplies the file when the top-level `Mods/Wasm/wasm.toml`
  is absent, so the limits line alone could not say whose configuration
  the server is running under.

### Performance

- `SenseRecordPicker` no longer sorts the world's whole alive entity set on
  every sense request. Only the lowest 41 ids can fit in a snapshot, so
  above 2000 alive entities it selects them with a bounded max-heap and
  sorts just the retained window: 6x faster at 6000 entities and 12x at
  20000 (measured; 872 us to 70 us per request at 20 TPS). Below the
  threshold the sort still wins on its own constant factor and is kept.
  Both paths return the same list, pinned by a differential test against
  the sort-and-truncate it replaces.
- The host no longer builds a fresh log source tag per guest log line. The
  tag is built once per guest call instead, so a guest looping the `log`
  import within its fuel budget no longer allocates a string per call for
  the rate limiter to throw away.
- The `config` import memoizes the UTF-8 encoding of the config text it
  last served, so a guest looping that import no longer re-encodes and
  re-allocates the whole file on every call. The memo keys on the string
  instance the host API hands back, so a reloaded config re-encodes; two
  modules reading different configs are covered by tests.
- Tick dispatch pre-sizes its result list to the loaded module count,
  dropping the grow-and-copy reallocations from every tick.
- The sense scan no longer fills a `HashSet` with the net id of every alive
  entity on every request to decide which position-history entries are stale.
  That set is built from the whole world entity list (thousands of inserts on
  a populated server, 20 times a second per brain) but is only ever probed
  for the at most 41 ids a snapshot can carry, and the history it prunes
  holds only those. Liveness is now asked of the world per tracked id, the
  same way the armed-glide prune already did, so the cost is proportional to
  the history rather than to the entity list.

### Fixed

- A guest string import (log, send_chat, queue, get_setting, query) read
  exactly the length the guest named, so a guest could have the host read,
  decode, and allocate its whole linear memory (32 MiB under the default
  cap, 4 GiB when the operator raised it) and repeat the call inside one
  fuel budget. The read is now capped at 64 KiB
  (`HordeForge.WasmHost.Abi.GuestStringLength`); a longer length is cut
  there, so the text is still a prefix of what the guest wrote. The chat
  and log caps only ever refused the result, after the bytes were read.
- `HordeForge.WasmHost.Core.MonotonicTimer.ElapsedMs(double)` reported NaN
  as a call cost, because NaN fails the `elapsed < 0.0` test. One NaN
  sample from the clock then poisoned every average and sum it reached,
  including the per-guest cost in the slow-dispatch warning. The bridge's
  copy already reported zero for it; the host copy now does too.
- A per-mod `max_memory_bytes` above the effective cap is ignored by design
  (a manifest can only tighten), and nothing said so: a module the operator
  believed was capped tighter than the host cap ran under the host cap
  silently. The load log now names the value asked for and the cap in
  force, the same visibility a fuel override already had.
- `ModuleRoots.IsLeafName` did not compile for the netstandard2.0 target:
  `string.IsNullOrEmpty` carries no `NotNullWhen(false)` annotation on that
  framework's reference assemblies, so the compiler kept the argument
  nullable and every dereference below it failed the build
  (CS8602). Replaced with explicit null and length tests.
- `wasm load`, `wasm reload`, and `wasm unload` no longer write the
  sender's client address next to the account name in the server log. The
  address names a person and adds nothing the name does not already carry,
  and that log outlives the session and travels with bug reports. The
  account name is still the whole attribution, so the operator can still
  see who compiled and started guest code; `tools/targetcheck` no longer
  gates on the `ClientInfo.ip` property nothing reads.
- The committed run evidence under `evidence/` no longer carries the
  joining player's account name and platform account id, which the game
  logs next to every join. They are replaced by
  `<player-name-redacted>` and `<platform-id-redacted>`, joining the Steam
  id and server address already redacted there; each evidence README says
  so, and the acceptance notes that quote those lines were rewritten to
  match. Entity ids, tick lines, guest messages, and the local filesystem
  paths the runs recorded are untouched.
- The sample boss guest, its manifest, the tests, and the docs that quote
  them named a real player as the name the guest watches for. The default
  is now `dave`, which identifies nobody, and the built guest fixtures
  match. A sample that ships a person's handle in its default is personal
  data in a committed config, not a demo value.
- A quoted string in `wasm.toml` or `wasm-mod.toml` accepted raw control
  characters, so a stray CR, NUL, ESC, NEL, or DEL rode the guest string ABI
  inside a setting value and, from there, into a log line or a chat message
  the host quotes the value into. TOML allows no raw control character in a
  string but the tab, so the parser rejects the rest, C1 included, and names
  the code point.
- `BotServant.ReleaseModule` rewrote the bot, yaw, floor, and ownership
  tables without taking the servant gate, while every other public entry
  point on the servant takes it. It is reachable from a module unload and
  from the reload that follows it, so a release running beside a guest
  command or a sense request on another thread walked the same plain
  `Dictionary` and `HashSet` the other call was rewriting. It takes the
  gate now, and the type comment names it as an entry point.
- `GuestRateLimiter.TrackedSourceCount` read the window table's count with
  no lock, so a caller reading it while a guest import inserted a source
  (and resized the table) was reading `Dictionary.Count` against a table in
  flux. It reads under the limiter gate like the rest of the type, and
  `SharedStateConcurrencyTests` covers the read beside concurrent writers.
- `wasm load`, `wasm reload <id>`, and `wasm unload <id>` now name their
  sender in the server log, by player name and address for a remote sender
  and as the local console otherwise. They compile and start guest code in
  the game process, and the only record of who asked was the console echo,
  which is gone with the session. The game targets this reads
  (`CommandSenderInfo.IsLocalGame`, `RemoteClientInfo`, `ClientInfo.ip`) are
  now in `make bridge-check`.
- `glide <net_id> <0|1>` accepted a flag on any player from any module, and
  the flag table was keyed by net id alone, so a second guest could clear
  the first one's armed flag and its descent clamp (docs/THREAT_MODEL.md,
  gap 5). The first module to change a player's flag holds it, and another
  module's command is refused with a `glide (not owner of <id>)` line. A
  module that unloads or reloads drops the flags it armed, with the buff
  that went with them, so no flag outlives the module that owned it.
- `GameHostApi.send_chat` had a stray `)` in the line that names the
  calling module in its failure log, so the net48 bridge mod did not compile
  at all.
- The `config` and `sense` host imports of the `zdtd` module called a
  `LogSource()` that does not exist, so the library did not compile at all.
  They report under the calling module's own log tag (`_currentLogSource`),
  the same value the two adjacent failure paths in those imports already
  use, which is what `SenseFailureIsLoggedNotSilentlySwallowed` asserts.
- A module whose store would not release (Wasmtime throws when a store is
  still in use) kept its compiled machine code for the life of the process:
  `WasmMod.Dispose` released the store first and let its exception skip the
  module handle, and had already marked itself disposed, so a retry was a
  no-op. Every such unload (a repeated `wasm reload` of a module whose
  shutdown left the store busy) grew the engine's memory. Both handles are
  now released whatever the first one does, and the store's exception still
  reaches the caller as a release failure.
- `WasmModHost.Dispose` released the linker before the engine and let a
  throw from the first skip the second, so the engine, which owns the
  compiled code of every module it compiled, could be stranded for the life
  of the process. Both are now released whatever the other does.
- The bridge never released the per-module state of the modules it dropped
  with a whole host: `BridgeHost.Shutdown` and a `Start` rebuilt over a
  failed one nulled the servant and settings, so the bots those modules had
  spawned stayed in the world as zombie bodies no later servant could drive
  or despawn, growing by up to the bot cap on every restart of the mod
  within one server process. Both paths now run the same per-module release
  an explicit `wasm unload` does, before the engine goes.
- `tests/HordeForge.WasmHost.Tests/LogShim.cs` was excluded from the test
  compile by an explicit `<Compile Remove>`, so neither type in it was in the
  suite: a second `Log` that `GameLogShim` already provides, and a
  `SharedLogCollection` no test names. The file is gone, and so is the
  exclusion. The csproj comment that called the file "redundant" now says
  what the five source-linked bridge files and the one log shim are.
- `wasm list` printed the whole status report under a different header, so
  "list loaded modules" answered with limits, rate-limit counters and the
  servant summary. It now prints the loaded module ids, one per line
  (`BridgeHost.ModuleIds`), which is what its help line claims. `wasm status`
  keeps the report.
- `wasm <sub> <extra>` dropped the extra word silently: `wasm reload trap 3`
  and `wasm list now` read as calls that did what was asked. An argument past
  a subcommand's own now prints the usage list, the way an unknown
  subcommand does.
- `tools/pinned.py` reported a `--root` that is not a directory as a failed
  read (exit 1). It is a usage error, exit 2, like every other tool in
  `tools/`, and the message says so.
- `targetcheck --help` answered with the usage text and exit 0 even when it
  was given another argument, so a mistyped flag alongside it was swallowed.
  `--help` with any other argument is now a usage error (exit 2).
- `make help` left out `make ruff-version`, which CI calls to install the
  pinned ruff. It is listed now.
- A mod whose store or compiled module would not release aborted the whole
  teardown. `Unload` released the handles after removing the mod from the
  registry, so the exception escaped before `BridgeHost` released the
  module's settings, rate-limit budget, and bots; `Dispose` had the same
  hole, which left every module after the failing one without its shutdown
  export and its engine memory, and reported them as cleanly stopped. Both
  now report the release failure as a `ModRunResult` and carry on.
- A module whose `config.toml` could not be read at load was remembered as
  having no config for the life of the server. The load path registered an
  empty string, which the config import served from its cache, so the
  documented "nothing is cached on a failed read, retry on the next call"
  path was never reached and the mod ran on host defaults with one log line
  at load and nothing after it.
- A guest that exports no linear memory named `memory` was answered with
  `0` from the config and sense imports and with a too-small-buffer status
  from the string returns, so it grew its buffer forever and the host said
  nothing. The string returns now trap and name the missing export, matching
  what the read path already did; the two imports log the cause and still
  return `0`, which is their wire contract.
- A modlet whose path could not be resolved was dropped from the modlet
  scan with no report, so staged modules vanished the way a modlet that
  carries no guest would. `ModuleRoots.CollectExtra` names it through the
  failure reason its callers already log.
- A bot despawn that threw during a module release left the body alive in
  the world with nothing tracking it, while `Despawn` already returned the
  id to the servant on the same failure. The release path does the same and
  counts the bodies it could not remove.
- The glide command reached live game state outside the per-verb catch the
  bot verbs have, so a fault while the world was unloading surfaced as a
  guest trap and was charged to the guest's error counters.
- Malformed `bot move`, `bot look`, `bot shoot`, and `bot remove` commands
  were dropped without a log line, while a malformed `glide` was reported.
- `wasm reload <id>` reported a reload that had already unloaded the
  previous instance the same way as a reload of an id that was never
  loaded. It now says which happened, since the first cost the operator a
  running module and the second cost nothing.
- A manifest read failure reported the underlying exception message with no
  file, and for a file that is not UTF-8 no position either. The reason now
  names the path, and a decode failure names the line and byte.
- The manifest parser accepted table names holding characters the key path
  rejects, and value errors named the line but not the key.
- `ModuleRoots.ResolveFile` combined the root, the id, and the file name in
  one `Path.Combine`, so a file name that is rooted or carries a separator
  dropped both and resolved to that file: `/etc/passwd` came back as the
  module's file while the on-disk spelling check still passed on the module
  directory. The id was already validated; the file name is now held to the
  same plain-leaf-name rule. Every current caller passes a literal, so no
  shipped load path reached it.
- `WasmModHost.Dispatch` did not compile. The result was narrowed through a
  `ModRunResult?` local guarded by `HasValue`, and the compiler drops the
  not-null state of a nullable value-type local at a loop back-edge, so
  `results.Add(result)` had no conversion to use. The pattern match on the
  return value states the same thing and compiles: the matched local is
  already the unwrapped struct, since `ModRunResult?` is
  `Nullable<ModRunResult>`, so a `ModRunResult result` pattern binds the
  value and `result.Value` does not exist on it. The result is added
  directly. The library, the net48 bridge, and the test suite were
  unbuildable at this commit.
- The C# style rules in `.editorconfig` were editor suggestions: nothing
  promoted them, so a rule set that a developer silently ignored was the
  only thing a CI run saw. `EnforceCodeStyleInBuild` is on, and
  `.editorconfig` names the rules it promotes (braces on a multi-line
  `if`/`else`, unused private members and parameters, unread private
  members, a simplifiable conditional, a using declaration in place of a
  using block).
  The tree passes all of them, so a new one fails the build.
- Two `targetcheck` helpers (`TypeKind`, `DecodeSignature`) took a
  `MetadataReader` they never read, a leftover from a shared signature
  shape. Removed, with the call sites.
- `BotServant` kept a `_botOwnersWithFloor` set that nothing read: the
  per-module bot count floor is a dictionary lookup in `EnsureSpawned`, so
  the set was written on every `bot count` and cleared on every release
  without ever answering a question. Removed, with the two writes.
- The Python gate had no exception-hygiene rules at all. `TRY` (with
  TRY003, a style preference about message length, off) and `RSE` are
  enabled; both pass the tree today, so control flow inside a `try` and a
  raised-instead-of-returned error now fail `make check-ci`.
- The Unicode line and paragraph separators (U+2028, U+2029) survived
  `TextSanitizer`, so a guest could split a server log line, a console line,
  or a global chat message with a character the C0 filter never saw. They
  are replaced with '?' like a raw newline, and a mod id carrying one is
  rejected, since an id lands in the log source tag and in a module path.
- The bot servant wrote guest command text into the server log from several
  parse paths, relying on the caller to have cleaned it. `BotServant.TryQueue`
  cleans the command itself, so no parse path can put a raw control
  character into a log line.
- `wasm reload` did not drop a module's chat-rejection or config-read-failure
  rate window, so a reloaded instance started inside the window its previous
  generation had saturated and had its first lines dropped. `ForgetModule`
  resets every per-module window.
- `GuestRateLimiter.ForgetSource` removed from the window table without the
  limiter's lock, so an unload racing a guest write could corrupt the table.
- A module whose directory resolved to nothing would have had its manifest
  read from the server process's working directory (`Path.Combine("", name)`
  is a relative path). `TryReadManifest` refuses instead.
- A mod id naming a Windows device (`con`, `nul`, `com1`, ..., also with a
  suffix like `aux.wasm`) or ending in a space or a period was accepted and
  then never loaded, because Windows has no directory by that name and stores
  one without its trailing space or period. Such ids are rejected with the
  rest, so a mod is either loadable on every platform or reported.
- The per-tick dispatch cost was measured on a hardwired `Stopwatch`
  (`BridgeHost.Tick`), the one clock in the bridge that no driver could
  replace. It is the only value in the run's own log lines that a replay
  could not reproduce, so two runs of the same inputs disagreed on the
  heartbeat, `wasm status`, and shutdown lines and the diff named the clock
  rather than the guest. It is measured through `BridgeHost.Timer`
  (`MonotonicTimer`), a replaceable monotonic source that defaults to the
  process clock, so production timing is unchanged and a simulation reports
  its own.
- The bot spawn log line printed its position with the default float
  format, which follows the server's locale: on a comma-decimal server the
  three coordinates were unreadable, and the same spawn printed different
  bytes on two machines. Positions are now fixed-point and invariant.
- The sense `wearing_glider` read swallowed every exception and returned
  0, so a game patch that broke the equipment read produced a brain that
  stopped seeing gliders with nothing in the log. The failure is now
  reported through the servant's capped warning path, the same one the rest
  of the sense scan uses.
- A failed tick dispatch logged an info-level line naming neither the tick
  nor the guest ("fuel exhausted during on_tick"), so the per-guest failure
  stream was unattributable in a log carrying one such line per guest per
  second, and a failed `on_player_join` was logged the same way. Both are
  warnings that name the tick, the mod, and the fuel the call consumed, which
  is what the unreleased section's telemetry entry already claimed they were.
- `make dist` staged the modlet with the third-party notices but not the
  project's own MIT LICENSE, so the shipped tree linked to a file that was
  not in it. Both travel in `dist/Mods/1_HordeForge_WasmHost/` now.
- The guest crates under `samples/` carried no `publish` declaration, so
  `cargo publish` offered wasm modules the host loads as raw `.wasm` and
  then failed on the missing package metadata. `publish = false` is declared
  once in `[workspace.package]` and inherited by every member.
- The NuGet manifest left `<RepositoryType>` undeclared, which shows a bare
  URL on the listing, and `tools/packcheck.py` did not look at
  `<TargetFrameworks>`, so dropping a framework README.md promises would
  ship silently. Both are checked now, with tests.
- The state a guest import reaches without the caller's lock is now guarded
  where it is actually shared. `GuestRateLimiter`'s window table, the
  per-mod counter fields on `WasmMod`, `TickTelemetry`'s counters, the
  bot servant's pooled sense buffers and bot/glide tables, the game host
  API's raw config cache, and the settings provider's probe fields were all
  plain fields and ordinary collections with no synchronization of their
  own: the bridge's own gate covers the game loop and the console thread,
  but nothing protected them if a guest call arrived on a second thread.
  Each now takes one private lock, held across no other, in the order
  `BridgeHost.Gate` -> host API -> servant -> limiter.
- `WasmModHost.ShutdownFailures` handed out the live list that `Dispose`
  fills under the host gate, so an embedder reading it from another thread
  could enumerate a `List` being appended to. It returns a read-only copy,
  like `ModIds`.
- `BridgeHost.ClockMs` is a documented cross-thread setter read on the hot
  path without the gate; the backing field is volatile so a swap is seen
  by every reader.
- The module tree list was filled and cleared in place under the bridge
  gate but enumerated by `ResolveModuleDir` / `ResolveModuleFile`, which a
  guest's config import reaches without that gate. It is published as an
  immutable snapshot now and replaced wholesale, so a resolve can never
  walk a list another thread is rewriting.
- `make test TEST_FILTER=...` reported success for a filter that matched no
  test: vstest prints "No test matches the given testcase filter" and exits
  0, so a mistyped filter read as a green run of the whole suite. The target
  now fails with the filter it was given, and `make test-list` prints every
  name a filter can match. The filter in `make help` was one of the failing
  kind (`Name~FuelExhausted`, which names no test here).
- `make dist` staged the Wasmtime native engine from `$(HOME)/.nuget/packages`
  regardless of where the SDK that ran the build actually restored it, so
  `NUGET_PACKAGES`, and the workspace-local SDK this Makefile prefers, both
  pointed the copy at a path that does not exist. It asks the SDK for its
  global packages folder now, and a missing engine is named instead of
  surfacing as a bare `cp: cannot stat`.
- The documented way to refresh `packages.lock.json` after a dependency bump
  was `dotnet build HordeForge.WasmHost.sln`, which covers only the host
  library and its tests: the committed lock files under `src/GameBridge` and
  `tools/targetcheck` were left stale, and the next locked `make check`
  failed on a manifest whose lock file had never been regenerated. `make
  locks` restores each project on its own.
- `apicheck.py` recorded a constructor's `: this(...)` / `: base(...)`
  clause as part of its signature, so a chained constructor read as a
  removal plus an addition and the gate failed on a clean tree
  (`ModRunResult`'s five-argument overload chains to the six-argument
  one). The clause is a body detail, like an accessor body, and is now
  stripped before the signature is recorded, with tests. The baseline
  picks up the members added since it was last written: `GuestStatus`
  and its constructor, the guest status codes, `WasmMod.Enabled`, the
  limits the host reads back, and the two exception types. No member was
  removed, so the version does not move.
- `docs/adrs/0009-align-abi-with-zdtd-server.md`: the decision to adopt
  the sibling zdtd-server plugin contract, which shipped with the 0.1.3
  ABI alignment and the 0.1.4 `zdtd` import module, had no decision
  record. ADR 0007's hook list now carries the fifth hook
  (`on_admin_command`, resolved and signature-checked at load), and ADR
  0006 records why guest standard streams are discarded by default.
- `WasmHostConfig` values the host cannot honor were not all rejected at
  construction. A `MaximumStackBytes` above the engine's 2 MiB caller-stack
  limit aborted the process from a panic inside Wasmtime, which no caller
  could catch, and a `FuelPerCall` above the 50,000,000 instruction ceiling
  the manifest parser enforces was accepted, so only the file path was
  bounded on how long one guest call can hold the game loop. Both are now
  rejected by name at construction, and the tests pin the inclusive
  boundaries.
- A memory ceiling past the wasm32 address space is rejected where it is
  read. `max_memory_bytes` was bounded below by one wasm page and not above,
  so a limits file naming a byte count past 4 GiB became the engine's static
  memory maximum and failed the host construction instead of the documented
  "invalid file, keep defaults" path. A wasm32 module can declare no more
  memory than that, so a larger `StaticMemoryMaximumBytes` bounds nothing.
  Both the manifest parser and `WasmModHost`'s config validation now name
  the bound; 4294967296 (the value `make dist` stages) is still accepted.
- The game world clock reached the guest ABI through a `(long)` cast of an
  `ulong`. A value past `long.MaxValue` arrived as a time before the world
  started, which a guest computing a day/night phase reads as a plausible
  answer to the wrong question. `get_world_time` and the sense v4 header now
  read the clock through `WorldTime.ToAbi`, which saturates.
- The repository tools drifted from the command-line contract in
  CONTRIBUTING. `tools/packcheck.py` printed its pass line to stdout while
  every other tool keeps stdout free, and both it and
  `tools/versioncheck.py` reported a mistyped `--root` as a failed check
  (exit 1, a bare `[Errno 2]`) instead of the documented usage error
  (exit 2), so a caller could not tell a broken checkout from a bad
  argument. `tools/targetcheck` read an unknown flag as the `GAME_DIR`
  path and reported `Assembly-CSharp.dll not found under --flag/...`.
- `wasm` with a mistyped subcommand printed the full status report, so a
  typo read like a command that worked. It now names the unknown word and
  prints the usage list, and `wasm help` prints the same list on request.
- `WasmModHost.ShutdownFailures` handed out the live list behind an
  `IReadOnlyList`, one downcast away from a caller rewriting host state.
  It returns a read-only copy, like `ModIds` and the dispatch results.
- The sense position history was pruned against the ids the snapshot
  reported, not against the live entity set, so any entity outside the
  41-record window lost its history on every scan and its `vy` and glide
  descent clamp read as a first sighting. The prune now runs against the
  alive set collected before the window is trimmed, and the glide-flag
  prune runs ahead of the record pass rather than after it, so a flag
  left by a player who left cannot clamp a reused net id for a scan.
- The shared `wasm.toml` settings cache keyed freshness on the last write
  time alone, so a rewrite that carried the original timestamp (a
  restore, a copy that preserves times) was never re-read and the old
  settings served for the life of the server. Freshness is now the write
  time and the file length. A `wasm.toml` that fails to parse is also no
  longer re-read and re-parsed on every 500 ms probe: it is reported once
  and the previous settings keep serving until the file changes.
- A per-module rate cap window outlived its module, so a module reloaded
  inside the second the old one saturated a cap resumed throttled and
  with the previous generation's drop count in `wasm status`. Unload and
  reload now drop the module's log, SimCommand, and sense windows.
- The per-mod `config.toml` fallback cached a read failure as a permanent
  "this mod has no config". Only a definitive absence is cached; a file
  that is locked, oversize, or mid-write is retried on the next call.
- `make check` failed on a clean tree. `tools/api-surface.txt` predated
  `TickTelemetry`, `WasmModHost.ShutdownFailures`, `InitModule`,
  `MaxModuleSizeBytes`, and `WasmPageBytes`, and recorded three
  `Snapshot` fields with their namespace-qualified `List<>` instead of the
  spelling the source uses, so `apicheck.py` read the drift as a surface
  shrink. The baseline is regenerated; no member was removed, so the
  version does not move. `tools/sbom.py` had drifted out of the pinned
  ruff's format since it was last run, and `BridgeHost.IsValidModId`
  pointed its reader at `ModIds.IsValid` rather than `ModId.IsValid`.
- The guest toolchain was whatever rustup last synced (`stable`), so two
  guests built a month apart could come off different rustc releases. The
  channel now lives in `samples/rust-toolchain.toml` and `make toolchain`
  installs exactly that one, and `require_zig` rejects a zig other than the
  pinned 0.16.0 by name before compiling the C and Zig guests.
- The four manifests declared `<LangVersion>latest</LangVersion>`, so the
  language version floated with the SDK band. It is pinned once in
  `Directory.Build.props`.
- A Release build carried the absolute checkout path in its PDB and debug
  metadata, so the same source built in two directories shipped different
  bytes. `Directory.Build.props` sets `ContinuousIntegrationBuild` (and with
  it `DeterministicSourcePaths`) whenever `CI` is set, which normalizes those
  paths and lets the SDK honor `SOURCE_DATE_EPOCH`.
- Three manifest tests still asserted `WasmModLoadException` where the parser
  now raises the narrower `WasmManifestException`, so they failed against the
  typed manifest errors. They assert the exact type the parser throws.
- `BotServant` declared `MaxVelocityDeltaTicks` twice, so the net48 bridge
  did not compile (`error CS0102`) and `make bridge` and `make check` were
  red. One declaration, with both comments, remains.
- The module scan walked each tree's directories in filesystem order, so
  the load order, which fixes the order every later tick dispatches mods in,
  could differ between runs on the same tree. Directories are now sorted
  ordinally, as the modlet trees already were.
- A sense snapshot took the first 41 alive entities in the game's entity
  list order and reported them in that order. The list is in whatever order
  the game built it, so the same world could hand a guest a different
  snapshot from run to run, and which entities were dropped at the 41-record
  cap moved with it. Records are now selected on the net id
  (`SenseRecordPicker`) and come out in ascending order, so the snapshot is
  a function of the entity set alone.
- The dropped-item summary in `wasm status` and the armed glide net id list
  were printed in hash-table order, so the same run printed a different
  line between runs. Sources are listed in ordinal key order and net ids
  ascending now.
- `WasmModHost.InitModule` took no internal lock while every other entry
  point did, so an enable racing a dispatch or an unload could enter a
  wasm store the unload was disposing, or hand one guest the setting the
  other was reading. It now serializes on the same gate.
- (breaking) A raw lone surrogate in a manifest string (basic or literal)
  reached the settings table instead of being rejected, while the `\uXXXX`
  form already was. Such a value has no UTF-8 form and cannot round-trip the
  guest string ABI. A manifest carrying one now fails to load, so a guest
  relying on it stops loading until the string is replaced.
- Armed glide flags are dropped for net ids that no longer name a live
  player, in the same sense scan that prunes the position history. The
  servant kept one entry per player who ever armed a glider for the life
  of the server, and a net id the game later reused to another entity
  kept clamping that entity's descent. `wasm status` now lists only the
  armed ids, not every id the servant still remembers.
- A module initialized on its own (the start scan, `wasm reload`) read its
  settings, its `config.toml`, and its log attribution from whichever mod
  the host happened to have called last, or from no mod at all on a fresh
  start. `WasmModHost.InitModule(id)` runs `on_enable` with the calling mod
  set, and the host clears that state after every dispatch.
- The sense position history is pruned by entity membership instead of a
  size comparison. A tick where entities left while others joined kept the
  departed ids, and a net id the game later reused was reported with a
  vertical velocity derived from the previous occupant.
- Guest rate limiter windows for sources that stopped writing are swept
  once the table grows past its threshold, so unloading and reloading
  modules no longer leaves one window per id ever seen.
- `apicheck` recorded a property whose accessor body nested braces as its
  private backing field, so `WasmModHost.Tick` and `WasmModHost.ModIds` were
  no longer on the recorded surface. The accessor body is now stripped with
  a brace count, so the declaration is what the baseline holds.
- `tools/api-surface.txt` regenerated: it still described the surface before
  the tick telemetry, the single-module init entry point, the shutdown
  failure list, and the collapsed TOML accessors landed, so the gate failed
  on a tree whose published surface had only grown. It was regenerated a
  second time for the members that landed with the typed manifest errors
  (`ManifestReadException`, `WasmManifestException`, `ModRunResult.GuestStatus`,
  `AbiConstants.StatusNotImplemented`/`StatusInternalError`, `WasmMod.Enabled`,
  `WasmModHost.InheritGuestStandardStreams`/`FuelPerCall`/`StaticMemoryMaximumBytes`),
  which the first regeneration predated, so `make check` was still red on a
  clean tree.
- The guest, fixture and dist targets failed on a clean clone with a bare
  `sh: .cargo/bin/cargo: No such file or directory` or a `cp: cannot stat`,
  because the in-project toolchain, zig and the sibling `zdtd-server`
  checkout were each an undocumented prerequisite. Each target now names the
  missing piece and the command that provides it, and the path to the
  sibling checkout is overridable with `ZDTD_SERVER=...`.
- `make help` listed neither `make clean` nor the guest toolchain step, and
  described `make check` without the bridge build it runs.
- `make check-ci` failed on a clean clone: `tools/` had never been formatted
  with the ruff the gate pins, so `ruff format --check` refused six files.
  They are formatted now, and `pyproject.toml` declares
  `required-version = "==0.16.4"` so a developer's ruff has to be the one CI
  installs rather than whatever happens to be on PATH.
- (breaking) `WasmModHost` guarded its "call it from one thread" contract with
  a comment only: two threads entering it skipped or duplicated mods in a
  dispatch, served one guest another guest's settings, and could enter a
  wasm store that was already running a call (the engine aborts the
  process). Every entry point now serializes on one internal gate, and the
  dispatch results and `ModIds` are per-call copies instead of host-owned
  live views, so a list handed to one caller cannot be refilled by a
  dispatch running on another thread. A consumer that read `ModIds` or a
  dispatch result and expected it to track later host state now holds the
  snapshot the call returned; re-read it after each dispatch instead.
  `WasmModHostConcurrencyTests` drives dispatch, load, and unload from
  several threads against real guest fixtures.
- The `config` host import cut its copy at `min(out_cap, len)` bytes, which
  could land inside a multi-byte UTF-8 character and hand the guest bytes it
  decodes as U+FFFD. The cut now stops on a character boundary
  (`Utf8Prefix`), and `docs/ABI.md` states the boundary.
- (breaking) Manifest and `config.toml` reads decoded the bytes in this class
  instead of through `File.ReadAllText`, whose reader silently switches
  encoding on a UTF-16 or UTF-32 BOM: a non-UTF-8 file now fails its load
  with a reason instead of loading, and a UTF-8 BOM is stripped explicitly.
  A manifest or shared `wasm.toml` written in a UTF-16 editor now stops
  loading and says why.
- (breaking) `ModId.IsValid` accepts U+FFFD, so a module folder whose name is
  not valid UTF-8 (legal on Linux) produced an id that no longer re-encodes
  to its own directory and the module silently never loaded. Replacement
  characters are rejected like the other invisible characters, so a folder
  with such a name is now refused at the id instead of loading nothing.
- The guest link flags (`--max-memory=33554432`, 1 MiB stack) move into the
  tracked `samples/.cargo/config.toml`. They lived only in the gitignored
  in-project toolchain config, so a fresh checkout and CI built guests that
  declare no memory maximum, which the host treats as the 4 GiB wasm32
  ceiling and refuses under the default cap.
- `wasm load` skips a module tree it cannot enumerate (permissions, a
  modlet being replaced) with a warning instead of aborting the whole
  scan and leaving the host unstarted.
- The server-side glide clamp measured the fall against a single tick while
  `vy` is averaged over every tick since the entity's stored position, so a
  player sampled several ticks after the last one (sense called below tick
  rate) was lifted back up even while sinking within the glide rate. The
  drop budget now covers the same interval `vy` was measured over.
- (breaking) `limits.max_memory_bytes` was accepted down to 1 byte. As a
  per-mod value it could only reject the module, but the shared `wasm.toml`
  value becomes the engine's memory ceiling, where the host constructor threw
  and took the bridge start down with it. The manifest parser now rejects
  anything below one wasm page, the bound the host already enforces, so an
  invalid file keeps the documented "log it and use the defaults" behavior.
  A per-mod value that tight was accepted before and is now a load failure.
- The CycloneDX SBOM now carries an SPDX license per NuGet component.
  `tools/sbom.py` holds the table and fails the build when a package
  reaches a committed lock file without a recorded license, and the SBOM
  skips lock files under `evidence/` (a frozen playtest record, not a
  shipped artifact).
- `BotServant.PruneDeadBots` collects dead ids into the pooled scratch list
  instead of allocating one per call, matching the pooling the sense path
  already does.

### Changed (breaking)

- A misspelled key in a manifest's `[limits]` table is rejected instead of
  ignored. It used to leave the host cap in force where the operator wrote a
  tighter one, and nothing in the log said so. Keys outside `[limits]` stay
  tolerated, so a manifest written for a newer host still loads, but a
  manifest that loaded under 0.3.1 with a key that does not exist now fails
  its load. The replacement is the correct spelling of the key.
- A shared `Mods/Wasm/wasm.toml` that exists but cannot be parsed now aborts
  the bridge start instead of falling back to the code defaults, which handed
  every guest a fuel budget and memory ceiling the operator never wrote. The
  server keeps running and no guest loads until the file is fixed, so a
  shared file with a syntax error that 0.3.1 ignored is now a stop.
- `on_enable` runs once per load generation. `WasmMod.Init` latches the
  enable, so a second `DispatchInit` or an `InitModule` after the load scan
  reports Ok without calling the guest again, and `WasmMod.Enabled` reads
  the latch back. An embedder that relied on a repeated enable to re-run the
  guest's setup has to reload the mod for that; a failed enable stays
  retryable. `docs/ABI.md` already documented the once-per-load contract.
- The `glide` queue verb only arms a net id that names a live player
  (`EntityPlayer`) in the loaded world. An armed flag applies the glide buff
  and clamps the entity's descent, so accepting any world net id let a guest
  steer entities it does not own, other players included. A guest that armed
  a non-player net id is now refused with a `glide (not a player)` line in
  the log, and `docs/ABI.md` states the gate.
- `TextSanitizer` also strips the invisible bidi controls (U+202A to U+202E,
  U+2066 to U+2069) and the zero-width no-break space (U+FEFF) from guest-
  and client-supplied log, chat and set-name text, replacing them with '?'
  like the control characters it already stripped. A guest that relied on
  those code points passing through gets '?' instead. Zero-width space,
  joiner, word joiner and variation selectors are left alone, so emoji
  sequences and non-Latin scripts still render.

### Changed

- The `wasm` console command now reports what it did instead of a bare
  result. `wasm load` names the modules it loaded and prints a `skipped`
  line, with the reason, for every module or tree the scan refused;
  `wasm reload` and `wasm unload` print why a load was refused (missing
  `module.wasm`, malformed `wasm-mod.toml`, over the size cap, an id that is
  not loaded) followed by the ids that are loaded, and an unload whose
  shutdown export failed says so on the command that unloaded it.
  `wasm list` prints the loaded ids, one per line; the counters stay in
  `wasm status`. `wasm help` and the game's `help wasm` print the same
  block, one line per subcommand with a description, instead of a bare list
  of forms. `BridgeHost.LoadAllModules` returns a `ModuleLoadScan`
  (loaded ids, skipped reasons) rather than a count, and `Reload` and
  `Unload` return the reason through an out parameter.
- `docs/GAME_HOOKS.md`, `docs/GUEST_AUTHORS.md`, and `docs/THREAT_MODEL.md`
  follow the console change, including the line references into
  `BridgeHost.cs` and `CmdWasm.cs` the shift moved.
- `docs/MIGRATION.md`: the upgrade steps the breaking entries below imply
  but never spell out, one subsection per consumer kind (operator, embedder,
  guest author) with the before, the after, and the fix, plus a table of the
  symbols removed in earlier releases and what replaces them. Those entries
  are written for the person auditing the change; nothing told the person
  who has to act on it. `docs/INDEX.md` carries the document, README points
  at it, and CONTRIBUTING now requires a release carrying breaking changes
  to have a section there before the tag, and records the order the release
  is cut in.
- `make dist` also ships the two zdtd guest modules (`fps_bot` 2.5.0 and
  `parachute` 0.1.0) as unmodified binaries from the sibling
  `hordeforge/zdtd-server` checkout, and neither the CycloneDX SBOM nor
  `THIRD-PARTY-NOTICES.md` named them: a modlet redistributing third-party
  modules with no attribution and no inventory entry. `tools/sbom.py` now
  emits them as `pkg:generic` components read from the manifests under
  `samples/`, the notices record where each one comes from and what ships,
  and the existing notices drift gate covers them too. Their license stays
  `NOASSERTION`, since this repository does not vendor the sources that
  declare it.
- `BotServant.TryQueue` called `TryQueueBot` from two branches of the same
  three-way split on the command prefix, so one verb had two entry paths
  through the same lock. The split is now glide against everything else,
  with one call site per handler.
- The CI ruff install resolves nothing beside the pinned wheel
  (`--no-deps --only-binary=:all:`): ruff ships no runtime dependencies, so
  a transitive resolution was pure added surface, and a source build of the
  linter in the pipeline was never intended.
- `docs/THREAT_MODEL.md`: every `path:line` re-resolved against the source
  after the files it points at moved, and the model gained what the drift had
  hidden. A modlet-carried `Wasm/` tree is a module source, and its
  `wasm.toml` becomes the shared limits file when `Mods/Wasm/wasm.toml` is
  absent, so a third-party modlet sets what loads and under which fuel and
  memory ceilings; that is now threat T5 and a named gap. Also recorded: the
  manifest fuel ceiling a module author can raise against (50,000,000
  instructions, 50x the default), the `is_self` ownership gate that does
  cover bots, the lowest-N-net-id rule that makes `sense` stable, and the
  invariant number parsing in the SimCommand path. `on_admin_command` and
  `query` are listed as present-but-unreachable so a later pass does not read
  a validated export as a live console hook.
- `SECURITY.md` names that modlet surface under "What is NOT sandboxed", and
  the chat row now says the 10/second cap is one shared counter across every
  module rather than a per-module line.
- The release workflow's tag gate is now `tools/versioncheck.py --tag`,
  the same tool `make check` runs, instead of a bash copy of its three
  version parses. One set of rules and one set of error messages cover both
  gates, so the workflow can no longer disagree with the tool it duplicated.
  `--tag` is covered by `tools/test_versioncheck.py`.
- The CI ruff install reads its version from `pyproject.toml` through
  `make ruff-version` instead of repeating the number, so bumping the
  `required-version` pin cannot leave the pipeline installing a ruff the
  tools gate then rejects. The release workflow also gained the concurrency
  group ci.yml has, so a re-pushed tag supersedes its own in-flight run.
- `evidence/acceptance-1/run_acceptance.sh` and
  `evidence/playtest-1/run_server.sh` fail when `dist/Mods` is not staged.
  Docker creates a missing bind-mount source as an empty directory, so a
  forgotten `make dist` used to start a server with no modlet and exit 0.
- Every `dotnet` target in the Makefile now passes
  `ContinuousIntegrationBuild=true`, not just the ones a CI environment
  variable happens to reach. Two builds of the same source on a developer
  machine produced different bytes before: the SDK wrote the absolute
  checkout path into the PDB and a wall-clock timestamp with it. Source paths
  are now normalized to `/_/`, and the DLL, PDB and packed library are
  byte-identical across builds. The NuGet archive itself still is not
  comparable by hash: `dotnet pack` stamps its OPC core-properties part with
  a build-time GUID and the current time, and no MSBuild property changes
  that. Compare the extracted package.
- `make clean` removes `samples/target/`, which held the compiled guests and
  was the one build output it left behind.
- The three versions the Makefile reads out of their declaring files (ruff
  from `pyproject.toml`, the staged Wasmtime version from the NuGet lock
  file, the guest Rust channel from `samples/rust-toolchain.toml`) come from
  `tools/pinned.py`, which parses each file in its own format and fails by
  name when a version is missing, instead of a regex quoted into a shell
  that prints nothing when the pattern stops matching. `make dist` now stops
  before staging rather than reaching for a native engine under an empty
  version.
- The join log line no longer writes the player name. It reads
  `[WasmHost] player spawned (entity 171); name dispatched to guests`: the
  entity id is what the dispatch is keyed on, and the server log outlives the
  session and travels with bug reports, so a name in it identifies a player
  for no diagnostic gain. Guests still receive the name through
  `on_player_join`, which is the documented ABI.
- The committed run evidence under `evidence/` no longer carries the Steam
  account identifiers and the server's public address. Both appear verbatim
  in the game's own log output and identify the person who ran the server;
  they are replaced by `<steam-id-redacted>` and `<server-ip-redacted>`, and
  each evidence README says so. Nothing else in the logs changed, so the
  runs still read the same.
- `SECURITY.md` and the README safety section no longer imply the host gives
  the operator per-guest permissions. Both now state what a loaded guest can
  reach through the imports: global chat, and the bot servant, which gates
  who may fire but not who may be hit, over a bot population no guest owns.
  The console surface (`wasm load` and `wasm reload`, no signature check and
  no recorded operator) and the absence of a module-count cap are named in
  the operational notes, and the threat model link is one click away.
- `docs/ARCHITECTURE.md` gains the source layout it is canonical for: the
  folder map of both `src/` projects, the dependency direction between the
  bridge and the host library, the rules that decide which folder new code
  goes in, and the source link that lets the net8 suite cover
  `GuestRateLimiter`.
- Every rate window, throttle, and file probe in the bridge reads one
  replaceable millisecond clock, `BridgeHost.ClockMs` (default
  `Environment.TickCount`), instead of each call site reading the process
  clock: the guest log, chat, SimCommand, and sense caps, the tick-failure
  and slow-dispatch log caps, the bot spawn top-up (`BotServant`), and the
  shared-settings probe (`WasmSettingsProvider`). Those windows decide
  whether a guest's output is accepted or dropped, so a run driven by a
  virtual clock makes the same decisions on every replay. Log output is
  unchanged at the default clock.
- Decision and requirement documents that had drifted from the code:
  ADR 0007 no longer claims the JSON manifest is still accepted (ADR 0005
  superseded it), PRD 0001 no longer claims undeclared-maximum modules are
  rejected (ADR 0004's 4 GiB amendment) or that JSON manifests are
  accepted, ADR 0005 links the record that superseded it, `docs/CONFIG.md`
  points MiniToml at ADR 0007, and the ADR index carries a status column.
- `BotServant.Glide` returns a copy of the armed flags instead of the live
  dictionary, so the "wasm status" read cannot reach the servant's state.
  `ModApi` applies its Harmony patches under a lock, so a second `InitMod`
  racing the first cannot stack duplicate postfixes.
- The tools lint gate covers more rule groups (S, A, BLE, DTZ, FBT, FURB,
  G, ICN, ISC, LOG, N, PERF, PIE, SLF, TID), all of which the tree passes
  today. `tools/` is reformatted to the pinned ruff's style.
- A `vX.Y.Z` tag now runs the full CI gate (docs, lint, guest lint, build,
  test) before the version check, by calling `ci.yml` as a reusable
  workflow instead of tagging on version agreement alone.
- CI caches the NuGet restore, keyed on the committed `packages.lock.json`
  files. Locked mode still verifies every content hash, so a warm cache
  changes restore time, not what is trusted.
- The acceptance image build ignores its own directory (`.dockerignore`),
  so recorded run logs no longer enter the build context or bust the cache.
- `WasmModHost` runs `DispatchTick`, `DispatchInit`, and
  `DispatchPlayerJoin` through one private `Dispatch` walk instead of three
  copies of the same loop, and the `log` and `tick` imports are defined once
  for both the `hordeforge` and `zdtd` host modules.
- `BridgeHost` formats a failed call for the log through one `Describe`
  helper, and the `ModRunResult?` unwrap in `Reload` and `Unload` uses a
  pattern match. Log lines are unchanged.
- `ModApi.ApplyHarmonyPatches` posts each hook through one `Patch` helper
  (same targets, same messages).
- `CmdWasm` writes console output through one `Output` helper.
- The repository tools (`doccheck.py`, `versioncheck.py`, `sbom.py`,
  `targetcheck`) share one command-line contract: `--help` documents every
  flag, `--root` selects the repository to work on, machine-readable data
  goes to stdout and progress to stderr, and exit codes are 0 pass, 1 check
  failed, 2 usage error. `versioncheck.py` moved its messages to stderr and
  gained `--root`.
- The C and Zig guests are held to the same posture as the rest of the tree.
  `make boss` compiled the C guest with no warning flag at all, so a source
  tree where rustc and clippy are denied could still build a C guest that
  warned; it now passes `-Wall -Wextra -Wpedantic -Werror -Wshadow -Wundef
  -Wcast-qual -Wstrict-prototypes`, and `make boss-zig` runs `zig fmt
  --check` on the Zig guest, the one source directory whose formatting
  nothing checked. The ruff gate covered `tools/` and nothing else, so
  `evidence/acceptance-1/telnet_session.py` shipped unlinted; `make check-ci`
  now runs both ruff commands over `tools evidence`.

### Removed

- `ModuleRoots.Contains` (an ordinal string search that `List.Contains`
  already does) and the null checks on `Order`, `ResolveDir`, and
  `ResolveFile` parameters, which the non-nullable signatures already
  require. One manifest test that duplicated another, and the redundant
  InlineData cases in the duplicate malformed-manifest theory, are gone
  (the surviving theory keeps every case).
- `ModApi.HostStarted` (the bridge mod's public flag, never read) and the
  `BotServant` weapon damage table. Stage 2 bots all carry the pistol
  (weapon id 0) and nothing reads the other five ids, so the table held
  values with no servant-side use; the pistol constant replaces it.
- `TomlArray` no longer stores the items it parsed. Items are still parsed,
  so a malformed one fails the load, but nothing reads array elements: no
  manifest field is an array, and `AsString` and its siblings reject an
  array value either way.
- `BotServant`'s `_botOwnersWithFloor` set, written when a module sets a bot
  count floor and cleared on release, and never read: the floor itself lives
  in `_countFloors`, which is what the spawn path consults.
- `WasmModHost.LogSource()`, a one-line accessor over `_currentLogSource`
  that the field's own comment already explains; the three call sites read
  the field.

## [0.3.1] - 2026-09-21

### Removed (breaking)

- `WasmModHost.TryInit(id, out result)` and `BotServant.ClearGlide()`. Both
  had zero callers; dispatch walks and `Glide` status cover their uses.
  `WasmModHost` is part of the published `HordeForge.WasmHost` package, so a
  consumer that called `TryInit` (the documented way to init a single freshly
  loaded mod outside a dispatch walk) does not compile against 0.3.1 and must
  move to the dispatch walk. The version number does not warn of this: it
  shipped in a patch slot, which under this project's rule carries no
  breaking change. The entry is marked here so the audit trail is right.

## [0.3.0] - 2026-09-20

### Changed (breaking)

- Only `wasm-mod.toml` is read: the deprecated `wasm-mod.json` manifest
  format and the MiniJson parser behind it are deleted, so a module still
  shipping JSON must convert its limits to TOML (same fields, snake_case
  keys). MiniToml no longer unwraps quoted keys and headers either; a
  quoted key is a literal name. ADR 0005 is marked superseded and
  `docs/ABI.md` states the current surface.

### Removed

- `NativeAssets` (the platform runtime-id map duplicated by the `make
  dist` staging) and the unused `StatusNotImplemented` /
  `StatusInternalError` / `SettingOk` ABI constants.

## [0.2.0] - 2026-09-11

This cycle changes the public C# surface of the host library and the bridge
(marked below). In this project's 0.x scheme the minor digit carries breaking
changes and the patch digit never does, so any 0.1.x remains safe to take
without reading further.

### Changed (breaking)

- Guest rate limiters bind their cap at construction
  (`new GuestRateLimiter(GuestRateLimiter.MaxCommandsPerSecond)`) and
  `TryWrite` lost its per-call override parameter, so a limiter's cap can
  no longer drift from its call sites. The generic `GameHostApi.RateLimiter`
  property is renamed `LogLimiter`, matching its siblings.
- Sense requests (`zdtd.sense`) are now rate capped per module
  (200/second, same reasoning as the SimCommand cap): building a snapshot
  scans the live world entity list on the host side, work the wasm fuel
  budget never sees, so an unbounded import loop could multiply that scan
  past the tick budget. Capped requests report "no world data" (0) and the
  drops surface in `wasm status`. `IGameHostApi.WriteSenseSnapshot` now
  receives the calling mod id so implementations can attribute and cap;
  implementers of the interface must add the parameter.
- `WasmModHost.DispatchPlayerJoin` takes the entity id as `int` (the wire
  type of `on_player_join`); the previous `long` parameter forced a silent
  narrowing cast on every caller.
- The zdtd sense snapshot is bumped to v4 (magic `ZBS4`, 40-byte records
  with server `vy` and the `wearing_glider` bit, ADR 0037), byte-identical
  to the sibling zdtd wire format; v3 guests fail loudly on the magic check
  instead of misreading records. `IGameHostApi` gains
  `TryGetRawConfig(modId, out content)` for the new `zdtd.config` import;
  implementers of the interface must add it.

### Changed

- `NativeAssets.StageNativeLibrary` stages from the newest installed
  Wasmtime NuGet package instead of a hard-coded version string, matching
  what `make dist` already does from the lock file.
- Named the zdtd queue/query result codes in `AbiConstants`
  (`QueueAccepted`, `QueueRejected`, `QueryNoAnswer`,
  `QueryBufferTooSmall`); no wire change (docs/ABI.md unchanged).
- The `zdtd` compatibility module serves the calling mod's own `config.toml`
  verbatim through `zdtd.config` (0 = none; the host never parses it, each
  guest owns its format). The bridge registers `Mods/Wasm/<id>/config.toml`
  at module load (invalidated on reload) and the bridge queue surface now
  handles the parachute mod's `glide <net_id> <0|1>` verb (tracked per
  player, surfaced in `wasm status`) and forwards non-verb queue text to the
  chat broadcast (the parachute deploy announce). Sense records report `vy`
  (from `Entity.motion`) and the `wearing_glider` bit (a worn item whose
  ItemClass carries the parachute tag); the new game API surface is pinned
  in `tools/targetcheck`.
- Dependency audit pass: test stack moved to the newest serviced pins
  (`Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3; runner stays 2.8.2,
  the correct pairing for xunit 2.x). `Wasmtime` stays at 44.0.0: that is
  the newest binding ever published on NuGet; engine advisories patched in
  46/47/48 have no .NET binding yet, and the current host configuration
  does not reach the affected surfaces (no filesystem preopens, single
  Engine/Store). See the updated SECURITY.md note.

### Added

- Release consistency gate (`tools/versioncheck.py`, part of `make check`
  and the tools unit tests): the three shipped-version declarations
  (`src/GameBridge/ModInfo.xml`, `<Version>` in the host library csproj,
  and the newest released `## [X.Y.Z]` changelog section) must agree, and
  the release workflow now checks all three against the tag instead of
  only ModInfo.xml. Also fixed the first real drift this gate exists for:
  ModInfo.xml still said 0.1.0 while the repository ships 0.1.5.
- `ModRunResult.ModId`: dispatch results now carry the registry id of the
  mod that produced them, plus a matching constructor overload. Attribution
  can no longer depend on list position, which was already impossible for
  `DispatchPlayerJoin` (it calls only the mods exporting the handler); the
  bridge's join failure logs name the module now.
- `WasmHostConfig` is validated fail-fast when the host is constructed:
  zero fuel per call, a sub-page memory ceiling, non-positive module size
  or stack caps, and an empty log source prefix are rejected with the
  offending value instead of surfacing later as instant fuel exhaustion or
  blanket module rejection.
- Guest SDK (`samples/guest-common`) covers the full hordeforge import
  surface: added the missing `get_join_player_name` binding plus safe
  wrappers `current_tick()`, `world_time()`, `join_player_name(&mut [u8])`,
  and `log_debug()` so guest code needs no `unsafe` for plain host reads;
  raw imports remain available. `guest-hello` and docs/GUEST_AUTHORS.md use
  the safe path, and the guide gained an `on_player_join` example for Rust
  guests.
- The unmodified zdtd parachute module (workspace sibling, sense v4 +
  `zdtd.config` + `glide`) is staged by `make dist` and `make fixtures`
  (module + its config.toml) and covered by host tests: it loads as-is,
  parses its own config.toml through `zdtd.config`, and arms/clears the
  glide exemption for a falling worn player. A live-server run through the
  `7dtd-playtest` orchestrator (parachute suite) is documented under
  `evidence/playtest-1/`.
- NuGet pack metadata on the host library (package id, version tracking
  CHANGELOG.md, license, repository): `dotnet pack` produces a complete
  package instead of an anonymous default.
- Committed NuGet lock files (`packages.lock.json` per project) with
  SHA512 content hashes for every direct and transitive package:
  restores now verify integrity and consumers get an exact inventory of
  what ships. Generated via `RestorePackagesWithLockFile` in
  Directory.Build.props.
- `make dist` derives the staged native Wasmtime version from the lock
  file instead of a hardcoded copy of the package version.
- Guest output caps covered by unit tests (`GuestRateLimiterTests`): the
  limiter file links into the net8 test project and a second constructor
  takes the millisecond clock, so window resets and TickCount wraparound
  are covered deterministically (ADR 0006 amended).
- Pure bridge helpers moved into the host library with tests:
  `ModuleRoots` (multi-tree module resolution), `SettingsTable`
  (get_setting precedence), `TextSanitizer` (log-forgery guard),
  `ManifestFiles` (bounded manifest reads), plus unit tests for the
  `doccheck` gate rules.
- `bridge-check` pins the glide buff surface (`EntityAlive.Buffs`,
  `EntityBuffs.AddBuff/RemoveBuff/HasBuff`) so a game update renaming
  them fails the gate instead of breaking glides at runtime.

## [0.1.5] - 2026-08-24

### Added

- The bot servant (`BotServant`): spawns bot entities (zombieSoldier
  bodies) on the live world, applies the brain's SimCommands (bot
  spawn/remove/count/move/look/shoot/skill/cfg), and builds the 'ZBS3'
  world snapshot from the live entity list (players, zombies, our bots).
  `queue` and `sense` now dispatch through it; `query` still returns no
  answer (cover/path is stage 3).
- Game API targets for the servant added to `tools/targetcheck`
  (World.Entities/GetEntity/SpawnEntityInWorld, Entity position/SetPosition/
  SetRotation, EntityAlive Health/IsDead/SetDead/DamageEntity,
  EntityFactory, EntityClass), with overload-aware method checking.

### Verified live (container acceptance run)

The unmodified zdtd fps_bot brain now drives real spawned bots on a live
dedicated server: 4 bots spawned, and the brain (fed the live sense
snapshot) targeted each other and ordered shots that the servant applied,
including headshots (`bot 173 shot 171 dmg=24 head`). 1555 shots over the
run; evidence `evidence/acceptance-1/servant-join.log`.

Live-run fixes: spawn retries until the world is ready (the game's own
EAIManager NREs during world creation, and a failed spawn must not latch
the spawned flag), and the servant's own bots are reported as bot kind in
the snapshot (they are zombie-bodied, so classification must check the bot
roster first or the brain never drives them).

### Not yet implemented (stage 3)

- cover/path queries, on_admin_command console wiring, per-bot loadout
  records (weapon info events), and disabling the stock zombie AI on bot
  bodies so the game does not also move them.

## [0.1.4] - 2026-08-24

### Added

- zdtd-server compatibility surface so sibling plugins run unmodified: the
  host defines the `zdtd` import module (log, tick, queue, sense, query)
  and accepts the bare zdtd hooks, including `void`-returning
  on_enable/on_tick/on_shutdown and the optional `on_admin_command` export.
- `SenseSnapshotWriter`: byte-identical 'ZBS3' world snapshot format shared
  by the host, the bridge, and tests.
- ADR 0004 amendment: modules without a declared memory maximum are treated
  as declaring the wasm32 ceiling (4 GiB) and load only when the operator
  raises the cap via `wasm.toml [limits] max_memory_bytes`.
- Verified: the unmodified zdtd `fps_bot` wasm loads, and its brain, fed a
  synthetic sense snapshot, queues `bot look` and `bot shoot` SimCommands
  (four new host tests, 43 total).

### Not yet implemented (stage 2)

- The bot servant: `queue` commands are accepted and logged, `sense`
  returns no world data yet, `query` has no answers. The bridge wires the
  imports but the game-side spawn/move/shoot servant and the live entity
  snapshot are the next slice.

## [0.1.3] - 2026-08-24

### Added

- `samples/guest-boss-zig`: the boss watcher written in Zig
  (zig build-exe, wasm32-wasi), reading its target name from the
  `get_setting` import so `[settings] boss_name` in wasm-mod.toml retunes
  it without rebuilding. Built via `make boss-zig`, staged in
  `dist/Mods/Wasm/boss-zig`, covered by two host tests (39 total).
- TOML mod config following the zdtd-server conventions (docs/CONFIG.md):
  `wasm-mod.toml` (`[limits]`, `[settings]`) per mod, shared
  `Mods/Wasm/wasm.toml` (`[limits]` at host start, `[settings]` re-read on
  change), snake_case keys, load order code defaults -> wasm.toml ->
  wasm-mod.toml. Parsed by a dependency-free MiniToml (ADR 0007). The
  deprecated JSON manifest is still accepted.
- get_setting is now calling-mod aware: a mod's own `[settings]` win over
  shared keys, so two mods can use the same key with different values.

### Changed (breaking ABI, aligned with zdtd-server)

The guest ABI now follows the zdtd-server plugin contract exactly:
exports are the bare hook names `on_enable`, `on_tick`, `on_shutdown`,
`on_player_join` (the `hordeforge:mod/` prefix is gone), hooks are no-arg
(the tick number is read via the `tick` import, renamed from `get_tick`),
and `on_player_join` receives `(entity_id)` (zdtd passes slot and entity
id; we have no ECS slot, and the name comes via `get_join_player_name`).
All guests (Rust, C, Zig), fixtures, tests, and docs updated.

### Verified live (aligned ABI + TOML config)

Second container acceptance run after the alignment: the Zig guest printed
"THE BOSS IS HERE" for `boss_name = "<player-name-redacted>"` read from its wasm-mod.toml
(no rebuild), with the join dispatched as `player spawned: <player-name-redacted> (entity
171)`. Evidence: `evidence/acceptance-1/aligned-abi-join.log`. The run
fixed a Harmony postfix naming bug: `RequestToSpawnPlayer`'s int
parameters are `_chunkViewDim` and `_nearEntityId` (not the player's id),
so the postfix must not declare `_entityId`; the entity id comes from
`ClientInfo.entityId`, now also verified by `tools/targetcheck`.

## [0.1.2] - 2026-08-24

### Added

- Player join events: optional guest export `hordeforge:mod/on_player_join`
  plus the `get_join_player_name` host import; the bridge patches
  `GameManager.RequestToSpawnPlayer` (verified via targetcheck) and
  forwards the joining player's name. Guests without the handler are
  unaffected.
- `samples/guest-boss`: a C guest built with the zig compiler that prints
  "THE BOSS IS HERE" to the console when the player <player-name-redacted> spawns. Built
  via `make boss`, staged in `dist/Mods/Wasm/boss`, covered by three new
  host tests (26 total).

### Verified live (container acceptance run)

- A real player join (loadgen bot, named `<player-name-redacted>` by the harness) reached
  the bridge (`[WasmHost] player spawned: <player-name-redacted>`) and was dispatched to
  the guest handler. Evidence: `evidence/acceptance-1/boss-join-server.log`.
- Hook findings: `GameManager.OnClientSpawned` and
  `GameManager.PlayerSpawnedInWorld` never fire on the dedicated server
  for remote joins; `RequestToSpawnPlayer` is the working server-side
  entry point.

## [0.1.1] - 2026-08-24

In-game acceptance completed (docker container, fresh steamcmd install,
V 3.1.0 b14). Evidence: `evidence/acceptance-1/`, `docs/ACCEPTANCE.md`.

### Fixed (found by the acceptance run)

- `GameTimer.Instance.ticks` reads 0 on the dedicated server; the bridge
  now maintains its own monotonic tick counter (one increment per hook
  run, 20 TPS).
- The game does not rate limit `ChatMessageServer` on its own; the bridge
  now caps guest chat globally at 10 messages/second with a visible drop
  counter in `wasm status` (the log rate limiter was extended to chat).
- The bridge resolved the modlet directory wrong on live servers
  (Native/ and Mods/Wasm are siblings of the modlet, not children of
  Mods/); corrected in BridgeHost.Start.

### Docs conventions

- Added docs/INDEX.md hub, docs/adrs/ (6 ADRs + template), docs/rfcs/,
  docs/prds/ (0001 wasm-mod-hosting), CHANGELOG.md, TODO.md,
  CONTRIBUTING.md, matching the workspace pattern.

## [0.1.0] - 2026-08-24

Initial experiment release.

### Added

- Embeddable host library (`HordeForge.WasmHost`, netstandard2.0 + net8.0)
  on Wasmtime.Dotnet 44 with per-call fuel budgets, load-time memory cap
  from the declared maximum, module size cap, and structured trap
  reporting.
- Guest ABI v0 (`hordeforge` imports: log, get_tick, get_world_time,
  get_setting, send_chat; exports: init, tick, shutdown). Documented in
  docs/ABI.md.
- Per-mod manifests (`wasm-mod.json`) with fuel and memory ceilings, parsed
  by a dependency-free internal parser.
- net48 in-game bridge (`1_HordeForge_WasmHost`): dedicated gate, native
  bootstrap, Harmony tick hook on GameManager.Update, `wasm` console
  commands, settings file, guest log rate capping.
- Rust guest SDK (`samples/guest-common`) and example guests, built with an
  in-project rustup toolchain.
- Test suite: 23 tests covering ABI round trips, traps, fuel exhaustion,
  memory cap, manifest handling, registry semantics.
- `tools/targetcheck`: validates every game API target against a server
  install; all V3.1.0 targets verified.
- Docs gates (`make check`): em dashes, AI attribution, links, TODO format.
- Design records: docs/INDEX.md, docs/adrs/ (6 ADRs), docs/rfcs/,
  docs/prds/.

### Known gaps

- The dedicated server on this machine crashes at boot (environment issue);
  the in-game acceptance instead ran successfully in a docker container
  with a fresh steamcmd install (see evidence/acceptance-1/ and
  docs/ACCEPTANCE.md). The modlet compiles and all game targets are
  verified against both the host install and the container build.
