# Acceptance status

## Live acceptance (2026-11-05): V3.3.0 b18

The modlet ran inside the stock V3.3.0 b18 dedicated server on this machine.
The install was not touched: the run used a btrfs reflink copy under
`.scratch/server/game`, its own `-configfile` and `UserDataFolder`, EAC
off, Navezgane, telnet 8087, game port 26970.

Install identity: steamcmd app 294420, buildid `25661908`,
`7DaysToDieServer_Data/Managed/Assembly-CSharp.dll` sha256
`e16ff9d539b8fbf64904c0bc090d502286a62464742a9396f0455936ad0beb87`,
11697152 bytes.

Build: `make dist GAME_DIR=<install> ZIG=<zig 0.16.0> DOTNET=<8.0.424>`
staged the 0.5.0 modlet (Wasmtime 48.0.2) and five guests. The host has zig
0.17.0 and .NET SDK 8.0.423; the repo pins zig 0.16.0 and SDK 8.0.424, so
both were downloaded into `.scratch/` for this run.

```bash
cd .scratch/server/game
LD_LIBRARY_PATH=$PWD/Mods/1_HordeForge_WasmHost/Native \
  ./7DaysToDieServer.x86_64 -logfile ../logs/fixed-server.log \
  -quit -batchmode -nographics -dedicated \
  -configfile=../serverconfig.xml
```

Staged bundle (sha256): bridge `3f61893282f0a37c6509834995b5fcdd057e5d66bcaf32c69cc742cda8645676`,
host `70c5e87e5bdf024a50c6b7e0d32bbac709b74e17997c6cffac074de2ed27b614`,
`libwasmtime.so` `a79ae67ddb3768d0ee8dd2be44ec27e297286649ec76a4a2bc4683466563728b`,
hello `2966af3d1dbd6604ed8b17c2e1d0d01c796e9fdf5e0e6ac95212e8b1ec3766eb`,
fps-bot `53009bbbdc500b9559005f625bcc491435632a0aa6fadb86f8efad91b5a3f0ce`,
parachute `e719f30388cd7d578767464ca1fe53b569539f6c2c67631d67f0a4a400c83de0`,
boss `bbba8ad7738835e1295370c4a88827d75622bd15b45ff3f108f43fbfdd457ac8`,
boss-zig `9866a6a92fd3b62afb95b21fcf14ca9e19390b79c8289d6d9bc8e6a64192ecbe`.

### Evidence

Logs are the game's own output and stay under git-ignored `.scratch/`;
paths are relative to the repo root.

| File | sha256 | What it shows |
|---|---|---|
| `.scratch/server/logs/b18-stock-server.log` | `4686e7781bc5285c525c914d8f9b9a5e84bed2feabbb6901a4619d6f4a76bbb5` | stock b18 boot and world start, no mod loaded |
| `.scratch/server/logs/b18-stock-console.txt` | `dbc266a0dceb7b59e1071a098b550a9f262ae5390c86940385300f2c25ebd18d` | stock `version` and `shutdown` over telnet |
| `.scratch/server/logs/fixed-server.log` | `f9e0570b3ba4e8facdd88f39e869d47019368661fc8a5321b125e9fa8549bc2f` | the acceptance run with all five guests |
| `.scratch/server/logs/fixed-console.txt` | `8521a4425f900bb94b55d7e56c059fa3c9c7a2f73a9c91f3d6d09ecf69674458` | `version`, `wasm status`, `wasm list` over telnet |
| `.scratch/server/logs/b18-prefix-sense-nre.log` | `5d945708fe10f356db4d33a292481b46b1c42e21239630373752b4e0210f4401` | same five guests before the sense fix: spawns, then NRE on every sense |
| `.scratch/server/logs/b18-sense-nre-pdb.log` | `05a3813424aa3da535f666b21ddb17adbb01800b4e45863864588c13ce848604` | diagnosis run with PDBs: the NRE at `BotServant.cs:561` |

Decisive lines from `fixed-server.log`:

```
INF [WasmHost] wasmtime native engine resolves on the loader path
INF [wasm/hello] hello mod loaded
INF [WasmHost] started; loaded 5 module(s) from .../Mods/Wasm
INF [WasmHost] limits: fuel/call 1000000, memory 4294967296 bytes, module cap 1048576 bytes, stack cap 1048576 bytes, guest stdio inherited no (shared config: .../Mods/Wasm/wasm.toml)
INF [WasmHost] patched GameManager.Update
INF [WasmHost] patched GameManager.RequestToSpawnPlayer
INF [wasm/hello] hello mod alive at tick 49700 (world 7000)
INF Chat (from '-non-player-', entity id '-1', to 'Global'): hello survivor from a wasm mod at tick 50000
INF [WasmHost] bot spawned entity 171 for fps-bot at 0.0,60.0,0.0
INF [WasmHost] bot 172 shot 171 dmg=12
INF [WasmHost] bot 173 shot 171 dmg=24 head
INF [WasmHost] heartbeat tick 49200, 5 module(s); dispatch: 0.11 ms last, 0.63 ms avg, 358.86 ms max over 1200 tick(s); 0 failure(s), 2 slow tick(s); failed since last heartbeat: none
```

