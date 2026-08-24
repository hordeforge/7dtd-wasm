# TODO

Checklist format only (enforced by tools/doccheck.py). Items are ordered by
dependency, not priority.

## In-game acceptance (done)

- [x] Run the modlet on a dedicated server that boots: done in a docker
      container with a fresh steamcmd install; evidence in
      evidence/acceptance-1/ and docs/ACCEPTANCE.md (host install on this
      machine crashes at boot, unrelated to the modlet).
- [x] Re-run `make bridge-check` against the container's game build:
      all targets present (V 3.1.0 b14).
- [ ] Repeat acceptance on the workspace's container LAN host when
      convenient, to cover the non-docker deployment path.

## Host

- [ ] Rate-cap guest log output with unit tests (currently bridge code
      exercised only in acceptance; see ADR 0006).
- [ ] Boot payload for init (RFC candidate).
- [ ] Event surface: entity, player, and world hooks (RFC candidate).
- [ ] ABI versioning pass and compatibility policy (RFC candidate).
- [ ] Re-validate budgets after each Wasmtime upgrade (fuel is approximate
      cost; see ADR 0002).

## Bridge

- [ ] Confirm native library loading on Windows in-game (documented, not
      exercised).

## Docs and gates

- [ ] Keep docs/adrs current when decisions change; a reversal marks the
      old ADR Superseded instead of rewriting it.
