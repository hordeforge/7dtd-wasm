# Playtest run 1: parachute suite on a real server (docker V3.2.0 b9)

Goal: run the unmodified zdtd parachute module end to end on a live 7DTD
dedicated server driven by a real stock client through the `7dtd-playtest`
orchestrator, and capture evidence.

## What was proven (passing run, 2026-08-30 01:31, exit=0 pass=2)

The full parachute suite passed on a real server:

- Client log (`output_log_client_7dtd_connect.txt`):
  `PASS parachute/parachute_fall_announce hit=True last=deployed their
  parachute pos=(850.00, 113.82, 642.00)` and
  `SUMMARY pass=2 fail=0` (orchestrator `exit=0`).
- Server log: `[wasm] parachute: config deploy_vy=-6 delay_ticks=10`
  (on_enable read its config.toml through `zdtd.config`), then during the
  fall `glide 175 armed`, `Chat ... deployed their parachute`, then
  `glide 175 cleared` on landing.
- The bridge's sense v4 reported the falling worn player:
  `vy=-14.375 y=113.031 wear=1` (from the temporary diagnostic; the vy is
  now derived from the per-tick position history since the stock server
  does not populate `Entity.motion` for remote players).

This proved the unmodified `parachute.wasm` loads, reads its config, parses
the ZBS4 sense snapshot, arms the glide exemption, announces through the
stock chat broadcast, and clears on landing, all through a real client.

## How to reproduce

1. `make bridge && make dist` in the repo root (stages `dist/Mods`).
2. `./run_server.sh` (this folder): runs the `7dtd-wasm-acceptance:latest`
   docker image (fresh steamcmd V3.2.0 b9) with the bridge, a
   parachute-only `Mods/Wasm` (see `wasm/`), the `parachute-items` modlet
   (item + glide buff), and the playtest serverconfig (telnet 8081
   password `retest`, crossplay off, fresh save).
3. Client prep: the client install needs `platform.cfg` with
   `crossplatform=None` (no EOS; V3.2.0's EOS path crashes on this Linux
   Proton setup), the stock Assembly-CSharp.dll (a RealEarth height-expand
   patch breaks chunk deserialization), and the `parachute` items/buffs
   modlet in the client `Mods/`.
4. `uv run ../7dtd-playtest/scripts/playtest_run.py --no-server --suite
   parachute --port 26900 --admin-port 8081` with `GAME` set to the client
   install and `LOGDIR` to this folder.

## Environment caveat (honest)

The live environment is flaky at the V3.2.0 client + Proton layer: after
the passing run, a client-side `NetPackageChunk` deserialization failure
("Attempted to read past the end of the stream") during world load became
persistent. It was isolated to the environment, not the wasm/bridge code:
reverting every bridge/config/modlet change, removing RealEarth, clearing
the client's `SavesLocal`, and rebuilding the server image from scratch did
not change the failure. The host-side evidence above stands from the
passing run.

## Files

| File | What it is |
|---|---|
| `run_server.sh` | Docker run: bridge + parachute wasm + items/buffs modlet, telnet 8081, ports 26900/26902 on loopback |
| `serverconfig.playtest.xml` | Navezgane, EAC off, crossplay off, fresh save `PlaytestParachute`, telnet password `retest` |
| `platform.cfg` | Server platform: Steam, no crossplay |
| `wasm/` | Parachute-only Mods/Wasm (module.wasm + config.toml + wasm-mod.toml + shared wasm.toml) |
| `parachute-items/` | Game modlet: the parachute item (V3.1+/3.2 schema fixup) + the glide buff + fall-damage patch |
| `logs/server.log` | Server log (bridge boot, parachute config, glide armed/cleared, announce) |
| `orchestrator.log` | Playtest orchestrator run log (client launch, barrier, summary) |