Decisive lines from `fixed-console.txt`:

```
*** Server version: V 3.3.0 (b18) Compatibility Version: V 3.3.0
Mod 1_HordeForge_WasmHost: 0.5.0
    fps-bot (fuel/call 1000000, init tick 0, calls 49809, fuel used 24007807, traps 0, fuel exhausted 0, errors 0)
    hello (fuel/call 1000000, init tick 0, calls 49809, fuel used 2579831, traps 0, fuel exhausted 0, errors 0)
    ...
    sense snapshots dropped: fps-bot=46328, parachute=46328
    bot servant log lines dropped: bot/shot=417
```

The sense drop counter is the per-module 200 per second cap on snapshot
builds, not a failure: both brains requested faster than the cap and kept
firing on the snapshots they got.

### What this proves

- The net48 bridge loads in a live stock V3.3.0 b18 dedicated server, the
  Wasmtime native engine resolves from the staged modlet, and both Harmony
  patches bind (`GameManager.Update`, `GameManager.RequestToSpawnPlayer`).
- The host starts with five guests. Every guest initializes, receives
  ticks, and stays inside its budget: `calls 49809` per module, `traps 0`,
  `fuel exhausted 0`, `errors 0`.
- The hello guest reads real world time (`world 7000` at tick 49700),
  receives the shared TOML settings, and reaches global chat every 1000
  ticks (`hello survivor from a wasm mod at tick 50000`; the chat limiter
  dropped 4 messages over the run).
- The unmodified zdtd `fps_bot` and `parachute` modules load against b18,
  their brains get sense snapshots, the servant spawns 8 zombieSoldier
  bodies, and the brains drive them to fire: 539 `bot <id> shot <id>` log
  lines, 85 of them headshots, with 401 more shot lines suppressed by the
  servant's 10 per second log cap.
- The tick rate is 20 TPS and dispatch settles low: 0.63 ms average over
  the last 1200-tick window. Two ticks before the world finished loading
  went over the 25 ms warning threshold (267.07 and 358.86 ms, both named
  `fps-bot`).

### Live finding fixed in this change

The first b18 run (`b18-prefix-sense-nre.log`) loaded everything, then the
unmodified brains got no data because every sense call threw:

```
WRN [WasmHost] sense failed: System.NullReferenceException: Object reference not set to an instance of an object at HordeForge.GameBridge.Bridge.BotServant.WriteSenseLocked (...) in <...>:0
WRN [WasmHost] suppressed 4901 sense failure log(s)
```

A diagnosis run with the bridge PDBs next to the DLL named the line:
`src/GameBridge/Bridge/BotServant.cs:561`, `record.NetId = e.entityId`.
`_senseRecords` was allocated as `new EntityRecord[MaxSenseRecords]`, a
null-filled array, and the sense scan assigned into those null slots; the
pooling commit (`e01a683`, 2026-08-25) landed after the b14 bot-servant
run, so this was the first live exercise of that path. The constructor now
constructs the slots once (9 lines, no per-call allocation). The same
source also gives `wasm status` its per-module counters and the guest
`on_tick` heartbeat, which the fixed run above exercises.

### Still unproven on b18

- Player join dispatch. No player was in the world in this run; the client
  booked for `7dtd-playtest` was the only one this host may attach. The
  join hook is proven on b14/b9 only (see below).
- The parachute glide sequence end to end: it needs a falling player. The
  module loads and ticks on b18; its scene was not reproduced here.
- Windows in-game native loading (documented, not exercised).
- Long-soak behavior (days of uptime, world saves, restarts).
- Bot spawn placement: all 8 bots came out at the servant's
  `0.0,60.0,0.0` fallback because the world spawn point was not ready
  until `StartGame done` (29.653 s), after the first sense/spawn at
  20.798 s. DM spawn-point placement was not re-tuned here.

## Earlier runs (V 3.1.0 b14, V3.2.0 b9)

The sections below are dated history. Their claims are for those builds
only; the live b18 section above is the current status.

### Attempt 1 (2026-08-24): SUCCESS in a docker container

