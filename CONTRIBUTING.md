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
make samples-check # guest lint gate: rustc warnings are build errors
make test         # host suite must stay green
make bridge       # net48 bridge against GAME_DIR
make bridge-check # game targets must pass after any game update
make check        # docs gate + guest lint gate + build + test + bridge
```

Every change lands with its tests and its docs updated in the same commit.
Compiler, analyzer, and rustc lint warnings fail the build (warnings are
errors repo-wide); a suppression needs a written reason next to it.
