# ADR 0009: Align the guest ABI with the sibling zdtd-server plugin contract

## Status

Accepted (2026-08-24).

## Context

The workspace ships a sibling project, `zdtd-server`, whose game plugins
are `wasm32-wasip1` modules. Both projects serve the same server with the
same mods, so a plugin author should not have to maintain two guest
builds. The sibling plugins were written against their own plugin
contract: import module `zdtd` with bare field names, hook exports under
bare names, and a sense snapshot in a layout only the sibling server
writes.

Quarantine had its own contract at the time: hooks exported as
`hordeforge:mod/on_enable`, host imports namespaced and taking a tick
argument, its own `get_tick` import, its own sense layout. Nothing
recorded which of the two contracts should win, and the two could not
both hold: the same .wasm file cannot import `zdtd` and not import it.

Options:

- **Keep both.** Leave the Quarantine contract and translate at the
  boundary. The translation is a second ABI to specify, test, and keep in
  step, and the point of a plugin ABI is that a plugin is written once.
- **Mirror the sibling contract and make Quarantine's own name the
  special case.** The `hordeforge` import module stays for Quarantine
  guests, the `zdtd` module is defined with the sibling's names and
  semantics, and hook exports are accepted under bare names. The cost is
  a breaking change to every existing guest (Rust, C, Zig), the
  fixtures, and the tests, all of which had to land together.
- **Drop Quarantine's own names and use the sibling's everywhere.** The
  `hordeforge` module has no external consumer, so this was available
  and was not taken: the two module names are cheap, and keeping them
  lets a plugin be portable while the project still owns a documented
  surface of its own.

## Decision

The guest ABI follows the sibling `zdtd-server` plugin contract: hooks
are exported under their bare names (`on_enable`, `on_tick`,
`on_player_join`, `on_shutdown`, and the optional `on_admin_command`),
the `hordeforge:mod/` prefix is gone, hooks take no arguments (the tick
number comes from the `tick` import), and `on_player_join` receives
`(entity_id)`, the one structural difference from the sibling, which
passes an ECS slot we do not have. The `zdtd` import module
(`log`, `tick`, `queue`, `sense`, `query`, `config`) is defined with the
sibling's names and wire behavior, so a sibling plugin loads
unmodified. Guest hooks are accepted with either an `i32` result or
`void`, because sibling plugins use `void`.

The host serves real game state for that surface: the bridge implements
`queue` and `sense` over the live world (`Bridge/BotServant.cs`), spawning
zombie bodies and driving them from queued verbs, and serves the calling
mod's own `config.toml` verbatim through `zdtd.config` without parsing
it. The sense layout follows the sibling; its version history is
recorded in ADR 0008.

## Consequences

Easy: a sibling plugin runs in Quarantine with no guest-side change; one
plugin ABI across the workspace; new plugin work has a second consumer
to stay honest about. Foreclosed: the namespaced export form and the
argument-taking hooks, and any guest that still uses them (all in-repo
guests were migrated in the same change); `on_player_join` will never see
an ECS slot unless the host gains one; the host must keep two import
modules in step with the sibling's, so a sibling contract change is a
breaking change here. Honest downside: the ABI is now partly owned by
another repository, and a rename in `zdtd-server` is a rename here;
`docs/ABI.md` is the contract of record so the coupling is at least
written down. Revisit if the two projects diverge enough that the shared
contract costs more than it saves, or if the component model (ADR 0003)
replaces the flat import surface.
