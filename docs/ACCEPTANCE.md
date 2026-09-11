# Acceptance status

## Attempt 1 (2026-08-24): SUCCESS in a docker container

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

## Bot servant verified live (2026-08-24)

The bot servant (stage 2) now runs on a live dedicated server in the
acceptance container: 4 bot entities spawned from the unmodified zdtd
fps_bot brain's sense/queue loop, and the brain drove them into combat
against each other with the servant applying the ordered damage (including
headshots). 1555 shots over the run; evidence:
`evidence/acceptance-1/servant-join.log`. Live-run fixes: spawn retries
until the world is ready, and bot classification checks the roster before
entity type (the bots are zombie-bodied).

Remaining (stage 3): cover/path queries, on_admin_command console wiring,
per-bot loadouts, and disabling the stock zombie AI on bot bodies.

## Aligned ABI verified live (2026-08-24)

After the ABI was aligned with the zdtd-server contract (bare hook
exports, TOML config), the container acceptance was re-run: a loadgen bot
join dispatched `on_player_join` to the Zig guest, which printed
"THE BOSS IS HERE" for the name configured in its `wasm-mod.toml`
(`boss_name = "maci1"`, set to the bot's harness-appended name). Evidence:
`evidence/acceptance-1/aligned-abi-join.log`.

Live-run finding fixed in this pass: `RequestToSpawnPlayer`'s int
parameters are `_chunkViewDim` and `_nearEntityId`, not the spawning
player's id, and Harmony postfix parameters must match the target's
parameter names exactly; the entity id comes from `ClientInfo.entityId`
(now also verified by `tools/targetcheck`).

## Native install status

The dedicated server installed on this machine still crashes at boot (Mono
SIGSEGV in the game entrypoint, before any mod loads; see
`evidence/acceptance-1/baseline.log` and `stock.log`). That crash is
unrelated to the modlet: the same modlet runs correctly against a fresh
install in the container. The native install should be repaired via Steam
(`steamcmd +app_update 294420 validate`) before trusting it for further
runs.

## Parachute playtest (2026-08-30)

The unmodified zdtd parachute module ran end to end on a live dedicated
server (docker, fresh steamcmd V3.2.0 b9) driven by a real stock client
through the `7dtd-playtest` orchestrator: the client wore the glider item,
fell, and the mod armed the glide exemption and announced through the stock
chat broadcast, which the client asserted (pass=2, exit=0). Server log
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
