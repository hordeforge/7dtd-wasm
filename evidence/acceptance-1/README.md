# Acceptance run 1: SUCCESS (docker container with a fresh steamcmd install)

Goal: run `dist/Mods` inside a live dedicated server and capture evidence
that the host loads, ticks, and lets the guest reach the game.

The native server install on this machine crashes at boot (see
`baseline.log` / `stock.log` below), so the run used a docker container
(`Dockerfile`) with a fresh steamcmd install of the dedicated server
(Steam app 294420), following the workspace's `7dtd-server-container`
pattern. The modlet and the `Wasm` module folder were bind-mounted into the
container's `Mods/`; the native Wasmtime library path was provided as a
process-start `LD_LIBRARY_PATH` (see `run_acceptance.sh`).

## Evidence

| File | What it shows |
|---|---|
| `acceptance-server.log` | Full server log of the acceptance run |
| `console.txt` | Telnet transcript: `wasm status`, `wasm list`, `version` |
| `serverconfig.container.xml` | Server config used (Navezgane, telnet 8081, EAC off) |
| `Dockerfile`, `run_acceptance.sh`, `telnet_session.py` | Reproducible run tooling |

Key log lines (from `acceptance-server.log`):

```
INF [MODS] Loaded Mod: 1_HordeForge_WasmHost (0.1.0)
INF [wasm] hello mod loaded
INF [WasmHost] started; loaded 1 module(s) from .../Mods/Wasm
INF [WasmHost] patched GameManager.Update
INF [wasm] hello mod alive at tick 57400 (world 7000)
INF Chat (from '-non-player-', entity id '-1', to 'Global'): hello survivor from a wasm mod at tick 58000
```

Telnet transcript (after "Logon successful"):

```
*** Server version: V 3.1.0 (b14) Compatibility Version: V 3.1.0
wasm status:
  host started, modules dir: .../Mods/Wasm
    hello (init tick 0, calls 63244, traps 0, fuel exhausted 0)
    guest log lines dropped: wasm=517
    chat guest log lines dropped: chat=5
version:
  Game version: V 3.1.0 (b14)
  Mod 1_HordeForge_WasmHost: 0.1.0
```

## What was proven

- The modlet loads and initializes inside a live dedicated server.
- The guest module loads, its init runs, and it receives ticks at the
  game's 20 TPS rhythm (100 ticks exactly every 5 seconds post-world-load).
- `get_world_time` reaches the real world (world 7000 = 11:40 in-game).
- `get_setting` reads the greeting from `wasm-settings.txt`.
- `send_chat` reaches the game's global chat pipeline.
- The log and chat rate limiters drop and count excess output (visible in
  `wasm status`); the chat drop count proved the game does not rate limit
  chat on its own.
- Zero guest traps and zero fuel-exhausted calls across ~63k dispatched
  ticks.
- `tools/targetcheck` passes against the container's fresh game build
  (V 3.1.0 b14), the same as this machine's install.

## Player join event (boss demo)

`boss-join-server.log` captures a live join by a loadgen bot
(`--name maci`, which the harness names `maci1`):

```
RequestToSpawnPlayer: 171, maci1, 5
[WasmHost] player spawned: maci1
```

The join reached the bridge and was dispatched to the guest's
`on_player_join` handler. The guest compares the name exactly, so `maci1`
does not print "THE BOSS IS HERE"; the exact `maci` match is covered by the
host test suite (`PlayerJoinDispatchPrintsBossMessage`). The loadgen
harness always appends its client id to bot names, so a live join with the
bare name "maci" needs a real client; the exact-match behavior itself is
unit-verified.

Hook findings from the live run: `GameManager.OnClientSpawned` and
`GameManager.PlayerSpawnedInWorld` never fire on the dedicated server for
remote joins; `GameManager.RequestToSpawnPlayer` is the server-side entry
point the game logs on every join, and the bridge patches that method.

## Findings that changed code

1. `GameTimer.Instance.ticks` reads 0 on the dedicated server, so the
   bridge now maintains its own monotonic tick counter (one increment per
   hook run, 20 TPS).
2. The game does not rate limit `ChatMessageServer`; the bridge now caps
   guest chat globally at 10 messages/second with a visible drop counter.
3. An empty `TelnetPassword` makes the telnet server reset every session;
   the acceptance config uses a local throwaway password (`wasmtest`).

## Reproducing

```bash
docker build -t 7dtd-wasm-acceptance .   # downloads the game (~17 GB)
bash run_acceptance.sh                    # starts the server with dist/Mods
# wait for "StartGame done", then:
python3 telnet_session.py 127.0.0.1 8081 wasmtest console.txt "wasm status" "wasm list" "version"
```

## Earlier failed attempts (native)

`baseline.log`, `stock.log`, and `stock_stdout.txt` document the native
crash on this machine: a Mono SIGSEGV in the game entrypoint before any mod
loads, identical with zero mods installed. The container run proves the
modlet itself is not the cause and works against a fresh install.
