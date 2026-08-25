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
make fixtures     # if you touched samples/ or guests
make samples-check # guest lint gate: rustc + clippy warnings are build errors
make test         # host suite must stay green
make bridge       # net48 bridge against GAME_DIR
make bridge-check # game targets must pass after any game update
make check        # docs gate + sbom tests + guest lint + build + test + bridge

# Dependency changes: bump the PackageReference, then regenerate every
# committed packages.lock.json with a plain (unlocked) restore; "make
# check" restores locked and fails when a manifest drifts from its lock.
dotnet build HordeForge.WasmHost.sln   # refresh all packages.lock.json
python3 tools/sbom.py                  # preview the CycloneDX SBOM make dist ships
```

Every change lands with its tests and its docs updated in the same commit.
Compiler, analyzer, and rustc lint warnings fail the build (warnings are
errors repo-wide); a suppression needs a written reason next to it.

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
  the release workflow rejects a `vX.Y.Z` tag that does not match all three.
- **Changelog before tag.** A release exists when its dated CHANGELOG.md
  section exists; tagging without cutting that section fails in CI.
- **Breaking changes bump the minor digit** and say "(breaking)" in their
  changelog entries. Breaking means the guest ABI (docs/ABI.md), the host
  library's public C# surface, or an operator-visible config/wire format.
- Historical note for consumers auditing old tags: 0.1.3 shipped a guest
  ABI break in a patch slot before this rule was written down; guests had
  to be rebuilt in the same release, but the version number did not warn
  them. Do not repeat that shape.

There is no deprecation policy yet: this is pre-1.0, symbols can disappear
between minors, and the changelog entry naming the replacement is the only
notice a removal gets.
