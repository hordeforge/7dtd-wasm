# Architecture decision records

One decision per file, in the order they were made. A decision still being
argued is an [RFC](../rfcs/), not an ADR. When a later decision reverses this
one, mark the file Superseded and link forward instead of rewriting history.

| ADR | Decision |
|---|---|
| [0001](0001-use-wasmtime-dotnet.md) | Embed the Wasmtime.Dotnet runtime |
| [0002](0002-fuel-budget-per-call.md) | Budget every guest call with fuel, not wall clock |
| [0003](0003-linear-memory-string-abi.md) | Strings cross the ABI as linear-memory pointers, never managed handles |
| [0004](0004-memory-cap-at-load.md) | Enforce the memory cap at load from the declared maximum |
| [0005](0005-dependency-free-manifest-parser.md) | Parse wasm-mod.json with an internal parser, not a JSON library |
| [0006](0006-guest-log-rate-capping-in-bridge.md) | Rate cap guest log output in the bridge, not the host |
| [0007](0007-toml-config-schema.md) | Mod config is TOML, following the zdtd-server conventions |
