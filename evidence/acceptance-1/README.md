# Acceptance run 1: BLOCKED (dedicated server cannot boot on this machine)

Goal: run `dist/Mods` inside a live dedicated server and capture the
`[WasmHost] started`, `hello mod loaded`, per-tick log lines, and the chat
greeting.

## Result

The dedicated server crashes at startup on this machine, **before any mod
code runs**. The acceptance run could not start.

## Evidence

- `server.log`     run with the HordeForge WasmHost modlet installed
- `baseline.log`   same run with the modlet removed, other mods restored
- `stock.log`      same run with ALL mods removed (completely stock game)
- `stock_stdout.txt` / `stdout.txt`  process stdout for the stock run

All three logs end identically:

```
The referenced script on this Behaviour (Game Object 'BlockProcessor') is missing!
Caught fatal signal - signo:11 code:1 errno:0 addr:0x8
#7 ... GameEntrypoint/<EntrypointCoroutine>d__8:MoveNext ()
```

A SIGSEGV (null dereference, `addr:0x8`) inside Mono during the game's
entrypoint coroutine, before mods are loaded (no `[MODS]` lines appear).

## Diagnostics performed

- Modlet presence: crash is identical with and without the modlet and with
  zero mods installed, so the modlet is not the cause.
- Binary integrity: `Assembly-CSharp.dll` is byte-identical to
  `Assembly-CSharp.dll.re_stock_bak`; `boot.config` and
  `ScriptingAssemblies.json` are stock. No tampering found.
- Install consistency: the dedicated server and client installs show the
  same Steam update pattern (data files newer than the exe); `BlockProcessor`
  is not a class in any game assembly, and the missing-script warning is
  stock noise.
- History: no successful dedicated server boot logs exist anywhere on this
  machine (no `server_prefab_*.txt` under `~/.cache/7dtd-loadgen`), and a
  `mono_crash.mem.1807609.1.blob` from 2026-08-21 shows the same failure
  class predates this session.

## Conclusion

The 7 Days to Die dedicated server binary cannot boot on this machine
(environment or install-level Mono crash). This matches the workspace's
stated gap ("the bridge has not been run inside a live dedicated server in
this workspace"). The modlet itself is unaffected: the host library and
sandbox behavior are covered by the 23-test suite, and the net48 bridge
compiles against this exact install with all game API targets verified by
`tools/targetcheck`.

## Suggested unblock paths (for a future run)

1. Repair or refresh the install via Steam (`steamcmd +app_update 294420
   validate`), then repeat this run; steamcmd is not installed on this
   machine yet.
2. Boot the server on a machine where it is known to run (the workspace's
   container LAN host per `7dtd-server-container`), stage `dist/Mods`, and
   capture the same evidence there.
3. Reproduce the Mono crash with the vendor before trusting the install.

## Install state

The dedicated server install was left exactly as found: all previously
present mods restored, no WasmHost files left behind. No game files were
modified or deleted.
