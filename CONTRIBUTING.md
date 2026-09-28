# Contributing

This is an experiment. Contributions that keep the sandbox honest are
welcome; contributions that weaken it are not.

## Before you start

- Read [docs/INDEX.md](docs/INDEX.md) for the design contract and
  [AGENTS.md](AGENTS.md) for the project rules.
- If a change alters the guest ABI (docs/ABI.md), it is breaking: update
  the host, the guests, the tests, and the docs in the same change.
- If a change makes a design decision, write an ADR
  ([docs/adrs/TEMPLATE.md](docs/adrs/TEMPLATE.md)); if it argues one,
  write an RFC first.

## Rules

- **Do not weaken limits.** Never remove fuel, memory, or module caps to
  make a guest work; tighten them if anything.
- **Untrusted by default.** Guests never see game objects, Reflection, or
  file access beyond the ABI.
- **Fail soft per module.** One broken guest must not stop the game loop.
- **No em dashes, no AI attribution** in any shipped text (enforced by
  `make check`).
- **Do not redistribute game assemblies or bulk IL.**
- **Secrets via env only**, never in argv or commits.

## Change flow

```bash
make toolchain     # once per clone: fills .cargo/ and .rustup/ (no system-wide Rust)
make test          # host suite must stay green
make test TEST_FILTER='FullyQualifiedName~WasmModHostTests'   # one test or class
make test-list     # every test name a TEST_FILTER can match
make samples-check # guest lint gate: rustc + clippy warnings are build errors
make fixtures      # if you touched samples/ or guests; also needs zig and ZDTD_SERVER
make bridge        # net48 bridge against GAME_DIR
make bridge-check  # game targets must pass after any game update
make check         # docs gate + sbom tests + tools lint + guest lint + build + test + bridge + bridge-check
make check-ci      # the same gate minus bridge and bridge-check (no game install needed)

# Dependency changes: bump the PackageReference, then regenerate every
# committed packages.lock.json; "make check" restores locked and fails when a
# manifest drifts from its lock.
make locks                             # refresh all four packages.lock.json
python3 tools/sbom.py                  # preview the CycloneDX SBOM make dist ships
```

`make locks` restores each project on its own rather than through the solution:
`HordeForge.WasmHost.sln` covers only the host library and its tests, so a
solution-level restore leaves the `src/GameBridge` and `tools/targetcheck` lock
files stale and locked restore fails on the next `make check`.

`make check-ci` is the full gate for anything that does not touch the game
bridge, and it is what CI runs, so it is the shortest honest answer to "is my
change shippable?". `make check` adds the two targets that need a dedicated
server install. `make help` lists every target and what it needs.

Every change lands with its tests and its docs updated in the same commit.
Compiler, analyzer, and rustc lint warnings fail the build (warnings are
errors repo-wide); a suppression needs a written reason next to it.

## Fuzzing

The untrusted-input surfaces (the manifest parser, and the guest-text
validators) have seeded randomized harnesses in the test project, so
`make test` is also the fuzz gate. There is no native fuzzing engine in
the .NET toolchain here, so a harness is a property test over a mutation
pass, and the assertions are the contract the host depends on: only
`WasmModLoadException` may leave the manifest parser, one input stays
inside a wall-clock budget, a parse is deterministic, and sanitized text
keeps its length and its kept characters.

```bash
# Longer soak, different input sequence. Both are also how a crash is
# replayed: every failure prints the seed and the exact input.
HORDEFORGE_FUZZ_ITERATIONS=100000 make test
HORDEFORGE_FUZZ_SEED=1234 make test
```

A new untrusted-input parser gets a harness in the same change: seed it
with the files the project actually ships, assert the exception type the
caller handles, and pin every input the harness found with a plain test
next to the parser.

## Repository tools

`tools/doccheck.py`, `tools/versioncheck.py`, `tools/packcheck.py`,
`tools/apicheck.py`, `tools/sbom.py` and `tools/targetcheck` share one
command-line contract:

- `--help` documents every flag; `--root` (where it applies) selects the
  repository to work on and defaults to the tool's own checkout.
- Machine-readable data goes to stdout (`sbom.py` prints the CycloneDX JSON
  unless `--output` names a file), so `sbom.py | jq .` works.
- Progress, diagnostics and summaries go to stderr; a caller piping stdout
  never sees gate chatter mixed into the data.
- Exit codes: 0 pass, 1 check failed, 2 usage error (unknown flag, too many
  arguments, or no server install under `GAME_DIR`).

## Versioning and releases

This is a 0.x experiment: the minor digit carries breaking changes, the
patch digit never does. A consumer on any 0.1.x must be able to take the
next patch without reading anything. The rules below were inferred from
how releases have actually been cut here and are now enforced, not just
requested:

