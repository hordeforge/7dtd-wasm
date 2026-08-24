# AGENTS.md - 7dtd-wasm (Quarantine)

WebAssembly mod host experiment for 7 Days to Die dedicated servers.
Runs untrusted guest mods as `wasm32-wasip1` modules inside an embedded
Wasmtime engine with hard limits (fuel, memory, module size). This is an
**experiment**: the ABI and host API surface are expected to change.

Workspace root guide: [`../MODDING_BEST_PRACTICES.md`](../MODDING_BEST_PRACTICES.md)

## Scope

| Owns | Does not own |
|---|---|
| Embeddable wasm host library (`HordeForge.WasmHost`) | Load generation (use `7dtd-loadgen`) |
| Guest ABI and host API surface (`docs/ABI.md`) | Server measurement (use `7dtd-server-apm`) |
| net48 in-game bridge mod `1_HordeForge_WasmHost` | Runtime optim patches (use `7dtd-server-optimizer`) |
| Guest SDK and sample mods (`samples/`) | Anti-cheat behavior (use `7dtd-server-guard`) |
| `tools/targetcheck` game API gate, `tools/doccheck.py` docs gate | Engine RE narratives (use `7dtd-engine-research`) |
| `dist/` modlet staging | Shipping game assemblies or bulk IL |

Docs conventions follow the workspace pattern: start at `docs/INDEX.md`,
then the document that owns the part being changed. Design decisions are
recorded as ADRs (`docs/adrs/`), open questions as RFCs (`docs/rfcs/`),
capability requirements as PRDs (`docs/prds/`); see the templates there.

## Critical rules

1. **This is an experiment.** The ABI (`docs/ABI.md`) is a contract between
   host and guests; changing it is a breaking change and must land together
   with updated guests and tests.
2. **Untrusted code is the default.** Guests are hostile; never expose game
   objects, Reflection, or file access beyond the documented ABI. Limits are
   enforced by the host, never by guest goodwill.
3. **Fail soft per module.** One trapped or fuel-burning guest must never
   stop the game loop or other modules (`ModRunResult` + per-mod try/catch).
4. **Every guest call is budgeted.** Fuel per call, memory maximum at load,
   module size cap. Do not remove limits to make a guest work.
5. **Re-validate game targets after every game update.** `make bridge-check`
   runs `tools/targetcheck` against the install; it must pass before the
   bridge is trusted (targets break silently on Steam patches).
6. **In-game mod DLL is net48** against the dedicated install's Managed;
   host tooling and tests are net8.0.
7. **No AI attribution** in commits, docs, or comments. **No em dashes** in
   any text this project ships (enforced by `make check`).
8. **Do not redistribute game assemblies or bulk IL.** The bridge references
   game DLLs but ships none; RE facts come from `7dtd-engine-research`.
9. Guests are built with the in-project rustup toolchain (`.cargo/`,
   `.rustup/`); do not install system-wide tooling.
10. Secrets (telnet, dashboard) via env only, never in argv or commits.

## Build / test / run

```bash
make build          # host library + tests (net8)
make test           # host test suite (needs prebuilt fixtures in tests/fixtures)
make fixtures       # rebuild guest fixtures from samples/ and stage them
make bridge         # net48 mod against GAME_DIR (defaults to this machine's install)
make bridge-check   # validate game API targets against GAME_DIR
make dist           # assemble dist/Mods/1_HordeForge_WasmHost + sample guests
make check          # doccheck + build + test + bridge + bridge-check (CI entry)
```

Known gaps (stated honestly): the bridge has run inside a live dedicated
server only in a docker container (acceptance succeeded; fresh steamcmd
install, V 3.1.0 b14); the native install on this machine crashes at boot,
so it was not used. Evidence: `evidence/acceptance-1/` and
`docs/ACCEPTANCE.md`. The host library and all sandbox guarantees are
covered by the test suite.
