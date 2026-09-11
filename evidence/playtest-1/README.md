# Playtest run 1: parachute suite on a real server (V3.2.0 b9)

Goal: run the unmodified zdtd parachute module end to end on a live 7DTD
dedicated server driven by a real stock client through the `7dtd-playtest`
orchestrator, and capture evidence.

## What was proven (passing run, 2026-09-04, exit=0 pass=3)

The full parachute suite passed on a real server (managed Safehouse pair
`srv-playtest` / `client-playtest`, suite `suites/parachute.json` in
7dtd-playtest):

- Client log (`output_log_client_7dtd_connect.txt`):
  `PASS parachute/parachute_equip item resolved=True`,
  `PASS parachute/parachute_fall_announce hit=True last=deployed their
  parachute pos=(850.00, 253.65, 642.00)`,
  `PASS parachute/parachute_land_safe y=95.1111 base=245.5289 buffGone=True`,
  and `SUMMARY pass=3 fail=0` (orchestrator `exit=0`).
- Server log: `[wasm] parachute: config deploy_vy=-6 delay_ticks=10`
  (on_enable read its config.toml through `zdtd.config`), then during the
  200-block fall `glide 175 armed`, `Chat ... deployed their parachute`,
  then `glide 175 cleared` on landing.
- The bridge's sense v4 reported the falling worn player (vy derived from
  the per-tick position history since the stock server does not populate
  `Entity.motion` for remote players).

This proved the unmodified `parachute.wasm` loads, reads its config, parses
the ZBS4 sense snapshot, arms the glide exemption, announces through the
stock chat broadcast, and clears on landing, all through a real client. The
landing case proves the glide is slow (sink rate) and safe (alive, no
broken/sprained leg).

## How to reproduce

1. `make bridge && make dist` in the repo root (stages `dist/Mods`).
2. In `../7dtd-playtest`:
   `uv run scripts/playtest_run.py --suite parachute --server stock
   --sandbox-root ../7dtd-sandbox --logdir ../7dtd-wasm/evidence/playtest-1
   --timeout 500`.
   The managed run stages the server mods (`1_HordeForge_WasmHost`,
   `parachute-items`, `wasm-bridge`) and the client mods (`playtest`,
   `fastconnect`, `parachute-items`, `parachute-client`) from the suite
   file, brings up the Safehouse pair, and runs the three cases.
3. The legacy docker path still works: `./run_server.sh` (this folder) runs
   the `7dtd-wasm-acceptance:latest` image with the bridge, a
   parachute-only `Mods/Wasm` (see `wasm/`), the `parachute-items` modlet,
   and the playtest serverconfig (telnet 8081 password `retest`, crossplay
   off, fresh save); then attach with `--no-server --port 26900
   --admin-port 8081` plus `PLAYTEST_TELNET_PASSWORD=retest` and `GAME` set
   to a Windows client install (the Linux Steam install has no
   `7DaysToDie.exe` for Proton to launch).

## Environment caveat (honest)

The operator Steam client + Proton path is flaky at the V3.2.0 layer: a
client-side `NetPackageChunk` deserialization failure ("Attempted to read
past the end of the stream") during world load was persistent there. It was
isolated to the environment, not the wasm/bridge code: reverting every
bridge/config/modlet change, removing RealEarth, clearing the client's
`SavesLocal`, and rebuilding the server image from scratch did not change
the failure. The managed Safehouse pair (Windows depot client, no Steam)
runs green; the host-side evidence above stands from the passing run.

## Files

| File | What it is |
|---|---|
| `run_server.sh` | Docker run (legacy attach path): bridge + parachute wasm + items/buffs modlet, telnet 8081, ports 26900/26902 on loopback |
| `serverconfig.playtest.xml` | Legacy attach serverconfig (Navezgane, EAC off, crossplay off, fresh save `PlaytestParachute`, telnet password `retest`) |
| `platform.cfg` | Server platform: Steam, no crossplay |
| `wasm/` | Parachute-only Mods/Wasm (module.wasm + config.toml + wasm-mod.toml + shared wasm.toml) |
| `wasm-bridge/` | Same tree as a stageable modlet (`Wasm/` + ModInfo.xml) for the managed suite, which can only stage whole modlets; `module.wasm` itself is not committed (copy the unmodified `../zdtd-server/mods/parachute/parachute.wasm` in at stage time) |
| `parachute-items/` | Game modlet: the parachute item (V3.1+/3.2 schema fixup) + the glide buff |
| `parachute-client/` | Client modlet source + built dll: slow-fall clamp (`vp_FPController.UpdateForces` postfix caps `m_FallSpeed` at -2.5) + no-damage (`vp_PlayerDamageHandler.OnMessage_FallImpact` prefix skips while gliding) |
| `logs/server.log` | Server log (bridge boot, parachute config, glide armed/cleared, announce) |
| `orchestrator.log` | Playtest orchestrator run log (client launch, barrier, summary) |