- **One shipped version, three declarations.** The version lives in
  `src/GameBridge/ModInfo.xml` (what a game server displays), `<Version>`
  in `src/HordeForge.WasmHost/HordeForge.WasmHost.csproj` (the publishable
  package), and the newest released `## [X.Y.Z]` section of CHANGELOG.md.
  `tools/versioncheck.py` (part of `make check`) fails when they disagree;
  the release workflow runs the same tool with `--tag`, which also requires
  all three to ship the version the `vX.Y.Z` tag names.
  A tag also has to pass the same CI gate a pull request does: the release
  workflow calls `ci.yml` as a reusable workflow, so there is one copy of
  the gate and a release never ships a tree main has not proved.
- **Changelog before tag.** A release exists when its dated CHANGELOG.md
  section exists; tagging without cutting that section fails in CI.
- **Breaking changes bump the minor digit** and say "(breaking)" in their
  changelog entries. Breaking means the guest ABI (docs/ABI.md), the host
  library's public C# surface, or an operator-visible config/wire format.
- **A release that carries breaking changes says what to do about them.**
  The changelog entry says what changed and why, which is written for the
  person auditing it; the action a consumer has to take goes in
  [docs/MIGRATION.md](docs/MIGRATION.md), one section per release, grouped
  by the consumer kind that has to act (operator, embedder, guest author),
  with the before, the after, and the fix. A breaking entry that names no
  migration step belongs in that file before the tag, or it is not ready to
  ship.
- **The public surface is pinned, so a break cannot pass unnoticed.**
  `tools/apicheck.py` (part of `make check`) compares the public and
  protected surface of `src/HordeForge.WasmHost` with the committed
  baseline `tools/api-surface.txt` and fails on any difference, a removal
  included. Accepting a change is deliberate: bump as the rule above says,
  write the changelog entry, then regenerate the baseline with
  `python3 tools/apicheck.py --update`, whose diff lands in the same commit.
  A member's body and doc comment are not surface, so editing them does not
  need the baseline rewritten.
- Historical note for consumers auditing old tags: 0.1.3 shipped a guest
  ABI break in a patch slot before this rule was written down; guests had
  to be rebuilt in the same release, but the version number did not warn
  them. 0.3.1 repeated it with `WasmModHost.TryInit`, a public member of
  the published package removed in a patch slot; its changelog entry is
  marked `(breaking)` so the audit trail is right even though the number
  does not warn. Both are why `apicheck` exists now.

There is no deprecation policy yet: this is pre-1.0, symbols can disappear
between minors, and the changelog entry naming the replacement is the only
notice a removal gets.

### Cutting a release

The order matters, because the first three steps are what the tag gate
checks and the last two are what the tag cannot check for you.

1. Retitle the `## Unreleased` section of CHANGELOG.md to
   `## [X.Y.Z] - YYYY-MM-DD` with the date it ships, and set the same
   version in `src/GameBridge/ModInfo.xml` and in `<Version>` in
   `src/HordeForge.WasmHost/HordeForge.WasmHost.csproj`. One commit, so the
   three declarations cannot be split.
2. Every `(breaking)` entry in that section has its upgrade steps in
   [docs/MIGRATION.md](docs/MIGRATION.md) under the same version heading.
3. `python3 tools/versioncheck.py` (it runs on `make check`) and
   `make check-ci`.
4. `make pack`, then attach `artifacts/packages/*.nupkg` to the release.
   The package is not published by the tag: pushing `vX.Y.Z` runs the CI
   gate and the tag check, and nothing else. nuget.org rejects a version
   that already exists, so a version is immutable once it lands there; if a
   published version turns out to be broken, the fix ships as the next
   patch and the published one stays.
5. `make dist` on a machine with a dedicated server install, then attach
   `dist/Mods/1_HordeForge_WasmHost` to the release. The tag workflow does
   not build the mod archive (it needs the game's own assemblies, which a
   hosted runner does not have), so a release whose modlet was never staged
   is one an operator can download broken. The staged tree is what
   `evidence/acceptance-1/run_acceptance.sh` and
   `evidence/playtest-1/run_server.sh` run against.
6. Tag `vX.Y.Z` at the commit that carries all of the above.

## Packaging

`HordeForge.WasmHost` ships two artifacts, and both are built by a target
rather than assembled by hand:

- `make pack` builds the NuGet package into `artifacts/packages/`. It runs
  `tools/packcheck.py` first, which fails when the manifest in
  `src/HordeForge.WasmHost/HordeForge.WasmHost.csproj` loses a field a
  consumer sees: the identity fields, the license expression (it must match
  `LICENSE`), the readme (`PackageReadmeFile` plus the file being packed),
  and `THIRD-PARTY-NOTICES.md`, which has to travel inside the package
  because the shipped closure includes Apache-2.0 WITH LLVM-exception
  Wasmtime. `make pack` runs on `make check-ci`, so a package that no longer
  packs or no longer declares its terms fails on the same gate as a broken
  test. The version itself is owned by `tools/versioncheck.py`.
- `make dist` assembles the game modlet under `dist/Mods` plus the sample
  guests and the CycloneDX SBOM. It needs a dedicated server install, so it
  stays out of the CI gate.
