# Design contract index

Quarantine (7dtd-wasm): WebAssembly mod host for 7 Days to Die.

Reading order for a new contributor: start here, then
[ARCHITECTURE.md](ARCHITECTURE.md) (how the pieces fit), [ABI.md](ABI.md)
(the guest contract), then the document that owns the part you are changing.
Every document defers definitions to the document listed as canonical; when
two documents disagree, the canonical one wins and the disagreement is a bug
to fix in the same change.

| Document | Owns | Canonical for |
|---|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Runtime pipeline, host library, bridge, guest toolchain, target verification | Component responsibilities, thread rules, limit mechanics, source layout |
| [CONFIG.md](CONFIG.md) | Mod config schema: wasm.toml, wasm-mod.toml, limits, settings, load order | Every config key, its default, and the resolution order |
| [ABI.md](ABI.md) | Guest contract: imports, exports, strings, status codes, manifests, versioning | Every symbol a guest may import or export and its signature |
| [GAME_HOOKS.md](GAME_HOOKS.md) | In-game integration: tick hook, console commands, settings, verified game API surface | Which game members the bridge touches and how |
| [GUEST_AUTHORS.md](GUEST_AUTHORS.md) | How to write a guest mod | Guest-side rules, deployment, manifest usage |
| [ACCEPTANCE.md](ACCEPTANCE.md) | In-game acceptance status and evidence | What has and has not been proven on a live server |
| [adrs/](adrs/README.md) | Accepted design decisions | Why each decision was made and what would justify revisiting it |
| [rfcs/](rfcs/README.md) | Design questions still being argued | Open questions and their drivers |
| [prds/](prds/README.md) | Capability requirements | What a capability must do and its acceptance boxes |
| [../SECURITY.md](../SECURITY.md) | Threat model, sandbox guarantees, operational notes | What the sandbox does and does not protect |
