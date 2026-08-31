#!/usr/bin/env bash
# Runs the 7dtd-wasm bridge + parachute mod inside the acceptance docker
# image (fresh steamcmd V 3.1.0 b14 install) for a live playtest: the
# server stays up (no -quit) so a real stock client can join through the
# 7dtd-playtest orchestrator.
#
# Prereqs: docker image 7dtd-wasm-acceptance:latest (evidence/acceptance-1),
#          `make dist` in the repo root (stages dist/Mods).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
DIST="$ROOT/dist"

mkdir -p "$HERE/logs"

docker rm -f 7dtd-wasm-playtest >/dev/null 2>&1 || true

# Loopback-only port bindings: the telnet console must never be reachable
# off-host (the playtest orchestrator uses the retest password).
docker run -d --name 7dtd-wasm-playtest \
  -p 127.0.0.1:8081:8081 \
  -p 127.0.0.1:26900:26900/tcp \
  -p 127.0.0.1:26900:26900/udp \
  -p 127.0.0.1:26902:26902/tcp \
  -p 127.0.0.1:26902:26902/udp \
  -e LD_LIBRARY_PATH=/game/Mods/1_HordeForge_WasmHost/Native \
  -v "$DIST/Mods/1_HordeForge_WasmHost:/game/Mods/1_HordeForge_WasmHost:ro" \
  -v "$HERE/wasm:/game/Mods/Wasm:ro" \
  -v "$HERE/parachute-items:/game/Mods/parachute-items:ro" \
  -v "$HERE/serverconfig.playtest.xml:/game/serverconfig.xml:ro" \
  -v "$HERE/platform.cfg:/game/platform.cfg:ro" \
  -v "$HERE/logs:/logs" \
  7dtd-wasm-acceptance:latest \
  /game/7DaysToDieServer.x86_64 \
    -logfile /logs/server.log \
    -batchmode -nographics -dedicated \
    -configfile=/game/serverconfig.xml

echo "container 7dtd-wasm-playtest started; log at $HERE/logs/server.log"
