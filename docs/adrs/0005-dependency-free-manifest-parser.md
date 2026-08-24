# ADR 0005: Parse wasm-mod.json with an internal parser, not a JSON library

## Status

Accepted (2026-08-24).

## Context

Per-mod manifests (`wasm-mod.json`) are read by the host library, which is
shared between the net8 host tooling and the net48 in-game bridge
(netstandard2.0). JSON parsing options:

- **System.Text.Json**: needs a package for netstandard2.0 and drags
  `System.Text.Encodings.Web` (and transitive deps) into the modlet
  closure. The game ships its own JSON assemblies in Managed; shipping a
  second copy risks shadowing the game's versions for other mods, the same
  class of problem that excluded `Unsafe.dll` from the modlet.
- **Newtonsoft.Json**: available in the game, but only for the bridge; the
  host library would still need a parser, and tests would need the package.
- **Internal minimal parser**: a ~150-line recursive descent parser for the
  JSON value grammar, zero dependencies, no closure growth, fully
  testable.

The sandbox host deliberately keeps its dependency surface small: the trust
boundary around untrusted code should not grow with JSON library dlls, and
the modlet closure stays exactly the bridge output plus the native engine.
The manifest shape is fixed and documented, so a strict parser is not a
maintenance burden.

## Decision

`HordeForge.WasmHost` contains `MiniJson`, a dependency-free JSON parser
that produces a tiny value tree, used only for manifests. `ModManifest.Parse`
rejects malformed JSON and out-of-range values with `WasmModLoadException`
so the caller can skip the module with a specific reason.

## Consequences

Easy: no new dlls in the modlet, no shadowing risk, host-side parsing is
unit-tested (malformed, out-of-range, unknown fields). Foreclosed: JSON
features beyond the implemented grammar (fractional/exponent numbers are
rejected by design). Honest downside: a hand-rolled parser is more code to
review than a library call; the manifest grammar must stay simple, and
adding a new manifest field means touching the parser. Revisit if the
manifest grows a real schema or the game's own JSON library proves
unambiguously safe to reference from the host.
