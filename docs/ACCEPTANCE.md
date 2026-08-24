# Acceptance status

## Attempt 1 (2026-08-24): blocked by a machine-level server crash

The in-game acceptance run was attempted on this machine with the modlet
staged into the installed dedicated server (`dist/Mods` copied into
`Mods/`, other workspace mods quarantined for a clean run).

Outcome: the dedicated server crashes at startup with a Mono SIGSEGV in the
game entrypoint coroutine, **before any mod loads**, with and without the
modlet, and with a completely stock install (zero mods). The crash is
environmental, not caused by the WasmHost code. Full evidence and
diagnostics: `evidence/acceptance-1/README.md`.

Environment facts captured from the boot log:

- Game: 7 Days to Die dedicated server, V3.1.0 line (Henpocalypse), Unity
  engine 2022.3.62f2
- Crash signature: `Caught fatal signal - signo:11 code:1 errno:0 addr:0x8`
  in `GameEntrypoint/<EntrypointCoroutine>d__8:MoveNext()`
- Server config used: `evidence/acceptance-1/serverconfig.xml` (Navezgane,
  telnet 8081, EAC off, fresh userdata)

## What this means for the project

The host library and all sandbox guarantees remain covered by the automated
test suite (23 tests). The net48 bridge compiles against this exact install
and `tools/targetcheck` verifies every game API target it uses. What is not
yet proven is the bridge running inside a live server process, which needs a
machine where the dedicated server boots (see the unblock paths in
`evidence/acceptance-1/README.md`).

## Unblock paths

1. Repair the install via Steam (`steamcmd +app_update 294420 validate`).
2. Run the acceptance on the workspace's container LAN host
   (`7dtd-server-container`), staging `dist/Mods` there.
3. Reproduce the Mono crash with the vendor before trusting this install.
