# 7dtd-wasm: WebAssembly mod host for 7 Days to Die

> **EXPERIMENT.** This project is an experiment: the ABI and host API are
> expected to change, the in-game bridge has not been accepted inside a live
> dedicated server yet, and nothing here is production-ready. It exists to
> answer one question: can 7 Days to Die dedicated servers host untrusted
> mods safely inside a WebAssembly sandbox?

## What this is

A mod host that runs guest mods as `wasm32-wasip1` WebAssembly modules inside
an embedded [Wasmtime](https://wasmtime.dev) engine, with hard limits:

- **Fuel**: every guest call (init, tick, shutdown) gets a fixed instruction
  budget; a burning loop stops at the budget and reports `FuelExhausted`.
- **Memory**: a guest's declared memory maximum is checked at load time
  against the host cap; oversized modules are rejected.
- **Module size**: a .wasm file larger than the cap is refused.
- **No game access beyond the ABI**: guests see no game objects, no
  Reflection, no .NET types. They talk to the game only through five host
  functions (see [docs/ABI.md](docs/ABI.md)).

A thin net48 mod (`1_HordeForge_WasmHost`) embeds the host in the dedicated
server, drives guests from `GameManager.Update` at the game tick rate, and
exposes a `wasm` console command (`list`, `load`, `reload`, `unload`,
`status`).

## Layout

| Path | What |
|---|---|
| `src/HordeForge.WasmHost` | Embeddable host library (netstandard2.0 + net8.0) |
| `src/GameBridge` | net48 in-game mod (ModApi, tick hook, console commands) |
| `samples/` | Rust guest SDK (`guest-common`) and example guests |
| `tests/` | Host test suite + prebuilt fixtures |
| `tools/targetcheck` | Validates game API targets against a server install |
| `tools/doccheck.py` | Docs quality gate (em dashes, links, attribution) |

## Why Wasmtime

`Wasmtime` on NuGet (the official Bytecode Alliance .NET binding) is the most
popular embeddable WebAssembly runtime for .NET by two orders of magnitude
(1.6M downloads vs. 10K for the next candidate), is actively maintained, and
ships exactly the sandbox primitives a 20 TPS game server needs: WASI
preview 1, per-call fuel budgets, memory limits, and trap reporting. The
engine underneath is native Rust; the binding bundles the right native
library per platform. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Quick start

```bash
make build          # host + tests
make fixtures       # compile guest fixtures
make test           # run the sandbox test suite
make bridge-check   # verify the game API targets on your server install
make dist           # stage the modlet under dist/
```

Copy `dist/Mods` into the dedicated server's `Mods/` folder, start the server
with EAC off (any C# mod forces `-noeac`), and run `wasm status` from the
server console. The `hello` sample module logs on load, reports every 100
ticks, and sends a chat greeting every 1000 ticks.

## Safety model

The threat model is "the guest is malicious." Guests cannot read or write
outside their own linear memory, cannot touch the game process beyond the
ABI, and are always interrupted at their budget. Details and limits in
[SECURITY.md](SECURITY.md).

## Status

- [x] Host library: load, dispatch, fuel, memory cap, traps (tested)
- [x] Rust guest SDK + sample mods
- [x] net48 bridge compiles against V3.1.0 and all targets are verified
- [ ] In-game acceptance on a live dedicated server (next step)
- [ ] ABI stability review before anything is called stable

## License

MIT, see [LICENSE](LICENSE).
