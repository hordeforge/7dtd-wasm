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

## Native install status

The dedicated server installed on this machine still crashes at boot (Mono
SIGSEGV in the game entrypoint, before any mod loads; see
`evidence/acceptance-1/baseline.log` and `stock.log`). That crash is
unrelated to the modlet: the same modlet runs correctly against a fresh
install in the container. The native install should be repaired via Steam
(`steamcmd +app_update 294420 validate`) before trusting it for further
runs.

## What remains unproven

- Windows in-game native loading (documented, not exercised).
- Long-soak behavior (days of uptime, world saves, restarts).
- The remaining RFC candidates (event surface, boot payload, ABI
  versioning) are design work, not acceptance gaps.