The modlet ran inside a live dedicated server (fresh steamcmd install in a
docker container, V 3.1.0 b14) and the guest module loaded, ticked at the
game's 20 TPS, read world time and settings, and sent global chat. Full
evidence and reproduction steps: `evidence/acceptance-1/README.md`
(server log `acceptance-server.log`, telnet transcript `console.txt`).

Environment facts:

- Game: 7 Days to Die dedicated server V 3.1.0 (b14), Unity 2022.3.62f2,
  fresh steamcmd install (Steam app 294420)
- Host: docker container, debian-based steamcmd image, game bind-mounted
  mods, native library path via process-start `LD_LIBRARY_PATH`
- Server config: Navezgane, telnet 8081, EAC off, fresh userdata

## Findings that changed code

1. `GameTimer.Instance.ticks` reads 0 on the dedicated server; the bridge
   now keeps its own monotonic tick counter.
2. The game does not rate limit `ChatMessageServer` on its own; the bridge
   caps guest chat (10 messages/second global) with a visible drop counter.
3. An empty telnet password makes the server reset every session; the
   acceptance config uses a local throwaway password.

### Bot servant verified live (2026-08-24)

The bot servant (stage 2) now runs on a live dedicated server in the
acceptance container: 4 bot entities spawned from the unmodified zdtd
fps_bot brain's sense/queue loop, and the brain drove them into combat
against each other with the servant applying the ordered damage (including
headshots). 1892 shots over the run, 279 of them headshots; evidence:
`evidence/acceptance-1/servant-join.log`. Live-run fixes: spawn retries
until the world is ready, and bot classification checks the roster before
entity type (the bots are zombie-bodied).

Remaining (stage 3): cover/path queries, on_admin_command console wiring,
per-bot loadouts, and disabling the stock zombie AI on bot bodies.

### Aligned ABI verified live (2026-08-24)

After the ABI was aligned with the zdtd-server contract (bare hook
exports, TOML config), the container acceptance was re-run: a loadgen bot
join dispatched `on_player_join` to the Zig guest, which printed
"THE BOSS IS HERE" for the name configured in its `wasm-mod.toml`
(`boss_name = "<player-name-redacted>"`, set to the bot's harness-appended name). Evidence:
`evidence/acceptance-1/aligned-abi-join.log`.

Live-run finding fixed in this pass: `RequestToSpawnPlayer`'s int
parameters are `_chunkViewDim` and `_nearEntityId`, not the spawning
player's id, and Harmony postfix parameters must match the target's
parameter names exactly; the entity id comes from `ClientInfo.entityId`
(now also verified by `tools/targetcheck`).

The earlier native install at this machine's Steam path crashed at boot
(Mono SIGSEGV in the game entrypoint before any mod loaded;
`evidence/acceptance-1/baseline.log` and `stock.log`). That install was
not used. The V3.3.0 b18 install in the section above is a separate fresh
steamcmd install and boots stock (`b18-stock-server.log`), so the old
crash was a property of that install, not of the game build.

### Parachute playtest (2026-09-04)

The unmodified zdtd parachute module ran end to end on a live dedicated
server (docker, fresh steamcmd V3.2.0 b9) driven by a real stock client
through the `7dtd-playtest` orchestrator: the client equipped the glider
item, fell, the mod armed the glide exemption and announced through the
stock chat broadcast, and the client landed safe (alive, no broken leg),
`SUMMARY pass=3 fail=0` with the orchestrator at `exit=0`. Server log
showed `parachute: config deploy_vy=-6 delay_ticks=10` (the `zdtd.config`
import serving the mod's config.toml), `glide <id> armed`, the announce,
and `glide <id> cleared` on landing. Evidence and reproduction:
`evidence/playtest-1/README.md`.

Live findings fixed in this pass: V3.2.0's `teleportplayer` does not move
remote-player entities (the fall is client-owned physics), the stock server
does not populate `Entity.motion` for remote players (sense v4 `vy` is
derived from the per-tick position history), and the V3.2.0 client on Linux
must run with `crossplatform=None` (the EOS path crashes) and a stock
`Assembly-CSharp.dll` (RealEarth's height-expand patch breaks chunk
deserialization). Environment caveat: the live client+Proton layer became
flaky after the passing run (a client-side `NetPackageChunk` read failure
during world load), isolated to the environment, not the wasm/bridge code.

## What remains unproven

- Windows in-game native loading (documented, not exercised).
- Long-soak behavior (days of uptime, world saves, restarts).
- The remaining RFC candidates (event surface, boot payload, ABI
  versioning) are design work, not acceptance gaps.
