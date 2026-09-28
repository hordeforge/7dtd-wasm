# Architecture decision records

One decision per file, in the order they were made. A decision still being
argued is an [RFC](../rfcs/), not an ADR. When a later decision reverses this
one, mark the file Superseded and link forward instead of rewriting history.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-use-wasmtime-dotnet.md) | Embed the Wasmtime.Dotnet runtime | Accepted |
| [0002](0002-fuel-budget-per-call.md) | Budget every guest call with fuel, not wall clock | Accepted |
| [0003](0003-linear-memory-string-abi.md) | Strings cross the ABI as linear-memory pointers, never managed handles | Accepted |
| [0004](0004-memory-cap-at-load.md) | Enforce the memory cap at load from the declared maximum | Accepted |
| [0005](0005-dependency-free-manifest-parser.md) | Parse wasm-mod.json with an internal parser, not a JSON library | Superseded by [0007](0007-toml-config-schema.md) |
| [0006](0006-guest-log-rate-capping-in-bridge.md) | Rate cap guest log output in the bridge, not the host | Accepted |
| [0007](0007-toml-config-schema.md) | Mod config is TOML, following the zdtd-server conventions | Accepted |
| [0008](0008-zdtd-sense-v4-and-config-import.md) | Adopt the zdtd sense v4 snapshot and the self-contained config import | Accepted |
| [0009](0009-align-abi-with-zdtd-server.md) | Align the guest ABI with the sibling zdtd-server plugin contract | Accepted |
